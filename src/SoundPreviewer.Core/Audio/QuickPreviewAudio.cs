namespace SoundPreviewer.Core.Audio;

public sealed record QuickPreviewAnalysis(bool Audible, int StartFrame, bool NeedsMoreAudio, int ClipEndFrame);

public static class QuickPreviewAudio
{
    public const double TargetSeconds = 0.5;
    public const double MaximumHoldSeconds = 4;
    public const double CaptureTailSeconds = 0.08;

    private const double WindowSeconds = 0.02;
    private const double MarginSeconds = 0.01;
    private const double MinimumSeconds = 0.1;
    private const double FadeSeconds = 0.005;
    private const double AudibleRms = 0.001;
    private const double AudiblePeak = 0.003;
    private const double QuietRms = 0.0001;
    private const double QuietPeak = 0.0003;

    public static QuickPreviewAnalysis Analyze(WaveData audio, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureShape(audio);
        int window = Frames(WindowSeconds, audio.SampleRate);
        int firstAudible = -1, firstNonQuiet = -1;
        for (int start = 0; start < audio.Frames; start += window)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var level = Measure(audio, start, Math.Min(start + window, audio.Frames));
            if (firstAudible < 0 && level.Rms >= AudibleRms && level.Peak >= AudiblePeak) firstAudible = start;
            if (firstNonQuiet < 0 && IsNonQuiet(level)) firstNonQuiet = start;
        }

        bool audible = firstAudible >= 0;
        int onset = audible ? firstAudible : firstNonQuiet;
        int clipStart = onset < 0 ? 0 : Math.Max(0, onset - Frames(MarginSeconds, audio.SampleRate));
        int target = Frames(TargetSeconds, audio.SampleRate);
        int clipEnd = (int)Math.Min(audio.Frames, (long)clipStart + target);
        bool needsMore = !audible || audio.Frames - clipStart < target;

        // Only the final quiet boundary is removed; gaps between audible events remain intact.
        if (firstNonQuiet >= 0)
        {
            int lastNonQuiet = clipStart;
            for (int start = clipStart; start < clipEnd; start += window)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int end = Math.Min(start + window, clipEnd);
                if (IsNonQuiet(Measure(audio, start, end))) lastNonQuiet = end;
            }
            int minimumEnd = clipStart + Frames(MinimumSeconds, audio.SampleRate);
            int paddedEnd = lastNonQuiet + Frames(MarginSeconds, audio.SampleRate);
            clipEnd = Math.Min(clipEnd, Math.Max(minimumEnd, paddedEnd));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new QuickPreviewAnalysis(audible, clipStart, needsMore, clipEnd);
    }

    public static WaveData Crop(WaveData audio, QuickPreviewAnalysis analysis, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureShape(audio);
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.StartFrame < 0 || analysis.ClipEndFrame <= analysis.StartFrame || analysis.ClipEndFrame > audio.Frames ||
            analysis.ClipEndFrame - analysis.StartFrame > Frames(TargetSeconds, audio.SampleRate))
            throw new InvalidDataException("Quick preview crop boundaries are invalid.");

        int frames = analysis.ClipEndFrame - analysis.StartFrame;
        var samples = new float[checked(frames * audio.Channels)];
        for (int frame = 0; frame < frames; frame++)
        {
            if ((frame & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (int channel = 0; channel < audio.Channels; channel++)
            {
                float sample = audio.Samples[(analysis.StartFrame + frame) * audio.Channels + channel];
                if (!float.IsFinite(sample)) throw new InvalidDataException("Quick preview audio contains non-finite samples.");
                samples[frame * audio.Channels + channel] = sample;
            }
        }
        int fade = Math.Min(Frames(FadeSeconds, audio.SampleRate), frames / 2);
        if (fade > 1)
        {
            for (int frame = 0; frame < fade; frame++)
            {
                float gain = frame / (float)(fade - 1);
                for (int channel = 0; channel < audio.Channels; channel++)
                {
                    if (analysis.StartFrame > 0) samples[frame * audio.Channels + channel] *= gain;
                    samples[(frames - frame - 1) * audio.Channels + channel] *= gain;
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new WaveData { SampleRate = audio.SampleRate, Channels = audio.Channels, Samples = samples };
    }

    private static (double Rms, double Peak) Measure(WaveData audio, int start, int end)
    {
        double squareSum = 0, peak = 0;
        for (int frame = start; frame < end; frame++)
        {
            for (int channel = 0; channel < audio.Channels; channel++)
            {
                float sample = audio.Samples[frame * audio.Channels + channel];
                if (!float.IsFinite(sample)) throw new InvalidDataException("Quick preview audio contains non-finite samples.");
                squareSum += sample * (double)sample;
                peak = Math.Max(peak, Math.Abs(sample));
            }
        }
        return (Math.Sqrt(squareSum / ((end - start) * (double)audio.Channels)), peak);
    }

    private static bool IsNonQuiet((double Rms, double Peak) level) => level.Rms >= QuietRms || level.Peak >= QuietPeak;

    private static int Frames(double seconds, int sampleRate) => Math.Max(1, (int)Math.Round(seconds * sampleRate));

    private static void EnsureShape(WaveData audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.Channels is < 1 or > 2 || audio.SampleRate is < 8000 or > 192000 ||
            audio.Samples.Length == 0 || audio.Samples.Length % audio.Channels != 0)
            throw new InvalidDataException("Quick preview audio shape is invalid.");
    }
}
