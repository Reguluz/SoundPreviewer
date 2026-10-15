using System.Text;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Audio;

public sealed class WaveData
{
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required float[] Samples { get; init; }
    public int Frames => Samples.Length / Channels;
    public double DurationSeconds => Frames / (double)SampleRate;
}

public static class WaveFile
{
    private const long MaximumFileBytes = 256L * 1024 * 1024;

    public static WaveData Read(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII);
        if (stream.Length is < 44 or > MaximumFileBytes) throw new InvalidDataException("WAV 大小无效或超过 256 MB 限制。");
        if (ReadTag(reader) != "RIFF") throw new InvalidDataException("仅支持 RIFF WAV 音频。");
        long riffEnd = reader.ReadUInt32() + 8L;
        if (riffEnd > stream.Length || riffEnd < 44 || ReadTag(reader) != "WAVE")
            throw new InvalidDataException("WAV 文件头损坏或文件未写完。");

        ushort format = 0, channels = 0, bits = 0, alignment = 0;
        int rate = 0;
        long dataPosition = -1, dataSize = 0;
        while (stream.Position + 8 <= riffEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tag = ReadTag(reader);
            long size = reader.ReadUInt32();
            long next = stream.Position + size + (size & 1);
            if (next > riffEnd) throw new InvalidDataException("WAV 数据块截断。");
            if (tag == "fmt ")
            {
                if (size < 16 || format != 0) throw new InvalidDataException("WAV 格式块无效。");
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                var byteRate = reader.ReadUInt32();
                alignment = reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (format == 0xfffe)
                {
                    if (size < 40 || reader.ReadUInt16() < 22) throw new InvalidDataException("WAV 扩展格式块损坏。");
                    var validBits = reader.ReadUInt16();
                    reader.ReadUInt32();
                    var subformat = new Guid(reader.ReadBytes(16));
                    format = subformat == new Guid("00000001-0000-0010-8000-00aa00389b71") ? (ushort)1 :
                        subformat == new Guid("00000003-0000-0010-8000-00aa00389b71") ? (ushort)3 : (ushort)0;
                    if (validBits != 0 && validBits != bits) throw new InvalidDataException("尚不支持不同有效位数的 WAV。");
                }
                if (channels is < 1 or > 2 || rate is < 8000 or > 192000 ||
                    (format == 1 && bits is not (8 or 16 or 24 or 32)) ||
                    (format == 3 && bits is not (32 or 64)) || format is not (1 or 3) ||
                    alignment != channels * (bits / 8) || byteRate != (uint)(rate * alignment))
                    throw new InvalidDataException("不支持的 WAV 格式；需要单声道或立体声 PCM / IEEE 浮点。");
            }
            else if (tag == "data")
            {
                if (dataPosition != -1) throw new InvalidDataException("WAV 包含多个音频数据块。");
                dataPosition = stream.Position;
                dataSize = size;
            }
            stream.Position = next;
        }
        if (format == 0 || dataPosition < 0 || dataSize == 0 || alignment == 0 || dataSize % alignment != 0)
            throw new InvalidDataException("WAV 缺少有效音频数据。");
        if (dataSize / alignment / (double)rate > 300)
            throw new InvalidDataException("预览 WAV 超过 300 秒限制。");

        var samples = new float[checked((int)(dataSize / (bits / 8)))];
        stream.Position = dataPosition;
        for (int i = 0; i < samples.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            samples[i] = format == 3 ? bits == 32 ? reader.ReadSingle() : (float)reader.ReadDouble() : bits switch
            {
                8 => (reader.ReadByte() - 128) / 128f,
                16 => reader.ReadInt16() / 32768f,
                24 => ReadPcm24(reader) / 8388608f,
                32 => (float)(reader.ReadInt32() / 2147483648.0),
                _ => throw new InvalidDataException("WAV 位深无效。")
            };
        }
        return new WaveData { SampleRate = rate, Channels = channels, Samples = samples };
    }

    public static AudioMetrics Validate(string path, CancellationToken cancellationToken = default)
    {
        var metrics = Analyze(Read(path, cancellationToken), cancellationToken);
        if (metrics.HasNonFinite) throw new InvalidDataException("音频包含 NaN / Infinity，未提交预览。");
        return metrics;
    }

    public static AudioMetrics Analyze(WaveData audio, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureShape(audio);
        double peak = 0, squareSum = 0;
        long finiteCount = 0, crossings = 0;
        bool nonFinite = false;
        double previous = 0;
        for (int frame = 0; frame < audio.Frames; frame++)
        {
            if ((frame & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            double mono = 0;
            for (int channel = 0; channel < audio.Channels; channel++)
            {
                float sample = audio.Samples[frame * audio.Channels + channel];
                if (!float.IsFinite(sample)) { nonFinite = true; continue; }
                peak = Math.Max(peak, Math.Abs(sample));
                squareSum += sample * (double)sample;
                finiteCount++;
                mono += sample / (double)audio.Channels;
            }
            if (frame > 0 && ((previous < 0 && mono >= 0) || (previous >= 0 && mono < 0))) crossings++;
            previous = mono;
        }
        var rms = finiteCount == 0 ? 0 : Math.Sqrt(squareSum / finiteCount);
        return new AudioMetrics
        {
            SampleRate = audio.SampleRate, Channels = audio.Channels, DurationSeconds = audio.DurationSeconds,
            Peak = peak, Rms = rms, ZeroCrossingRate = crossings / (double)Math.Max(1, audio.Frames - 1),
            IsSilent = rms < 0.00001, HasClipping = peak >= 0.999, HasNonFinite = nonFinite
        };
    }

    public static void WritePcm16(string path, WaveData audio)
    {
        EnsureShape(audio);
        if (audio.Samples.Any(s => !float.IsFinite(s))) throw new InvalidDataException("无法写入非有限音频数值。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        int bytes = checked(audio.Samples.Length * 2);
        writer.Write("RIFF"u8); writer.Write(checked(bytes + 36)); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)audio.Channels);
        writer.Write(audio.SampleRate); writer.Write(audio.SampleRate * audio.Channels * 2);
        writer.Write((ushort)(audio.Channels * 2)); writer.Write((ushort)16);
        writer.Write("data"u8); writer.Write(bytes);
        foreach (float sample in audio.Samples)
            writer.Write((short)Math.Round(Math.Clamp((double)sample, -1, short.MaxValue / 32768.0) * 32768));
        writer.Flush();
        stream.Flush(true);
    }

    public static void WriteFloat32(string path, WaveData audio, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureShape(audio);
        long byteCount = audio.Samples.LongLength * sizeof(float);
        if (byteCount + 56 > MaximumFileBytes || audio.DurationSeconds > 300)
            throw new InvalidDataException("预览 WAV 超出大小或时长限制。");
        int bytes = (int)byteCount;
        for (int index = 0; index < audio.Samples.Length; index++)
        {
            if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!float.IsFinite(audio.Samples[index])) throw new InvalidDataException("无法写入非有限音频数值。");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write("RIFF"u8); writer.Write(checked(bytes + 48)); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((ushort)3); writer.Write((ushort)audio.Channels);
        writer.Write(audio.SampleRate); writer.Write(audio.SampleRate * audio.Channels * sizeof(float));
        writer.Write((ushort)(audio.Channels * sizeof(float))); writer.Write((ushort)32);
        writer.Write("fact"u8); writer.Write(4); writer.Write(audio.Frames);
        writer.Write("data"u8); writer.Write(bytes);
        for (int index = 0; index < audio.Samples.Length; index++)
        {
            if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            writer.Write(audio.Samples[index]);
        }
        writer.Flush();
        stream.Flush(true);
    }

    public static WaveData Concatenate(IEnumerable<WaveData> source)
    {
        var segments = source.ToArray();
        if (segments.Length == 0) throw new ArgumentException("没有音频片段。");
        foreach (var segment in segments)
        {
            EnsureShape(segment);
            if (segment.SampleRate != segments[0].SampleRate || segment.Channels != segments[0].Channels)
                throw new InvalidDataException("拼接的音频采样率和声道数必须一致。");
        }
        var samples = new float[checked(segments.Sum(s => s.Samples.Length))];
        int offset = 0;
        foreach (var segment in segments)
        {
            segment.Samples.CopyTo(samples, offset);
            offset += segment.Samples.Length;
        }
        return new WaveData { SampleRate = segments[0].SampleRate, Channels = segments[0].Channels, Samples = samples };
    }

    public static WaveData Resample(WaveData audio, int sampleRate)
    {
        EnsureShape(audio);
        if (sampleRate is < 8000 or > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (sampleRate == audio.SampleRate) return audio;
        int frames = checked((int)Math.Round(audio.Frames * (double)sampleRate / audio.SampleRate));
        var samples = new float[checked(frames * audio.Channels)];
        const int radius = 16;
        double cutoff = Math.Min(1, sampleRate / (double)audio.SampleRate) * 0.95;
        // Windowed sinc avoids the aliasing of interpolation-only downsampling.
        for (int frame = 0; frame < frames; frame++)
        {
            double position = frame * (double)audio.SampleRate / sampleRate;
            int center = (int)position;
            for (int channel = 0; channel < audio.Channels; channel++)
            {
                double sum = 0, weights = 0;
                for (int tap = center - radius + 1; tap <= center + radius; tap++)
                {
                    if (tap < 0 || tap >= audio.Frames) continue;
                    double distance = position - tap;
                    if (Math.Abs(distance) >= radius) continue;
                    double x = Math.PI * distance * cutoff;
                    double sinc = Math.Abs(x) < 1e-12 ? 1 : Math.Sin(x) / x;
                    double weight = cutoff * sinc * (0.5 + 0.5 * Math.Cos(Math.PI * distance / radius));
                    sum += audio.Samples[tap * audio.Channels + channel] * weight;
                    weights += weight;
                }
                samples[frame * audio.Channels + channel] = (float)(Math.Abs(weights) > 1e-12 ? sum / weights : 0);
            }
        }
        return new WaveData { SampleRate = sampleRate, Channels = audio.Channels, Samples = samples };
    }

    private static void EnsureShape(WaveData audio)
    {
        if (audio.Channels is < 1 or > 2 || audio.SampleRate is < 8000 or > 192000 ||
            audio.Samples.Length == 0 || audio.Samples.Length % audio.Channels != 0)
            throw new InvalidDataException("音频结构无效。");
    }

    private static string ReadTag(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));

    private static int ReadPcm24(BinaryReader reader)
    {
        int value = reader.ReadByte() | reader.ReadByte() << 8 | reader.ReadByte() << 16;
        return (value & 0x800000) != 0 ? value | unchecked((int)0xff000000) : value;
    }
}
