using System.Reflection;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static class Program
{
    private static string _fixtureRoot = "";
    private static string TestExecutable => Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");

    public static async Task<int> Main(string[] args)
    {
        // This executable is a test fixture only. It is never shipped as a synthesizer adapter.
        if (args.Contains("--request", StringComparer.Ordinal)) return await FakeWorkerAsync(args);
        if (args.Length == 2 && args[0] == "--test-process") return await TestProcessAsync(args[1]);
        if (args.Length == 2 && args[0] == "--create-ui-fixture") return await CreateUiFixtureAsync(args[1]);

        string repository = FindRepository();
        _fixtureRoot = Path.Combine(repository, "artifacts", "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fixtureRoot);
        var tests = new List<(string Name, Func<Task> Run)>
        {
            ("Template validation and content/version/template cache keys", TestFingerprintsAsync),
            ("PCM round trip, silence, clipping and IEEE nonfinite validation", TestWaveAsync),
            ("WAV 8/24/32 bit decoding and malformed chunk rejection", TestWaveFormatsAsync),
            ("Concatenation and rate conversion preserve shape and duration", TestAudioTransformsAsync),
            ("Recursive scanning, duplicate identities and explicit source synth", TestScanIdentityAsync),
            ("Content hashing, feedback preservation and missing-source protection", TestIncrementalScanAsync),
            ("Vital banks retain internal identities and invalidate resource changes", TestBankScanningAsync),
            ("SQLite UTF8 round trip, transactions, settings and interrupted recovery", TestStorageAsync),
            ("Child process exit, bounded pipes, timeout and cancellation", TestProcessRunnerAsync),
            ("Worker protocol commits valid output and rejects bad results", TestWorkerAsync),
            ("Worker cancellation and timeout leave committed audio intact", TestWorkerInterruptionAsync),
            ("Queue cache reuse, changed-template regeneration and bounded retry", TestQueueAsync)
        };
        if (args.Contains("--test-playback", StringComparer.Ordinal))
            tests.Add(("Muted native playback, pause/seek/resume/reload and completion", TestNativePlaybackAsync));
        int failed = 0;
        try
        {
            foreach (var test in tests)
            {
                try
                {
                    await test.Run();
                    Console.WriteLine($"PASS {test.Name}");
                }
                catch (Exception error)
                {
                    failed++;
                    Console.Error.WriteLine($"FAIL {test.Name}\n{error}");
                }
            }
            Console.WriteLine($"{tests.Count - failed}/{tests.Count} checks passed. Synthetic audio/process fixtures test infrastructure only; plugin compatibility is not tested.");
            return failed == 0 ? 0 : 1;
        }
        finally
        {
            string allowed = Path.GetFullPath(Path.Combine(repository, "artifacts", "tests")) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(_fixtureRoot);
            if (target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(target).Length == 32)
                Directory.Delete(target, recursive: true);
        }
    }

    private static Task TestFingerprintsAsync()
    {
        var preset = Preset("cache");
        var plugin = new PluginConfiguration { Synth = SynthKind.Vital, Backend = RenderBackend.VitalCli, Version = "1.5.5" };
        var template = new PreviewTemplate();
        string original = Fingerprints.CacheKey(preset, plugin, template);
        Equal(original, Fingerprints.CacheKey(preset, plugin, template), "Cache key must be deterministic.");
        preset.Favorite = true;
        preset.Feedback = -1;
        Equal(original, Fingerprints.CacheKey(preset, plugin, template), "Feedback must not invalidate rendering.");
        preset.ContentHash = "changed";
        NotEqual(original, Fingerprints.CacheKey(preset, plugin, template), "Content bytes must invalidate rendering.");
        preset.ContentHash = "fixture";
        plugin.Version = "1.5.6";
        NotEqual(original, Fingerprints.CacheKey(preset, plugin, template), "Plugin version must invalidate rendering.");
        plugin.Version = "1.5.5";
        template.Velocity++;
        NotEqual(original, Fingerprints.CacheKey(preset, plugin, template), "Velocity must invalidate rendering.");
        template.Velocity--;
        template.TailSeconds++;
        NotEqual(original, Fingerprints.CacheKey(preset, plugin, template), "Release duration must invalidate rendering.");
        Throws<ArgumentException>(() => new PreviewTemplate { HoldSeconds = double.NaN }.Validate());
        Throws<ArgumentException>(() => new PreviewTemplate { LowNote = -1 }.Validate());
        Throws<ArgumentException>(() => new PreviewTemplate { SampleRate = 22050 }.Validate());
        Equal(Fingerprints.PresetId(SynthKind.Serum1, Path.Combine(_fixtureRoot, "same.fxp")),
            Fingerprints.PresetId(SynthKind.Serum1, Path.Combine(_fixtureRoot.ToUpperInvariant(), "SAME.FXP")), "Windows source IDs must ignore path case.");
        NotEqual(Fingerprints.PresetId(SynthKind.Serum1, Path.Combine(_fixtureRoot, "same.fxp")),
            Fingerprints.PresetId(SynthKind.Sylenth1, Path.Combine(_fixtureRoot, "same.fxp")), "Shared extension must not merge different synthesizers.");
        return Task.CompletedTask;
    }

    private static Task TestWaveAsync()
    {
        string directory = Fixture("wave");
        var tone = Tone(48000, 2, 0.2);
        string path = Path.Combine(directory, "tone.wav");
        WaveFile.WritePcm16(path, tone);
        var decoded = WaveFile.Read(path);
        Equal(48000, decoded.SampleRate, "PCM rate");
        Equal(2, decoded.Channels, "PCM channels");
        Equal(tone.Frames, decoded.Frames, "PCM frame count");
        Near(0.2, decoded.DurationSeconds, 0.00001, "PCM duration");
        for (int i = 0; i < decoded.Samples.Length; i++) Near(tone.Samples[i], decoded.Samples[i], 0.00004, "PCM quantization");
        var metrics = WaveFile.Validate(path);
        True(!metrics.IsSilent && !metrics.HasClipping && !metrics.HasNonFinite, "Normal tone must pass quality checks.");
        True(metrics.Peak > 0.49 && metrics.Rms > 0.3 && metrics.ZeroCrossingRate > 0, "Tone measurements must reflect its signal.");
        var silence = new WaveData { SampleRate = 44100, Channels = 1, Samples = new float[441] };
        True(WaveFile.Analyze(silence).IsSilent, "Silence is flagged for review.");
        var clipping = new WaveData { SampleRate = 44100, Channels = 1, Samples = [-1, 1, 0] };
        True(WaveFile.Analyze(clipping).HasClipping, "Full-scale signal is flagged for review.");
        var invalid = new WaveData { SampleRate = 44100, Channels = 1, Samples = [float.NaN, float.PositiveInfinity, 0.5f] };
        True(WaveFile.Analyze(invalid).HasNonFinite, "NaN and infinity are detected.");
        Throws<InvalidDataException>(() => WaveFile.WritePcm16(Path.Combine(directory, "invalid.wav"), invalid));
        string nonfinitePath = Path.Combine(directory, "nonfinite.wav");
        WriteRawWave(nonfinitePath, 3, 32, FloatBytes(float.NaN, float.PositiveInfinity, 0));
        True(float.IsNaN(WaveFile.Read(nonfinitePath).Samples[0]), "IEEE decoding preserves NaN for validation.");
        Throws<InvalidDataException>(() => WaveFile.Validate(nonfinitePath));
        Throws<IOException>(() => WaveFile.WritePcm16(path, tone));
        return Task.CompletedTask;
    }

    private static Task TestWaveFormatsAsync()
    {
        string directory = Fixture("formats");
        string eight = Path.Combine(directory, "8.wav");
        WriteRawWave(eight, 1, 8, [0, 128, 255, 64]);
        var eightData = WaveFile.Read(eight);
        Near(-1, eightData.Samples[0], 0, "Unsigned PCM silence offset");
        Near(0, eightData.Samples[1], 0, "Unsigned PCM midpoint");
        string twentyFour = Path.Combine(directory, "24.wav");
        WriteRawWave(twentyFour, 1, 24, [0, 0, 128, 0, 0, 0, 255, 255, 127, 0, 0, 64]);
        var twentyFourData = WaveFile.Read(twentyFour);
        Near(-1, twentyFourData.Samples[0], 0, "PCM24 sign extension");
        Near(0.5, twentyFourData.Samples[3], 0, "PCM24 decode");
        string thirtyTwo = Path.Combine(directory, "32.wav");
        byte[] pcm32 = BitConverter.GetBytes(int.MinValue).Concat(BitConverter.GetBytes(int.MaxValue)).ToArray();
        WriteRawWave(thirtyTwo, 1, 32, pcm32);
        Near(-1, WaveFile.Read(thirtyTwo).Samples[0], 0, "PCM32 sign");
        string junk = Path.Combine(directory, "odd-junk.wav");
        WriteRawWave(junk, 1, 16, [0, 0, 0, 0], oddJunk: true);
        Equal(2, WaveFile.Read(junk).Frames, "Odd chunks must respect padding.");
        string truncated = Path.Combine(directory, "truncated.wav");
        File.WriteAllBytes(truncated, File.ReadAllBytes(junk)[..^1]);
        Throws<InvalidDataException>(() => WaveFile.Read(truncated));
        string alignment = Path.Combine(directory, "unaligned.wav");
        WriteRawWave(alignment, 1, 16, [1, 2, 3]);
        Throws<InvalidDataException>(() => WaveFile.Read(alignment));
        string unsupported = Path.Combine(directory, "unsupported.wav");
        WriteRawWave(unsupported, 6, 16, [0, 0]);
        Throws<InvalidDataException>(() => WaveFile.Read(unsupported));
        return Task.CompletedTask;
    }

    private static Task TestAudioTransformsAsync()
    {
        var a = Tone(44100, 2, 0.1);
        var b = Tone(44100, 2, 0.2);
        var joined = WaveFile.Concatenate([a, b]);
        Equal(a.Frames + b.Frames, joined.Frames, "Concatenation frame count");
        Near(0.3, joined.DurationSeconds, 0.00001, "Concatenation duration");
        Equal(b.Samples[0], joined.Samples[a.Samples.Length], "Boundary must preserve segment samples.");
        var converted = WaveFile.Resample(joined, 48000);
        Equal(2, converted.Channels, "Resampling channels");
        Equal(48000, converted.SampleRate, "Resampling rate");
        Near(joined.DurationSeconds, converted.DurationSeconds, 1 / 48000.0, "Resampling duration");
        True(converted.Samples.All(float.IsFinite), "Resampling must produce finite audio.");
        Near(WaveFile.Analyze(joined).Rms, WaveFile.Analyze(converted).Rms, 0.005, "Resampling tone energy");
        Throws<InvalidDataException>(() => WaveFile.Concatenate([a, Tone(48000, 2, 0.1)]));
        Throws<InvalidDataException>(() => WaveFile.Analyze(new WaveData { SampleRate = 44100, Channels = 2, Samples = [0] }));
        return Task.CompletedTask;
    }

    private static async Task TestScanIdentityAsync()
    {
        string directory = Fixture("scan-identities");
        string nested = Path.Combine(directory, "Pack A");
        Directory.CreateDirectory(nested);
        WriteFxp(Path.Combine(directory, "Same.fxp"), 1);
        WriteFxp(Path.Combine(nested, "Same.fxp"), 2);
        WriteFxp(Path.Combine(directory, "TotalLength.fxp"), 3, totalLength: true);
        File.WriteAllText(Path.Combine(directory, "Bank.fxb"), "test-only bank, not a real bank");
        File.WriteAllText(Path.Combine(directory, "ignore.txt"), "not a preset");
        var sources = new[]
        {
            new SourceFolder { Id = "serum", Path = directory, Synth = SynthKind.Serum1 },
            new SourceFolder { Id = "sylenth", Path = directory, Synth = SynthKind.Sylenth1 }
        };
        var result = await new ScanService().ScanAsync(sources, [], CancellationToken.None);
        Equal(4, result.Presets.Count(p => p.Name == "Same"), "Duplicate names and common extensions must retain source synth.");
        Equal(result.Presets.Count, result.Presets.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count(), "All scanner identities must be unique.");
        True(result.Presets.Where(p => p.SourcePath.EndsWith(".fxb", StringComparison.OrdinalIgnoreCase)).All(p => p.Status == PresetStatus.Unsupported), "Unverified banks must be explicit Unsupported records.");
        True(result.Presets.Any(p => p.Synth == SynthKind.Sylenth1) && result.Presets.Any(p => p.Synth == SynthKind.Serum1), "Synth type comes from source configuration.");
        True(result.Presets.Where(p => p.Name == "Same").All(p => p.Status == PresetStatus.Pending), "Valid VST envelopes are accepted as individual presets.");
        True(result.Presets.Where(p => p.Name == "TotalLength").All(p => p.Status == PresetStatus.Pending && p.Message.Length > 0), "Observed total-length FXP variant remains loadable with a diagnostic.");
        result.Presets.Single(p => p.Name == "TotalLength" && p.Synth == SynthKind.Serum1).Status = PresetStatus.Unsupported;
        var accepted = await new ScanService().ScanAsync(sources, result.Presets, CancellationToken.None);
        Equal(PresetStatus.Pending, accepted.Presets.Single(p => p.Name == "TotalLength" && p.Synth == SynthKind.Serum1).Status, "Same-hash unsupported record is revalidated after compatibility rules improve.");
    }

    private static async Task TestIncrementalScanAsync()
    {
        string directory = Fixture("scan-incremental");
        string path = Path.Combine(directory, "Sound.vital");
        File.WriteAllText(path, "{\"preset_name\":\"one\",\"settings\":{}}");
        var source = new SourceFolder { Id = "vital", Path = directory, Synth = SynthKind.Vital };
        var scanner = new ScanService();
        var first = await scanner.ScanAsync([source], [], CancellationToken.None);
        Equal(1, first.Added, "Initial scan adds preset.");
        var preset = first.Presets.Single();
        string originalHash = preset.ContentHash;
        DateTime write = File.GetLastWriteTimeUtc(path);
        preset.Favorite = true;
        preset.Feedback = 1;
        preset.FeedbackUtc = DateTime.UtcNow;
        preset.Status = PresetStatus.Ready;
        preset.Attempts = 2;
        preset.CacheKey = "old-cache";
        preset.PreviewPath = Path.Combine(directory, "old.wav");
        var unchanged = await scanner.ScanAsync([source], [preset], CancellationToken.None);
        Equal(0, unchanged.Changed, "Unchanged content does not invalidate preview.");
        Equal(PresetStatus.Ready, unchanged.Presets.Single().Status, "Unchanged preview remains Ready.");
        File.WriteAllText(path, "{\"preset_name\":\"two\",\"settings\":{}}");
        File.SetLastWriteTimeUtc(path, write);
        var changed = await scanner.ScanAsync([source], unchanged.Presets, CancellationToken.None);
        Equal(1, changed.Changed, "Changed bytes with same size and timestamp are detected.");
        var updated = changed.Presets.Single();
        NotEqual(originalHash, updated.ContentHash, "Content hash changes.");
        True(updated.Favorite && updated.Feedback == 1 && updated.FeedbackUtc is not null, "Personal feedback survives invalidation.");
        Equal(PresetStatus.Pending, updated.Status, "Changed content is queued.");
        Equal(0, updated.Attempts, "Changed content receives fresh attempts.");
        Equal("", updated.CacheKey, "Changed content clears committed cache key.");
        string unavailable = Path.Combine(directory, "offline-root");
        source.Path = unavailable;
        var incomplete = await scanner.ScanAsync([source], changed.Presets, CancellationToken.None);
        Equal(PresetStatus.Pending, incomplete.Presets.Single().Status, "Inaccessible root must not declare a preset missing.");
        True(incomplete.Warnings.Count > 0, "Incomplete scan must report a warning.");
        source.Path = directory;
        File.Delete(path);
        var missing = await scanner.ScanAsync([source], changed.Presets, CancellationToken.None);
        Equal(PresetStatus.Missing, missing.Presets.Single().Status, "Successful complete scan records removed files.");
        var removed = missing.Presets.Single();
        removed.PreviewPath = Path.Combine(directory, "previous.wav");
        WaveFile.WritePcm16(removed.PreviewPath, Tone(44100, 1, 0.1));
        File.WriteAllText(path, "{\"preset_name\":\"two\",\"settings\":{}}");
        var reappeared = await scanner.ScanAsync([source], missing.Presets, CancellationToken.None);
        Equal(PresetStatus.NeedsReview, reappeared.Presets.Single().Status, "Reappearing preset with old audio requires renewed confirmation.");
    }

    private static async Task TestBankScanningAsync()
    {
        string directory = Fixture("banks");
        string path = Path.Combine(directory, "Pack.vitalbank");
        var source = new SourceFolder { Id = "bank", Path = directory, Synth = SynthKind.Vital };
        var scanner = new ScanService();
        WriteBank(path, "resource-a");
        var first = await scanner.ScanAsync([source], [], CancellationToken.None);
        Equal(2, first.Presets.Count, "Vital archive enumerates individual presets.");
        Equal(2, first.Presets.Select(p => p.Id).Distinct().Count(), "Same bank preset names retain distinct internal paths.");
        True(first.Presets.All(p => p.Status == PresetStatus.Unsupported && p.InternalPath.Length > 0), "Unverified bank render support stays explicit.");
        first.Presets[0].Favorite = true;
        var hashes = first.Presets.ToDictionary(p => p.Id, p => p.ContentHash);
        WriteBank(path, "resource-b");
        var changed = await scanner.ScanAsync([source], first.Presets, CancellationToken.None);
        Equal(2, changed.Changed, "A bank resource change invalidates all affected archive records.");
        True(changed.Presets.All(p => p.ContentHash != hashes[p.Id]), "Bank hashes include dependency resource bytes.");
        True(changed.Presets.Single(p => p.Id == first.Presets[0].Id).Favorite, "Bank rescans retain feedback.");
        WriteBank(path, "unsafe", unsafePath: true);
        var unsafeBank = await scanner.ScanAsync([source], changed.Presets, CancellationToken.None);
        True(unsafeBank.Warnings.Count > 0, "Archive traversal is rejected visibly.");
        True(unsafeBank.Presets.All(p => p.Status != PresetStatus.Missing), "Unreadable replacement bank cannot declare old entries missing.");
        True(!File.Exists(Path.Combine(directory, "evil.vital")), "Scanner never extracts archive paths.");
    }

    private static Task TestStorageAsync()
    {
        string directory = Fixture("storage");
        string database = Path.Combine(directory, "library.sqlite");
        var preset = Preset("O'Brien \"音色\"; DROP TABLE presets; --");
        preset.Status = PresetStatus.Rendering;
        preset.Attempts = 1;
        preset.Favorite = true;
        preset.Feedback = -1;
        preset.FeedbackUtc = DateTime.UtcNow;
        preset.SourcePath = Path.Combine(directory, "中文 O'Brien.vital");
        var settings = new AppSettings
        {
            Sources = [new SourceFolder { Path = directory, Synth = SynthKind.Vital }],
            PreviewDirectory = Path.Combine(directory, "试听 ' Audio"),
            PlaybackVolume = 0.42,
            Template = new PreviewTemplate { MiddleNote = 65 }
        };
        {
            var store = new LibraryStore(database);
            store.Initialize();
            store.Upsert(preset);
            store.SaveSettings(settings);
            var loaded = store.LoadPresets().Single();
            Equal(preset.Name, loaded.Name, "Bound UTF8 record names must round trip.");
            Equal(preset.SourcePath, loaded.SourcePath, "Bound UTF8 paths must round trip.");
            True(loaded.Favorite && loaded.Feedback == -1, "Feedback persists.");
            var replacement = Preset("updated");
            replacement.Id = preset.Id;
            replacement.Status = PresetStatus.Rendering;
            replacement.Attempts = 1;
            store.UpsertMany([replacement, Preset("second")]);
            Equal(2, store.LoadPresets().Count, "Upsert replaces identity without duplication.");
            Equal("updated", store.LoadPresets().Single(p => p.Id == preset.Id).Name, "Upsert persists replacement.");
            var tentative = Preset("must-roll-back");
            tentative.Id = preset.Id;
            var invalid = Preset("invalid");
            invalid.Id = "";
            Throws<ArgumentException>(() => store.UpsertMany([tentative, invalid]));
            Equal("updated", store.LoadPresets().Single(p => p.Id == preset.Id).Name, "A batch transaction must roll back all writes after an error.");
        }
        {
            var reopened = new LibraryStore(database);
            reopened.Initialize();
            var loadedSettings = reopened.LoadSettings();
            Equal(settings.PreviewDirectory, loadedSettings.PreviewDirectory, "Settings persist escaped path.");
            Equal(65, loadedSettings.Template.MiddleNote, "Template persists.");
            Near(0.42, loadedSettings.PlaybackVolume, 0, "Volume persists.");
            reopened.RecoverInterrupted();
            Equal(PresetStatus.Pending, reopened.LoadPresets().Single(p => p.Id == preset.Id).Status, "Interrupted work becomes resumable.");
            Equal(1, reopened.LoadPresets().Single(p => p.Id == preset.Id).Attempts, "Recovery preserves recorded attempt history.");
        }
        True(File.ReadAllBytes(database).AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8), "Storage must be a real SQLite database.");
        return Task.CompletedTask;
    }

    private static async Task TestProcessRunnerAsync()
    {
        var output = await ProcessRunner.RunAsync(TestExecutable, ["--test-process", "flood"], Fixture("process"), TimeSpan.FromSeconds(10));
        Equal(17, output.ExitCode, "Nonzero child exit must be preserved.");
        Equal(16000, output.StandardOutput.Length, "Stdout must be bounded while fully drained.");
        Equal(16000, output.StandardError.Length, "Stderr must be bounded while fully drained.");
        await ThrowsAsync<TimeoutException>(() => ProcessRunner.RunAsync(TestExecutable, ["--test-process", "wait"], _fixtureRoot, TimeSpan.FromMilliseconds(150)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await ThrowsAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(TestExecutable, ["--test-process", "wait"], _fixtureRoot, TimeSpan.FromSeconds(10), cancellation.Token));
    }

    private static async Task TestWorkerAsync()
    {
        string directory = Fixture("worker 中文 & ' spaces");
        var request = Request("success", Path.Combine(directory, "preview.wav"));
        var client = new WorkerProcessClient();
        var result = await client.RenderAsync(request, TestExecutable, CancellationToken.None);
        True(result.Success, $"Valid test worker must succeed: {result.Message}");
        Equal(request.OutputPath, result.OutputPath, "Output must commit to requested final path.");
        True(File.Exists(request.OutputPath) && result.Metrics is not null && result.Notes.Count == 3, "Commit contains validated audio and mapping.");
        True(!Directory.EnumerateDirectories(directory, ".soundpreviewer-*").Any(), "Success staging must be removed.");
        byte[] committed = File.ReadAllBytes(request.OutputPath);
        foreach (string mode in new[] { "failure", "wrongpath", "nonfinite", "badnotes", "nullnotes", "nullnote", "crashafterresult", "noresult" })
        {
            request.Preset.Name = mode;
            var rejected = await client.RenderAsync(request, TestExecutable, CancellationToken.None);
            True(!rejected.Success && !string.IsNullOrWhiteSpace(rejected.Message), $"{mode} must fail with an actionable reason.");
            True(File.ReadAllBytes(request.OutputPath).SequenceEqual(committed), $"{mode} must leave previously committed audio intact.");
            True(!Directory.EnumerateDirectories(directory, ".soundpreviewer-*").Any(), $"{mode} staging must be removed.");
        }
        request.Preset.Name = "silence";
        var silence = await client.RenderAsync(request, TestExecutable, CancellationToken.None);
        True(silence.Success && silence.NeedsReview, "Silent valid output is retained with review state.");
        request.Preset.Name = "clipping";
        var clipping = await client.RenderAsync(request, TestExecutable, CancellationToken.None);
        True(clipping.Success && clipping.NeedsReview, "Clipping valid output is retained with review state.");
        request.Preset.Name = "one-silent-note";
        var silentNote = await client.RenderAsync(request, TestExecutable, CancellationToken.None);
        True(silentNote.Success && silentNote.NeedsReview && silentNote.Notes[1].Metrics?.IsSilent == true, "Individual silent pitch is detected even when aggregate audio is not silent.");
    }

    private static async Task TestWorkerInterruptionAsync()
    {
        string directory = Fixture("worker-interruption");
        var request = Request("wait", Path.Combine(directory, "existing.wav"));
        WaveFile.WritePcm16(request.OutputPath, Tone(44100, 1, 0.1));
        byte[] committed = File.ReadAllBytes(request.OutputPath);
        var client = new WorkerProcessClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await ThrowsAsync<OperationCanceledException>(() => client.RenderAsync(request, TestExecutable, cancellation.Token));
        True(File.ReadAllBytes(request.OutputPath).SequenceEqual(committed), "Cancellation preserves previous committed file.");
        request.Plugin.TimeoutSeconds = 5;
        var timeout = await client.RenderAsync(request, TestExecutable, CancellationToken.None);
        True(!timeout.Success && !string.IsNullOrWhiteSpace(timeout.Message), "Worker timeout returns visible failure.");
        True(File.ReadAllBytes(request.OutputPath).SequenceEqual(committed), "Timeout preserves previous committed file.");
        True(!Directory.EnumerateDirectories(directory, ".soundpreviewer-*").Any(), "Interrupted staging must be removed.");
    }

    private static async Task TestQueueAsync()
    {
        string directory = Fixture("queue");
        var store = new LibraryStore(Path.Combine(directory, "library.sqlite"));
        store.Initialize();
        var settings = new AppSettings
        {
            PreviewDirectory = Path.Combine(directory, "previews"),
            MaxAttempts = 1,
            Template = new PreviewTemplate { HoldSeconds = 0.1, TailSeconds = 0.02 },
            Plugins = [new PluginConfiguration { Synth = SynthKind.Vital, Backend = RenderBackend.ExternalWorker, ExecutablePath = TestExecutable, Version = "test-only", TimeoutSeconds = 5 }]
        };
        var preset = Preset("success");
        var queue = new RenderQueue(store);
        var progress = new InlineProgress();
        await queue.RunAsync(settings, [preset], TestExecutable, progress, CancellationToken.None);
        Equal(PresetStatus.Ready, preset.Status, $"Valid worker completes queue: {preset.Message}");
        string firstKey = preset.CacheKey;
        string firstPath = preset.PreviewPath;
        DateTime written = File.GetLastWriteTimeUtc(firstPath);
        await queue.RunAsync(settings, [preset], TestExecutable, progress, CancellationToken.None);
        Equal(written, File.GetLastWriteTimeUtc(firstPath), "Valid cache must not be rewritten.");
        Equal(1, preset.Attempts, "Valid cache must not consume attempts.");
        File.Delete(firstPath);
        await queue.RunAsync(settings, [preset], TestExecutable, progress, CancellationToken.None);
        True(File.Exists(preset.PreviewPath), "Deleted cache file is regenerated during the current session.");
        preset.Notes[1].MidiNote = 127;
        await queue.RunAsync(settings, [preset], TestExecutable, progress, CancellationToken.None);
        Equal(settings.Template.MiddleNote, preset.Notes[1].MidiNote, "Invalid persisted note map cannot be reused as a valid cache.");
        settings.Template.Velocity++;
        await queue.RunAsync(settings, [preset], TestExecutable, progress, CancellationToken.None);
        Equal(PresetStatus.Ready, preset.Status, "Template changes rerender successful records even after old retry limit.");
        NotEqual(firstKey, preset.CacheKey, "Template change commits fresh cache key.");
        preset.Name = "failure";
        settings.Template.Velocity++;
        await queue.RunAsync(settings, [preset], TestExecutable, progress, CancellationToken.None);
        Equal(PresetStatus.Failed, preset.Status, "A failed regeneration must remain visible.");
        int renderedAfterFailure = progress.RenderingCalls;
        await queue.RunAsync(settings, [preset], TestExecutable, progress, CancellationToken.None);
        Equal(renderedAfterFailure, progress.RenderingCalls, "A failed changed-template render cannot reset its retry budget on every queue run.");
        var failure = Preset("failure");
        await queue.RunAsync(settings, [failure], TestExecutable, progress, CancellationToken.None);
        Equal(PresetStatus.Failed, failure.Status, "Worker failure is recorded.");
        Equal(1, failure.Attempts, "Retry limit is enforced.");
        await queue.RunAsync(settings, [failure], TestExecutable, progress, CancellationToken.None);
        Equal(1, failure.Attempts, "A repeated queue run cannot retry forever.");
        var invalidOutput = Preset("badnotes");
        var nextValid = Preset("success");
        await queue.RunAsync(settings, [invalidOutput, nextValid], TestExecutable, progress, CancellationToken.None);
        Equal(PresetStatus.Failed, invalidOutput.Status, "Malformed worker output is contained as one failed task.");
        Equal(PresetStatus.Ready, nextValid.Status, "Later tasks continue after malformed worker output.");
        var recovered = Preset("success");
        recovered.Status = PresetStatus.Pending;
        recovered.Attempts = settings.MaxAttempts;
        await queue.RunAsync(settings, [recovered], TestExecutable, progress, CancellationToken.None);
        Equal(PresetStatus.Ready, recovered.Status, "Recovered interrupted task receives a resumable attempt at prior limit.");
        var waiting = Preset("wait");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await ThrowsAsync<OperationCanceledException>(() => queue.RunAsync(settings, [waiting], TestExecutable, progress, cancellation.Token));
        Equal(PresetStatus.Pending, waiting.Status, "Queue cancellation returns current task to pending.");
        Equal(0, waiting.Attempts, "User pause does not count as a failed attempt.");
        Equal(PresetStatus.Pending, store.LoadPresets().Single(p => p.Id == waiting.Id).Status, "Pause state is durable.");
    }

    private static async Task<int> FakeWorkerAsync(string[] args)
    {
        string requestPath = Argument(args, "--request");
        string resultPath = Argument(args, "--result");
        var request = JsonSerializer.Deserialize<RenderRequest>(await File.ReadAllTextAsync(requestPath), LibraryJson.Options)!;
        string mode = request.Preset.Name;
        if (mode == "wait") { await Task.Delay(TimeSpan.FromSeconds(30)); return 0; }
        if (mode == "noresult") return 9;
        if (mode == "failure")
        {
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new RenderResult { Message = "Test-only intentional failure." }, LibraryJson.Options));
            return 1;
        }
        double duration = request.Template.HoldSeconds + request.Template.TailSeconds;
        int frames = (int)Math.Round(request.Template.SampleRate * duration);
        var audio = Tone(request.Template.SampleRate, 1, frames * 3 / (double)request.Template.SampleRate);
        if (mode == "silence") Array.Clear(audio.Samples);
        if (mode == "clipping") Array.Fill(audio.Samples, 1f);
        if (mode == "one-silent-note") Array.Clear(audio.Samples, frames, frames);
        if (mode == "nonfinite") WriteRawWave(request.OutputPath, 3, 32, FloatBytes(float.NaN, 0, 0));
        else WaveFile.WritePcm16(request.OutputPath, audio);
        var result = new RenderResult
        {
            Success = true,
            OutputPath = mode == "wrongpath" ? Path.Combine(Path.GetDirectoryName(request.OutputPath)!, "other.wav") : request.OutputPath,
            AdapterVersion = "test-fixture-v1",
            Message = "Test-only PCM fixture; no synthesizer was loaded.",
            Notes = request.Template.Pitches.Select((pitch, index) => new NotePreview
            {
                MidiNote = pitch,
                StartSeconds = index * frames / (double)request.Template.SampleRate,
                DurationSeconds = frames / (double)request.Template.SampleRate,
                ActualHoldSeconds = request.Template.HoldSeconds,
                ActualTailSeconds = frames / (double)request.Template.SampleRate - request.Template.HoldSeconds,
                ActualVelocity = request.Template.Velocity
            }).ToList()
        };
        if (mode == "badnotes") result.Notes[1].MidiNote = 127;
        if (mode == "nullnotes") result.Notes = null!;
        if (mode == "nullnote") result.Notes[1] = null!;
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, LibraryJson.Options));
        return mode == "crashafterresult" ? 7 : 0;
    }

    private static async Task TestNativePlaybackAsync()
    {
        string path = Path.Combine(Fixture("native-playback"), "test-only-float32.wav");
        var tone = Tone(44100, 1, 0.8);
        tone.Samples[0] = 1.5f;
        WriteRawWave(path, 3, 32, FloatBytes(tone.Samples));
        byte[] original = File.ReadAllBytes(path);
        var audio = WaveFile.Read(path);
        using var player = new WavePlayer { Volume = 0 };
        Throws<InvalidOperationException>(() => player.Resume());
        Throws<ArgumentOutOfRangeException>(() => player.Volume = double.NaN);
        player.Load(audio);
        Near(0.8, player.DurationSeconds, 1 / 44100.0, "Native float input duration");
        True(!player.IsPlaying, "Loading audio must not begin playback.");
        player.Play();
        await Task.Delay(250);
        True(player.PositionSeconds > 0.05 && player.IsPlaying, "Muted native playback position advances.");
        player.Pause();
        double paused = player.PositionSeconds;
        await Task.Delay(100);
        Near(paused, player.PositionSeconds, 0.05, "Paused device position stays stable.");
        True(!player.IsPlaying, "Pause stops playback progress.");
        player.Seek(0.4);
        True(!player.IsPlaying, "Seeking while paused retains paused state.");
        await Task.Delay(100);
        Near(0.4, player.PositionSeconds, 0.05, "Paused seek keeps requested position.");
        player.Resume();
        await Task.Delay(120);
        True(player.PositionSeconds > 0.45 && player.IsPlaying, "Resume advances after a paused seek.");
        player.Stop();
        Near(0, player.PositionSeconds, 0, "Stop resets position.");
        True(!player.IsPlaying, "Stop releases active playback.");
        player.Play(player.DurationSeconds);
        True(!player.IsPlaying, "Play at the exact end does not queue an empty driver buffer.");
        player.Resume();
        await Task.Delay(100);
        True(player.PositionSeconds > 0 && player.PositionSeconds < player.DurationSeconds, "Resume after completion restarts audio.");
        player.Unload();
        Near(0, player.DurationSeconds, 0, "Unload clears duration.");
        player.Load(audio);
        player.Seek(0.7);
        player.Resume();
        await Task.Delay(450);
        True(!player.IsPlaying, "Driver completion ends playback.");
        Near(player.DurationSeconds, player.PositionSeconds, 0.01, "Completion reports the final sample position.");
        True(File.ReadAllBytes(path).SequenceEqual(original), "PCM16 audition must leave original float32 bytes unchanged.");
        player.Dispose();
        Throws<ObjectDisposedException>(() => player.Load(audio));
    }

    private static async Task<int> CreateUiFixtureAsync(string requestedDirectory)
    {
        string repository = FindRepository();
        string artifacts = Path.GetFullPath(Path.Combine(repository, "artifacts")) + Path.DirectorySeparatorChar;
        string directory = Path.GetFullPath(requestedDirectory);
        if (!directory.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("UI fixture must stay in this repository's artifacts directory.");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "TEST-ONLY-440Hz.vital");
        string preview = Path.Combine(directory, "TEST-ONLY-440Hz-float32.wav");
        var template = new PreviewTemplate { HoldSeconds = 1, TailSeconds = 0.3 };
        var audio = Tone(44100, 1, (template.HoldSeconds + template.TailSeconds) * 3);
        WriteRawWave(preview, 3, 32, FloatBytes(audio.Samples));
        await File.WriteAllTextAsync(source, "{\"testOnly\":true,\"preset_name\":\"TEST AUDIO: 440Hz\",\"settings\":{}}");
        var settings = new AppSettings { PreviewDirectory = directory, Template = template };
        var preset = new PresetRecord
        {
            Id = Fingerprints.PresetId(SynthKind.Vital, source), Name = "TEST AUDIO: 440Hz", Synth = SynthKind.Vital,
            SourceId = "test-only-ui-fixture", SourcePath = source, ContentHash = Fingerprints.HashText("test-only"),
            Package = "TEST ONLY / no synthesizer loaded", Status = PresetStatus.Ready,
            PreviewPath = preview, Metrics = WaveFile.Validate(preview), AdapterVersion = "test-only-ui-fixture-v1",
            PluginVersion = "no synthesizer loaded", RenderedUtc = DateTime.UtcNow,
            Message = "TEST ONLY: generated 440Hz IEEE float32 PCM; no plugin compatibility claim.",
            TemplateJson = JsonSerializer.Serialize(template, LibraryJson.Options),
            Notes = template.Pitches.Select((pitch, index) => new NotePreview
            {
                MidiNote = pitch, StartSeconds = index * 1.3, DurationSeconds = 1.3,
                ActualHoldSeconds = 1, ActualTailSeconds = 0.3, ActualVelocity = template.Velocity
            }).ToList()
        };
        preset.CacheKey = Fingerprints.CacheKey(preset, settings.Plugins.Single(p => p.Synth == SynthKind.Vital), template);
        var store = new LibraryStore(Path.Combine(directory, "library.db"));
        store.Initialize();
        store.SaveSettings(settings);
        store.Upsert(preset);
        Console.WriteLine($"Created TEST ONLY UI fixture: {directory}");
        return 0;
    }

    private static async Task<int> TestProcessAsync(string mode)
    {
        if (mode == "wait") { await Task.Delay(TimeSpan.FromSeconds(30)); return 0; }
        Console.Out.Write(new string('o', 40000));
        Console.Error.Write(new string('e', 40000));
        return 17;
    }

    private static RenderRequest Request(string mode, string output) => new()
    {
        Preset = Preset(mode),
        Plugin = new PluginConfiguration { Synth = SynthKind.Vital, Backend = RenderBackend.ExternalWorker, Version = "test-only", TimeoutSeconds = 5 },
        Template = new PreviewTemplate { HoldSeconds = 0.1, TailSeconds = 0.02 },
        OutputPath = output,
        CacheKey = "test-only-cache"
    };

    private static PresetRecord Preset(string name) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Name = name, Synth = SynthKind.Vital,
        ContentHash = "fixture", SourcePath = Path.Combine(_fixtureRoot, name + ".vital")
    };

    private static WaveData Tone(int rate, int channels, double duration)
    {
        int frames = (int)Math.Round(rate * duration);
        float[] samples = new float[frames * channels];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++) samples[i * channels + c] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / rate));
        return new WaveData { SampleRate = rate, Channels = channels, Samples = samples };
    }

    private static void WriteRawWave(string path, ushort format, ushort bits, byte[] data, bool oddJunk = false)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        int padding = data.Length & 1;
        writer.Write("RIFF"u8); writer.Write(36 + data.Length + padding + (oddJunk ? 10 : 0) + (format == 3 ? 12 : 0)); writer.Write("WAVE"u8);
        if (oddJunk) { writer.Write("JUNK"u8); writer.Write(1); writer.Write((byte)0); writer.Write((byte)0); }
        writer.Write("fmt "u8); writer.Write(16); writer.Write(format); writer.Write((ushort)1); writer.Write(44100);
        writer.Write(44100 * (bits / 8)); writer.Write((ushort)(bits / 8)); writer.Write(bits);
        if (format == 3) { writer.Write("fact"u8); writer.Write(4); writer.Write(data.Length / (bits / 8)); }
        writer.Write("data"u8); writer.Write(data.Length); writer.Write(data);
        if (padding != 0) writer.Write((byte)0);
    }

    private static byte[] FloatBytes(params float[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();
    private static void WriteFxp(string path, byte payload, bool totalLength = false)
    {
        byte[] bytes = new byte[60];
        "CcnK"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), totalLength ? 60u : 52u);
        "FPCh"u8.CopyTo(bytes.AsSpan(8));
        bytes[^1] = payload;
        File.WriteAllBytes(path, bytes);
    }
    private static void WriteBank(string path, string resource, bool unsafePath = false)
    {
        using var archive = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        foreach (string internalPath in unsafePath ? new[] { "../evil.vital" } : new[] { "Presets/A/Same.vital", "Presets/B/Same.vital" })
        {
            var entry = archive.CreateEntry(internalPath);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("{\"preset_name\":\"Same\",\"settings\":{}}");
        }
        using var resourceWriter = new StreamWriter(archive.CreateEntry("Wavetables/Resource.wav").Open(), Encoding.UTF8);
        resourceWriter.Write(resource);
    }
    private static string Fixture(string name) { string path = Path.Combine(_fixtureRoot, name); Directory.CreateDirectory(path); return path; }
    private static string Argument(string[] args, string name) => args[Array.IndexOf(args, name) + 1];
    private static string FindRepository()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "SoundPreviewer.Core", "SoundPreviewer.Core.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Run tests from the SoundPreviewer repository.");
    }
    private static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual, string message) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message} Expected {expected}, actual {actual}."); }
    private static void NotEqual<T>(T unexpected, T actual, string message) { if (EqualityComparer<T>.Default.Equals(unexpected, actual)) throw new InvalidOperationException(message); }
    private static void Near(double expected, double actual, double tolerance, string message) { if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance) throw new InvalidOperationException($"{message} Expected {expected} +/- {tolerance}, actual {actual}."); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected exception {typeof(T).Name}.");
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected exception {typeof(T).Name}.");
    }
    private sealed class InlineProgress : IProgress<QueueProgress>
    {
        public int RenderingCalls { get; private set; }
        public void Report(QueueProgress value) { if (value.Preset?.Status == PresetStatus.Rendering) RenderingCalls++; }
    }
}
