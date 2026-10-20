using System.Numerics;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Audio;

public sealed record InitAudioComparison(bool IsMatch, double WaveformSimilarity, double EnvelopeSimilarity,
    double SpectrumSimilarity, string Detail)
{
    public int MatchedReferences { get; init; }
    public double? EarlyWaveformSimilarity { get; init; }
}

public sealed class InitAudioSignature
{
    internal InitNoteSignature[] Notes { get; }
    internal InitAudioSignature[]? References { get; }
    internal InitNoteTolerance[]? Tolerances { get; }
    internal InitAudioSignature(InitNoteSignature[] notes, InitAudioSignature[]? references = null, InitNoteTolerance[]? tolerances = null)
    { Notes = notes; References = references; Tolerances = tolerances; }
}

internal sealed record InitNoteSignature(int Pitch, int Rate, int Channels, double Duration, double Rms,
    double StereoCorrelation, double[] Wave, double[] Envelope, double[] Spectrum);
internal sealed record InitNoteTolerance(double Wave, double Early, double Spectrum, double Envelope,
    double EnvelopeError, double LogGain, double StereoDelta);

public static class InitAudioComparer
{
    public const string PolicyVersion = Fingerprints.InitializationComparisonPolicy;
    private static readonly InitNoteTolerance HardLimits = new(0.90, 0.99, 0.985, 0.998, 0.05, Math.Log(1.06), 0.01);
    private const string StabilityFailureKey = "SoundPreviewer.InitializationBaselineUnstable";

    internal static bool IsStabilityFailure(Exception error) =>
        error is InvalidDataException && error.Data[StabilityFailureKey] is true;

    private static InvalidDataException StabilityFailure(string message)
    {
        var error = new InvalidDataException(message);
        error.Data[StabilityFailureKey] = true;
        return error;
    }

    private readonly record struct Features(double Gain, double Envelope, double EnvelopeError, double Spectrum,
        double StereoDelta, double Wave = 0, double Early = 0);

    public static InitAudioSignature CreateCalibratedSignature(IReadOnlyList<InitAudioSignature> references,
        CancellationToken cancellationToken = default)
    {
        if (references.Count != 3 || references.Any(reference => reference.References is not null))
            throw new InvalidDataException("默认启动校准需要三份独立原始参考，不能嵌套校准结果。");
        bool allowDrift = references.All(reference => reference.Notes.All(note => note.Duration >= 0.25));
        var pairs = new List<Features[]>();
        for (int first = 0; first < 3; first++)
            for (int second = first + 1; second < 3; second++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!allowDrift)
                {
                    if (!CompareSingle(references[first], references[second], null, cancellationToken).IsMatch)
                        throw StabilityFailure("默认启动基准不稳定：不足四分之一秒的参考仅支持原严格比对。");
                    continue;
                }
                var measured = new Features[3];
                for (int note = 0; note < 3; note++)
                {
                    var expected = references[first].Notes[note];
                    var actual = references[second].Notes[note];
                    if (!SameStructure(expected, actual)) throw new InvalidDataException("默认启动基准音区结构不一致。");
                    measured[note] = MeasureWaves(expected, actual, MeasureCheap(expected, actual), cancellationToken);
                    if (!Within(measured[note], HardLimits))
                        throw StabilityFailure($"默认启动基准不稳定：MIDI {expected.Pitch} 的三次参考超出固定校准边界 " +
                            $"(波形 {measured[note].Wave:F5}，起音 {measured[note].Early:F5}，频谱 {measured[note].Spectrum:F5}，包络 {measured[note].Envelope:F5}，" +
                            $"包络误差 {measured[note].EnvelopeError:F5}，增益比 {measured[note].Gain:F5}，立体声差 {measured[note].StereoDelta:F5})。");
                }
                pairs.Add(measured);
            }
        InitNoteTolerance[]? tolerances = allowDrift ? Enumerable.Range(0, 3).Select(note =>
        {
            var values = pairs.Select(pair => pair[note]).ToArray();
            // Three observations are not a statistical guarantee. Expansion remains inside immutable hard limits.
            return new InitNoteTolerance(
                Math.Max(HardLimits.Wave, 1 - 2 * (1 - values.Min(value => value.Wave)) - 0.003),
                Math.Max(HardLimits.Early, Math.Min(0.995, 1 - 2 * (1 - values.Min(value => value.Early)) - 0.001)),
                Math.Max(HardLimits.Spectrum, 1 - 2 * (1 - values.Min(value => value.Spectrum)) - 0.004),
                Math.Max(HardLimits.Envelope, 1 - 2 * (1 - values.Min(value => value.Envelope)) - 0.0002),
                Math.Min(HardLimits.EnvelopeError, 2 * values.Max(value => value.EnvelopeError) + 0.003),
                Math.Min(HardLimits.LogGain, 2 * values.Max(value => Math.Abs(Math.Log(value.Gain))) + 0.005),
                Math.Min(HardLimits.StereoDelta, 2 * values.Max(value => value.StereoDelta) + 0.0005));
        }).ToArray() : null;
        return new InitAudioSignature(references[0].Notes, references.ToArray(), tolerances);
    }

    public static InitAudioSignature CreateSignature(WaveData audio, IReadOnlyList<NotePreview> notes,
        CancellationToken cancellationToken = default)
    {
        if (notes.Count != 3) throw new InvalidDataException("初始化比对需要三个音区。");
        var signatures = new InitNoteSignature[3];
        for (int index = 0; index < 3; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var note = notes[index];
            int first = checked((int)Math.Round(note.StartSeconds * audio.SampleRate));
            int frames = checked((int)Math.Round(note.DurationSeconds * audio.SampleRate));
            if (first < 0 || frames < 32 || first > audio.Frames - frames)
                throw new InvalidDataException("初始化比对音区映射无效或太短。");
            signatures[index] = MakeNote(audio, first, frames, note.MidiNote, cancellationToken);
        }
        return new InitAudioSignature(signatures);
    }

    public static InitAudioComparison Compare(InitAudioSignature baseline, WaveData candidate,
        IReadOnlyList<NotePreview> notes, CancellationToken cancellationToken = default)
    {
        var actual = CreateSignature(candidate, notes, cancellationToken);
        if (baseline.References is null) return CompareSingle(baseline, actual, null, cancellationToken);
        var comparisons = baseline.References.Select(reference => CompareSingle(reference, actual, baseline.Tolerances, cancellationToken)).ToArray();
        var matches = comparisons.Where(comparison => comparison.IsMatch).ToArray();
        var closest = comparisons.OrderByDescending(comparison => comparison.WaveformSimilarity).First();
        return closest with { IsMatch = matches.Length >= 2, MatchedReferences = matches.Length,
            Detail = matches.Length >= 2
                ? $"三个音区整体匹配 {matches.Length}/3 份默认启动参考，拒绝保存默认或极近似声音。"
                : $"三个音区整体仅匹配 {matches.Length}/3 份默认启动参考，未达到拒绝条件。" };
    }

    private static InitAudioComparison CompareSingle(InitAudioSignature baseline, InitAudioSignature actual,
        InitNoteTolerance[]? tolerances, CancellationToken cancellationToken)
    {
        double minimumWave = 1, minimumEnvelope = 1, minimumSpectrum = 1;
        double? minimumEarly = null;
        bool strict = true, calibrated = tolerances is not null;
        for (int index = 0; index < 3; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = baseline.Notes[index];
            var observed = actual.Notes[index];
            if (!SameStructure(expected, observed))
                return new(false, 0, 0, 0, "音区时长、结构或有效声音与默认启动基准不同。");
            var features = MeasureCheap(expected, observed);
            minimumEnvelope = Math.Min(minimumEnvelope, features.Envelope);
            minimumSpectrum = Math.Min(minimumSpectrum, features.Spectrum);
            strict &= features.Gain is >= 0.94 and <= 1.06 && features.Envelope >= 0.998 && features.EnvelopeError <= 0.04 &&
                features.Spectrum >= 0.9995 && features.StereoDelta <= 0.03;
            calibrated &= tolerances is not null && WithinCheap(features, tolerances[index]);
            if (!strict && !calibrated)
                return new(false, 0, minimumEnvelope, minimumSpectrum, "响度、包络、频谱或立体声结构与默认启动基准不同。");
            // Only near-identical cheap features merit the bounded phase-lag search.
            double wave = BestCorrelation(expected.Wave, observed.Wave, expected.Pitch, expected.Rate, cancellationToken);
            minimumWave = Math.Min(minimumWave, wave);
            strict &= wave >= 0.995;
            calibrated &= tolerances is not null && wave >= tolerances[index].Wave;
            if (calibrated)
            {
                double early = EarlyCorrelation(expected, observed, cancellationToken);
                minimumEarly = Math.Min(minimumEarly ?? 1, early);
                calibrated &= early >= tolerances![index].Early;
            }
            if (!strict && !calibrated) return new(false, minimumWave, minimumEnvelope, minimumSpectrum, "完整波形或起音不符合默认启动校准范围。")
                { EarlyWaveformSimilarity = minimumEarly };
        }
        string detail = $"三音区最小波形相关 {minimumWave:F5}、包络相似 {minimumEnvelope:F5}、频谱相似 {minimumSpectrum:F5}。";
        return new(true, minimumWave, minimumEnvelope, minimumSpectrum, "三个音区均与默认启动基准相同或极近似。" + detail)
            { MatchedReferences = 1, EarlyWaveformSimilarity = minimumEarly };
    }

    private static bool SameStructure(InitNoteSignature expected, InitNoteSignature actual)
    {
        if (expected.Rms < 0.00001) throw new InvalidDataException("默认启动基准存在静音音区，不能用于拒绝音色。");
        return actual.Rms >= 0.00001 && expected.Pitch == actual.Pitch && expected.Rate == actual.Rate &&
            expected.Channels == actual.Channels && Math.Abs(expected.Duration - actual.Duration) <= 0.02;
    }

    private static Features MeasureCheap(InitNoteSignature expected, InitNoteSignature actual) => new(actual.Rms / expected.Rms,
        Cosine(expected.Envelope, actual.Envelope), expected.Envelope.Length == actual.Envelope.Length
            ? expected.Envelope.Zip(actual.Envelope, (left, right) => Math.Abs(left - right)).Average() : double.PositiveInfinity,
        Cosine(expected.Spectrum, actual.Spectrum), Math.Abs(expected.StereoCorrelation - actual.StereoCorrelation));

    private static Features MeasureWaves(InitNoteSignature expected, InitNoteSignature actual, Features cheap, CancellationToken token) =>
        cheap with { Wave = BestCorrelation(expected.Wave, actual.Wave, expected.Pitch, expected.Rate, token), Early = EarlyCorrelation(expected, actual, token) };

    private static double EarlyCorrelation(InitNoteSignature expected, InitNoteSignature actual, CancellationToken token)
    {
        double[] First(InitNoteSignature note)
        {
            var wave = note.Wave.Take(note.Rate / 10).ToArray();
            double mean = wave.Average();
            for (int index = 0; index < wave.Length; index++) wave[index] -= mean;
            return wave;
        }
        return BestCorrelation(First(expected), First(actual), expected.Pitch, expected.Rate, token);
    }

    private static bool WithinCheap(Features value, InitNoteTolerance bounds) => value.Gain is >= 0.94 and <= 1.06 &&
        Math.Abs(Math.Log(value.Gain)) <= bounds.LogGain && value.Envelope >= bounds.Envelope && value.EnvelopeError <= bounds.EnvelopeError &&
        value.Spectrum >= bounds.Spectrum && value.StereoDelta <= bounds.StereoDelta;

    private static bool Within(Features value, InitNoteTolerance bounds) => WithinCheap(value, bounds) && value.Wave >= bounds.Wave && value.Early >= bounds.Early;

    private static InitNoteSignature MakeNote(WaveData audio, int first, int frames, int pitch, CancellationToken token)
    {
        var channelEnergy = new double[audio.Channels];
        double stereoProduct = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            if ((frame & 4095) == 0) token.ThrowIfCancellationRequested();
            for (int channel = 0; channel < audio.Channels; channel++)
            {
                double value = audio.Samples[(first + frame) * audio.Channels + channel];
                if (!double.IsFinite(value)) throw new InvalidDataException("初始化比对音频包含非有限数值。");
                channelEnergy[channel] += value * value;
            }
            if (audio.Channels == 2)
                stereoProduct += audio.Samples[(first + frame) * 2] * (double)audio.Samples[(first + frame) * 2 + 1];
        }
        double rms = Math.Sqrt(channelEnergy.Sum() / (frames * (double)audio.Channels));
        int selected = audio.Channels == 2 && channelEnergy[1] > channelEnergy[0] ? 1 : 0;
        double stereo = audio.Channels == 1 ? 1 : stereoProduct / Math.Sqrt(Math.Max(1e-30, channelEnergy[0] * channelEnergy[1]));
        // Preserve high-note phase resolution; coarse downsampling can make identical oscillators look different.
        int stride = Math.Max(1, audio.SampleRate / 44100);
        int waveFrames = Math.Min(frames, audio.SampleRate / 2);
        var wave = new double[waveFrames / stride];
        for (int item = 0; item < wave.Length; item++)
        {
            if ((item & 4095) == 0) token.ThrowIfCancellationRequested();
            for (int offset = 0; offset < stride; offset++)
                wave[item] += audio.Samples[(first + item * stride + offset) * audio.Channels + selected] / (double)stride;
        }
        double mean = wave.Average();
        for (int item = 0; item < wave.Length; item++) wave[item] -= mean;
        double frequency = 440 * Math.Pow(2, (pitch - 69) / 12.0);
        int window = Math.Max(1, (int)Math.Round(Math.Max(2, Math.Ceiling(frequency * 0.04)) * audio.SampleRate / frequency));
        var envelope = new List<double>();
        for (int start = 0; start + window <= frames; start += window)
        {
            token.ThrowIfCancellationRequested();
            double energy = 0;
            for (int offset = 0; offset < window; offset++)
                for (int channel = 0; channel < audio.Channels; channel++)
                {
                    double sample = audio.Samples[(first + start + offset) * audio.Channels + channel];
                    energy += sample * sample;
                }
            envelope.Add(Math.Sqrt(energy / (window * (double)audio.Channels)) / Math.Max(rms, 1e-30));
        }
        if (envelope.Count == 0) envelope.Add(1);
        return new(pitch, audio.SampleRate / stride, audio.Channels, frames / (double)audio.SampleRate, rms,
            stereo, wave, envelope.ToArray(), Spectrum(audio, first, frames, selected, token));
    }

    private static double[] Spectrum(WaveData audio, int first, int frames, int channel, CancellationToken token)
    {
        const int size = 2048;
        var power = new double[size / 2];
        int windows = frames <= size ? 1 : Math.Min(8, (frames - size) / (size / 2) + 1);
        for (int window = 0; window < windows; window++)
        {
            token.ThrowIfCancellationRequested();
            int start = windows == 1 ? 0 : window * (frames - size) / (windows - 1);
            var fft = new Complex[size];
            for (int index = 0; index < size && start + index < frames; index++)
                fft[index] = new Complex(audio.Samples[(first + start + index) * audio.Channels + channel] *
                    (0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (size - 1))), 0);
            // Iterative radix-2 FFT over a Hann window; retain power, not phase.
            for (int i = 1, j = 0; i < size; i++)
            {
                int bit = size >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) (fft[i], fft[j]) = (fft[j], fft[i]);
            }
            for (int length = 2; length <= size; length <<= 1)
            {
                Complex step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
                for (int startBlock = 0; startBlock < size; startBlock += length)
                {
                    Complex multiplier = Complex.One;
                    for (int i = 0; i < length / 2; i++)
                    {
                        Complex even = fft[startBlock + i], odd = fft[startBlock + i + length / 2] * multiplier;
                        fft[startBlock + i] = even + odd;
                        fft[startBlock + i + length / 2] = even - odd;
                        multiplier *= step;
                    }
                }
            }
            for (int bin = 1; bin < power.Length; bin++) power[bin] += fft[bin].Magnitude * fft[bin].Magnitude;
        }
        return power;
    }

    private static double BestCorrelation(double[] expected, double[] actual, int pitch, int rate, CancellationToken token)
    {
        int length = Math.Min(expected.Length, actual.Length);
        int maximumLag = Math.Min(length / 4, (int)Math.Ceiling(rate / (440 * Math.Pow(2, (pitch - 69) / 12.0)) * 1.2));
        double best = 0;
        for (int lag = -maximumLag; lag <= maximumLag; lag++)
        {
            if ((lag & 15) == 0) token.ThrowIfCancellationRequested();
            int firstExpected = Math.Max(0, lag), firstActual = Math.Max(0, -lag), count = length - Math.Abs(lag);
            double dot = 0, left = 0, right = 0;
            for (int i = 0; i < count; i++)
            {
                double a = expected[firstExpected + i], b = actual[firstActual + i];
                dot += a * b; left += a * a; right += b * b;
            }
            best = Math.Max(best, Math.Abs(dot) / Math.Sqrt(Math.Max(1e-30, left * right)));
        }
        return Math.Min(1, best);
    }

    private static double Cosine(double[] left, double[] right)
    {
        if (left.Length != right.Length) return 0;
        double dot = 0, a = 0, b = 0;
        for (int index = 0; index < left.Length; index++)
        { dot += left[index] * right[index]; a += left[index] * left[index]; b += right[index] * right[index]; }
        return Math.Min(1, dot / Math.Sqrt(Math.Max(1e-60, a * b)));
    }
}
