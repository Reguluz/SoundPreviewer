using System.Buffers.Binary;

namespace SoundPreviewer.Core.Audio;

/// <summary>Exact program-state comparison with only observed Sylenth metadata rewrites allowed.</summary>
public static class SylenthPresetState
{
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

    public static string DescribeMismatch(ReadOnlySpan<byte> target, ReadOnlySpan<byte> actual) =>
        $"源 FXP 状态 {Describe(target)}，插件回读状态 {Describe(actual)}。" +
        "当前插件未返回可核验的相同音色状态；请更新匹配的 Sylenth1 插件，或在匹配版本中重新导出独立 FXP。";

    private static bool IsKnownLayout(ReadOnlySpan<byte> bytes) => bytes.Length == 1028 &&
        bytes[..4].SequenceEqual("1lys"u8) && BinaryPrimitives.ReadInt32LittleEndian(bytes[8..12]) == 1 &&
        BinaryPrimitives.ReadInt32LittleEndian(bytes[12..16]) == 243;

    private static int Version(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadInt32LittleEndian(bytes[4..8]);
    private static string Describe(ReadOnlySpan<byte> bytes) => IsKnownLayout(bytes)
        ? $"1lys / {bytes.Length} 字节 / 格式标识 {Version(bytes)}" : $"{bytes.Length} 字节（不同格式）";
}
