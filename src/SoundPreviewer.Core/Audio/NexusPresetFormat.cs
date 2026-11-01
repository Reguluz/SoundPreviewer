namespace SoundPreviewer.Core.Audio;

public static class NexusPresetFormat
{
    public const int LegacyChunkBytes = 1356;

    public static bool IsLegacy(uint version, int chunkBytes) => chunkBytes == LegacyChunkBytes &&
        (version is 1 or 0x442 || version >> 24 == 1);

    public static bool IsSupported(uint version, int chunkBytes, ReadOnlySpan<byte> prefix) =>
        IsLegacy(version, chunkBytes) || version >> 24 == 2 && chunkBytes >= 32 &&
        prefix.Length >= 16 && prefix[..16].SequenceEqual("NEXUS2 Preset\0\0\0"u8);
}
