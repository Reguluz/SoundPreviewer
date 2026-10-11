using System.Text.Json;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Core.Services;

public sealed record QueueProgress(int Completed, int Total, string PresetName, string Message, PresetRecord? Preset);

public sealed class RenderQueue(LibraryStore store)
{
    public async Task RunAsync(AppSettings settings, IReadOnlyList<PresetRecord> presets, string workerPath,
        IProgress<QueueProgress> progress, CancellationToken cancellationToken, bool forceRegenerate = false)
    {
        if (settings.MaxAttempts is < 1 or > 5) throw new ArgumentException("重试上限须为 1 到 5。");
        if (!double.IsFinite(settings.StorageBudgetGb) || settings.StorageBudgetGb <= 0) throw new ArgumentException("存储预算须大于 0。");
        var candidates = presets.Where(p => p.Synth != SynthKind.Unknown &&
            string.IsNullOrEmpty(p.DuplicateOfId) &&
            p.Status is not (PresetStatus.Unsupported or PresetStatus.Missing or PresetStatus.Unidentified)).ToList();
        var completed = 0;
        long? occupiedBytes = null;
        foreach (var preset in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasPreview = HasValidPreview(preset, cancellationToken);
            if (hasPreview && !forceRegenerate)
            {
                if (preset.Status is not (PresetStatus.Ready or PresetStatus.NeedsReview))
                {
                    preset.Status = PresetStatus.NeedsReview;
                    preset.Message = "保留已生成的有效预览，请试听确认。";
                    store.Upsert(preset);
                }
                progress.Report(new(++completed, candidates.Count, preset.Name, "已生成有效预览，默认跳过。", preset));
                continue;
            }
            settings.Template.Validate();
            var plugin = settings.Plugins.FirstOrDefault(p => p.Synth == preset.Synth);
            if (plugin is null || plugin.Backend == RenderBackend.Unconfigured)
            {
                progress.Report(new(++completed, candidates.Count, preset.Name, "音源尚未配置渲染器。", preset));
                continue;
            }
            var configurationError = !File.Exists(plugin.ExecutablePath) ? $"{preset.Synth} 渲染程序不存在。" :
                plugin.TimeoutSeconds is < 5 or > 3600 ? "进程超时须为 5 到 3600 秒。" :
                string.IsNullOrWhiteSpace(plugin.Version) ? $"请先填写 {preset.Synth} 的实际版本。" : "";
            if (configurationError.Length > 0)
            {
                if (!hasPreview)
                {
                    preset.Status = PresetStatus.Failed;
                    preset.Message = configurationError;
                    store.Upsert(preset);
                }
                progress.Report(new(++completed, candidates.Count, preset.Name, configurationError, preset));
                continue;
            }
            var key = Fingerprints.CacheKey(preset, plugin, settings.Template);
            var lastAttemptKey = preset.AttemptedCacheKey.Length > 0 ? preset.AttemptedCacheKey : preset.CacheKey;
            bool resetAttempts = forceRegenerate || lastAttemptKey.Length > 0 && lastAttemptKey != key ||
                preset.Status is PresetStatus.Ready or PresetStatus.NeedsReview;
            if (!resetAttempts && preset.Attempts >= settings.MaxAttempts && preset.Status != PresetStatus.Pending)
            {
                progress.Report(new(++completed, candidates.Count, preset.Name, "已达到重试上限。", preset));
                continue;
            }
            Directory.CreateDirectory(settings.PreviewDirectory);
            var estimatedBytes = (settings.Template.HoldSeconds + settings.Template.TailSeconds) * 3 * settings.Template.SampleRate * 8 + 4096;
            occupiedBytes ??= Directory.EnumerateFiles(settings.PreviewDirectory, "*.wav", SearchOption.TopDirectoryOnly).Sum(p => new FileInfo(p).Length);
            var outputPath = Path.Combine(settings.PreviewDirectory, key + ".wav");
            var replacedBytes = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
            if (occupiedBytes.Value - replacedBytes + estimatedBytes > settings.StorageBudgetGb * 1024 * 1024 * 1024)
                throw new IOException("预览目录已达到存储预算，请调整预算或预览目录。");
            PresetRecord? previous = hasPreview ? JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(preset, LibraryJson.Options), LibraryJson.Options) : null;
            if (resetAttempts) preset.Attempts = 0;
            else if (preset.Attempts >= settings.MaxAttempts && preset.Status == PresetStatus.Pending)
                preset.Attempts = settings.MaxAttempts - 1;
            bool generated = false;
            while (preset.Attempts < settings.MaxAttempts)
            {
                bool attemptStarted = false;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    preset.Status = PresetStatus.Rendering;
                    preset.AttemptedCacheKey = key;
                    preset.Attempts++;
                    attemptStarted = true;
                    preset.Message = $"第 {preset.Attempts} 次尝试";
                    store.Upsert(preset);
                    progress.Report(new(completed, candidates.Count, preset.Name, preset.Message, preset));
                    var request = new RenderRequest
                    {
                        Preset = preset, Plugin = plugin, Template = settings.Template,
                        OutputPath = outputPath, CacheKey = key,
                        MaxOutputBytes = Math.Min(256L * 1024 * 1024, checked((long)(settings.StorageBudgetGb * 1024 * 1024 * 1024)) - occupiedBytes.Value + replacedBytes)
                    };
                    var result = await new WorkerProcessClient().RenderAsync(request, workerPath, cancellationToken);
                    if (!result.Success) throw new InvalidOperationException(result.Message);
                    preset.PreviewPath = result.OutputPath;
                    preset.CacheKey = key;
                    preset.Metrics = result.Metrics;
                    preset.Notes = result.Notes;
                    preset.AdapterVersion = result.AdapterVersion;
                    preset.PluginVersion = plugin.Version;
                    preset.TemplateJson = JsonSerializer.Serialize(settings.Template, LibraryJson.Options);
                    preset.RenderedUtc = DateTime.UtcNow;
                    preset.Status = result.NeedsReview ? PresetStatus.NeedsReview : PresetStatus.Ready;
                    preset.Message = result.Message;
                    occupiedBytes = occupiedBytes.Value - replacedBytes + new FileInfo(result.OutputPath).Length;
                    generated = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    if (previous is not null) RestorePreview(preset, previous, "已停止，保留上次已生成的预览。");
                    else
                    {
                        preset.Status = PresetStatus.Pending;
                        if (attemptStarted) preset.Attempts = Math.Max(0, preset.Attempts - 1);
                        preset.Message = "已停止，可继续。";
                    }
                    store.Upsert(preset);
                    progress.Report(new(completed, candidates.Count, preset.Name, preset.Message, preset));
                    throw;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or TimeoutException or ArgumentException)
                {
                    preset.Status = PresetStatus.Failed;
                    preset.Message = ex.Message;
                    store.Upsert(preset);
                    progress.Report(new(completed, candidates.Count, preset.Name, ex.Message, preset));
                }
            }
            if (!generated && previous is not null) RestorePreview(preset, previous, "重新生成失败，保留上次预览：" + preset.Message);
            store.Upsert(preset);
            progress.Report(new(++completed, candidates.Count, preset.Name, preset.Message, preset));
        }
    }

    private static bool HasValidPreview(PresetRecord preset, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(preset.PreviewPath) || !File.Exists(preset.PreviewPath) ||
            string.IsNullOrWhiteSpace(preset.CacheKey) || string.IsNullOrWhiteSpace(preset.TemplateJson)) return false;
        try
        {
            token.ThrowIfCancellationRequested();
            var template = JsonSerializer.Deserialize<PreviewTemplate>(preset.TemplateJson, LibraryJson.Options)
                ?? throw new InvalidDataException("预览缺少已保存的试听模板。");
            template.Validate();
            var metrics = WaveFile.Validate(preset.PreviewPath);
            token.ThrowIfCancellationRequested();
            if (metrics.SampleRate != template.SampleRate) return false;
            WorkerProcessClient.ValidateNotes(new RenderResult { Metrics = metrics, Notes = preset.Notes, AdapterVersion = preset.AdapterVersion }, template);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            return false;
        }
    }

    private static void RestorePreview(PresetRecord preset, PresetRecord previous, string message)
    {
        preset.Status = previous.Status is PresetStatus.Ready or PresetStatus.NeedsReview ? previous.Status : PresetStatus.NeedsReview;
        preset.Attempts = previous.Attempts;
        preset.Message = string.IsNullOrWhiteSpace(previous.Message) ? message : previous.Message + " " + message;
        preset.PreviewPath = previous.PreviewPath;
        preset.CacheKey = previous.CacheKey;
        preset.AttemptedCacheKey = previous.AttemptedCacheKey;
        preset.Metrics = previous.Metrics;
        preset.Notes = previous.Notes;
        preset.AdapterVersion = previous.AdapterVersion;
        preset.PluginVersion = previous.PluginVersion;
        preset.TemplateJson = previous.TemplateJson;
        preset.RenderedUtc = previous.RenderedUtc;
    }
}
