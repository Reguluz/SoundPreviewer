using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SoundPreviewer.Core.Audio;

public static partial class PreviewAudioFile
{
    // A small, standard MP4 uuid atom binds exact PCM timing to this container.
    // Its digest covers the container and timing header, so renamed/moved files remain
    // portable without a sidecar and altered audio cannot reuse stale timing.
    private const int TimingAtomSize = 96;
    private static readonly byte[] TimingId = Convert.FromHexString("ec1d4eb93f16406191a70cf1826cb1df");
    private sealed record Timing(int SampleRate, int Channels, int Frames, int NativeDecodedFrames, int Platform = 0);

    private static void WriteTiming(string path, Timing value, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (stream.Length is < 8 or > MaximumFileBytes - TimingAtomSize) throw new InvalidDataException("AAC 文件大小无效或超过限制。");
        byte[] atom = new byte[TimingAtomSize];
        BinaryPrimitives.WriteUInt32BigEndian(atom, TimingAtomSize);
        "uuid"u8.CopyTo(atom.AsSpan(4));
        TimingId.CopyTo(atom, 8);
        BinaryPrimitives.WriteInt32BigEndian(atom.AsSpan(24), 1);
        BinaryPrimitives.WriteInt32BigEndian(atom.AsSpan(28), value.SampleRate);
        BinaryPrimitives.WriteInt32BigEndian(atom.AsSpan(32), value.Channels);
        BinaryPrimitives.WriteInt64BigEndian(atom.AsSpan(36), value.Frames);
        BinaryPrimitives.WriteInt64BigEndian(atom.AsSpan(44), value.NativeDecodedFrames);
        BinaryPrimitives.WriteInt32BigEndian(atom.AsSpan(52), CurrentCodecPlatform);
        BinaryPrimitives.WriteInt32BigEndian(atom.AsSpan(56), AacBitRate);
        BinaryPrimitives.WriteInt32BigEndian(atom.AsSpan(60), 1); // Native gapless start; no guessed leading trim.
        HashContent(stream, stream.Length, atom.AsSpan(0, 64), token).CopyTo(atom, 64);
        token.ThrowIfCancellationRequested();
        stream.Position = stream.Length;
        stream.Write(atom);
        stream.Flush(flushToDisk: true);
    }

    private static Timing ReadTiming(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < TimingAtomSize + 8 or > MaximumFileBytes)
            throw new InvalidDataException("AAC 文件大小无效或超过 256 MiB 限制。");
        Span<byte> header = stackalloc byte[8];
        stream.ReadExactly(header);
        if (!header[4..].SequenceEqual("ftyp"u8)) throw new InvalidDataException("文件不是有效的 M4A 容器。");
        stream.Position = stream.Length - TimingAtomSize;
        byte[] atom = new byte[TimingAtomSize];
        stream.ReadExactly(atom);
        if (BinaryPrimitives.ReadUInt32BigEndian(atom) != TimingAtomSize || !atom.AsSpan(4, 4).SequenceEqual("uuid"u8) ||
            !atom.AsSpan(8, 16).SequenceEqual(TimingId) || BinaryPrimitives.ReadInt32BigEndian(atom.AsSpan(24)) != 1 ||
            BinaryPrimitives.ReadInt32BigEndian(atom.AsSpan(56)) != AacBitRate || BinaryPrimitives.ReadInt32BigEndian(atom.AsSpan(60)) != 1)
            throw new InvalidDataException("AAC 缺少可信的原始预览时序记录，请重新生成；已有 WAV 可继续使用。");
        int rate = BinaryPrimitives.ReadInt32BigEndian(atom.AsSpan(28));
        int channels = BinaryPrimitives.ReadInt32BigEndian(atom.AsSpan(32));
        int platform = BinaryPrimitives.ReadInt32BigEndian(atom.AsSpan(52));
        long frames = BinaryPrimitives.ReadInt64BigEndian(atom.AsSpan(36));
        long nativeFrames = BinaryPrimitives.ReadInt64BigEndian(atom.AsSpan(44));
        if (rate is not (44100 or 48000) || channels is < 1 or > 2 || platform is not (1 or 2) || frames < 1 || frames > rate * 300L ||
            nativeFrames < frames || nativeFrames > frames + MaximumPaddingFrames || frames * channels * sizeof(float) > MaximumFileBytes)
            throw new InvalidDataException("AAC 原始预览时序记录无效或超过解码限制。");
        if (!HashContent(stream, stream.Length - TimingAtomSize, atom.AsSpan(0, 64), token).AsSpan().SequenceEqual(atom.AsSpan(64, 32)))
            throw new InvalidDataException("AAC 文件内容与时序记录不一致，可能已修改或未完整写入，请重新生成。");
        return new(rate, channels, (int)frames, (int)nativeFrames, platform);
    }

    private static byte[] HashContent(Stream stream, long length, ReadOnlySpan<byte> timingHeader, CancellationToken token)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long remaining = length;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count == 0) throw new EndOfStreamException("AAC 文件读取中断。");
            hash.AppendData(buffer, 0, count);
            remaining -= count;
        }
        hash.AppendData(timingHeader);
        token.ThrowIfCancellationRequested();
        return hash.GetHashAndReset();
    }
}
