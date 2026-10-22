using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Scanning;

public static class VitalBankPresetSource
{
    public const int MaximumPresetBytes = 16 * 1024 * 1024;
    public const long MaximumBankBytes = 256L * 1024 * 1024;
    public const string ExportInstructions = "请先在 Vital 中导入此音色包，导出需要的独立 .vital，再添加导出目录并扫描。";

    // The native loader restores sample PCM and wavetable sources from JSON. Do
    // not resolve names through a user's global Vital resource directories.
    public static void ValidateEmbeddedResources(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("settings", out var settings) ||
            settings.ValueKind != JsonValueKind.Object || !root.TryGetProperty("synth_version", out var version) ||
            version.ValueKind != JsonValueKind.String || !Version.TryParse(version.GetString(), out var parsed) || parsed.Major < 1)
            throw Unavailable("包内预制缺少已验证的 Vital 版本或完整设置。");
        if (!settings.TryGetProperty("sample", out var sample) || sample.ValueKind != JsonValueKind.Object ||
            !sample.TryGetProperty("length", out var lengthValue) || lengthValue.ValueKind != JsonValueKind.Number || !lengthValue.TryGetInt32(out int length) || length is < 1 or > 1764000 ||
            !sample.TryGetProperty("sample_rate", out var rateValue) || rateValue.ValueKind != JsonValueKind.Number || !rateValue.TryGetInt32(out int rate) || rate is < 8000 or > 384000)
            throw Unavailable("包内预制缺少完整的内嵌采样状态。");
        ValidateBase64(sample, "samples", length * 2);
        if (sample.TryGetProperty("samples_stereo", out _)) ValidateBase64(sample, "samples_stereo", length * 2);
        if (!settings.TryGetProperty("wavetables", out var tables) || tables.ValueKind != JsonValueKind.Array || tables.GetArrayLength() != 3)
            throw Unavailable("包内预制缺少完整的三组内嵌波表。");
        foreach (var table in tables.EnumerateArray())
        {
            if (table.ValueKind != JsonValueKind.Object || !table.TryGetProperty("groups", out var groups) ||
                groups.ValueKind != JsonValueKind.Array || groups.GetArrayLength() is < 1 or > 32)
                throw Unavailable("包内波表分组不完整或超过读取限制。");
            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("components", out var components) ||
                    components.ValueKind != JsonValueKind.Array || components.GetArrayLength() is < 1 or > 32)
                    throw Unavailable("包内波表缺少完整的资源组件。");
                foreach (var component in components.EnumerateArray())
                {
                    if (component.ValueKind != JsonValueKind.Object || !component.TryGetProperty("type", out var typeValue) ||
                        typeValue.ValueKind != JsonValueKind.String)
                        throw Unavailable("包内波表资源类型无效。");
                    string type = typeValue.GetString()!;
                    if (type is not ("Wave Source" or "Audio File Source" or "Line Source" or "Frequency Filter" or "Wave Folder"))
                        throw Unavailable($"包内波表使用尚未验证的资源类型“{type}”。");
                    if (!component.TryGetProperty("keyframes", out var frames) || frames.ValueKind != JsonValueKind.Array ||
                        frames.GetArrayLength() is < 1 or > 257)
                        throw Unavailable("包内波表关键帧不完整或超过读取限制。");
                    int audioSamples = 0;
                    if (type == "Audio File Source")
                    {
                        audioSamples = ValidateBase64(component, "audio_file") / 2;
                        if (audioSamples < 2 || !FiniteNumber(component, "window_size", 1, 1764000) ||
                            !component.TryGetProperty("normalize_gain", out var normalize) || normalize.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            throw Unavailable("包内音频波表缺少完整数据或窗口设置。");
                    }
                    foreach (var frame in frames.EnumerateArray())
                    {
                        if (frame.ValueKind != JsonValueKind.Object || !frame.TryGetProperty("position", out var position) ||
                            position.ValueKind != JsonValueKind.Number || !position.TryGetInt32(out int index) || index is < 0 or > 256)
                            throw Unavailable("包内波表关键帧位置无效。");
                        if (type == "Wave Source") ValidateBase64(frame, "wave_data", 2048 * sizeof(float), floatSamples: true);
                        if (type == "Audio File Source" && (!FiniteNumber(frame, "start_position", 0, audioSamples - 1) ||
                            !FiniteNumber(frame, "window_size", 1, 1764000) || !FiniteNumber(frame, "window_fade", 0, 1)))
                            throw Unavailable("包内音频波表关键帧不完整。");
                        if (type == "Line Source")
                        {
                            if (!frame.TryGetProperty("line", out var line) || line.ValueKind != JsonValueKind.Object ||
                                !line.TryGetProperty("num_points", out var pointCount) || pointCount.ValueKind != JsonValueKind.Number ||
                                !pointCount.TryGetInt32(out int count) || count is < 2 or > 4096 ||
                                !line.TryGetProperty("points", out var points) || !FiniteNumbers(points) || points.GetArrayLength() != count * 2 ||
                                !line.TryGetProperty("powers", out var powers) || !FiniteNumbers(powers) || powers.GetArrayLength() != count)
                                throw Unavailable("包内程序化波表曲线不完整。");
                        }
                    }
                }
            }
        }
    }

    private static bool FiniteNumber(JsonElement owner, string name, double minimum, double maximum) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) &&
        double.IsFinite(number) && number >= minimum && number <= maximum;

    private static bool FiniteNumbers(JsonElement array) => array.ValueKind == JsonValueKind.Array && array.EnumerateArray().All(value =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number));

    private static int ValidateBase64(JsonElement owner, string field, int? expectedBytes = null, bool floatSamples = false)
    {
        if (!owner.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
            throw Unavailable("包内采样或波表只包含名称，缺少内嵌声音数据。");
        byte[] decoded;
        try { decoded = value.GetBytesFromBase64(); }
        catch (FormatException) { throw Unavailable("包内采样或波表数据无效，不能用外部资源或初始音色替代。"); }
        if (decoded.Length == 0 || decoded.Length > MaximumPresetBytes || decoded.Length % 2 != 0 ||
            expectedBytes is { } expected && decoded.Length != expected)
            throw Unavailable("包内采样或波表数据长度不完整。");
        if (floatSamples)
            for (int i = 0; i < decoded.Length; i += sizeof(float))
                if (!float.IsFinite(BitConverter.ToSingle(decoded, i))) throw Unavailable("包内波表包含无效采样值。");
        return decoded.Length;
    }

    private static InvalidDataException Unavailable(string reason) => new("无法预览：" + reason + ExportInstructions);

    public static byte[] ReadVerifiedEntry(PresetRecord preset, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (preset.Synth != SynthKind.Vital || preset.BankSlot is not null ||
            !Path.GetExtension(preset.SourcePath).Equals(".vitalbank", StringComparison.OrdinalIgnoreCase) ||
            !IsSafeEntryPath(preset.InternalPath) || string.IsNullOrWhiteSpace(preset.ContentHash))
            throw Unavailable("音色包条目的来源身份不完整，请重新扫描。");
        var file = new FileInfo(preset.SourcePath);
        long size = file.Length;
        DateTime modified = file.LastWriteTimeUtc;
        if (size > MaximumBankBytes) throw Unavailable("音色包超过 256 MiB 读取限制。");
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        int count;
        while ((count = stream.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        string bankHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var presets = archive.Entries.Where(entry => Path.GetExtension(entry.FullName).Equals(".vital", StringComparison.OrdinalIgnoreCase)).ToList();
        if (presets.Count > 4096 || presets.Any(entry => entry.Length > MaximumPresetBytes) || presets.Sum(entry => entry.Length) > MaximumBankBytes)
            throw Unavailable("音色包条目超过安全读取大小限制。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in presets)
            if (!IsSafeEntryPath(entry.FullName.Replace('\\', '/')) || !names.Add(entry.FullName.Replace('\\', '/')))
                throw Unavailable("音色包内部路径无效或重复。");
        var selected = presets.SingleOrDefault(entry => entry.FullName.Replace('\\', '/') == preset.InternalPath)
            ?? throw Unavailable("音色包中已找不到所选条目，请重新扫描。");
        using var input = selected.Open();
        using var content = new MemoryStream();
        while ((count = input.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            if (content.Length + count > MaximumPresetBytes) throw Unavailable("包内预制展开后超过 16 MiB 读取限制。");
            content.Write(buffer, 0, count);
        }
        byte[] bytes = content.ToArray();
        string entryHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string composite = Fingerprints.HashText($"{bankHash}|{preset.InternalPath}|{entryHash}");
        file.Refresh();
        if (file.Length != size || file.LastWriteTimeUtc != modified || !string.Equals(composite, preset.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("音色包或包内预制在扫描后发生变化，请重新扫描后再生成预览。");
        token.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(bytes);
        ValidateEmbeddedResources(document.RootElement);
        return bytes;
    }

    private static bool IsSafeEntryPath(string path) => !string.IsNullOrEmpty(path) && !path.Contains('\\') && !path.StartsWith('/') &&
        path.Split('/').All(part => part.Length > 0 && part is not ("." or "..") && !part.Contains(':')) &&
        Path.GetExtension(path).Equals(".vital", StringComparison.OrdinalIgnoreCase);
}
