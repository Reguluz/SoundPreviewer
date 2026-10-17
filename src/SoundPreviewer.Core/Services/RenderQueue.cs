using System.Text.Json;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Core.Services;

public static class QueueStages
{
    public const string Preparing = "准备";
    public const string Validating = "校验";
    public const string Rendering = "渲染";
    public const string Completed = "完成";
    public const string Stopped = "已停止";
    public const string Failed = "失败";
}

public sealed record QueueProgress(int Completed, int Total, string PresetName, string Message, PresetRecord? Preset)
{
    public string Stage { get; init; } = "";
    public bool IsIndeterminate { get; init; }
}

public sealed class QueueResult
{
    private readonly Dictionary<SynthKind, int> unconfiguredSynths = new();
    public int Requested { get; internal set; }
    public int Eligible { get; internal set; }
    public int Generated { get; internal set; }
    public int Reused { get; internal set; }
    public int Failed { get; internal set; }
    public int Unconfigured { get; internal set; }
    public int RetryLimited { get; internal set; }
    public int Excluded { get; internal set; }
    public int Processed => Generated + Reused + Failed + Unconfigured + RetryLimited;
    public int Remaining => Math.Max(0, Eligible - Processed);
    public IReadOnlyDictionary<SynthKind, int> UnconfiguredSynths => unconfiguredSynths;
    public string Summary
    {
        get
        {
            if (Eligible == 0) return $"没有可生成项目：所选 {Requested} 项，排除 {Excluded} 项（重复项或当前不可生成项目）。";
            string prefix = Generated == 0 && Reused == 0 ? "本次没有生成预览。" : "";
            string missing = Unconfigured == 0 ? "" : " 尚未配置渲染器：" + string.Join("、", unconfiguredSynths.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key} {pair.Value} 项")) + "。";
            return prefix + $"新生成 {Generated} 项，复用 {Reused} 项，失败 {Failed} 项，未配置 {Unconfigured} 项，重试上限 {RetryLimited} 项，排除 {Excluded} 项。" + missing;
        }
    }

    internal void AddUnconfigured(SynthKind synth)
    {
        Unconfigured++;
        unconfiguredSynths[synth] = unconfiguredSynths.GetValueOrDefault(synth) + 1;
    }
}

public sealed class RenderQueue(LibraryStore store)
{
    public async Task<QueueResult> RunAsync(AppSettings settings, IReadOnlyList<PresetRecord> presets, string workerPath,
        IProgress<QueueProgress> progress, CancellationToken cancellationToken, bool forceRegenerate = false)
    {
        var summary = new QueueResult { Requested = presets.Count };
        bool prepared = false;
        PresetRecord? current = null;
        long? occupiedBytes = null;
        PreviewReferences? previewReferences = null;
        var baselines = new Dictionary<SynthKind, PreparedInitBaseline>();
        Report(QueueStages.Preparing, "正在准备生成队列...", indeterminate: true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (settings.MaxAttempts is < 1 or > 5) throw new ArgumentException("重试上限须为 1 到 5。");
            if (!double.IsFinite(settings.StorageBudgetGb) || settings.StorageBudgetGb <= 0) throw new ArgumentException("存储预算须大于 0。");
            if (forceRegenerate)
                previewReferences = new PreviewReferences(store.LoadPresets(), presets, settings.PreviewDirectory, cancellationToken);
            var candidates = new List<PresetRecord>();
            foreach (var preset in presets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (preset.Synth == SynthKind.Unknown || !string.IsNullOrEmpty(preset.DuplicateOfId) ||
                    preset.Status is PresetStatus.Unsupported or PresetStatus.Missing or PresetStatus.Unidentified)
                {
                    summary.Excluded++;
                    continue;
                }
                candidates.Add(preset);
                summary.Eligible++;
            }
            prepared = true;
            Report(QueueStages.Preparing, $"待处理 {summary.Eligible} 项，排除 {summary.Excluded} 项。", indeterminate: true);
            var plugins = settings.Plugins.GroupBy(plugin => plugin.Synth).ToDictionary(group => group.Key, group => group.First());
            var pending = new List<PresetRecord>();
            foreach (var preset in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool unconfigured = !plugins.TryGetValue(preset.Synth, out var plugin) || plugin.Backend == RenderBackend.Unconfigured;
                if (unconfigured && (forceRegenerate || !HasPreviewMetadata(preset))) summary.AddUnconfigured(preset.Synth);
                else pending.Add(preset);
            }
            if (summary.Unconfigured > 0)
                Report(QueueStages.Preparing, $"已识别 {summary.Unconfigured} 项未配置渲染器。" + summary.Summary);
            foreach (var preset in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current = preset;
                Report(QueueStages.Validating, "正在核对已有预览及试听配置...", preset, true);
                bool hasPreview = HasValidPreview(preset, cancellationToken);
                if (hasPreview && !forceRegenerate)
                {
                    if (preset.Status is not (PresetStatus.Ready or PresetStatus.NeedsReview))
                    {
                        preset.Status = PresetStatus.NeedsReview;
                        preset.Message = "保留已生成的有效预览，请试听确认。";
                        store.Upsert(preset);
                    }
                    summary.Reused++;
                    Report(QueueStages.Validating, "已生成有效预览，默认跳过。", preset);
                    current = null;
                    continue;
                }
                if (!plugins.TryGetValue(preset.Synth, out var plugin) || plugin.Backend == RenderBackend.Unconfigured)
                {
                    summary.AddUnconfigured(preset.Synth);
                    Report(QueueStages.Validating, "音源尚未配置渲染器。", preset);
                    current = null;
                    continue;
                }
                settings.Template.Validate();
                var configurationError = !File.Exists(plugin.ExecutablePath) ? $"{preset.Synth} 渲染程序不存在。" :
                    plugin.TimeoutSeconds is < 5 or > 3600 ? "进程超时须为 5 到 3600 秒。" :
                    string.IsNullOrWhiteSpace(plugin.Version) ? $"请先填写 {preset.Synth} 的实际版本。" : "";
                if (configurationError.Length > 0)
                {
                    preset.LastRenderOutcome = "失败";
                    preset.LastRenderMessage = configurationError;
                    if (!hasPreview)
                    {
                        preset.Status = PresetStatus.Failed;
                        preset.Message = configurationError;
                    }
                    store.Upsert(preset);
                    summary.Failed++;
                    Report(QueueStages.Validating, configurationError, preset);
                    current = null;
                    continue;
                }
                var key = Fingerprints.CacheKey(preset, plugin, settings.Template);
                var lastAttemptKey = preset.AttemptedCacheKey.Length > 0 ? preset.AttemptedCacheKey : preset.CacheKey;
                bool resetAttempts = forceRegenerate || lastAttemptKey.Length > 0 && lastAttemptKey != key ||
                    preset.Status is PresetStatus.Ready or PresetStatus.NeedsReview;
                if (!resetAttempts && preset.Attempts >= settings.MaxAttempts && preset.Status != PresetStatus.Pending)
                {
                    summary.RetryLimited++;
                    Report(QueueStages.Validating, "已达到重试上限。", preset);
                    current = null;
                    continue;
                }
                if (!baselines.TryGetValue(preset.Synth, out var baseline))
                {
                    Report(QueueStages.Validating, "正在检查或建立默认启动音色基准...", preset, true);
                    string previousBaselinePath = plugin.InitBaselinePath;
                    try { baseline = await InitBaselineService.PrepareAsync(settings, plugin, workerPath, cancellationToken); }
                    finally
                    {
                        if (plugin.InitBaselinePath != previousBaselinePath) store.SaveSettings(settings);
                    }
                    baselines.Add(preset.Synth, baseline);
                }
                if (InitBaselineService.Supports(plugin) && !baseline.State.CanCompare)
                {
                    string reason = "默认启动音色保护未就绪：" + baseline.State.Detail;
                    preset.LastRenderOutcome = "失败";
                    preset.LastRenderMessage = reason;
                    if (!hasPreview) { preset.Status = PresetStatus.Failed; preset.Message = reason; }
                    store.Upsert(preset);
                    summary.Failed++;
                    Report(QueueStages.Validating, reason, preset);
                    current = null;
                    continue;
                }
                Directory.CreateDirectory(settings.PreviewDirectory);
                var estimatedBytes = (settings.Template.QuickPreview ? 0.5 : settings.Template.HoldSeconds + settings.Template.TailSeconds)
                    * 3 * settings.Template.SampleRate * 8 + 4096;
                if (occupiedBytes is null)
                {
                    Report(QueueStages.Preparing, "正在统计预览目录占用...", preset, true);
                    occupiedBytes = 0;
                    foreach (string path in Directory.EnumerateFiles(settings.PreviewDirectory, "*.wav", SearchOption.TopDirectoryOnly))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        occupiedBytes += new FileInfo(path).Length;
                    }
                }
                var outputPath = Path.Combine(settings.PreviewDirectory, key + ".wav");
                var replacedBytes = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
                var oldCache = hasPreview ? previewReferences?.Reclaimable(preset.PreviewPath, preset.CacheKey, outputPath, 1) : null;
                long reclaimableBytes = oldCache?.Bytes ?? 0;
                if (occupiedBytes.Value - replacedBytes - reclaimableBytes + estimatedBytes > settings.StorageBudgetGb * 1024 * 1024 * 1024)
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
                        preset.LastRenderOutcome = "生成中";
                        preset.LastRenderMessage = preset.Message;
                        store.Upsert(preset);
                        Report(QueueStages.Rendering, preset.Message, preset, true);
                        var request = new RenderRequest
                        {
                            Preset = preset, Plugin = plugin, Template = settings.Template,
                            OutputPath = outputPath, CacheKey = key,
                            MaxOutputBytes = Math.Min(256L * 1024 * 1024, checked((long)(settings.StorageBudgetGb * 1024 * 1024 * 1024)) - occupiedBytes.Value + replacedBytes + reclaimableBytes)
                        };
                        var result = await new WorkerProcessClient().RenderAsync(request, workerPath, cancellationToken,
                            (candidate, audio, token) =>
                            {
                                if (baseline.Signature is not null)
                                {
                                    var comparison = InitAudioComparer.Compare(baseline.Signature, audio, candidate.Notes, token);
                                    if (comparison.IsMatch) throw new InvalidDataException(InitBaselineService.RejectionPrefix + comparison.Detail);
                                }
                                else
                                {
                                    candidate.NeedsReview = true;
                                    candidate.Message += " 默认音色保护未适配：" + baseline.State.Detail;
                                }
                                return Task.CompletedTask;
                            });
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
                        preset.LastRenderOutcome = "成功";
                        preset.LastRenderMessage = result.Message;
                        occupiedBytes = occupiedBytes.Value - replacedBytes + new FileInfo(result.OutputPath).Length;
                        generated = true;
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        preset.LastRenderOutcome = "已停止";
                        preset.LastRenderMessage = "已停止；已存在的有效预览保留，可继续生成。";
                        if (previous is not null) RestorePreview(preset, previous, "已停止，保留上次已生成的预览。");
                        else
                        {
                            preset.Status = PresetStatus.Pending;
                            if (attemptStarted) preset.Attempts = Math.Max(0, preset.Attempts - 1);
                            preset.Message = "已停止，可继续。";
                        }
                        store.Upsert(preset);
                        throw;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or TimeoutException or ArgumentException)
                    {
                        preset.Status = PresetStatus.Failed;
                        preset.Message = ex.Message;
                        preset.LastRenderOutcome = "失败";
                        preset.LastRenderMessage = ex.Message;
                        store.Upsert(preset);
                        Report(QueueStages.Rendering, ex.Message, preset);
                        if (ex.Message.StartsWith(InitBaselineService.RejectionPrefix, StringComparison.Ordinal)) break;
                    }
                }
                if (!generated && previous is not null) RestorePreview(preset, previous, "重新生成失败，保留上次预览：" + preset.Message);
                store.Upsert(preset);
                if (generated)
                {
                    previewReferences?.Move(preset.Id, preset.PreviewPath);
                    if (oldCache is not null && previewReferences is not null)
                    {
                        // The new record is committed before releasing an unshared, application-owned old cache.
                        var cleanup = previewReferences.DeleteCommitted(oldCache, preset.PreviewPath);
                        occupiedBytes -= cleanup.FreedBytes;
                        if (cleanup.Warning.Length > 0)
                        {
                            preset.Message += " " + cleanup.Warning;
                            try { store.Upsert(preset); }
                            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or JsonException)
                            { Report(QueueStages.Rendering, "新预览已保存，缓存清理诊断未能入库：" + error.Message, preset); }
                        }
                    }
                    summary.Generated++;
                }
                else summary.Failed++;
                Report(QueueStages.Rendering, preset.Message, preset);
                current = null;
            }
            Report(QueueStages.Completed, summary.Summary);
            return summary;
        }
        catch (OperationCanceledException)
        {
            if (current is not null)
            {
                current.LastRenderOutcome = "已停止";
                current.LastRenderMessage = "已停止；已存在的有效预览保留，可继续生成。";
                store.Upsert(current);
            }
            Report(QueueStages.Stopped, $"已停止，可继续；剩余 {summary.Remaining} 项。" + summary.Summary, current);
            throw;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or OverflowException)
        {
            Report(QueueStages.Failed, error.Message + " " + summary.Summary, current);
            throw;
        }

        void Report(string stage, string message, PresetRecord? preset = null, bool indeterminate = false) =>
            progress.Report(new(summary.Processed, prepared ? summary.Eligible : summary.Requested, preset?.Name ?? "", message, preset)
            { Stage = stage, IsIndeterminate = indeterminate });
    }

    private static bool HasPreviewMetadata(PresetRecord preset) => !string.IsNullOrWhiteSpace(preset.PreviewPath) &&
        !string.IsNullOrWhiteSpace(preset.CacheKey) && !string.IsNullOrWhiteSpace(preset.TemplateJson);

    private sealed record OwnedCache(string Path, string Key, long Bytes);

    private sealed class PreviewReferences
    {
        private readonly Dictionary<string, string> recordPaths = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> counts = new(ApplicationPaths.PathComparer);
        private readonly HashSet<string> sourcePaths = new(ApplicationPaths.PathComparer);
        private readonly string? root;

        public PreviewReferences(IEnumerable<PresetRecord> stored, IEnumerable<PresetRecord> requested, string directory, CancellationToken token)
        {
            root = Normalize(directory);
            var records = new Dictionary<string, PresetRecord>(StringComparer.Ordinal);
            foreach (var record in stored.Concat(requested))
            {
                token.ThrowIfCancellationRequested();
                records[record.Id] = record;
            }
            foreach (var record in records.Values)
            {
                token.ThrowIfCancellationRequested();
                if (Normalize(record.SourcePath) is string source) sourcePaths.Add(source);
                Move(record.Id, record.PreviewPath);
            }
        }

        public void Move(string id, string path)
        {
            if (recordPaths.Remove(id, out var previous))
            {
                if (counts[previous] == 1) counts.Remove(previous);
                else counts[previous]--;
            }
            if (Normalize(path) is not string next) return;
            recordPaths[id] = next;
            counts[next] = counts.GetValueOrDefault(next) + 1;
        }

        public OwnedCache? Reclaimable(string path, string key, string nextPath, int expectedReferences)
        {
            if (root is null || key.Length != 64 || !key.All(char.IsAsciiHexDigit) || Normalize(path) is not string normalized ||
                string.Equals(normalized, Normalize(nextPath), ApplicationPaths.PathComparison) || sourcePaths.Contains(normalized) ||
                counts.GetValueOrDefault(normalized) != expectedReferences ||
                !string.Equals(Path.GetDirectoryName(normalized), root, ApplicationPaths.PathComparison) ||
                !string.Equals(Path.GetFileName(normalized), key + ".wav", ApplicationPaths.PathComparison)) return null;
            try
            {
                var rootAttributes = File.GetAttributes(root);
                var attributes = File.GetAttributes(normalized);
                if ((rootAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory ||
                    (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return null;
                return new OwnedCache(normalized, key, new FileInfo(normalized).Length);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { return null; }
        }

        public (long FreedBytes, string Warning) DeleteCommitted(OwnedCache old, string nextPath)
        {
            if (Reclaimable(old.Path, old.Key, nextPath, 0) is null)
                return (0, File.Exists(old.Path) ? "已保留未能安全回收的旧缓存。" : "");
            try
            {
                File.Delete(old.Path);
                return (old.Bytes, "");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { return (0, "新预览已保存，旧缓存清理失败：" + error.Message); }
        }

        private static string? Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { return null; }
        }
    }

    private static bool HasValidPreview(PresetRecord preset, CancellationToken token)
    {
        if (!HasPreviewMetadata(preset) || !File.Exists(preset.PreviewPath)) return false;
        try
        {
            token.ThrowIfCancellationRequested();
            var template = JsonSerializer.Deserialize<PreviewTemplate>(preset.TemplateJson, LibraryJson.Options)
                ?? throw new InvalidDataException("预览缺少已保存的试听模板。");
            template.Validate();
            var metrics = WaveFile.Validate(preset.PreviewPath, token);
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
