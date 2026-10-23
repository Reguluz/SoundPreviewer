using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SoundPreviewer.Core.Audio;

/// <summary>Exact program-state comparison with only observed Sylenth metadata rewrites allowed.</summary>
public static class SylenthPresetState
{
    // Version-8 legacy schema observed in the New Age Hippies single presets.
    // Hash only the format header and parameter IDs, never names or values.
    private const string LegacySchemaHash = "8bddfade9536d1549972a61bb4a13dea269ecd688b474455562feb97562ecf54";

    public static string? LegacyProgramName(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 816 or > 8192 ||
            !Convert.ToHexString(SHA256.HashData(bytes[..812])).Equals(LegacySchemaHash, StringComparison.OrdinalIgnoreCase) ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes[812..816]) != 1) return null;
        int cursor = 816;
        string name = "";
        for (int index = 0; index < 9; index++)
        {
            if (cursor >= bytes.Length) return null;
            int length = bytes[cursor++];
            if (length > bytes.Length - cursor) return null;
            if (index == 0)
            {
                if (length is < 1 or > 35 || bytes.Slice(cursor, length).Contains((byte)0)) return null;
                name = Encoding.Latin1.GetString(bytes.Slice(cursor, length));
            }
            cursor += length;
        }
        int floats = 0, integers = 0;
        for (int index = 0; index < 192; index++)
        {
            if (bytes.Length - cursor < 8) return null;
            int kind = BinaryPrimitives.ReadInt32LittleEndian(bytes[cursor..]);
            int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[(cursor + 4)..]);
            cursor += 8;
            if (count != 1 || kind is not (0 or 6)) return null;
            int size = kind == 6 ? 4 : 2;
            if (bytes.Length - cursor < size) return null;
            if (kind == 6)
            {
                if (!float.IsFinite(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[cursor..])))) return null;
                floats++;
            }
            else integers++;
            cursor += size;
        }
        return cursor == bytes.Length && floats == 122 && integers == 70 ? name : null;
    }

    public static bool IsLegacyReadback(ReadOnlySpan<byte> source, ReadOnlySpan<byte> actual) =>
        LegacyProgramName(source) is { } name && IsKnownLayout(actual) && Version(actual) == 2200 &&
        BinaryPrimitives.ReadInt32LittleEndian(actual[16..20]) == 0 &&
        Encoding.Latin1.GetString(actual[^36..]).TrimEnd('\0') == name;

    public static bool Matches(ReadOnlySpan<byte> target, ReadOnlySpan<byte> actual)
    {
        if (target.SequenceEqual(actual)) return true;
        // The installed 2.2.1.2 x86 plugin reports 2200 after loading these older
        // single-program states. Real 2202/2211 samples preserve every other byte.
        // Keep the complete program slot, name and parameter payload exact.
        return IsKnownLayout(target) && IsKnownLayout(actual) &&
            Version(actual) == 2200 && Version(target) is 2202 or 2211 &&
            target[..4].SequenceEqual(actual[..4]) && target[8..].SequenceEqual(actual[8..]);
    }

    public static string DescribeMismatch(ReadOnlySpan<byte> target, ReadOnlySpan<byte> actual)
    {
        string detail = $"源 FXP 状态 {Describe(target)}，插件回读状态 {Describe(actual)}。";
        if (!IsKnownLayout(target) || !IsKnownLayout(actual))
            return detail + "源预制与插件回读使用不同或尚未适配的状态格式，当前宿主无法核验转换结果；这不表示文件损坏或 DAW 无法加载。请在当前 Sylenth1 中载入并另存为独立 FXP 后重新扫描。";
        int difference = 0;
        while (difference < target.Length && target[difference] == actual[difference]) difference++;
        return detail + $"目标状态与回读内容不一致（首个差异偏移 {difference}）；请核对实际使用的 Sylenth1 版本，在该插件中重新导出独立 FXP 后扫描。";
    }

    private static bool IsKnownLayout(ReadOnlySpan<byte> bytes) => bytes.Length == 1028 &&
        bytes[..4].SequenceEqual("1lys"u8) && BinaryPrimitives.ReadInt32LittleEndian(bytes[8..12]) == 1 &&
        BinaryPrimitives.ReadInt32LittleEndian(bytes[12..16]) == 243;

    private static int Version(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadInt32LittleEndian(bytes[4..8]);
    private static string Describe(ReadOnlySpan<byte> bytes) => IsKnownLayout(bytes)
        ? $"1lys / {bytes.Length} 字节 / 格式标识 {Version(bytes)}" : $"{bytes.Length} 字节（不同格式）";
}
