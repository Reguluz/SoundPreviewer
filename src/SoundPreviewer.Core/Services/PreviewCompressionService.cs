using System.Text.Json;
using System.Text.Json.Nodes;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Core.Services;

public sealed record PreviewCompressionResult(int Compressed, int Skipped, int Failed, long FreedBytes,
    IReadOnlyList<string> Warnings);

public sealed record PreviewCompressionProgress(int Processed, int Total, int Compressed, long FreedBytes);

/// <summary>Explicitly converts owned WAV previews without changing their sound template or source records.</summary>
public static class PreviewCompressionService
{
    public static async Task<PreviewCompressionResult> CompressAsync(LibraryStore store, AppSettings settings,
        IProgress<PreviewCompressionProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        token.ThrowIfCancellationRequested();
        string root = Path.GetDirectoryName(store.DatabasePath)!;
        EnsureNoLinks(root);
        var records = store.LoadPresets();
        var groups = records.Where(record => Path.GetExtension(record.PreviewPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            .GroupBy(record => Normalize(record.PreviewPath, root) ?? record.PreviewPath, ApplicationPaths.PathComparer).ToList();
        var warnings = new List<string>();
        int compressed = 0, skipped = 0, failed = 0, processed = 0;
        long freed = 0;
        var protectedPaths = ProtectedPaths(settings, records, root);
        bool ownedRoot = ApplicationPaths.SamePath(root, settings.PreviewDirectory) && !HasOtherDatabase(root, store.DatabasePath);
        var cleanup = new List<CompletedCompression>();
        progress?.Report(new(0, groups.Count, 0, 0));
        try
        {
            foreach (var group in groups)
            {
                token.ThrowIfCancellationRequested();
                string source = group.Key;
                string? stage = null, published = null, publishedHash = null;
                bool committed = false;
                try
                {
                    var references = group.ToList();
                    if (!ownedRoot || !CanConvert(source, references, root, protectedPaths))
                    {
                        skipped++;
                        warnings.Add("已保留非本库自有、受保护或无法确认归属的 WAV：" + source);
                        continue;
                    }
                    string destination = Path.ChangeExtension(source, ".m4a");
                    EnsureNoLinks(destination);
                    if (File.Exists(destination) || Directory.Exists(destination))
                    {
                        skipped++;
                        warnings.Add("压缩目标已存在，未覆盖原 WAV 或已有音频：" + destination);
                        continue;
                    }
                    string sourceHash = Fingerprints.HashFile(source);
                    long originalBytes = new FileInfo(source).Length;
                    var audio = WaveFile.Read(source, token);
                    var originalMetrics = WaveFile.Analyze(audio, token);
                    ValidateReferences(references, originalMetrics);
                    stage = Path.Combine(root, ".sp-compress-" + Guid.NewGuid().ToString("N") + ".m4a");
                    await PreviewAudioFile.EncodeAsync(audio, stage, token).ConfigureAwait(false);
                    var decoded = PreviewAudioFile.Read(stage, token);
                    if (decoded.SampleRate != audio.SampleRate || decoded.Channels != audio.Channels || decoded.Frames != audio.Frames)
                        throw new InvalidDataException("压缩音频的采样率、声道或长度发生变化，已保留原 WAV。");
                    var metrics = WaveFile.Analyze(decoded, token);
                    ValidateReferences(references, metrics);
                    long compressedBytes = new FileInfo(stage).Length;
                    if (compressedBytes >= originalBytes)
                    {
                        skipped++;
                        warnings.Add("压缩后未节省空间，已保留原 WAV：" + source);
                        continue;
                    }
                    token.ThrowIfCancellationRequested();
                    EnsureNoLinks(source);
                    if (Fingerprints.HashFile(source) != sourceHash)
                        throw new IOException("压缩期间原 WAV 发生变化，未更改原记录：" + source);
                    // AudioOnly loads and merges each latest record in one transaction,
                    // preserving metadata edits and independently changed preview mappings.
                    var updates = references.Select(record =>
                    {
                        var changed = Clone(record);
                        changed.PreviewPath = destination;
                        // AudioOnly uses NeedsReview as a downgrade signal; compression
                        // must preserve even a concurrently changed confirmation status.
                        changed.Status = PresetStatus.Ready;
                        return (Rendered: changed, Identity: record, AudioOnly: true);
                    }).ToList();
                    publishedHash = Fingerprints.HashFile(stage);
                    token.ThrowIfCancellationRequested();
                    File.Move(stage, destination); // Never overwrite an existing cache or unrelated file.
                    published = destination;
                    token.ThrowIfCancellationRequested();
                    var merged = store.MergeRenderStates(updates);
                    committed = true;
                    if (!merged.Any(record => ApplicationPaths.SamePath(record.PreviewPath, destination)))
                    {
                        // All references changed independently before the merge; our audio is unused.
                        committed = false;
                        skipped++;
                        warnings.Add("原预览引用已变化，保留当前音色库记录：" + source);
                        continue;
                    }
                    compressed++;
                    cleanup.Add(new(source, sourceHash, originalBytes - compressedBytes, references));
                    // Batch reference checks avoid reloading the library for every file.
                    // A final check also runs on Stop before returning control to the UI.
                    if (cleanup.Count >= 256)
                    {
                        freed += CleanupCommitted(store, settings, root, cleanup, warnings);
                        cleanup.Clear();
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (IsFileError(error))
                {
                    failed++;
                    warnings.Add("压缩失败，原预览及记录已保留：" + source + "；" + error.Message);
                }
                finally
                {
                    DeleteCreated(stage, null, warnings);
                    if (!committed) DeleteCreated(published, publishedHash, warnings);
                    processed++;
                    progress?.Report(new(processed, groups.Count, compressed, freed));
                }
            }
        }
        finally
        {
            freed += CleanupCommitted(store, settings, root, cleanup, warnings);
            progress?.Report(new(processed, groups.Count, compressed, freed));
        }
        return new(compressed, skipped, failed, freed, warnings);
    }

    private sealed record CompletedCompression(string Path, string Hash, long Saving, IReadOnlyList<PresetRecord> References);

    private static long CleanupCommitted(LibraryStore store, AppSettings settings, string root,
        IReadOnlyList<CompletedCompression> completed, List<string> warnings)
    {
        if (completed.Count == 0) return 0;
        long freed = 0;
        try
        {
            var latest = store.LoadPresets();
            var referenced = latest.Select(record => Normalize(record.PreviewPath, root)).Where(path => path is not null)
                .Cast<string>().ToHashSet(ApplicationPaths.PathComparer);
            var protectedPaths = ProtectedPaths(settings, latest, root);
            protectedPaths.UnionWith(ProtectedPaths(store.LoadSettings(), latest, root));
            bool shared = HasOtherDatabase(root, store.DatabasePath);
            foreach (var item in completed)
            {
                try
                {
                    if (shared || referenced.Contains(item.Path) || !CanConvert(item.Path, item.References, root, protectedPaths) ||
                        Fingerprints.HashFile(item.Path) != item.Hash)
                        warnings.Add("压缩预览已保存，原 WAV 仍被引用或无法安全清理，已保留：" + item.Path);
                    else
                    {
                        File.Delete(item.Path);
                        freed += item.Saving;
                    }
                }
                catch (Exception error) when (IsFileError(error))
                { warnings.Add("压缩预览已保存，原 WAV 未能清理：" + item.Path + "；" + error.Message); }
            }
        }
        catch (Exception error) when (IsFileError(error))
        { warnings.Add("压缩预览已保存，无法确认最新引用，已保留原 WAV：" + error.Message); }
        return freed;
    }

    private static bool CanConvert(string path, IReadOnlyList<PresetRecord> references, string root, HashSet<string> protectedPaths)
    {
        if (references.Count == 0 || !File.Exists(path) || protectedPaths.Contains(path) ||
            !ApplicationPaths.SamePath(Path.GetDirectoryName(path) ?? "", root)) return false;
        string key = Path.GetFileNameWithoutExtension(path);
        if (!Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
            key.Length != 64 || !key.All(char.IsAsciiHexDigit) ||
            references.Any(record => !string.Equals(record.CacheKey, key, ApplicationPaths.PathComparison))) return false;
        EnsureNoLinks(path);
        return true;
    }

    private static void ValidateReferences(IEnumerable<PresetRecord> references, AudioMetrics metrics)
    {
        if (metrics.HasNonFinite) throw new InvalidDataException("预览音频包含 NaN / Infinity。");
        foreach (var record in references)
        {
            var template = JsonSerializer.Deserialize<PreviewTemplate>(record.TemplateJson, LibraryJson.Options)
                ?? throw new InvalidDataException("预览缺少已保存的试听模板。");
            template.Validate();
            if (metrics.SampleRate != template.SampleRate) throw new InvalidDataException("预览采样率与已保存模板不符。");
            WorkerProcessClient.ValidateNotes(new RenderResult { Metrics = metrics, Notes = record.Notes, AdapterVersion = record.AdapterVersion }, template);
        }
    }

    private static HashSet<string> ProtectedPaths(AppSettings settings, IEnumerable<PresetRecord> records, string root)
    {
        var paths = new HashSet<string>(ApplicationPaths.PathComparer);
        foreach (var record in records)
            if (Normalize(record.SourcePath, root) is string source) paths.Add(source);
        var sourceRoots = settings.Sources.Select(source => Normalize(source.Path, root)).Where(path => path is not null).Cast<string>().ToList();
        // Only referenced root-level previews can be converted. Protect a reference even
        // when its source folder is currently disabled or the source itself is missing.
        foreach (var record in records)
            if (Normalize(record.PreviewPath, root) is string preview && sourceRoots.Any(source => ApplicationPaths.IsWithin(preview, source)))
                paths.Add(preview);
        foreach (var plugin in settings.Plugins)
        {
            if (Normalize(plugin.InitBaselinePath, root) is not string baseline || !File.Exists(baseline)) continue;
            EnsureNoLinks(baseline);
            if (new FileInfo(baseline).Length > 4 * 1024 * 1024)
                throw new InvalidDataException("无法安全确认过大的基准配置，未压缩任何新的预览：" + baseline);
            var metadata = JsonNode.Parse(File.ReadAllText(baseline));
            foreach (string audio in BaselinePaths(metadata))
                if (Normalize(audio, root) is string path) paths.Add(path);
        }
        return paths;
    }

    private static IEnumerable<string> BaselinePaths(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var (key, value) in obj)
            {
                if ((key.Equals("WavPath", StringComparison.OrdinalIgnoreCase) || key.Equals("OutputPath", StringComparison.OrdinalIgnoreCase)) &&
                    value is JsonValue text && text.TryGetValue<string>(out string? path) && !string.IsNullOrWhiteSpace(path)) yield return path;
                else foreach (string nested in BaselinePaths(value)) yield return nested;
            }
        else if (node is JsonArray array)
            foreach (var value in array) foreach (string path in BaselinePaths(value)) yield return path;
    }

    private static bool HasOtherDatabase(string root, string database) =>
        Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).Any(path =>
            Path.GetExtension(path).Equals(".db", StringComparison.OrdinalIgnoreCase) && !ApplicationPaths.SamePath(path, database));

    private static bool SamePreview(PresetRecord record, string path, string root) =>
        Normalize(record.PreviewPath, root) is string preview && string.Equals(preview, path, ApplicationPaths.PathComparison);

    private static string? Normalize(string? path, string root) => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path, root);

    private static PresetRecord Clone(PresetRecord record) =>
        JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(record, LibraryJson.Options), LibraryJson.Options)!;

    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or
        NotSupportedException or JsonException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception;

    private static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不能压缩链接或重解析点路径：" + current);
    }

    private static void DeleteCreated(string? path, string? expectedHash, List<string> warnings)
    {
        if (path is null || !File.Exists(path)) return;
        try
        {
            EnsureNoLinks(path);
            if (expectedHash is null || Fingerprints.HashFile(path) == expectedHash) File.Delete(path);
            else warnings.Add("临时压缩文件发生变化，已保留：" + path);
        }
        catch (Exception error) when (IsFileError(error))
        { warnings.Add("临时压缩文件未能清理：" + path + "；" + error.Message); }
    }
}
