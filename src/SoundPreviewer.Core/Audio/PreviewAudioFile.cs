using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Audio;

public static partial class PreviewAudioFile
{
    public const int AacBitRate = 128000;
    private const long MaximumFileBytes = 256L * 1024 * 1024;
    private const int MaximumPaddingFrames = 8192;
    private static readonly object WindowsStartupGate = new();

    public static bool IsCompressed(string path) => Path.GetExtension(path).Equals(".m4a", StringComparison.OrdinalIgnoreCase);
    public static bool IsSupportedExtension(string path) => IsCompressed(path) || Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase);

    public static WaveData Read(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupportedExtension(path)) throw new NotSupportedException("预览文件需要 WAV 或 AAC M4A 格式。");
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("预览音频文件不存在。", fullPath);
        if (!IsCompressed(fullPath)) return WaveFile.Read(fullPath, cancellationToken);
        var timing = ReadTiming(fullPath, cancellationToken);
        var decoded = DecodeNative(fullPath, timing.SampleRate, timing.Channels, timing.Frames, cancellationToken);
        return decoded.Audio;
    }

    public static AudioMetrics Validate(string path, CancellationToken cancellationToken = default)
    {
        var metrics = WaveFile.Analyze(Read(path, cancellationToken), cancellationToken);
        if (metrics.HasNonFinite) throw new InvalidDataException("音频包含 NaN / Infinity，未提交预览。");
        return metrics;
    }

    public static async Task EncodeAsync(WaveData audio, string outputPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCompressed(outputPath)) throw new ArgumentException("AAC 预览输出必须使用 .m4a 扩展名。", nameof(outputPath));
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("此平台暂不支持系统 AAC 编码，请选择 WAV 预览格式。");
        var metrics = WaveFile.Analyze(audio, cancellationToken);
        if (audio.SampleRate is not (44100 or 48000) || audio.Channels is < 1 or > 2 ||
            audio.Frames < 1 || audio.DurationSeconds > 300 || audio.Samples.LongLength * sizeof(float) > MaximumFileBytes || metrics.HasNonFinite)
            throw new InvalidDataException("AAC 预览需要 44.1/48 kHz 单声道或立体声、有限数值且不超过 300 秒的音频。");
        string destination = Path.GetFullPath(outputPath);
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string pending = Path.Combine(directory, ".aac-" + Guid.NewGuid().ToString("N") + ".m4a");
        try
        {
            if (OperatingSystem.IsWindows())
                await Task.Run(() => EncodeWindows(audio, pending, cancellationToken), cancellationToken).ConfigureAwait(false);
            else
                await EncodeMacAsync(audio, pending, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Both system decoders apply their own native gapless timing. Keep the
            // original frame count in this file to remove only trailing AAC padding.
            var decoded = await Task.Run(() => DecodeNative(pending, audio.SampleRate, audio.Channels, audio.Frames, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            WriteTiming(pending, new Timing(audio.SampleRate, audio.Channels, audio.Frames, decoded.RawFrames), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pending, destination, overwrite: true);
        }
        catch (Exception error) when (IsNativeCodecFailure(error))
        {
            throw new IOException("系统 AAC 编码未完成，可选择 WAV 预览格式。", error);
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
        }
    }

    private sealed record Decoded(WaveData Audio, int RawFrames);
    private static int CurrentCodecPlatform => OperatingSystem.IsWindows() ? 1 : 2;
    private static bool IsNativeCodecFailure(Exception error) =>
        error is InvalidOperationException or Win32Exception or TimeoutException or DllNotFoundException or EntryPointNotFoundException;

    private static Decoded DecodeNative(string path, int sampleRate, int channels, int frames, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (OperatingSystem.IsWindows()) return DecodeWindows(path, sampleRate, channels, frames, token);
            if (OperatingSystem.IsMacOS()) return DecodeMac(path, sampleRate, channels, frames, token);
        }
        catch (Exception error) when (IsNativeCodecFailure(error))
        {
            throw new IOException("系统 AAC 解码未完成，可选择 WAV 重新生成预览。", error);
        }
        throw new PlatformNotSupportedException("此平台暂不支持系统 AAC 解码；已有 WAV 预览仍可使用。");
    }

    private static void StartWindowsMediaFoundation()
    {
        try { lock (WindowsStartupGate) MediaFoundationApi.Startup(); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            throw new NotSupportedException("系统缺少 AAC 媒体组件，请选择 WAV 预览格式；Windows N 版可安装系统 Media Feature Pack。", error);
        }
    }

    private static void EncodeWindows(WaveData audio, string path, CancellationToken token)
    {
        StartWindowsMediaFoundation();
        try
        {
            if (!MediaFoundationEncoder.GetEncodeBitrates(AudioSubtypes.MFAudioFormat_AAC, audio.SampleRate, audio.Channels).Contains(AacBitRate))
                throw new NotSupportedException("系统没有匹配当前采样率、声道和 128 kbps 的 AAC 编码器，请选择 WAV 预览格式。");
            MediaFoundationEncoder.EncodeToAac(new PcmProvider(audio, token), path, AacBitRate);
        }
        catch (COMException error) { throw new NotSupportedException("系统 AAC 编码器不可用，请选择 WAV 预览格式。", error); }
        token.ThrowIfCancellationRequested();
    }

    private sealed class PcmProvider(WaveData audio, CancellationToken token) : IWaveProvider
    {
        private int position;
        public WaveFormat WaveFormat { get; } = new(audio.SampleRate, 16, audio.Channels);
        public int Read(byte[] buffer, int offset, int count)
        {
            token.ThrowIfCancellationRequested();
            int samples = Math.Min(count / WaveFormat.BlockAlign * audio.Channels, audio.Samples.Length - position);
            for (int i = 0; i < samples; i++)
            {
                short pcm = (short)Math.Round(Math.Clamp((double)audio.Samples[position++], -1, short.MaxValue / 32768d) * 32768);
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset + i * 2, 2), pcm);
            }
            return samples * 2;
        }
    }

    private static Decoded DecodeWindows(string path, int rate, int channels, int frames, CancellationToken token)
    {
        StartWindowsMediaFoundation();
        try
        {
            using var reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings
                { RequestFloatOutput = true, SingleReaderObject = true });
            if (reader.WaveFormat.SampleRate != rate || reader.WaveFormat.Channels != channels ||
                reader.WaveFormat.BitsPerSample != 32 || reader.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
                throw new InvalidDataException("AAC 解码改变了原采样率、声道数或浮点格式。");
            var samples = new float[checked(frames * channels)];
            byte[] buffer = new byte[16384];
            int rawSamples = 0, count;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                count = reader.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                if (count % (channels * sizeof(float)) != 0) throw new InvalidDataException("AAC 解码返回了不完整的音频帧。");
                int available = count / sizeof(float);
                if (rawSamples + (long)available > (frames + (long)MaximumPaddingFrames) * channels)
                    throw new InvalidDataException("AAC 解码时长超过可信原始帧数及尾部补齐限制。");
                int copy = Math.Min(available, Math.Max(0, samples.Length - rawSamples));
                if (copy > 0) Buffer.BlockCopy(buffer, 0, samples, rawSamples * sizeof(float), copy * sizeof(float));
                rawSamples += available;
            }
            if (rawSamples < samples.Length) throw new InvalidDataException("AAC 音频截断，无法恢复完整的原预览时长。");
            token.ThrowIfCancellationRequested();
            var result = new WaveData { SampleRate = rate, Channels = channels, Samples = samples };
            if (WaveFile.Analyze(result, token).HasNonFinite) throw new InvalidDataException("AAC 解码包含非有限采样值。");
            return new(result, rawSamples / channels);
        }
        catch (COMException error) { throw new NotSupportedException("系统 AAC 解码器不可用或文件损坏；可选择 WAV 重新生成预览。", error); }
    }

    private static async Task EncodeMacAsync(WaveData audio, string path, CancellationToken token)
    {
        string scratch = CreateScratch(path);
        try
        {
            string input = Path.Combine(scratch, "input.wav");
            WaveFile.WriteFloat32(input, audio, token);
            var outcome = await ProcessRunner.RunAsync("/usr/bin/afconvert",
                [input, path, "-f", "m4af", "-d", "aac@" + audio.SampleRate, "-b", AacBitRate.ToString(), "-s", "0", "-q", "127", "-c", audio.Channels.ToString()],
                scratch, TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
            if (outcome.ExitCode != 0) throw new NotSupportedException("系统 AAC 编码未完成，请选择 WAV 预览格式：" + outcome.StandardError);
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    private static Decoded DecodeMac(string path, int rate, int channels, int frames, CancellationToken token)
    {
        string scratch = CreateScratch(path);
        try
        {
            string output = Path.Combine(scratch, "decoded.wav");
            // Run the async process helper off the caller's UI synchronization
            // context; Read is also used by synchronous waveform/playback callers.
            long maximumOutputBytes = (frames + (long)MaximumPaddingFrames) * channels * sizeof(float) + 65536;
            var outcome = Task.Run(() => RunMacDecodeAsync(path, output, scratch, maximumOutputBytes, token),
                CancellationToken.None).GetAwaiter().GetResult();
            if (outcome.ExitCode != 0) throw new InvalidDataException("系统 AAC 解码未完成：" + outcome.StandardError);
            return ReadMacDecodedWave(output, rate, channels, frames, token);
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    private static async Task<ProcessOutcome> RunMacDecodeAsync(string input, string output, string scratch,
        long maximumOutputBytes, CancellationToken token)
    {
        using var outputLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
        var conversion = ProcessRunner.RunAsync("/usr/bin/afconvert", [input, output, "-f", "WAVE", "-d", "LEF32"],
            scratch, TimeSpan.FromSeconds(60), outputLimit.Token);
        try
        {
            while (!conversion.IsCompleted)
            {
                if (File.Exists(output) && new FileInfo(output).Length > maximumOutputBytes)
                    throw new InvalidDataException("AAC 原生解码输出超过可信时长限制，已停止解码。");
                await Task.WhenAny(conversion, Task.Delay(20, CancellationToken.None)).ConfigureAwait(false);
            }
            var outcome = await conversion.ConfigureAwait(false);
            if (File.Exists(output) && new FileInfo(output).Length > maximumOutputBytes)
                throw new InvalidDataException("AAC 原生解码输出超过可信时长限制。");
            return outcome;
        }
        catch
        {
            outputLimit.Cancel();
            // ProcessRunner owns this exact process tree and waits for it to end.
            // Preserve the original limit, cancellation, or codec error afterwards.
            try { await conversion.ConfigureAwait(false); }
            catch (Exception) { }
            throw;
        }
    }

    private static Decoded ReadMacDecodedWave(string path, int rate, int channels, int frames, CancellationToken token)
    {
        // afconvert produces IEEE float WAV. Inspect its bounded native output
        // before allocating, including the small amount of possible AAC padding.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long maximumPcmBytes = (frames + (long)MaximumPaddingFrames) * channels * sizeof(float);
        if (stream.Length < 44 || stream.Length > maximumPcmBytes + 65536)
            throw new InvalidDataException("AAC 原生解码输出超过可信时长限制。");
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != 0x46464952) throw new InvalidDataException("AAC 原生解码未返回 RIFF WAV。");
        long end = reader.ReadUInt32() + 8L;
        if (end > stream.Length || end < 44 || reader.ReadUInt32() != 0x45564157)
            throw new InvalidDataException("AAC 原生解码 WAV 文件头无效。");
        bool formatFound = false;
        long dataOffset = -1, dataBytes = 0;
        while (stream.Position + 8 <= end)
        {
            token.ThrowIfCancellationRequested();
            uint tag = reader.ReadUInt32();
            long size = reader.ReadUInt32(), next = stream.Position + size + (size & 1);
            if (next > end) throw new InvalidDataException("AAC 原生解码 WAV 数据截断。");
            if (tag == 0x20746d66)
            {
                if (formatFound || size < 16 || reader.ReadUInt16() != 3 || reader.ReadUInt16() != channels ||
                    reader.ReadInt32() != rate || reader.ReadUInt32() != rate * channels * sizeof(float) ||
                    reader.ReadUInt16() != channels * sizeof(float) || reader.ReadUInt16() != 32)
                    throw new InvalidDataException("AAC 解码改变了采样率、声道或 IEEE 浮点格式。");
                formatFound = true;
            }
            else if (tag == 0x61746164)
            {
                if (dataOffset >= 0) throw new InvalidDataException("AAC 原生解码包含多个音频数据块。");
                dataOffset = stream.Position;
                dataBytes = size;
            }
            stream.Position = next;
        }
        if (!formatFound || dataOffset < 0 || dataBytes % (channels * sizeof(float)) != 0 ||
            dataBytes < frames * (long)channels * sizeof(float) || dataBytes > maximumPcmBytes)
            throw new InvalidDataException("AAC 解码时长与原始预览不一致。");
        var samples = new float[checked(frames * channels)];
        stream.Position = dataOffset;
        for (int i = 0; i < samples.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            samples[i] = reader.ReadSingle();
            if (!float.IsFinite(samples[i])) throw new InvalidDataException("AAC 解码包含非有限采样值。");
        }
        return new(new WaveData { SampleRate = rate, Channels = channels, Samples = samples }, (int)(dataBytes / (channels * sizeof(float))));
    }

    private static string CreateScratch(string path)
    {
        string directory = Path.Combine(Path.GetDirectoryName(path)!, ".aac-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
