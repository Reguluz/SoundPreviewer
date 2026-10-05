using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SoundPreviewer.Core.Audio;

public sealed class WavePlayer : IDisposable
{
    private enum PlaybackState { Stopped, Playing, Paused }
    private const uint StillPlaying = 33;
    private readonly uint headerSize = (uint)Marshal.SizeOf<WaveHeader>();
    private IntPtr device;
    private IntPtr header;
    private IntPtr buffer;
    private short[]? pcm;
    private int sampleRate;
    private int channels;
    private int startFrame;
    private int positionFrame;
    private bool prepared;
    private bool disposed;
    private double volume = 1;
    private PlaybackState state;

    public double DurationSeconds => sampleRate == 0 ? 0 : Frames / (double)sampleRate;
    private int Frames => pcm is null || channels == 0 ? 0 : pcm.Length / channels;

    public double PositionSeconds
    {
        get
        {
            RefreshPosition();
            return sampleRate == 0 ? 0 : positionFrame / (double)sampleRate;
        }
    }

    public bool IsPlaying
    {
        get { RefreshPosition(); return state == PlaybackState.Playing; }
    }

    public double Volume
    {
        get => volume;
        set
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
            if (device != IntPtr.Zero) SetDeviceVolume(value);
            volume = value;
        }
    }

    public void Load(WaveData audio)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var metrics = WaveFile.Analyze(audio);
        if (metrics.HasNonFinite) throw new InvalidDataException("音频包含 NaN / Infinity，无法试听。");
        var decoded = new short[audio.Samples.Length];
        for (int i = 0; i < decoded.Length; i++)
            decoded[i] = (short)Math.Round(Math.Clamp((double)audio.Samples[i], -1, short.MaxValue / 32768.0) * 32768);
        Unload();
        sampleRate = audio.SampleRate;
        channels = audio.Channels;
        pcm = decoded;
        var format = new WaveFormat
        {
            FormatTag = 1, Channels = (ushort)channels, SampleRate = (uint)sampleRate,
            AverageBytesPerSecond = (uint)(sampleRate * channels * 2), BlockAlignment = (ushort)(channels * 2), BitsPerSample = 16
        };
        try
        {
            Check(WaveOutOpen(out device, uint.MaxValue, ref format, IntPtr.Zero, IntPtr.Zero, 0), "打开默认音频设备");
            SetDeviceVolume(volume);
        }
        catch
        {
            if (device != IntPtr.Zero) { WaveOutClose(device); device = IntPtr.Zero; }
            pcm = null;
            sampleRate = channels = 0;
            throw;
        }
    }

    public void Play(double startSeconds = 0)
    {
        EnsureLoaded();
        QueueFrom(ToFrame(startSeconds), paused: false);
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RefreshPosition();
        if (state != PlaybackState.Playing) return;
        Check(WaveOutPause(device), "暂停试听");
        RefreshPosition();
        state = PlaybackState.Paused;
    }

    public void Resume()
    {
        EnsureLoaded();
        RefreshPosition();
        if (state == PlaybackState.Playing) return;
        if (state == PlaybackState.Paused && header != IntPtr.Zero)
        {
            Check(WaveOutRestart(device), "继续试听");
            state = PlaybackState.Playing;
        }
        else QueueFrom(positionFrame >= Frames ? 0 : positionFrame, paused: false);
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ReleaseBuffer();
        state = PlaybackState.Stopped;
        startFrame = positionFrame = 0;
    }

    public void Seek(double seconds)
    {
        EnsureLoaded();
        RefreshPosition();
        var previousState = state;
        int frame = ToFrame(seconds);
        if (previousState is PlaybackState.Playing or PlaybackState.Paused)
            QueueFrom(frame, previousState == PlaybackState.Paused);
        else
        {
            ReleaseBuffer();
            startFrame = positionFrame = frame;
        }
    }

    public void Unload()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ReleaseResources();
        pcm = null;
        sampleRate = channels = startFrame = positionFrame = 0;
        state = PlaybackState.Stopped;
    }

    public void Dispose()
    {
        if (disposed) return;
        Unload();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    ~WavePlayer()
    {
        try { ReleaseResources(); }
        catch { /* Never free buffers still owned by an audio driver. */ }
    }

    private void QueueFrom(int frame, bool paused)
    {
        ReleaseBuffer();
        startFrame = positionFrame = frame;
        state = PlaybackState.Stopped;
        if (frame >= Frames) return;
        int first = checked(frame * channels);
        int count = pcm!.Length - first;
        try
        {
            buffer = Marshal.AllocHGlobal(checked(count * 2));
            Marshal.Copy(pcm, first, buffer, count);
            header = Marshal.AllocHGlobal((int)headerSize);
            Marshal.StructureToPtr(new WaveHeader { Data = buffer, BufferLength = checked((uint)(count * 2)) }, header, false);
            Check(WaveOutPrepareHeader(device, header, headerSize), "准备试听音频");
            prepared = true;
            if (paused) Check(WaveOutPause(device), "保持试听暂停");
            Check(WaveOutWrite(device, header, headerSize), "提交试听音频");
            if (!paused) Check(WaveOutRestart(device), "启动试听");
            state = paused ? PlaybackState.Paused : PlaybackState.Playing;
        }
        catch
        {
            ReleaseBuffer();
            throw;
        }
    }

    private void RefreshPosition()
    {
        if (disposed || device == IntPtr.Zero || header == IntPtr.Zero) return;
        if ((Marshal.PtrToStructure<WaveHeader>(header).Flags & 1) != 0)
        {
            positionFrame = Frames;
            state = PlaybackState.Stopped;
            return;
        }
        var time = new MultimediaTime { Type = 4 };
        Check(WaveOutGetPosition(device, ref time, (uint)Marshal.SizeOf<MultimediaTime>()), "读取试听位置");
        long elapsed = time.Type switch
        {
            4 => time.Value / (channels * 2L),
            2 => time.Value,
            1 => time.Value * (long)sampleRate / 1000,
            _ => throw new IOException("音频设备返回了不支持的位置格式。")
        };
        positionFrame = (int)Math.Clamp(startFrame + elapsed, 0, Frames);
    }

    private void ReleaseBuffer()
    {
        if (device != IntPtr.Zero && header != IntPtr.Zero)
        {
            // A driver must return and unprepare its header before either pointer is freed.
            Check(WaveOutReset(device), "停止试听");
            if (prepared)
            {
                uint result = StillPlaying;
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    result = WaveOutUnprepareHeader(device, header, headerSize);
                    if (result != StillPlaying) break;
                    Thread.Sleep(5);
                }
                Check(result, "释放试听缓冲区");
                prepared = false;
            }
        }
        if (header != IntPtr.Zero) { Marshal.FreeHGlobal(header); header = IntPtr.Zero; }
        if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; }
    }

    private void ReleaseResources()
    {
        ReleaseBuffer();
        if (device != IntPtr.Zero)
        {
            Check(WaveOutClose(device), "关闭试听设备");
            device = IntPtr.Zero;
        }
    }

    private int ToFrame(double seconds)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        return (int)Math.Round(Math.Clamp(seconds, 0, DurationSeconds) * sampleRate);
    }

    private void EnsureLoaded()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pcm is null || device == IntPtr.Zero) throw new InvalidOperationException("尚未载入可试听的 WAV。");
    }

    private void SetDeviceVolume(double gain)
    {
        uint level = (uint)Math.Round(gain * ushort.MaxValue);
        Check(WaveOutSetVolume(device, level | level << 16), "设置试听音量");
    }

    private static void Check(uint result, string action)
    {
        if (result == 0) return;
        var message = new StringBuilder(256);
        WaveOutGetErrorText(result, message, (uint)message.Capacity);
        throw new Win32Exception((int)result, $"{action}失败（Windows 音频错误 {result}）：{message}");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SampleRate;
        public uint AverageBytesPerSecond;
        public ushort BlockAlignment;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nuint User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct MultimediaTime
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Value;
    }

    [DllImport("winmm.dll", EntryPoint = "waveOutOpen")]
    private static extern uint WaveOutOpen(out IntPtr device, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll", EntryPoint = "waveOutPrepareHeader")]
    private static extern uint WaveOutPrepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveOutWrite")]
    private static extern uint WaveOutWrite(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveOutPause")]
    private static extern uint WaveOutPause(IntPtr device);
    [DllImport("winmm.dll", EntryPoint = "waveOutRestart")]
    private static extern uint WaveOutRestart(IntPtr device);
    [DllImport("winmm.dll", EntryPoint = "waveOutReset")]
    private static extern uint WaveOutReset(IntPtr device);
    [DllImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader")]
    private static extern uint WaveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveOutClose")]
    private static extern uint WaveOutClose(IntPtr device);
    [DllImport("winmm.dll", EntryPoint = "waveOutGetPosition")]
    private static extern uint WaveOutGetPosition(IntPtr device, ref MultimediaTime time, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveOutSetVolume")]
    private static extern uint WaveOutSetVolume(IntPtr device, uint volume);
    [DllImport("winmm.dll", EntryPoint = "waveOutGetErrorTextW", CharSet = CharSet.Unicode)]
    private static extern uint WaveOutGetErrorText(uint error, StringBuilder text, uint characters);
}
