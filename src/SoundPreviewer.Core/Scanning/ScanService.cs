using System.IO.Compression;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Scanning;

public sealed class ScanService
{
    private const long MaximumVitalBytes = 16 * 1024 * 1024;
    private const int MaximumBankEntries = 4096;
    private const long MaximumBankExpandedBytes = 256 * 1024 * 1024;

    public Task<ScanResult> ScanAsync(IReadOnlyList<SourceFolder> sources, IReadOnlyList<PresetRecord> existing, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(existing);
        cancellationToken.ThrowIfCancellationRequested();
        var sourceSnapshot = sources.Select(source => new SourceFolder { Id = source.Id, Path = source.Path, Synth = source.Synth, Enabled = source.Enabled }).ToList();
        var recordSnapshot = existing.ToList();
        return Task.Run(() => Scan(sourceSnapshot, recordSnapshot, cancellationToken), cancellationToken);
    }

    private static ScanResult Scan(IReadOnlyList<SourceFolder> sources, IReadOnlyList<PresetRecord> existing, CancellationToken token)
    {
        var warnings = new List<string>();
        var records = existing.GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => Clone(g.First()), StringComparer.Ordinal);
        var scannedIds = new HashSet<string>(StringComparer.Ordinal);
        var completedSources = new HashSet<string>(StringComparer.Ordinal);
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        var enabledSources = sources.Where(s => s.Enabled).ToList();
        int added = 0;
        int changed = 0;
        foreach (var source in enabledSources)
        {
            token.ThrowIfCancellationRequested();
            try { roots[source.Id] = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Path)); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                warnings.Add($"音色来源路径无效：{source.Path}；{ex.Message}");
            }
        }

        foreach (var source in enabledSources)
        {
            token.ThrowIfCancellationRequested();
            if (!roots.TryGetValue(source.Id, out string? root)) continue;
            if (!Directory.Exists(root))
            {
                warnings.Add($"音色来源不可访问，保留已有记录：{root}");
                continue;
            }
            bool complete = true;
            var directories = new Stack<string>();
            directories.Push(root);
            while (directories.TryPop(out string? directory))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        complete = false;
                        warnings.Add($"已跳过目录链接，保留已有记录：{directory}");
                        continue;
                    }
                    foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        FileAttributes attributes;
                        try { attributes = File.GetAttributes(path); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            warnings.Add($"无法读取文件属性：{path}；{ex.Message}");
                            complete = false;
                            continue;
                        }
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            complete = false;
                            warnings.Add($"已跳过文件或目录链接，保留已有记录：{path}");
                            continue;
                        }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            directories.Push(path);
                            continue;
                        }
                        if (!Supports(source.Synth, Path.GetExtension(path))) continue;
                        try
                        {
                            foreach (var candidate in ReadPresets(source, root, path, token, warnings))
                            {
                                if (!scannedIds.Add(candidate.Id)) continue;
                                if (records.TryGetValue(candidate.Id, out var previous))
                                {
                                    if (enabledSources.Any(s => s.Id == previous.SourceId && s.Synth == candidate.Synth && roots.TryGetValue(s.Id, out string? ownerRoot) && IsInside(candidate.SourcePath, ownerRoot)))
                                        candidate.SourceId = previous.SourceId;
                                    if (string.Equals(previous.ContentHash, candidate.ContentHash, StringComparison.Ordinal))
                                    {
                                        RefreshLocation(previous, candidate);
                                        if (candidate.Status == PresetStatus.Unsupported)
                                        {
                                            previous.Status = PresetStatus.Unsupported;
                                            previous.Message = candidate.Message;
                                            ClearPreview(previous);
                                        }
                                        else if (previous.Status == PresetStatus.Unsupported)
                                        {
                                            previous.Status = PresetStatus.Pending;
                                            previous.Message = candidate.Message;
                                            ClearPreview(previous);
                                        }
                                        else if (previous.Status == PresetStatus.Missing)
                                        {
                                            previous.Status = candidate.Status;
                                            previous.Message = candidate.Message;
                                            if (candidate.Status == PresetStatus.Pending && File.Exists(previous.PreviewPath))
                                            {
                                                previous.Status = PresetStatus.NeedsReview;
                                                previous.Message = "源音色已重新出现，保留的预览须重新试听确认。";
                                            }
                                            else ClearPreview(previous);
                                        }
                                    }
                                    else
                                    {
                                        candidate.Favorite = previous.Favorite;
                                        candidate.Feedback = previous.Feedback;
                                        candidate.FeedbackUtc = previous.FeedbackUtc;
                                        records[candidate.Id] = candidate;
                                        changed++;
                                    }
                                }
                                else
                                {
                                    records.Add(candidate.Id, candidate);
                                    added++;
                                }
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or NotSupportedException)
                        {
                            warnings.Add($"无法完整读取音色，保留已有记录：{path}；{ex.Message}");
                            complete = false;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"无法完整扫描目录，保留已有记录：{directory}；{ex.Message}");
                    complete = false;
                }
            }
            if (complete) completedSources.Add(source.Id);
        }

        token.ThrowIfCancellationRequested();
        foreach (var record in records.Values)
        {
            if (completedSources.Contains(record.SourceId) && !scannedIds.Contains(record.Id))
            {
                record.Status = PresetStatus.Missing;
                record.Message = "源音色已移除或不再属于此来源。";
            }
        }
        return new ScanResult
        {
            Presets = records.Values.OrderBy(p => p.Synth).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal).ToList(),
            Warnings = warnings,
            Added = added,
            Changed = changed
        };
    }

    private static bool IsInside(string path, string root)
    {
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return string.Equals(path, root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Supports(SynthKind synth, string extension)
    {
        if (synth == SynthKind.Vital) return extension.Equals(".vital", StringComparison.OrdinalIgnoreCase) || extension.Equals(".vitalbank", StringComparison.OrdinalIgnoreCase);
        if (extension.Equals(".fxp", StringComparison.OrdinalIgnoreCase)) return true;
        return synth == SynthKind.Serum2 && extension.Equals(".SerumPreset", StringComparison.OrdinalIgnoreCase) ||
            synth == SynthKind.Sylenth1 && extension.Equals(".fxb", StringComparison.OrdinalIgnoreCase);
    }

    private static List<PresetRecord> ReadPresets(SourceFolder source, string root, string path, CancellationToken token, List<string> warnings)
    {
        var file = new FileInfo(path);
        long fileSize = file.Length;
        DateTime lastWriteUtc = file.LastWriteTimeUtc;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        string fileHash = Hash(stream, token);
        stream.Position = 0;
        string extension = file.Extension;
        string package = Path.GetDirectoryName(Path.GetRelativePath(root, path)) ?? "";
        if (package.Length == 0 || package == ".") package = Path.GetFileName(root);

        if (extension.Equals(".vitalbank", StringComparison.OrdinalIgnoreCase))
        {
            var bankRecords = new List<PresetRecord>();
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entries = archive.Entries.Where(e => Path.GetExtension(e.FullName).Equals(".vital", StringComparison.OrdinalIgnoreCase)).ToList();
            if (entries.Count > MaximumBankEntries || entries.Any(e => e.Length > MaximumVitalBytes) || entries.Sum(e => e.Length) > MaximumBankExpandedBytes)
                throw new InvalidDataException("Vital 音色包超过安全读取大小限制。");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                string internalPath = entry.FullName.Replace('\\', '/');
                if (internalPath.StartsWith('/') || internalPath.Split('/').Any(part => part is ".." or "." || part.Contains(':')))
                    throw new InvalidDataException("Vital 音色包含不安全的内部路径。");
                if (!names.Add(internalPath)) throw new InvalidDataException("Vital 音色包含重复的内部路径。");
                if (entry.Length > MaximumVitalBytes) throw new InvalidDataException("Vital 音色包中的预设超过安全读取大小限制。");
                using var entryStream = entry.Open();
                using var content = new MemoryStream();
                CopyBounded(entryStream, content, MaximumVitalBytes, token);
                content.Position = 0;
                string entryHash = Hash(content, token);
                content.Position = 0;
                var record = CreateRecord(source, path, Path.GetFileNameWithoutExtension(internalPath), Path.GetFileNameWithoutExtension(path), Fingerprints.HashText($"{fileHash}|{internalPath}|{entryHash}"), entry.Length, lastWriteUtc, internalPath);
                record.Status = PresetStatus.Unsupported;
                record.Message = "已识别 Vital 音色包条目；尚未验证原生音色包导入及依赖资源，不生成预览。";
                try { ValidateVital(content, record); }
                catch (JsonException ex)
                {
                    record.Message = $"Vital 音色包条目无效：{ex.Message}";
                    warnings.Add($"{path} / {internalPath}：{record.Message}");
                }
                bankRecords.Add(record);
            }
            if (entries.Count == 0)
            {
                var empty = CreateRecord(source, path, Path.GetFileNameWithoutExtension(path), package, fileHash, fileSize, lastWriteUtc);
                empty.Status = PresetStatus.Unsupported;
                empty.Message = "此 Vital 音色包没有可识别的 .vital 条目。";
                bankRecords.Add(empty);
            }
            VerifyStable(file, fileSize, lastWriteUtc);
            return bankRecords;
        }

        var preset = CreateRecord(source, path, Path.GetFileNameWithoutExtension(path), package, fileHash, fileSize, lastWriteUtc);
        if (extension.Equals(".vital", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (fileSize > MaximumVitalBytes) throw new JsonException("Vital 预设超过安全读取大小限制。");
                ValidateVital(stream, preset);
            }
            catch (JsonException ex)
            {
                preset.Status = PresetStatus.Unsupported;
                preset.Message = $"Vital 预设 JSON 无效：{ex.Message}";
                warnings.Add($"{path}：{preset.Message}");
            }
        }
        else if (extension.Equals(".fxb", StringComparison.OrdinalIgnoreCase))
        {
            preset.Status = PresetStatus.Unsupported;
            preset.Message = "Sylenth1 FXB 为不透明音色库；需要原生适配器确认音色槽位，暂不拆分或生成预览。";
        }
        else if (extension.Equals(".fxp", StringComparison.OrdinalIgnoreCase))
        {
            Span<byte> header = stackalloc byte[12];
            int bytes = stream.Read(header);
            uint declaredSize = bytes == header.Length ? BinaryPrimitives.ReadUInt32BigEndian(header[4..8]) : 0;
            if (bytes != header.Length || fileSize < 56 || !header[..4].SequenceEqual("CcnK"u8) ||
                (declaredSize != fileSize - 8 && declaredSize != fileSize) ||
                (!header[8..12].SequenceEqual("FxCk"u8) && !header[8..12].SequenceEqual("FPCh"u8)) ||
                (header[8..12].SequenceEqual("FPCh"u8) && fileSize < 60))
            {
                preset.Status = PresetStatus.Unsupported;
                preset.Message = "FXP 文件头无效或此文件实际为音色库。";
                warnings.Add($"{path}：{preset.Message}");
            }
            else if (declaredSize == fileSize)
                preset.Message = "FXP 使用完整文件长度字段，已按兼容格式识别；实际预制加载仍须由音源确认。";
        }
        VerifyStable(file, fileSize, lastWriteUtc);
        return [preset];
    }

    private static void VerifyStable(FileInfo file, long length, DateTime lastWriteUtc)
    {
        file.Refresh();
        if (!file.Exists || file.Length != length || file.LastWriteTimeUtc != lastWriteUtc)
            throw new IOException("扫描过程中源文件发生变化，请重新扫描。");
    }

    private static void ValidateVital(Stream stream, PresetRecord preset)
    {
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
            throw new JsonException("缺少有效的 settings 对象。");
        if (root.TryGetProperty("preset_name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
            preset.Name = name.GetString()!;
    }

    private static PresetRecord CreateRecord(SourceFolder source, string path, string name, string package, string hash, long size, DateTime modified, string internalPath = "") => new()
    {
        Id = Fingerprints.PresetId(source.Synth, path, internalPath),
        SourceId = source.Id,
        Synth = source.Synth,
        SourcePath = Path.GetFullPath(path),
        InternalPath = internalPath,
        Name = name,
        Package = package,
        ContentHash = hash,
        FileSize = size,
        LastWriteUtc = modified,
        Status = PresetStatus.Pending
    };

    private static void RefreshLocation(PresetRecord previous, PresetRecord candidate)
    {
        previous.SourceId = candidate.SourceId;
        previous.SourcePath = candidate.SourcePath;
        previous.Name = candidate.Name;
        previous.Package = candidate.Package;
        previous.FileSize = candidate.FileSize;
        previous.LastWriteUtc = candidate.LastWriteUtc;
    }

    private static void ClearPreview(PresetRecord preset)
    {
        preset.PreviewPath = "";
        preset.CacheKey = "";
        preset.RenderedUtc = null;
        preset.Metrics = null;
        preset.Notes = [];
        preset.AdapterVersion = "";
        preset.PluginVersion = "";
        preset.TemplateJson = "";
    }

    private static PresetRecord Clone(PresetRecord record) => JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(record, LibraryJson.Options), LibraryJson.Options)!;

    private static string Hash(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void CopyBounded(Stream source, Stream destination, long maximum, CancellationToken token)
    {
        byte[] buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            total += count;
            if (total > maximum) throw new InvalidDataException("音色条目展开后超过安全读取大小限制。");
            destination.Write(buffer, 0, count);
        }
    }
}
