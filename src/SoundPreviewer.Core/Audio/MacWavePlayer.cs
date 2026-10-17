using System.Runtime.InteropServices;

namespace SoundPreviewer.Core.Audio;

/// <summary>Native Audio Queue output; the preview file and shared PCM conversion stay unchanged.</summary>
internal sealed class MacWavePlayer : IWavePlayback
{
    private enum PlaybackState { Stopped, Playing, Paused }
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const uint IsRunningProperty = 0x6171726e; // 'aqrn'
    private static readonly OutputCallback BufferReturned = OnBufferReturned;
    private IntPtr queue;
    private IntPtr buffer;
    private GCHandle callbackHandle;
    private QueueContext? context;
    private short[]? pcm;
    private int sampleRate;
    private int channels;
    private int startFrame;
    private int positionFrame;
    private bool started;
    private bool disposed;
    private double volume = 1;
    private PlaybackState state;

    private int Frames => pcm is null || channels == 0 ? 0 : pcm.Length / channels;
    public double DurationSeconds => sampleRate == 0 ? 0 : Frames / (double)sampleRate;
    public double PositionSeconds
    {
        get { RefreshPosition(); return sampleRate == 0 ? 0 : positionFrame / (double)sampleRate; }
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
            if (queue != IntPtr.Zero) Check(AudioQueueSetParameter(queue, 1, (float)value), "设置试听音量");
            volume = value;
        }
    }

    public void Load(short[] decoded, int rate, int channelCount)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Unload();
        pcm = decoded;
        sampleRate = rate;
        channels = channelCount;
        try { CreateQueue(); }
        catch
        {
            ReleaseQueue();
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
        Check(AudioQueuePause(queue), "暂停试听");
        RefreshPosition();
        state = PlaybackState.Paused;
    }

    public void Resume()
    {
        EnsureLoaded();
        RefreshPosition();
        if (state == PlaybackState.Playing) return;
        if (state == PlaybackState.Paused && buffer != IntPtr.Zero)
        {
            Check(AudioQueueStart(queue, IntPtr.Zero), "继续试听");
            started = true;
            state = PlaybackState.Playing;
        }
        else QueueFrom(positionFrame >= Frames ? 0 : positionFrame, paused: false);
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ReleaseQueue();
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
            ReleaseQueue();
            startFrame = positionFrame = frame;
        }
    }

    public void Unload()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ReleaseQueue();
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

    ~MacWavePlayer()
    {
        try { ReleaseQueue(); }
        catch { /* Keep callback data alive if the native queue could not be disposed. */ }
    }

    private void CreateQueue()
    {
        context = new QueueContext();
        callbackHandle = GCHandle.Alloc(context);
        var format = new StreamDescription
        {
            SampleRate = sampleRate,
            FormatId = 0x6c70636d, // 'lpcm'
            FormatFlags = 4 | 8, // signed integer, packed, native little-endian
            BytesPerPacket = checked((uint)channels * 2), FramesPerPacket = 1,
            BytesPerFrame = checked((uint)channels * 2), ChannelsPerFrame = (uint)channels,
            BitsPerChannel = 16
        };
        // A null run loop gives Audio Queue its own callback thread in GUI and worker contexts.
        Check(AudioQueueNewOutput(ref format, BufferReturned, GCHandle.ToIntPtr(callbackHandle),
            IntPtr.Zero, IntPtr.Zero, 0, out queue), "打开默认音频设备");
        Check(AudioQueueSetParameter(queue, 1, (float)volume), "设置试听音量");
    }

    private void QueueFrom(int frame, bool paused)
    {
        // Each submitted segment has its own timeline and callback state. This avoids old callbacks
        // completing a new segment after a seek/reset and makes sample positions relative to zero.
        ReleaseQueue();
        startFrame = positionFrame = frame;
        state = PlaybackState.Stopped;
        if (frame >= Frames) return;
        try
        {
            CreateQueue();
            int first = checked(frame * channels);
            int samples = pcm!.Length - first;
            uint bytes = checked((uint)samples * 2);
            Check(AudioQueueAllocateBuffer(queue, bytes, out buffer), "准备试听音频");
            var nativeBuffer = Marshal.PtrToStructure<QueueBuffer>(buffer);
            Marshal.Copy(pcm, first, nativeBuffer.AudioData, samples);
            // AudioQueueBuffer's remaining members are read-only native allocation metadata.
            Marshal.WriteInt32(buffer, (int)Marshal.OffsetOf<QueueBuffer>(nameof(QueueBuffer.AudioDataByteSize)), unchecked((int)bytes));
            Check(AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero), "提交试听音频");
            if (!paused)
            {
                Check(AudioQueueStart(queue, IntPtr.Zero), "启动试听");
                started = true;
            }
            state = paused ? PlaybackState.Paused : PlaybackState.Playing;
        }
        catch
        {
            ReleaseQueue();
            throw;
        }
    }

    private void RefreshPosition()
    {
        if (disposed || queue == IntPtr.Zero || !started || state != PlaybackState.Playing) return;
        if (context is not null && Volatile.Read(ref context.StopResult) != 0)
            Check(Volatile.Read(ref context.StopResult), "结束试听");
        if (context is not null && Volatile.Read(ref context.BufferReturned) != 0)
        {
            uint size = sizeof(uint);
            Check(AudioQueueGetProperty(queue, IsRunningProperty, out uint running, ref size), "读取试听状态");
            if (running == 0)
            {
                positionFrame = Frames;
                state = PlaybackState.Stopped;
                return;
            }
        }
        int result = AudioQueueGetCurrentTime(queue, IntPtr.Zero, out var time, IntPtr.Zero);
        // The queue has no valid hardware time during its brief startup interval.
        if (result == -66678) return; // kAudioQueueErr_InvalidRunState
        Check(result, "读取试听位置");
        if ((time.Flags & 1) != 0 && double.IsFinite(time.SampleTime))
            positionFrame = (int)Math.Clamp(startFrame + Math.Max(0, time.SampleTime), 0, Frames);
    }

    private void ReleaseQueue()
    {
        if (queue != IntPtr.Zero)
        {
            // Immediate disposal is synchronous and guarantees no callbacks after it returns.
            // The queue owns its buffers, so no separately freed pointer can race the driver.
            if (context is not null) Volatile.Write(ref context.Disposing, 1);
            Check(AudioQueueDispose(queue, 1), "关闭试听设备");
            queue = buffer = IntPtr.Zero;
        }
        if (callbackHandle.IsAllocated) callbackHandle.Free();
        context = null;
        started = false;
    }

    private static void OnBufferReturned(IntPtr userData, IntPtr audioQueue, IntPtr returnedBuffer)
    {
        // Never let managed exceptions cross the native audio callback boundary.
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is not QueueContext data || Volatile.Read(ref data.Disposing) != 0) return;
            // A returned output buffer can precede the physical device's final sample. Drain it
            // asynchronously and report completion only once IsRunning becomes false.
            int result = AudioQueueStop(audioQueue, 0);
            Volatile.Write(ref data.StopResult, result);
            Volatile.Write(ref data.BufferReturned, 1);
        }
        catch { }
    }

    private int ToFrame(double seconds)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        return (int)Math.Round(Math.Clamp(seconds, 0, DurationSeconds) * sampleRate);
    }

    private void EnsureLoaded()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pcm is null) throw new InvalidOperationException("尚未载入可试听的 WAV。");
    }

    private static void Check(int result, string action)
    {
        if (result != 0) throw new IOException($"{action}失败（macOS 音频错误 {result}）。");
    }

    private sealed class QueueContext
    {
        public int BufferReturned;
        public int StopResult;
        public int Disposing;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StreamDescription
    {
        public double SampleRate;
        public uint FormatId, FormatFlags, BytesPerPacket, FramesPerPacket;
        public uint BytesPerFrame, ChannelsPerFrame, BitsPerChannel, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct AudioTimeStamp
    {
        [FieldOffset(0)] public double SampleTime;
        [FieldOffset(56)] public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OutputCallback(IntPtr userData, IntPtr audioQueue, IntPtr buffer);

    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueNewOutput(ref StreamDescription format, OutputCallback callback,
        IntPtr userData, IntPtr callbackRunLoop, IntPtr callbackRunLoopMode, uint flags, out IntPtr queue);
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint bytes, out IntPtr buffer);
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packetDescriptions, IntPtr descriptions);
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueuePause(IntPtr queue);
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueStop(IntPtr queue, byte immediate);
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueDispose(IntPtr queue, byte immediate);
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueSetParameter(IntPtr queue, uint parameter, float value);
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueGetCurrentTime(IntPtr queue, IntPtr timeline, out AudioTimeStamp time, IntPtr discontinuity);
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueGetProperty(IntPtr queue, uint property, out uint data, ref uint bytes);
}
