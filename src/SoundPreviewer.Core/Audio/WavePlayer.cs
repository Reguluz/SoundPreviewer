namespace SoundPreviewer.Core.Audio;

/// <summary>Auditions the same PCM data through the operating system's default output device.</summary>
public sealed class WavePlayer : IDisposable
{
    private readonly IWavePlayback playback = OperatingSystem.IsWindows() ? new WindowsWavePlayer() :
        OperatingSystem.IsMacOS() ? new MacWavePlayer() :
        throw new PlatformNotSupportedException("音频试听目前支持 Windows 和 macOS。");
    private bool disposed;

    public double DurationSeconds => playback.DurationSeconds;
    public double PositionSeconds => playback.PositionSeconds;
    public bool IsPlaying => playback.IsPlaying;
    public double Volume { get => playback.Volume; set => playback.Volume = value; }

    public void Load(WaveData audio)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var metrics = WaveFile.Analyze(audio);
        if (metrics.HasNonFinite) throw new InvalidDataException("音频包含 NaN / Infinity，无法试听。");
        var decoded = new short[audio.Samples.Length];
        for (int i = 0; i < decoded.Length; i++)
            decoded[i] = (short)Math.Round(Math.Clamp((double)audio.Samples[i], -1, short.MaxValue / 32768.0) * 32768);
        playback.Load(decoded, audio.SampleRate, audio.Channels);
    }

    public void Play(double startSeconds = 0) => playback.Play(startSeconds);
    public void Pause() => playback.Pause();
    public void Resume() => playback.Resume();
    public void Stop() => playback.Stop();
    public void Seek(double seconds) => playback.Seek(seconds);
    public void Unload() => playback.Unload();

    public void Dispose()
    {
        if (disposed) return;
        playback.Dispose();
        disposed = true;
    }
}

internal interface IWavePlayback : IDisposable
{
    double DurationSeconds { get; }
    double PositionSeconds { get; }
    bool IsPlaying { get; }
    double Volume { get; set; }
    void Load(short[] pcm, int sampleRate, int channels);
    void Play(double startSeconds = 0);
    void Pause();
    void Resume();
    void Stop();
    void Seek(double seconds);
    void Unload();
}
