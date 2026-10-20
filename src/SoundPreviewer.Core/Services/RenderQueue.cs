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
    public int ActiveCount { get; init; }
    public int MaxConcurrency { get; init; } = 1;
    public double ItemElapsedSeconds { get; init; }
    public long WorkingSetBytes { get; init; }
    public long MemoryBudgetBytes { get; init; }
    public string ResourceMessage { get; init; } = "";
    public int AllowedConcurrency { get; init; } = 1;
    public int EffectiveConcurrency { get; init; }
    public int PreparingCount { get; init; }
    public string ConcurrencyMessage { get; init; } = "";
    public int Generated { get; init; }
    public int Reused { get; init; }
    public int Failed { get; init; }
    public int Unconfigured { get; init; }
    public int RetryLimited { get; init; }
    public int Succeeded => Generated + Reused;
}

public sealed class RenderResourceLimitException(string message, QueueResult summary) : InvalidOperationException(message)
{
    public QueueResult Summary { get; } = summary;
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
    public double ElapsedSeconds { get; internal set; }
    public int PeakActiveCount { get; internal set; }
    public int RequestedConcurrency { get; internal set; }
    public int ResolvedConcurrency { get; internal set; } = 1;
    public long MemoryBudgetBytes { get; internal set; }
    public long PeakWorkingSetBytes { get; internal set; }
    public bool ResourceStopped { get; internal set; }
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

public sealed partial class RenderQueue(LibraryStore store, IRenderResourceProbe? resourceProbe = null)
{
    public Task<QueueResult> RunAsync(AppSettings settings, IReadOnlyList<PresetRecord> presets, string workerPath,
        IProgress<QueueProgress> progress, CancellationToken cancellationToken, bool forceRegenerate = false) =>
        new Execution(store, settings, presets, workerPath, progress, cancellationToken, forceRegenerate,
            resourceProbe ?? new SystemRenderResourceProbe()).RunAsync();

    public Task<InitBaselineState> GenerateInitializationBaselineAsync(AppSettings settings, PluginConfiguration plugin,
        string workerPath, IProgress<QueueProgress> progress, CancellationToken cancellationToken) =>
        new Execution(store, settings, [], workerPath, progress, cancellationToken, false,
            resourceProbe ?? new SystemRenderResourceProbe()).RunBaselineAsync(plugin);

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
