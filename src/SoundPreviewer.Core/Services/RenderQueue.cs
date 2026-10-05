using System.Text.Json;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Core.Services;

public sealed record QueueProgress(int Completed, int Total, string PresetName, string Message, PresetRecord? Preset);

public sealed class RenderQueue(LibraryStore store)
{
    public async Task RunAsync(AppSettings settings, IReadOnlyList<PresetRecord> presets, string workerPath,
        IProgress<QueueProgress> progress, CancellationToken cancellationToken)
    {
        settings.Template.Validate();
        if (settings.MaxAttempts is < 1 or > 5) throw new ArgumentException("重试上限须为 1 到 5。");
        if (!double.IsFinite(settings.StorageBudgetGb) || settings.StorageBudgetGb <= 0) throw new ArgumentException("存储预算须大于 0。");
        Directory.CreateDirectory(settings.PreviewDirectory);
        var candidates = presets.Where(p => p.Status is not (PresetStatus.Unsupported or PresetStatus.Missing)).ToList();
        var completed = 0;
        foreach (var preset in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                preset.Status = PresetStatus.Failed;
                preset.Message = configurationError;
                store.Upsert(preset);
                progress.Report(new(++completed, candidates.Count, preset.Name, configurationError, preset));
                continue;
            }
            var key = Fingerprints.CacheKey(preset, plugin, settings.Template);
            var lastAttemptKey = preset.AttemptedCacheKey.Length > 0 ? preset.AttemptedCacheKey : preset.CacheKey;
            if (lastAttemptKey.Length > 0 && lastAttemptKey != key)
            {
                preset.Attempts = 0;
                preset.Status = PresetStatus.Pending;
                preset.AttemptedCacheKey = key;
                preset.Message = "渲染配置已变更，等待生成。";
                store.Upsert(preset);
            }
            if (preset.Status is PresetStatus.Ready or PresetStatus.NeedsReview && !File.Exists(preset.PreviewPath))
            {
                preset.Status = PresetStatus.Pending;
                preset.Attempts = 0;
                preset.Message = "预览文件已丢失，等待重新生成。";
            }
            if (preset.CacheKey == key && File.Exists(preset.PreviewPath) && preset.Status is PresetStatus.Ready or PresetStatus.NeedsReview)
            {
                try
                {
                    var metrics = WaveFile.Validate(preset.PreviewPath);
                    WorkerProcessClient.ValidateNotes(new RenderResult { Metrics = metrics, Notes = preset.Notes, AdapterVersion = preset.AdapterVersion }, settings.Template);
                    progress.Report(new(++completed, candidates.Count, preset.Name, "有效缓存，已跳过。", preset));
                    continue;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
                {
                    preset.Status = PresetStatus.Pending;
                    preset.Attempts = 0;
                    preset.Message = "缓存校验失败，重新生成。";
                }
            }
            if (preset.Attempts >= settings.MaxAttempts && preset.Status == PresetStatus.Pending)
                preset.Attempts = settings.MaxAttempts - 1;
            if (preset.Attempts >= settings.MaxAttempts)
            {
                progress.Report(new(++completed, candidates.Count, preset.Name, "已达到重试上限。", preset));
                continue;
            }
            var estimatedBytes = (settings.Template.HoldSeconds + settings.Template.TailSeconds) * 3 * settings.Template.SampleRate * 8 + 4096;
            var occupiedBytes = Directory.EnumerateFiles(settings.PreviewDirectory, "*.wav", SearchOption.TopDirectoryOnly).Sum(p => new FileInfo(p).Length);
            var outputPath = Path.Combine(settings.PreviewDirectory, key + ".wav");
            var replacedBytes = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
            if (occupiedBytes - replacedBytes + estimatedBytes > settings.StorageBudgetGb * 1024 * 1024 * 1024)
                throw new IOException("预览目录已达到存储预算，请调整预算或预览目录。");
            while (preset.Attempts < settings.MaxAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                preset.Status = PresetStatus.Rendering;
                preset.AttemptedCacheKey = key;
                preset.Attempts++;
                preset.Message = $"第 {preset.Attempts} 次尝试";
                store.Upsert(preset);
                progress.Report(new(completed, candidates.Count, preset.Name, preset.Message, preset));
                try
                {
                    var request = new RenderRequest
                    {
                        Preset = preset, Plugin = plugin, Template = settings.Template,
                        OutputPath = outputPath, CacheKey = key,
                        MaxOutputBytes = Math.Min(256L * 1024 * 1024, checked((long)(settings.StorageBudgetGb * 1024 * 1024 * 1024)) - occupiedBytes + replacedBytes)
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
                    break;
                }
                catch (OperationCanceledException)
                {
                    preset.Status = PresetStatus.Pending;
                    preset.Attempts = Math.Max(0, preset.Attempts - 1);
                    preset.Message = "已暂停，可继续。";
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
            store.Upsert(preset);
            progress.Report(new(++completed, candidates.Count, preset.Name, preset.Message, preset));
        }
    }
}
