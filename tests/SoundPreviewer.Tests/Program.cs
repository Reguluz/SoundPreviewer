using System.Reflection;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;
using SoundPreviewer.Worker;

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
            ("Fingerprint and WAV operations honor cancellation without changing hashes", TestCancellationAsync),
            ("PCM round trip, silence, clipping and IEEE nonfinite validation", TestWaveAsync),
            ("WAV 8/24/32 bit decoding and malformed chunk rejection", TestWaveFormatsAsync),
            ("Concatenation and rate conversion preserve shape and duration", TestAudioTransformsAsync),
            ("Recursive scanning, duplicate identities and explicit source synth", TestScanIdentityAsync),
            ("Content hashing, feedback preservation and missing-source protection", TestIncrementalScanAsync),
            ("Persisted MD5 duplicates, canonical promotion and legacy index migration", TestMd5IndexAsync),
            ("Vital banks retain internal identities and invalidate resource changes", TestBankScanningAsync),
            ("Mixed roots identify unique suffixes and verified VST IDs", TestMixedRootDetectionAsync),
            ("Directory evidence, ambiguity and fixed-source overlap stay conservative", TestDetectionAmbiguityAsync),
            ("Manual synth assignment preserves identity and invalidates only derived data", TestManualClassificationAsync),
            ("Legacy JSON enum values and explicit sources remain compatible", TestLegacySettingsAsync),
            ("Plugin discovery validates PE exports, architecture and VST3 bundle paths", TestPluginDiscoveryAsync),
            ("Renderer readiness separates configuration, architecture and actual verification", TestRenderReadinessAsync),
            ("SQLite UTF8 round trip, transactions, settings and interrupted recovery", TestStorageAsync),
            ("Child process exit, bounded pipes, timeout and cancellation", TestProcessRunnerAsync),
            ("Worker protocol commits valid output and rejects bad results", TestWorkerAsync),
            ("Worker cancellation and timeout leave committed audio intact", TestWorkerInterruptionAsync),
            ("Queue default preview reuse, explicit force and bounded retry", TestQueueAsync),
            ("Queue preparation, outcome summaries and persistent running progress", TestQueueFeedbackAsync),
            ("Stopped batches preserve committed results and resume incrementally", TestQueueStopResumeAsync)
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

    private static async Task TestMd5IndexAsync()
    {
        string directory = Fixture("md5-index");
        string nested = Path.Combine(directory, "Another folder");
        Directory.CreateDirectory(nested);
        string firstPath = Path.Combine(directory, "Original.vital");
        string copyPath = Path.Combine(nested, "Renamed-copy.vital");
        File.WriteAllText(firstPath, "{\"settings\":{}}");
        File.Copy(firstPath, copyPath);
        File.WriteAllText(Path.Combine(directory, "Different.vital"), "{\"settings\":{\"test\":1}}");
        var source = new SourceFolder { Id = "md5-root", Path = directory, Synth = SynthKind.Vital };
        var scanner = new ScanService();
        var initial = await scanner.ScanAsync([source], []);
        Equal(3, initial.Presets.Count, "MD5 indexing retains every original path rather than deleting copied files.");
        string md5 = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(firstPath))).ToLowerInvariant();
        var copies = initial.Presets.Where(p => p.ContentMd5 == md5).ToList();
        Equal(2, copies.Count, "Identical bytes receive the same persisted MD5 despite renamed files and folders.");
        True(initial.Presets.All(p => p.ContentMd5.Length == 32 && p.ContentHash.Length == 64), "MD5 duplicate identity coexists with SHA256 invalidation hashes.");
        var canonical = copies.Single(p => p.DuplicateOfId.Length == 0);
        var duplicate = copies.Single(p => p.DuplicateOfId.Length > 0);
        Equal(canonical.Id, duplicate.DuplicateOfId, "A duplicate points to a stable representative record.");
        canonical.Favorite = true; canonical.Feedback = 1; canonical.Tags.Add("original-history");
        duplicate.Feedback = -1; duplicate.Tags.Add("copy-history");
        CompletePreview(canonical, Path.Combine(directory, "canonical-test-only.wav"));
        var ids = initial.Presets.Select(p => p.Id).Order().ToArray();
        var unchanged = await scanner.ScanAsync([source], initial.Presets);
        Equal(0, unchanged.Added, "Persisted duplicate identities are not recreated on a later scan.");
        Equal(0, unchanged.Changed, "Duplicate metadata alone cannot mark unchanged source bytes modified.");
        True(ids.SequenceEqual(unchanged.Presets.Select(p => p.Id).Order()), "All path-qualified record IDs survive duplicate rebuilding.");
        var retained = unchanged.Presets.Single(p => p.Id == canonical.Id);
        var retainedCopy = unchanged.Presets.Single(p => p.Id == duplicate.Id);
        True(retained.Favorite && retained.Feedback == 1 && retained.Tags.Contains("original-history"), "Representative feedback and history are retained.");
        True(retainedCopy.Feedback == -1 && retainedCopy.Tags.Contains("copy-history"), "Copied-path feedback and history remain independent.");
        Equal(retained.Id, retainedCopy.DuplicateOfId, "A representative with a valid preview remains preferred.");
        var legacy = new JsonObject(JsonNode.Parse(JsonSerializer.Serialize(retainedCopy, LibraryJson.Options))!.AsObject()
            .Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())));
        legacy.Remove(nameof(PresetRecord.ContentMd5)); legacy.Remove(nameof(PresetRecord.DuplicateOfId));
        var legacyRecord = JsonSerializer.Deserialize<PresetRecord>(legacy.ToJsonString(), LibraryJson.Options)!;
        Equal("", legacyRecord.ContentMd5, "Old metadata defaults to no MD5 until an accessible rescan.");
        string databasePath = Path.Combine(directory, "legacy-v1.db");
        CreateLegacyDatabase(databasePath, legacyRecord);
        var store = new LibraryStore(databasePath);
        store.Initialize();
        Equal(legacyRecord.Id, store.LoadPresets().Single().Id, "SQLite v1 migration preserves existing identities.");
        Equal(-1, store.LoadPresets().Single().Feedback, "SQLite v1 migration preserves old personal feedback.");
        var backfilled = await scanner.ScanAsync([source], store.LoadPresets());
        Equal(0, backfilled.Changed, "MD5 backfill of unchanged SHA256 bytes is not a content invalidation.");
        Equal(md5, backfilled.Presets.Single(p => p.Id == legacyRecord.Id).ContentMd5, "An unchanged legacy record receives a real MD5 on rescan.");
        store.UpsertMany(backfilled.Presets);
        Equal(2, store.FindByMd5(md5).Count, "Persisted MD5 index finds both retained physical paths.");
        Equal(2, store.FindByMd5(md5.ToUpperInvariant()).Count, "MD5 lookup accepts uppercase hexadecimal without losing matches.");
        Throws<ArgumentException>(() => store.FindByMd5("' OR 1=1 --"));
        File.Delete(retained.SourcePath);
        var promoted = await scanner.ScanAsync([source], unchanged.Presets);
        var surviving = promoted.Presets.Single(p => p.Id == retainedCopy.Id);
        Equal(PresetStatus.Missing, promoted.Presets.Single(p => p.Id == retained.Id).Status, "Removed representative retains its missing-source history.");
        Equal("", surviving.DuplicateOfId, "A remaining accessible copy is promoted to representative.");
        Equal(retainedCopy.Id, surviving.Id, "Promotion keeps the copy's own record identity.");
        True(surviving.Feedback == -1 && surviving.Tags.Contains("copy-history"), "Promotion keeps the copy's own feedback instead of replacing it with representative feedback.");
        Equal(retained.PreviewPath, surviving.PreviewPath, "Promotion can retain a byte-identical verified preview without rendering again.");
        Equal(PresetStatus.NeedsReview, surviving.Status, "A promoted preview requires renewed source-to-audio confirmation.");

        string banks = Path.Combine(directory, "banks");
        Directory.CreateDirectory(banks);
        string bankPath = Path.Combine(banks, "Original.vitalbank");
        WriteBank(bankPath, "same test-only resources");
        File.Copy(bankPath, Path.Combine(banks, "Copied.vitalbank"));
        var bankRecords = (await scanner.ScanAsync([new SourceFolder { Id = "bank-md5", Path = banks, Synth = SynthKind.Vital }], [])).Presets;
        Equal(4, bankRecords.Count, "Copied banks retain all source and internal-entry identities.");
        Equal(2, bankRecords.Count(p => p.DuplicateOfId.Length == 0), "Different internal bank paths never collapse merely because their archive MD5 is identical.");
        foreach (var group in bankRecords.GroupBy(p => p.InternalPath))
            Equal(1, group.Count(p => p.DuplicateOfId.Length > 0), "Only matching internal paths in copied banks form duplicate groups.");
        string fxpDirectory = Path.Combine(directory, "cross-synth");
        Directory.CreateDirectory(fxpDirectory);
        WriteFxp(Path.Combine(fxpDirectory, "Shared.fxp"), 1);
        var differentSynths = await scanner.ScanAsync([
            new SourceFolder { Id = "serum-md5", Path = fxpDirectory, Synth = SynthKind.Serum1 },
            new SourceFolder { Id = "sylenth-md5", Path = fxpDirectory, Synth = SynthKind.Sylenth1 }], []);
        True(differentSynths.Presets.All(p => p.DuplicateOfId.Length == 0), "Identical bytes explicitly assigned to different synth contexts cannot share render identity.");

        var recovering = Preset("recover-old-preview");
        CompletePreview(recovering, Path.Combine(directory, "recover-test-only.wav"));
        string oldKey = recovering.CacheKey;
        recovering.Status = PresetStatus.Rendering;
        recovering.Attempts = 2;
        var corrupt = Preset("recover-corrupt-preview");
        CompletePreview(corrupt, Path.Combine(directory, "corrupt-test-only.wav"));
        File.WriteAllBytes(corrupt.PreviewPath, "not a WAV"u8.ToArray());
        corrupt.Status = PresetStatus.Rendering;
        var wrongRate = Preset("recover-wrong-rate");
        CompletePreview(wrongRate, Path.Combine(directory, "wrong-rate-test-only.wav"));
        var changedTemplate = JsonSerializer.Deserialize<PreviewTemplate>(wrongRate.TemplateJson, LibraryJson.Options)!;
        changedTemplate.SampleRate = 48000;
        wrongRate.TemplateJson = JsonSerializer.Serialize(changedTemplate, LibraryJson.Options);
        wrongRate.Status = PresetStatus.Rendering;
        var incomplete = Preset("recover-incomplete-notes");
        CompletePreview(incomplete, Path.Combine(directory, "incomplete-test-only.wav"));
        incomplete.Notes.RemoveAt(2);
        incomplete.Status = PresetStatus.Rendering;
        store.UpsertMany([recovering, corrupt, wrongRate, incomplete]);
        Equal(4, store.RecoverInterrupted(), "Interrupted preview validation handles complete and malformed stored outputs independently.");
        var recovered = store.LoadPresets().Single(p => p.Id == recovering.Id);
        Equal(PresetStatus.NeedsReview, recovered.Status, "A stopped forced render retains its old complete preview after restart.");
        Equal(oldKey, recovered.CacheKey, "Restart recovery retains the previous cache mapping.");
        Equal(2, recovered.Attempts, "Restart recovery preserves attempt history.");
        foreach (var invalid in new[] { corrupt, wrongRate, incomplete })
        {
            var pending = store.LoadPresets().Single(p => p.Id == invalid.Id);
            Equal(PresetStatus.Pending, pending.Status, "Corrupt, mismatched-rate or incomplete note metadata cannot be recovered as complete audio.");
            Equal("", pending.PreviewPath, "Invalid old audio is not retained as the active preview mapping.");
        }
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

    private static async Task TestMixedRootDetectionAsync()
    {
        string directory = Fixture("mixed-auto-root");
        File.WriteAllText(Path.Combine(directory, "Upper.VITAL"), "{\"settings\":{}}");
        File.WriteAllText(Path.Combine(directory, "Native.SerumPreset"), "test-only native preset envelope");
        WriteBank(Path.Combine(directory, "Bundle.VITALBANK"), "test-only resource");
        WriteFxp(Path.Combine(directory, "Serum-header.fxp"), 1, pluginId: "XfsX");
        WriteFxp(Path.Combine(directory, "Sylenth-header.FXP"), 2, pluginId: "syl1");
        WriteFxp(Path.Combine(directory, "Unknown-header.fxp"), 3, pluginId: "nope");
        WriteFxp(Path.Combine(directory, "Absent-id.fxp"), 4);
        string corrupt = Path.Combine(directory, "Corrupt-header.fxp");
        WriteFxp(corrupt, 5, pluginId: "XfsX");
        byte[] corruptBytes = File.ReadAllBytes(corrupt);
        corruptBytes[0] = (byte)'?';
        File.WriteAllBytes(corrupt, corruptBytes);
        WriteVstBank(Path.Combine(directory, "Known-bank.fxb"), "syl1", trailingBytes: 16);
        File.WriteAllText(Path.Combine(directory, "Opaque.fxb"), "test-only unknown bank");
        File.WriteAllText(Path.Combine(directory, "Ignored.txt"), "not a preset");
        var source = new SourceFolder { Id = "mixed", Path = directory, Synth = SynthKind.Unknown, AutoDetect = true };
        var result = await new ScanService().ScanAsync([source], []);
        Equal(11, result.Presets.Count, "A single mixed root indexes all supported suffixes including bank entries.");
        var records = result.Presets.Where(p => p.InternalPath.Length == 0).ToDictionary(p => p.Name, StringComparer.Ordinal);
        Equal(SynthKind.Vital, records["Upper"].Synth, "Case-insensitive Vital suffix is unique evidence.");
        Equal(SynthKind.Serum2, records["Native"].Synth, "Native SerumPreset suffix identifies Serum 2.");
        Equal(SynthKind.Serum1, records["Serum-header"].Synth, "Verified Serum FXP ID identifies Serum 1.");
        Equal(SynthKind.Sylenth1, records["Sylenth-header"].Synth, "Verified Sylenth1 FXP ID identifies Sylenth1.");
        foreach (string name in new[] { "Unknown-header", "Absent-id", "Corrupt-header", "Opaque" })
        {
            Equal(SynthKind.Unknown, records[name].Synth, "Unknown shared format stays unclassified.");
            Equal(PresetStatus.Unidentified, records[name].Status, "Unclassified files cannot enter the render queue.");
            True(records[name].DetectionReason.Length > 0, "Unclassified files retain a detection reason.");
        }
        Equal(SynthKind.Sylenth1, records["Known-bank"].Synth, "Verified bank product ID identifies its synth despite trailing data.");
        Equal(PresetStatus.Unsupported, records["Known-bank"].Status, "Identifying a bank never enables slot rendering.");
        True(result.Presets.Where(p => p.InternalPath.Length > 0).All(p => p.Synth == SynthKind.Vital && p.Status == PresetStatus.Unsupported),
            "Unique bank format identifies the synth without claiming bank render support.");
        True(result.Presets.All(p => p.SourceId == source.Id && p.DetectionReason.Length > 0), "Mixed-root records retain source and evidence.");
        Equal(11, result.Presets.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count(), "Mixed identities remain unique.");
        source.Path = directory.ToUpperInvariant();
        var caseRescan = await new ScanService().ScanAsync([source], result.Presets);
        Equal(0, caseRescan.Added, "Changing Windows root path case must not duplicate mixed records.");
        Equal(11, caseRescan.Presets.Count, "Case-normalized rescan preserves record identities.");
    }

    private static async Task TestDetectionAmbiguityAsync()
    {
        string directory = Fixture("detect-evidence");
        string Add(string relative, string pluginId = "nope")
        {
            string path = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteFxp(path, 1, pluginId: pluginId);
            return path;
        }
        Add(Path.Combine("SERUM 2", "Deep", "Nearest.fxp"), "XfsX");
        Add(Path.Combine("Serum 1", "Serum 2", "Nested.fxp"), "XfsX");
        Add(Path.Combine("Serum 1 Serum 2", "Conflict.fxp"), "XfsX");
        Add(Path.Combine("Serum2ish", "FalsePositive.fxp"), "XfsX");
        Add(Path.Combine("vitalizer", "NoSubstring.fxp"));
        Add(Path.Combine("Serum 2", "UnknownWithHint.fxp"));
        Add(Path.Combine("Serum 2", "HeaderConflict.fxp"), "syl1");
        Add(Path.Combine("Serum 2", "LegacySerum.fxp"), "XfsX");
        Add(Path.Combine("serum_2", "Underscore.fxp"), "XfsX");
        Add(Path.Combine("SERUM-2", "Hyphen.fxp"), "XfsX");
        Add(Path.Combine("Serum.2", "Dot.fxp"), "XfsX");
        var auto = new SourceFolder { Id = "auto", Path = directory, Synth = SynthKind.Unknown, AutoDetect = true };
        var scanner = new ScanService();
        var result = await scanner.ScanAsync([auto], []);
        var byName = result.Presets.ToDictionary(p => p.Name, StringComparer.Ordinal);
        Equal(SynthKind.Serum2, byName["Nearest"].Synth, "Nearest recognized ancestor matches case-insensitively.");
        Equal(SynthKind.Serum2, byName["Nested"].Synth, "Nearest token overrides more distant directory hints.");
        Equal(SynthKind.Serum2, byName["LegacySerum"].Synth, "Serum 2 directory can refine a compatible Serum-family header.");
        foreach (string name in new[] { "Underscore", "Hyphen", "Dot" })
            Equal(SynthKind.Serum2, byName[name].Synth, "Version tokens accept established directory separators.");
        Equal(SynthKind.Serum1, byName["FalsePositive"].Synth, "A version substring cannot refine a verified Serum family header.");
        Equal(SynthKind.Sylenth1, byName["HeaderConflict"].Synth, "Verified product header wins a misleading folder name.");
        foreach (string name in new[] { "Conflict", "NoSubstring", "UnknownWithHint" })
        {
            Equal(SynthKind.Unknown, byName[name].Synth, "Ambiguous, substring or conflicting evidence cannot assign a synth.");
            Equal(PresetStatus.Unidentified, byName[name].Status, "Ambiguous records remain explicitly unclassified.");
        }
        string isolatedRoot = Path.Combine(directory, "Serum 2", "Isolated");
        Directory.CreateDirectory(isolatedRoot);
        WriteFxp(Path.Combine(isolatedRoot, "OutsideRoot.fxp"), 1, pluginId: "XfsX");
        var bounded = await scanner.ScanAsync([new SourceFolder { Id = "bounded", Path = isolatedRoot, AutoDetect = true }], []);
        Equal(SynthKind.Serum1, bounded.Presets.Single().Synth, "Ancestors above a selected root must not refine classification.");

        string sharedPath = Path.Combine(directory, "Shared.fxp");
        WriteFxp(sharedPath, 1, pluginId: "XfsX");
        var fixedSource = new SourceFolder { Id = "fixed", Path = directory, Synth = SynthKind.Sylenth1 };
        foreach (var sources in new[] { new[] { auto, fixedSource }, new[] { fixedSource, auto } })
        {
            var overlap = await scanner.ScanAsync(sources, []);
            var shared = overlap.Presets.Where(p => p.SourcePath.Equals(sharedPath, StringComparison.OrdinalIgnoreCase)).ToList();
            Equal(1, shared.Count, "Overlapping auto and fixed sources cannot create a duplicate physical file.");
            Equal(SynthKind.Sylenth1, shared.Single().Synth, "Explicit fixed source assignment takes priority over detection.");
            Equal(fixedSource.Id, shared.Single().SourceId, "Fixed source owns the overlapping record regardless of configuration order.");
        }
    }

    private static async Task TestManualClassificationAsync()
    {
        string directory = Fixture("detect-manual");
        string path = Path.Combine(directory, "Manual.fxp");
        WriteFxp(path, 1, pluginId: "nope");
        var source = new SourceFolder { Id = "auto", Path = directory, Synth = SynthKind.Unknown, AutoDetect = true };
        var scanner = new ScanService();
        var initial = await scanner.ScanAsync([source], []);
        var preset = initial.Presets.Single();
        string id = preset.Id;
        preset.Favorite = true;
        preset.Feedback = -1;
        preset.FeedbackUtc = DateTime.UtcNow;
        preset.Tags = ["personal-tag"];
        preset.Status = PresetStatus.Ready;
        preset.PreviewPath = Path.Combine(directory, "old.wav");
        WaveFile.WritePcm16(preset.PreviewPath, Tone(44100, 1, 0.1));
        preset.CacheKey = "old-key";
        preset.AttemptedCacheKey = "old-attempt";
        preset.Attempts = 3;
        preset.Notes = [new NotePreview { MidiNote = 60 }];
        ScanService.AssignSynth(preset, SynthKind.Serum1);
        Equal(id, preset.Id, "Manual classification preserves immutable preset identity.");
        True(preset.SynthAssignedManually && preset.DetectionReason.Length > 0, "Manual assignment is explicit and explained.");
        Equal(PresetStatus.Pending, preset.Status, "A classified standalone preset becomes eligible for rendering.");
        Equal("", preset.PreviewPath, "Changing synth invalidates old audio mapping.");
        Equal("", preset.CacheKey, "Changing synth invalidates committed cache.");
        Equal("", preset.AttemptedCacheKey, "Changing synth resets failed-key history.");
        Equal(0, preset.Attempts, "Manual classification receives a fresh attempt budget.");
        Equal(0, preset.Notes.Count, "Stale note mapping is cleared.");
        True(preset.Favorite && preset.Feedback == -1 && preset.Tags.Contains("personal-tag"), "Feedback and personal tags survive classification.");
        Throws<ArgumentException>(() => ScanService.AssignSynth(preset, SynthKind.Unknown));
        string beforeInvalid = JsonSerializer.Serialize(preset, LibraryJson.Options);
        Throws<ArgumentException>(() => ScanService.AssignSynth(preset, (SynthKind)99));
        Throws<ArgumentException>(() => ScanService.AssignSynth(preset, SynthKind.Vital));
        Equal(beforeInvalid, JsonSerializer.Serialize(preset, LibraryJson.Options), "Invalid or incompatible classification cannot partly mutate a record.");
        var store = new LibraryStore(Path.Combine(directory, "manual.db"));
        store.Initialize(); store.Upsert(preset);
        var persisted = store.LoadPresets().Single();
        True(persisted.SynthAssignedManually && persisted.Tags.Contains("personal-tag"), "Classification and tags survive SQLite round trip.");
        var unchanged = await scanner.ScanAsync([source], [persisted]);
        Equal(0, unchanged.Added, "Manual classification does not create a second scanner identity.");
        Equal(id, unchanged.Presets.Single().Id, "Rescan preserves manually assigned identity.");
        Equal(SynthKind.Serum1, unchanged.Presets.Single().Synth, "Rescan cannot override manual assignment with unknown evidence.");
        WriteFxp(path, 2, pluginId: "syl1");
        var changed = await scanner.ScanAsync([source], unchanged.Presets);
        Equal(1, changed.Changed, "Content mutation invalidates one existing manually classified record.");
        var updated = changed.Presets.Single();
        Equal(id, updated.Id, "Content mutation does not replace the manually classified identity.");
        Equal(SynthKind.Serum1, updated.Synth, "Explicit manual choice survives newly recognized conflicting bytes.");
        True(updated.SynthAssignedManually && updated.Favorite && updated.Feedback == -1 && updated.Tags.Contains("personal-tag"),
            "Changed content preserves manual classification, feedback and custom tags.");
        updated.Status = PresetStatus.Ready;
        updated.PreviewPath = Path.Combine(directory, "old.wav");
        updated.CacheKey = "same-synth-key";
        updated.Attempts = 2;
        ScanService.AssignSynth(updated, SynthKind.Serum1);
        Equal(PresetStatus.Ready, updated.Status, "Confirming an unchanged synth does not discard a valid preview.");
        Equal("same-synth-key", updated.CacheKey, "Same-synth confirmation preserves cache.");
        ScanService.AssignSynth(updated, SynthKind.Serum2);
        Equal(id, updated.Id, "Changing synth at unchanged content still preserves identity.");
        Equal(PresetStatus.Pending, updated.Status, "Changing classification alone invalidates preview status.");
        Equal("", updated.CacheKey, "Changing classification alone invalidates preview cache.");
        Equal(0, updated.Attempts, "Changing classification alone resets attempts.");
        var reassigned = await scanner.ScanAsync([source], [updated]);
        Equal(SynthKind.Serum2, reassigned.Presets.Single().Synth, "A corrected manual synth persists on the next scan.");

        string derivedPath = Path.Combine(directory, "Derived.fxp");
        WriteFxp(derivedPath, 1, pluginId: "nope");
        var derived = (await scanner.ScanAsync([source], changed.Presets)).Presets.Single(p => p.Name == "Derived");
        string derivedId = derived.Id;
        derived.Favorite = true; derived.Tags = ["keep-me"];
        WriteFxp(derivedPath, 1, pluginId: "XfsX");
        var identified = await scanner.ScanAsync([source], [derived]);
        var detected = identified.Presets.Single(p => p.Name == "Derived");
        Equal(derivedId, detected.Id, "Automatic reclassification preserves physical identity.");
        Equal(SynthKind.Serum1, detected.Synth, "New recognized evidence upgrades an unclassified record.");
        Equal(PresetStatus.Pending, detected.Status, "Automatically identified standalone preset can render.");
        True(detected.Favorite && detected.Tags.Contains("keep-me"), "Automatic classification preserves personal data.");
        detected.Status = PresetStatus.Ready;
        detected.PreviewPath = Path.Combine(directory, "old.wav");
        detected.CacheKey = "identified-cache";
        WriteFxp(derivedPath, 1, pluginId: "nope");
        var declassified = (await scanner.ScanAsync([source], [detected])).Presets.Single(p => p.Name == "Derived");
        Equal(derivedId, declassified.Id, "Loss of recognized evidence preserves identity.");
        Equal(SynthKind.Unknown, declassified.Synth, "An automatic classification cannot survive changed unknown evidence.");
        Equal(PresetStatus.Unidentified, declassified.Status, "Loss of evidence blocks rendering again.");
        Equal("", declassified.CacheKey, "Loss of evidence invalidates preview cache.");
        True(declassified.Favorite && declassified.Tags.Contains("keep-me"), "Declassification preserves personal data.");

        string bankPath = Path.Combine(directory, "ManualBank.fxb");
        File.WriteAllText(bankPath, "test-only bank");
        var bank = (await scanner.ScanAsync([source], [])).Presets.Single(p => p.Name == "ManualBank");
        ScanService.AssignSynth(bank, SynthKind.Sylenth1);
        Equal(PresetStatus.Unsupported, bank.Status, "Manual bank identification never enables unimplemented bank rendering.");
    }

    private static Task TestLegacySettingsAsync()
    {
        Equal(0, (int)SynthKind.Vital, "Existing Vital enum value is stable.");
        Equal(1, (int)SynthKind.Serum1, "Existing Serum 1 enum value is stable.");
        Equal(2, (int)SynthKind.Serum2, "Existing Serum 2 enum value is stable.");
        Equal(3, (int)SynthKind.Sylenth1, "Existing Sylenth1 enum value is stable.");
        Equal(4, (int)SynthKind.Unknown, "Unknown is appended rather than shifting old enums.");
        Equal(6, (int)PresetStatus.Missing, "Existing status values remain stable.");
        Equal(7, (int)PresetStatus.Unidentified, "Unidentified is appended.");
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"Sources\":[{\"Id\":\"legacy\",\"Path\":\"C:\\\\Old\\\\Presets\",\"Synth\":1,\"Enabled\":true}]}", LibraryJson.Options)!;
        True(!settings.Sources.Single().AutoDetect && settings.Sources.Single().Synth == SynthKind.Serum1,
            "Legacy serialized source retains explicit synth mode.");
        Equal(4, settings.Plugins.Count, "Unknown classification must not create a fifth render plugin configuration.");
        True(settings.Plugins.All(plugin => plugin.Synth != SynthKind.Unknown), "Only actual synthesizers have default plugin settings.");
        True(settings.PluginDirectories.Count == 0 && settings.DiscoveredPlugins.Count == 0, "Legacy settings default to an empty shared plugin inventory.");
        var record = JsonSerializer.Deserialize<PresetRecord>("{\"Id\":\"old\",\"Synth\":3,\"Status\":2,\"Favorite\":true}", LibraryJson.Options)!;
        True(record.Synth == SynthKind.Sylenth1 && record.Status == PresetStatus.Ready && record.Favorite,
            "Legacy serialized preset keeps synth/status/feedback.");
        True(!record.SynthAssignedManually && record.DetectionReason.Length == 0 && record.Tags.Count == 0,
            "New classification metadata has compatible empty defaults.");
        return Task.CompletedTask;
    }

    private static async Task TestPluginDiscoveryAsync()
    {
        string directory = Fixture("plugin-discovery");
        string nested = Path.Combine(directory, "Nested");
        Directory.CreateDirectory(nested);
        string x64 = Path.Combine(directory, "Serum.dll");
        string x86 = Path.Combine(nested, "Sylenth1.dll");
        WritePeFixture(x64, x64: true, "VSTPluginMain");
        WritePeFixture(x86, x64: false, "VSTPluginMain");
        WritePeFixture(Path.Combine(directory, "Unrelated.dll"), x64: true, "NotAPlugin");
        WritePeFixture(Path.Combine(directory, "SerumFX_x64.dll"), x64: true, "VSTPluginMain");
        WritePeFixture(Path.Combine(directory, "Vital.exe"), x64: true);
        File.WriteAllText(Path.Combine(directory, "Broken.dll"), "MZ TEST ONLY not a valid PE");
        string bundle = Path.Combine(directory, "Serum 2.vst3");
        string binary = Path.Combine(bundle, "Contents", "x86_64-win", "Serum 2.vst3");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        WritePeFixture(binary, x64: true, "GetPluginFactory");
        string plainVst3 = Path.Combine(directory, "Plain.vst3");
        WritePeFixture(plainVst3, x64: true, "GetPluginFactory");
        string missing = Path.Combine(directory, "Unavailable");
        var scanner = new PluginScanService();
        var result = await scanner.ScanAsync([directory, nested, directory.ToUpperInvariant(), missing]);
        Equal(4, result.Plugins.Count, "Valid instrument candidates deduplicate overlapping roots and bundle contents.");
        Equal(4, result.Plugins.Select(plugin => plugin.Id).Distinct(StringComparer.Ordinal).Count(), "Discovered binaries have stable unique identities.");
        var x64Plugin = result.Plugins.Single(plugin => plugin.BinaryPath.Equals(x64, StringComparison.OrdinalIgnoreCase));
        var x86Plugin = result.Plugins.Single(plugin => plugin.BinaryPath.Equals(x86, StringComparison.OrdinalIgnoreCase));
        Equal(PluginArchitecture.X64, x64Plugin.Architecture, "PE32+ machine architecture is detected without loading a DLL.");
        Equal(PluginArchitecture.X86, x86Plugin.Architecture, "PE32 machine architecture is detected without loading a DLL.");
        Equal(PluginBinaryFormat.Vst2, x64Plugin.Format, "VST2 candidate requires a compatible entry export.");
        var bundled = result.Plugins.Single(plugin => plugin.BinaryPath.Equals(binary, StringComparison.OrdinalIgnoreCase));
        Equal(bundle, bundled.FilePath, "VST3 bundle retains the selectable outer path.");
        Equal(binary, bundled.BinaryPath, "VST3 bundle retains the actual architecture binary path.");
        Equal(PluginBinaryFormat.Vst3, bundled.Format, "VST3 factory export is recognized.");
        True(result.Plugins.All(plugin => plugin.Synth == SynthKind.Unknown && plugin.Reason.Length > 0),
            "Familiar filenames without product metadata cannot auto-assign a synthesizer.");
        True(result.UnavailableRoots.Any(path => path.Equals(missing, StringComparison.OrdinalIgnoreCase)), "Unavailable search roots are reported explicitly.");
        True(result.Warnings.Count > 0, "Malformed PE candidates surface bounded diagnostics.");
        True(!result.Plugins.Any(plugin => Path.GetFileName(plugin.BinaryPath).Contains("SerumFX", StringComparison.OrdinalIgnoreCase)),
            "The known Serum effect filename cannot become an instrument candidate.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => scanner.ScanAsync([directory], cancellation.Token));
        var settings = new AppSettings { DiscoveredPlugins = result.Plugins };
        var roundTrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, LibraryJson.Options), LibraryJson.Options)!;
        Equal(4, roundTrip.DiscoveredPlugins.Count, "Discovery records survive settings serialization without becoming active plugins.");
        True(roundTrip.Plugins.All(plugin => plugin.Backend == RenderBackend.Unconfigured), "Discovery alone never enables an unverified renderer.");

        DiscoveredPlugin Candidate(SynthKind synth, string candidatePath, string candidateBinary, string version,
            PluginBinaryFormat format, PluginArchitecture architecture = PluginArchitecture.X64) => new()
        {
            Id = Guid.NewGuid().ToString("N"), Synth = synth, FilePath = candidatePath, BinaryPath = candidateBinary,
            Version = version, Format = format, Architecture = architecture, Name = "TEST ONLY metadata candidate",
            Reason = "TEST ONLY selection fixture; product metadata is not inferred from this binary."
        };
        string newer = Path.Combine(directory, "test-only-newer.dll");
        File.Copy(x64, newer);
        var oldSerum = Candidate(SynthKind.Serum1, x64, x64, "1.1.0", PluginBinaryFormat.Vst2);
        var newSerum = Candidate(SynthKind.Serum1, newer, newer, "1.4.9", PluginBinaryFormat.Vst2);
        var newerVst3 = Candidate(SynthKind.Serum1, plainVst3, plainVst3, "2.5.0", PluginBinaryFormat.Vst3);
        var serum2 = Candidate(SynthKind.Serum2, bundle, binary, "2.2.0", PluginBinaryFormat.Vst3);
        string standalone = Path.Combine(directory, "Vital.exe");
        var vital = Candidate(SynthKind.Vital, standalone, standalone, "1.5.5", PluginBinaryFormat.Standalone);
        var sylenth32 = Candidate(SynthKind.Sylenth1, x86, x86, "2.2.1.2", PluginBinaryFormat.Vst2, PluginArchitecture.X86);
        var candidates = new[] { newerVst3, oldSerum, sylenth32, vital, serum2, newSerum }.Concat(result.Plugins).ToList();
        var selectedSettings = new AppSettings();
        var serumConfiguration = selectedSettings.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1);
        serumConfiguration.ExecutablePath = TestExecutable;
        serumConfiguration.Backend = RenderBackend.ExternalWorker;
        Equal(3, PluginSelectionService.ApplyAutomatic(candidates, selectedSettings.Plugins), "Only known x64 candidates are automatically associated.");
        Equal(newer, serumConfiguration.PluginPath, "Within the supported VST2 preference, newest version wins regardless of discovery order.");
        Equal("1.4.9", serumConfiguration.Version, "Selected metadata version is persisted.");
        Equal(TestExecutable, serumConfiguration.ExecutablePath, "Plugin association does not overwrite an advanced external renderer program.");
        Equal(RenderBackend.ExternalWorker, serumConfiguration.Backend, "Plugin association preserves the chosen external backend.");
        var vitalConfiguration = selectedSettings.Plugins.Single(plugin => plugin.Synth == SynthKind.Vital);
        Equal(standalone, vitalConfiguration.ExecutablePath, "Known Vital standalone fills its executable path.");
        Equal(RenderBackend.VitalCli, vitalConfiguration.Backend, "Unconfigured Vital standalone can use the native backend.");
        Equal(bundle, selectedSettings.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum2).PluginPath, "Serum 2 retains a VST3 bundle selection.");
        Equal("", selectedSettings.Plugins.Single(plugin => plugin.Synth == SynthKind.Sylenth1).PluginPath, "An x86 discovery is never automatically enabled for the x64 host.");
        Equal(0, PluginSelectionService.ApplyAutomatic(candidates, selectedSettings.Plugins), "Repeated association leaves valid chosen binaries unchanged.");
        PluginSelectionService.Assign(serumConfiguration, oldSerum);
        PluginSelectionService.ApplyAutomatic([newSerum], selectedSettings.Plugins);
        Equal(x64, serumConfiguration.PluginPath, "A valid manually chosen older DLL survives a later scan that omits it.");
        var bundleConfiguration = selectedSettings.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum2);
        var otherSerum2 = Candidate(SynthKind.Serum2, plainVst3, plainVst3, "9.0.0", PluginBinaryFormat.Vst3);
        PluginSelectionService.ApplyAutomatic([otherSerum2], selectedSettings.Plugins);
        Equal(bundle, bundleConfiguration.PluginPath, "A valid selected bundle directory survives an omitted-root scan.");
        Equal(Fingerprints.HashFile(x64), Fingerprints.PluginBinaryHash(serumConfiguration, candidates),
            "Ordinary DLL fingerprint remains its existing SHA256 hash.");
        string bundleHash = Fingerprints.PluginBinaryHash(bundleConfiguration, [serum2]);
        True(bundleHash.Length > 0, "A VST3 bundle fingerprints its discovered internal binary.");
        Equal(bundleHash, Fingerprints.PluginBinaryHash(bundleConfiguration, [serum2, serum2, otherSerum2]),
            "Duplicate inventory entries and unrelated bundles cannot change a selected bundle fingerprint.");
        bundleConfiguration.PluginHash = bundleHash;
        var bundlePreset = Preset("test-only-bundle-cache");
        bundlePreset.Synth = SynthKind.Serum2;
        string bundleCache = Fingerprints.CacheKey(bundlePreset, bundleConfiguration, selectedSettings.Template);
        bundleConfiguration.PluginHash = Fingerprints.PluginBinaryHash(bundleConfiguration, [serum2]);
        Equal(bundleCache, Fingerprints.CacheKey(bundlePreset, bundleConfiguration, selectedSettings.Template),
            "Unchanged bundle bytes, path and version retain the same cache key.");
        byte[] originalBinary = File.ReadAllBytes(binary);
        byte[] modifiedBinary = originalBinary.ToArray();
        modifiedBinary[^1] ^= 1;
        File.WriteAllBytes(binary, modifiedBinary);
        string changedBundleHash = Fingerprints.PluginBinaryHash(bundleConfiguration, [serum2]);
        NotEqual(bundleHash, changedBundleHash, "Changing internal PE bytes changes the bundle fingerprint without a version change.");
        bundleConfiguration.PluginHash = changedBundleHash;
        NotEqual(bundleCache, Fingerprints.CacheKey(bundlePreset, bundleConfiguration, selectedSettings.Template),
            "Changing internal bundle bytes invalidates the render cache at the same path/version.");
        Equal("2.2.0", bundleConfiguration.Version, "Bundle byte invalidation does not fabricate a new plugin version.");
        File.WriteAllBytes(binary, originalBinary);
        string alternateBinary = Path.Combine(bundle, "Contents", "x86-win", "Serum 2.vst3");
        Directory.CreateDirectory(Path.GetDirectoryName(alternateBinary)!);
        WritePeFixture(alternateBinary, x64: false, "GetPluginFactory");
        var alternate = Candidate(SynthKind.Serum2, bundle, alternateBinary, "2.2.0", PluginBinaryFormat.Vst3, PluginArchitecture.X86);
        string completeBundleHash = Fingerprints.PluginBinaryHash(bundleConfiguration, [serum2, alternate]);
        NotEqual(bundleHash, completeBundleHash, "All discovered bundle architecture binaries contribute to its fingerprint.");
        Equal(completeBundleHash, Fingerprints.PluginBinaryHash(bundleConfiguration, [alternate, serum2, alternate]),
            "Bundle binary ordering and duplicates do not affect the composite fingerprint.");
        var missingBinary = Candidate(SynthKind.Serum2, bundle, Path.Combine(directory, "offline-binary.vst3"), "2.2.0", PluginBinaryFormat.Vst3);
        Equal("", Fingerprints.PluginBinaryHash(bundleConfiguration, [missingBinary]), "An unavailable bundle binary has no invented fingerprint.");
        Equal("", Fingerprints.PluginBinaryHash(bundleConfiguration, []), "A bundle without discovered binaries cannot hash a directory as a file.");
        vitalConfiguration.Backend = RenderBackend.ExternalWorker;
        vitalConfiguration.ExecutablePath = TestExecutable;
        PluginSelectionService.ApplyAutomatic([vital], selectedSettings.Plugins);
        Equal(TestExecutable, vitalConfiguration.ExecutablePath, "Automatic Vital discovery cannot replace an active external renderer.");
        Equal(RenderBackend.ExternalWorker, vitalConfiguration.Backend, "Automatic Vital discovery cannot change an active external backend.");
        vitalConfiguration.ExecutablePath = standalone;
        True(!PluginSelectionService.Matches(vitalConfiguration, vital), "A matching standalone filename does not imply a native backend selection.");
        vitalConfiguration.PluginPath = newer;
        vitalConfiguration.PluginHash = "old-vst-hash";
        PluginSelectionService.Assign(vitalConfiguration, vital);
        Equal(RenderBackend.VitalCli, vitalConfiguration.Backend, "Explicit standalone selection switches to the native backend.");
        Equal("", vitalConfiguration.PluginPath, "Standalone selection clears the inactive VST path.");
        Equal("", vitalConfiguration.PluginHash, "Standalone selection clears the inactive VST fingerprint.");
        True(PluginSelectionService.Matches(vitalConfiguration, vital), "Standalone is matched only after selecting the native backend.");
        vitalConfiguration.ExecutableHash = "old-native-hash";
        var vitalVst = Candidate(SynthKind.Vital, newer, newer, "1.5.7", PluginBinaryFormat.Vst2);
        PluginSelectionService.Assign(vitalConfiguration, vitalVst);
        Equal(RenderBackend.Unconfigured, vitalConfiguration.Backend, "Native-to-VST switch requires a compatible external renderer configuration.");
        Equal("", vitalConfiguration.ExecutablePath, "Native-to-VST switch clears the stale CLI executable.");
        Equal("", vitalConfiguration.ExecutableHash, "Native-to-VST switch clears the stale CLI fingerprint.");
        Equal(newer, vitalConfiguration.PluginPath, "Native-to-VST switch keeps the selected plugin binary.");
        Equal("1.5.7", vitalConfiguration.Version, "Native-to-VST switch uses the selected plugin version.");
        vitalConfiguration.PluginHash = "vst-selection-hash";
        PluginSelectionService.Assign(vitalConfiguration, vital);
        Equal(RenderBackend.VitalCli, vitalConfiguration.Backend, "VST-to-native switch restores the native backend deliberately.");
        Equal(standalone, vitalConfiguration.ExecutablePath, "VST-to-native switch restores the selected standalone executable.");
        Equal("", vitalConfiguration.PluginPath, "VST-to-native switch removes the inactive plugin mapping.");
        Equal("", vitalConfiguration.PluginHash, "VST-to-native switch removes the inactive plugin fingerprint.");
        string beforeInvalid = JsonSerializer.Serialize(serumConfiguration, LibraryJson.Options);
        Throws<ArgumentException>(() => PluginSelectionService.Assign(serumConfiguration, sylenth32));
        Throws<ArgumentException>(() => PluginSelectionService.Assign(serumConfiguration, x64Plugin));
        Equal(beforeInvalid, JsonSerializer.Serialize(serumConfiguration, LibraryJson.Options), "Mismatched or unknown candidates cannot partly mutate a plugin configuration.");
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
        foreach (string guard in new[] { "unknown-synth", "unidentified-status", "unknown-plugin" })
        {
            var guarded = Request("guard-marker-success", Path.Combine(directory, guard + ".wav"));
            guarded.Plugin.ExecutablePath = TestExecutable;
            if (guard == "unknown-synth") guarded.Preset.Synth = SynthKind.Unknown;
            if (guard == "unidentified-status") guarded.Preset.Status = PresetStatus.Unidentified;
            if (guard == "unknown-plugin") guarded.Plugin.Synth = SynthKind.Unknown;
            var rejected = await RenderDispatcher.RenderAsync(guarded, CancellationToken.None);
            True(!rejected.Success && rejected.Message.Length > 0, "Worker dispatcher rejects unclassified requests with a reason.");
            True(!File.Exists(guarded.OutputPath + ".external-ran"), "Unclassified requests cannot invoke an external renderer.");
            True(!File.Exists(guarded.OutputPath), "Unclassified requests cannot commit audio.");
        }
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
        var unknown = Preset("guard-marker-success");
        unknown.Synth = SynthKind.Unknown;
        var unidentified = Preset("guard-marker-success");
        unidentified.Status = PresetStatus.Unidentified;
        await queue.RunAsync(settings, [unknown, unidentified], TestExecutable, progress, CancellationToken.None);
        Equal(0, progress.RenderingCalls, "Queue never invokes a worker for unclassified records.");
        Equal(0, unknown.Attempts, "Unknown synth does not consume render attempts.");
        Equal(0, unidentified.Attempts, "Unidentified status does not consume render attempts.");
        Equal(PresetStatus.Pending, unknown.Status, "Queue preserves an unknown record's existing status.");
        Equal(PresetStatus.Unidentified, unidentified.Status, "Queue preserves explicit unidentified status.");
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
        int renderedBeforeSettings = progress.RenderingCalls;
        await queue.RunAsync(settings, [preset], TestExecutable, progress, CancellationToken.None);
        Equal(PresetStatus.Ready, preset.Status, "Template changes keep an already complete preview by default.");
        Equal(firstKey, preset.CacheKey, "Default generation retains the original committed template cache key.");
        Equal(renderedBeforeSettings, progress.RenderingCalls, "Default generation skips complete previews using their stored template.");
        var plugin = settings.Plugins.Single();
        plugin.Version = "changed-test-version";
        plugin.Backend = RenderBackend.Unconfigured;
        plugin.ExecutablePath = Path.Combine(directory, "missing-renderer.exe");
        await queue.RunAsync(settings, [preset], TestExecutable, progress, CancellationToken.None);
        Equal(renderedBeforeSettings, progress.RenderingCalls, "A complete preview is reused before checking a changed or unavailable renderer.");
        Equal(PresetStatus.Ready, preset.Status, "Unavailable new renderer cannot destroy a complete preview.");
        plugin.Backend = RenderBackend.ExternalWorker;
        plugin.ExecutablePath = TestExecutable;
        await queue.RunAsync(settings, [preset], TestExecutable, progress, CancellationToken.None, forceRegenerate: true);
        Equal(PresetStatus.Ready, preset.Status, "Explicit force regenerates complete records with the changed settings.");
        NotEqual(firstKey, preset.CacheKey, "Explicit force commits a new template/plugin cache key.");
        Equal(renderedBeforeSettings + 1, progress.RenderingCalls, "Explicit force bypasses complete-preview reuse.");
        string committedKey = preset.CacheKey;
        string committedPath = preset.PreviewPath;
        byte[] committedBytes = File.ReadAllBytes(committedPath);
        preset.Name = "failure";
        settings.Template.Velocity++;
        await queue.RunAsync(settings, [preset], TestExecutable, progress, CancellationToken.None, forceRegenerate: true);
        Equal(PresetStatus.Ready, preset.Status, "A failed forced regeneration preserves the previously complete record.");
        Equal(committedKey, preset.CacheKey, "Failed force preserves the old cache mapping.");
        Equal(committedPath, preset.PreviewPath, "Failed force preserves the old audio path.");
        True(File.ReadAllBytes(committedPath).SequenceEqual(committedBytes), "Failed force cannot overwrite prior committed audio.");
        int renderedAfterFailure = progress.RenderingCalls;
        await queue.RunAsync(settings, [preset], TestExecutable, progress, CancellationToken.None);
        Equal(renderedAfterFailure, progress.RenderingCalls, "Default generation resumes by skipping the preserved complete preview.");
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

    private static Task TestRenderReadinessAsync()
    {
        string directory = Fixture("render-readiness");
        string host = Path.Combine(directory, "TEST-ONLY-host.exe");
        File.WriteAllText(host, "TEST ONLY placeholder; this readiness test must never execute a renderer.");
        string serum64 = Path.Combine(directory, "TEST-ONLY-Serum-x64.dll");
        string sylenth32 = Path.Combine(directory, "TEST-ONLY-Sylenth-x86.dll");
        string serumVst3 = Path.Combine(directory, "TEST-ONLY-Serum.vst3");
        WritePeFixture(serum64, x64: true, "VSTPluginMain");
        WritePeFixture(sylenth32, x64: false, "VSTPluginMain");
        WritePeFixture(serumVst3, x64: true, "GetPluginFactory");
        var discovery = new[]
        {
            new DiscoveredPlugin { Synth = SynthKind.Serum1, FilePath = serum64, BinaryPath = serum64, Version = "TEST ONLY 1", Architecture = PluginArchitecture.X64, Format = PluginBinaryFormat.Vst2 },
            new DiscoveredPlugin { Synth = SynthKind.Sylenth1, FilePath = sylenth32, BinaryPath = sylenth32, Version = "TEST ONLY 2", Architecture = PluginArchitecture.X86, Format = PluginBinaryFormat.Vst2 },
            new DiscoveredPlugin { Synth = SynthKind.Serum1, FilePath = serumVst3, BinaryPath = serumVst3, Version = "TEST ONLY 1", Architecture = PluginArchitecture.X64, Format = PluginBinaryFormat.Vst3 }
        };
        var absent = RenderReadinessService.Evaluate(new AppSettings { Plugins = [], DiscoveredPlugins = discovery.ToList() });
        Equal(4, absent.Count, "Readiness reports all supported synths even when old settings omit their configurations.");
        True(absent.Select(p => p.Synth).ToHashSet().SetEquals(new[] { SynthKind.Vital, SynthKind.Serum1, SynthKind.Serum2, SynthKind.Sylenth1 }), "Readiness inventory cannot omit a supported synth or invent an unknown render target.");
        True(absent.All(p => !p.CanAttemptRender && !p.IsVerified && !string.IsNullOrWhiteSpace(p.Status) && !string.IsNullOrWhiteSpace(p.Detail)), "Missing configuration is actionable and cannot be presented as successful verification.");

        var sylenth = new PluginConfiguration { Synth = SynthKind.Sylenth1, PluginPath = sylenth32, Version = "TEST ONLY 2" };
        var unconfigured32 = RenderReadinessService.Evaluate(sylenth, discovery);
        True(!unconfigured32.CanAttemptRender && !unconfigured32.IsVerified, "Finding a 32-bit plugin does not configure an actual renderer.");
        True(unconfigured32.Detail.Contains("32", StringComparison.Ordinal) || unconfigured32.Detail.Contains("x86", StringComparison.OrdinalIgnoreCase), "An unconfigured x86 plugin exposes its real architecture instead of requiring a nonexistent 64-bit package.");

        var serum = new PluginConfiguration { Synth = SynthKind.Serum1, PluginPath = serum64, ExecutablePath = host, Version = "TEST ONLY 1", Backend = RenderBackend.DawDreamer };
        var ready = RenderReadinessService.Evaluate(serum, discovery);
        True(ready.CanAttemptRender && !ready.IsVerified, "Structurally configured x64 VST2 can be attempted, but file discovery cannot verify real synthesis.");
        True(!string.IsNullOrWhiteSpace(ready.Status) && !string.IsNullOrWhiteSpace(ready.Detail), "Readiness includes a visible explanation rather than an unexplained success flag.");
        sylenth.Backend = RenderBackend.DawDreamer; sylenth.ExecutablePath = host;
        True(!RenderReadinessService.Evaluate(sylenth, discovery).CanAttemptRender, "The built-in x64 DawDreamer bridge cannot attempt an x86 DLL.");
        serum.PluginPath = serumVst3;
        True(!RenderReadinessService.Evaluate(serum, discovery).CanAttemptRender, "Discovering a VST3 is not evidence that this VST2 FXP bridge can render it.");
        serum.PluginPath = Path.Combine(directory, "missing.dll");
        True(!RenderReadinessService.Evaluate(serum, discovery).CanAttemptRender, "Missing configured plugin files block an attempt.");
        serum.PluginPath = serum64; serum.ExecutablePath = Path.Combine(directory, "missing-host.exe");
        True(!RenderReadinessService.Evaluate(serum, discovery).CanAttemptRender, "Missing host executables block an attempt.");
        serum.ExecutablePath = host; serum.Version = "";
        True(!RenderReadinessService.Evaluate(serum, discovery).CanAttemptRender, "A blank actual version remains an incomplete renderer configuration.");
        serum.Version = "TEST ONLY 1";
        foreach (var (synth, architecture) in new[]
        {
            (SynthKind.Serum1, PluginArchitecture.Unknown),
            (SynthKind.Sylenth1, PluginArchitecture.X64),
            (SynthKind.Unknown, PluginArchitecture.X64)
        })
        {
            var uncertain = new[] { new DiscoveredPlugin { Synth = synth, FilePath = serum64, BinaryPath = serum64, Architecture = architecture, Format = PluginBinaryFormat.Vst2 } };
            True(!RenderReadinessService.Evaluate(serum, uncertain).CanAttemptRender, "Unknown architecture or a different/unknown product at the configured path cannot establish DawDreamer readiness.");
        }

        sylenth.Backend = RenderBackend.ExternalWorker;
        var external32 = RenderReadinessService.Evaluate(sylenth, discovery);
        True(external32.CanAttemptRender && !external32.IsVerified, "A custom matching external worker is not globally denied merely because its plugin is x86.");
        True(external32.Detail.Contains("32", StringComparison.Ordinal) || external32.Detail.Contains("x86", StringComparison.OrdinalIgnoreCase), "The external-worker result still identifies the x86 host-matching responsibility.");
        var externalOnly = new PluginConfiguration { Synth = SynthKind.Sylenth1, Backend = RenderBackend.ExternalWorker, ExecutablePath = host, Version = "TEST ONLY external adapter" };
        True(RenderReadinessService.Evaluate(externalOnly, []).CanAttemptRender, "A custom external protocol renderer can manage its own plugin and does not require DawDreamer discovery fields.");
        var vital = new PluginConfiguration { Synth = SynthKind.Vital, Backend = RenderBackend.VitalCli, ExecutablePath = host, Version = "TEST ONLY" };
        var vitalReadiness = RenderReadinessService.Evaluate(vital, discovery);
        True(vitalReadiness.CanAttemptRender && !vitalReadiness.IsVerified, "Vital CLI configuration is attemptable but cannot verify preset loading in a read-only status check.");
        var vitalSettings = new AppSettings { Plugins = [vital], Template = new PreviewTemplate { Velocity = 89, HoldSeconds = 1, TailSeconds = 0.3 } };
        string beforeReadiness = JsonSerializer.Serialize(vitalSettings, LibraryJson.Options);
        True(RenderReadinessService.Evaluate(vitalSettings).Single(p => p.Synth == SynthKind.Vital).CanAttemptRender, "Global readiness accepts the native adapter's actual fixed parameters.");
        Equal(beforeReadiness, JsonSerializer.Serialize(vitalSettings, LibraryJson.Options), "Readiness evaluation does not change the user's settings, source paths or template.");
        vitalSettings.Template.HoldSeconds = 1.5; vitalSettings.Template.TailSeconds = 0.45;
        True(!RenderReadinessService.Evaluate(vitalSettings).Single(p => p.Synth == SynthKind.Vital).CanAttemptRender, "Global readiness explains unsupported fractional native hold time before a render is attempted.");
        serum.Backend = RenderBackend.VitalCli; serum.Version = "TEST ONLY";
        True(!RenderReadinessService.Evaluate(serum, discovery).CanAttemptRender, "A Vital-only backend cannot be marked ready for Serum.");
        return Task.CompletedTask;
    }

    private static async Task TestCancellationAsync()
    {
        string directory = Fixture("cancelable-io");
        string largePath = Path.Combine(directory, "test-only-64MiB.bin");
        using (var file = new FileStream(largePath, FileMode.CreateNew, FileAccess.Write)) file.SetLength(64L * 1024 * 1024);
        string expected;
        using (var file = File.OpenRead(largePath)) expected = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        Equal(expected, Fingerprints.HashFile(largePath), "Cancelable file hashing preserves the previous SHA256 algorithm.");
        Equal(expected, Fingerprints.HashFile(largePath, CancellationToken.None), "An explicit uncanceled token cannot change fingerprints.");
        string bundle = Path.Combine(directory, "test-only.vst3");
        string binary = Path.Combine(bundle, "Contents", "x86_64-win", "test-only.vst3");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllBytes(binary, "TEST ONLY binary fixture"u8.ToArray());
        var plugin = new PluginConfiguration { Synth = SynthKind.Vital, PluginPath = bundle };
        var discovered = new[] { new DiscoveredPlugin { FilePath = bundle, BinaryPath = binary, Synth = SynthKind.Vital, Format = PluginBinaryFormat.Vst3, Architecture = PluginArchitecture.X64 } };
        Equal(Fingerprints.PluginBinaryHash(plugin, discovered), Fingerprints.PluginBinaryHash(plugin, discovered, CancellationToken.None), "An uncanceled token preserves VST3 bundle fingerprint ordering and encoding.");
        string wavePath = Path.Combine(directory, "test-only-audio.wav");
        WaveFile.WritePcm16(wavePath, Tone(44100, 1, 0.2));
        var audio = WaveFile.Read(wavePath);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            Throws<OperationCanceledException>(() => Fingerprints.HashFile(largePath, canceled.Token));
            Throws<OperationCanceledException>(() => Fingerprints.PluginBinaryHash(plugin, discovered, canceled.Token));
            Throws<OperationCanceledException>(() => WaveFile.Read(wavePath, canceled.Token));
            Throws<OperationCanceledException>(() => WaveFile.Validate(wavePath, canceled.Token));
            Throws<OperationCanceledException>(() => WaveFile.Analyze(audio, canceled.Token));
        }

        var largeAudio = new WaveData { SampleRate = 44100, Channels = 2, Samples = new float[44100 * 2 * 180] };
        var operations = new (string Name, Func<CancellationToken, object> Run)[]
        {
            ("64 MiB file fingerprint", token => Fingerprints.HashFile(largePath, token)),
            ("large WAV analysis", token => WaveFile.Analyze(largeAudio, token))
        };
        foreach (var operation in operations)
        {
            using var cancellation = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = Task.Run(() => { started.SetResult(); return operation.Run(cancellation.Token); });
            try
            {
                await started.Task;
                await Task.Delay(1);
                var elapsed = Stopwatch.StartNew();
                cancellation.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => run);
                True(elapsed.Elapsed < TimeSpan.FromSeconds(3), operation.Name + " observes cancellation promptly while processing real input.");
            }
            finally
            {
                cancellation.Cancel();
                try { await run; } catch (OperationCanceledException) { }
            }
        }
    }

    private static async Task TestQueueFeedbackAsync()
    {
        string directory = Fixture("queue-feedback");
        var store = new LibraryStore(Path.Combine(directory, "library.db"));
        store.Initialize();
        var settings = new AppSettings
        {
            PreviewDirectory = Path.Combine(directory, "previews"), MaxAttempts = 1,
            Template = new PreviewTemplate { HoldSeconds = 0.1, TailSeconds = 0.02 },
            Plugins = [new PluginConfiguration { Synth = SynthKind.Vital, Backend = RenderBackend.ExternalWorker, ExecutablePath = TestExecutable, Version = "test-only", TimeoutSeconds = 30 }]
        };
        var queue = new RenderQueue(store);

        static void CheckFinished(RecordingProgress progress, QueueResult result)
        {
            var events = progress.Snapshots;
            True(events.Length >= 2, "Even a queue with no work must report preparation and completion.");
            Equal(QueueStages.Preparing, events[0].Stage, "The first queue event identifies preparation before expensive per-item checks.");
            Equal(QueueStages.Completed, events[^1].Stage, "The terminal event identifies a finished queue.");
            True(!events[^1].IsIndeterminate, "A completed queue cannot leave an indeterminate progress indicator running.");
            Equal(result.Eligible, events[^1].Completed, "Terminal progress counts evaluated eligible records, not excluded paths.");
            Equal(result.Eligible, events[^1].Total, "Terminal progress total agrees with the outcome summary.");
            True(!string.IsNullOrWhiteSpace(result.Summary) && !string.IsNullOrWhiteSpace(events[^1].Message), "No-op and completed queues expose an actionable outcome summary.");
            True(events.All(p => p.Completed >= 0 && p.Total >= p.Completed && !string.IsNullOrWhiteSpace(p.Stage)), "Progress counters and stage names remain valid throughout the operation.");
            True(events.Zip(events.Skip(1)).All(pair => pair.First.Completed <= pair.Second.Completed), "Completed-item progress cannot move backwards.");
        }

        var emptyProgress = new RecordingProgress();
        var empty = await queue.RunAsync(settings, [], TestExecutable, emptyProgress, CancellationToken.None);
        Equal(0, empty.Requested, "An empty queue reports no requested records.");
        Equal(0, empty.Eligible, "An empty queue reports no eligible records.");
        Equal(0, empty.Generated, "An empty queue cannot claim generated audio.");
        CheckFinished(emptyProgress, empty);

        var excluded = new[] { Preset("unknown"), Preset("unidentified"), Preset("duplicate"), Preset("unsupported"), Preset("missing") };
        excluded[0].Synth = SynthKind.Unknown;
        excluded[1].Status = PresetStatus.Unidentified;
        excluded[2].DuplicateOfId = "test-only-canonical";
        excluded[3].Status = PresetStatus.Unsupported;
        excluded[4].Status = PresetStatus.Missing;
        var excludedProgress = new RecordingProgress();
        var excludedResult = await queue.RunAsync(settings, excluded, TestExecutable, excludedProgress, CancellationToken.None);
        Equal(5, excludedResult.Requested, "All original paths are represented in requested counts.");
        Equal(5, excludedResult.Excluded, "Unknown, unidentified, duplicate, unsupported and missing records are counted as excluded.");
        Equal(0, excludedResult.Eligible, "An entirely excluded list has no eligible work.");
        True(excluded.All(p => p.Attempts == 0), "Excluded records never consume render attempts.");
        CheckFinished(excludedProgress, excludedResult);

        var unconfiguredSettings = new AppSettings { PreviewDirectory = Path.Combine(directory, "unconfigured-outputs"), Plugins = [] };
        const int unconfiguredCount = 6000;
        var unconfiguredPresets = Enumerable.Range(0, unconfiguredCount).Select(index => Preset("unconfigured-" + index)).ToArray();
        var unconfiguredProgress = new RecordingProgress();
        var unconfigured = await queue.RunAsync(unconfiguredSettings, unconfiguredPresets, TestExecutable, unconfiguredProgress, CancellationToken.None);
        Equal(unconfiguredCount, unconfigured.Requested, "A fully unconfigured queue still accounts for every input.");
        Equal(unconfiguredCount, unconfigured.Eligible, "Unconfigured records remain eligible once a renderer is supplied.");
        Equal(unconfiguredCount, unconfigured.Unconfigured, "Unconfigured backends are summarized separately from rendering failures.");
        Equal(unconfiguredCount, unconfigured.UnconfiguredSynths[SynthKind.Vital], "Unconfigured summary groups disclose the affected synth and item count.");
        Equal(0, unconfigured.Generated, "A fully unconfigured queue cannot claim generated previews.");
        Equal(0, unconfigured.Failed, "A missing backend is not a failed child render.");
        True(unconfiguredPresets.All(p => p.Status == PresetStatus.Pending && p.Attempts == 0), "Unconfigured outcomes preserve pending state and retry history.");
        True(!Directory.Exists(unconfiguredSettings.PreviewDirectory), "Unconfigured outcomes do not create output directories or start a worker.");
        True(unconfiguredProgress.Snapshots.Length <= 6, "Thousands of metadata-free unconfigured records must be summarized in a bounded number of events, not flood the UI once per path.");
        CheckFinished(unconfiguredProgress, unconfigured);

        var success = Preset("success"); success.ContentHash = Fingerprints.HashText("TEST ONLY progress success");
        var reused = Preset("reused"); reused.ContentHash = Fingerprints.HashText("TEST ONLY progress reused");
        CompletePreview(reused, Path.Combine(directory, "test-only-reused.wav"));
        var failure = Preset("failure"); failure.ContentHash = Fingerprints.HashText("TEST ONLY progress failure");
        var retryLimited = Preset("failure"); retryLimited.ContentHash = Fingerprints.HashText("TEST ONLY progress retry limit");
        retryLimited.Status = PresetStatus.Failed;
        retryLimited.Attempts = settings.MaxAttempts;
        retryLimited.AttemptedCacheKey = Fingerprints.CacheKey(retryLimited, settings.Plugins.Single(), settings.Template);
        var noBackend = Preset("no-backend"); noBackend.Synth = SynthKind.Serum1;
        var mixedProgress = new RecordingProgress();
        var mixed = await queue.RunAsync(settings, [success, reused, failure, retryLimited, noBackend, .. excluded], TestExecutable, mixedProgress, CancellationToken.None);
        Equal(10, mixed.Requested, "A mixed result includes every requested physical record.");
        Equal(5, mixed.Eligible, "Only eligible records contribute to work progress.");
        Equal(1, mixed.Generated, "Committed audio is counted as generated.");
        Equal(1, mixed.Reused, "An existing complete preview is counted as reused.");
        Equal(1, mixed.Failed, "A final child failure is counted exactly once.");
        Equal(1, mixed.RetryLimited, "A record already at its retry limit is reported separately.");
        Equal(1, mixed.Unconfigured, "A record without a backend remains visible in the summary.");
        Equal(1, mixed.UnconfiguredSynths[SynthKind.Serum1], "A mixed result names the unconfigured synth rather than implying a successful render.");
        Equal(5, mixed.Excluded, "Excluded records are not silently lost from the summary.");
        Equal(mixed.Eligible, mixed.Generated + mixed.Reused + mixed.Failed + mixed.RetryLimited + mixed.Unconfigured, "Every eligible item has exactly one final outcome.");
        CheckFinished(mixedProgress, mixed);
        foreach (var preset in new[] { success, reused, failure, retryLimited })
        {
            var events = mixedProgress.Snapshots.Where(p => p.PresetId == preset.Id).ToArray();
            True(events.Length > 0 && events[0].Stage == QueueStages.Validating, "Every eligible item reports validation before preview checks and cache/config work.");
        }
        True(mixedProgress.Snapshots.Any(p => p.PresetId == success.Id && p.Stage == QueueStages.Rendering && p.IsIndeterminate), "A running external renderer is represented as indeterminate work, not a completed item.");

        var cachedProgress = new RecordingProgress();
        var cachedWithoutBackend = await queue.RunAsync(unconfiguredSettings, [reused], TestExecutable, cachedProgress, CancellationToken.None);
        Equal(1, cachedWithoutBackend.Reused, "A valid saved preview is still validated and reused when its backend is now unconfigured.");
        Equal(0, cachedWithoutBackend.Unconfigured, "Bulk unconfigured classification must not hide a complete reusable preview.");
        True(cachedProgress.Snapshots.Any(p => p.PresetId == reused.Id && p.Stage == QueueStages.Validating), "Records with saved preview metadata still enter real validation before backend checks.");
        CheckFinished(cachedProgress, cachedWithoutBackend);

        var needsReview = Preset("review");
        needsReview.ContentHash = Fingerprints.HashText("TEST ONLY generated but not yet confirmed");
        var reviewResult = await queue.RunAsync(settings, [needsReview], TestExecutable, new RecordingProgress(), CancellationToken.None);
        Equal(1, reviewResult.Generated, "A committed preview requiring confirmation still counts as generated audio.");
        Equal(PresetStatus.NeedsReview, needsReview.Status, "A successful but unverified render retains its confirmation requirement.");
        True(File.Exists(needsReview.PreviewPath), "Awaiting confirmation does not mean audio is absent.");
        var persistedReview = store.LoadPresets().Single(p => p.Id == needsReview.Id);
        Equal(PresetStatus.NeedsReview, persistedReview.Status, "Generated-but-unconfirmed status survives persisted indexing.");
        Equal(needsReview.PreviewPath, persistedReview.PreviewPath, "The generated-but-unconfirmed record keeps its committed audio mapping.");
        var reuseReview = await queue.RunAsync(unconfiguredSettings, [persistedReview], TestExecutable, new RecordingProgress(), CancellationToken.None);
        Equal(1, reuseReview.Reused, "Default generation reuses complete NeedsReview audio even with no current renderer.");
        Equal(0, reuseReview.Generated, "Reusing unconfirmed audio must not claim it was rendered again.");
        Equal(PresetStatus.NeedsReview, persistedReview.Status, "Reuse cannot silently mark an unverified preset as confirmed.");

        using (var cancellation = new CancellationTokenSource())
        {
            var preparation = new RecordingProgress(p => { if (p.Stage == QueueStages.Preparing) cancellation.Cancel(); });
            var notStarted = Preset("gate");
            await ThrowsAsync<OperationCanceledException>(() => queue.RunAsync(settings, [notStarted], TestExecutable, preparation, cancellation.Token));
            Equal(0, notStarted.Attempts, "Cancellation from the first preparation event occurs before attempts or child launch.");
            Equal(QueueStages.Stopped, preparation.Snapshots[^1].Stage, "Cancellation has a terminal stopped event rather than a silent disappearance.");
            True(!preparation.Snapshots[^1].IsIndeterminate, "Stopped preparation clears its active progress indicator.");
        }

        var waiting = Preset("gate");
        waiting.SourcePath = Path.Combine(directory, "waiting-test-only.vital");
        waiting.ContentHash = Fingerprints.HashText("TEST ONLY progress waiting");
        var runningProgress = new RecordingProgress();
        using (var cancellation = new CancellationTokenSource())
        {
            var run = queue.RunAsync(settings, [waiting], TestExecutable, runningProgress, cancellation.Token);
            try
            {
                int childPid = await WaitForStartedAsync(waiting.SourcePath + ".started", run);
                await Task.Delay(150);
                var current = runningProgress.Snapshots[^1];
                Equal(QueueStages.Rendering, current.Stage, "While a child is still working, the last progress state remains rendering.");
                True(current.IsIndeterminate && !run.IsCompleted, "A pending child keeps active indeterminate progress until there is an actual result.");
                Equal(0, current.Completed, "Starting a child must not prematurely increment completed work.");
                cancellation.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => run);
                True(!IsRunning(childPid), "Stopping progress also terminates the actual waiting child.");
                var stopped = runningProgress.Snapshots[^1];
                Equal(QueueStages.Stopped, stopped.Stage, "A stopped render has a distinct terminal progress state.");
                True(!stopped.IsIndeterminate && !string.IsNullOrWhiteSpace(stopped.Message), "Stopped rendering clears active progress and leaves a visible summary.");
                Equal(0, stopped.Completed, "Canceled uncommitted work cannot be reported as completed.");
            }
            finally
            {
                cancellation.Cancel();
                try { await run; } catch (OperationCanceledException) { }
            }
        }
    }

    private static async Task TestQueueStopResumeAsync()
    {
        string directory = Fixture("stopped-batch");
        var store = new LibraryStore(Path.Combine(directory, "library.db"));
        store.Initialize();
        var settings = new AppSettings
        {
            PreviewDirectory = Path.Combine(directory, "previews"), MaxAttempts = 1,
            Template = new PreviewTemplate { HoldSeconds = 0.1, TailSeconds = 0.02 },
            Plugins = [new PluginConfiguration { Synth = SynthKind.Vital, Backend = RenderBackend.ExternalWorker, ExecutablePath = TestExecutable, Version = "test-only", TimeoutSeconds = 30 }]
        };
        var first = Preset("success"); first.ContentHash = Fingerprints.HashText("TEST ONLY first source");
        var second = Preset("gate"); second.ContentHash = Fingerprints.HashText("TEST ONLY second source");
        second.SourcePath = Path.Combine(directory, "second-test-only.vital");
        var third = Preset("success"); third.ContentHash = Fingerprints.HashText("TEST ONLY third source");
        var presets = new[] { first, second, third };
        store.UpsertMany(presets);
        var queue = new RenderQueue(store);
        var progress = new InlineProgress();
        string marker = second.SourcePath + ".started";

        async Task StopAtSecondAsync(bool force, Action inspectCommitted)
        {
            if (File.Exists(marker)) File.Delete(marker);
            using var cancellation = new CancellationTokenSource();
            var run = queue.RunAsync(settings, presets, TestExecutable, progress, cancellation.Token, forceRegenerate: force);
            try
            {
                int childPid = await WaitForStartedAsync(marker, run);
                inspectCommitted();
                var elapsed = Stopwatch.StartNew();
                cancellation.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => run);
                elapsed.Stop();
                True(elapsed.Elapsed < TimeSpan.FromSeconds(3), "Stop must end the current child promptly instead of waiting for its 30-second timeout.");
                True(!IsRunning(childPid), "The stopped TEST ONLY child process must be terminated before the queue returns.");
                Console.WriteLine($"  TEST ONLY child stop: {elapsed.Elapsed.TotalMilliseconds:F0} ms ({(force ? "forced" : "default")} batch)");
                True(!Directory.EnumerateDirectories(settings.PreviewDirectory, ".soundpreviewer-*").Any(), "Stop removes uncommitted temporary output directories.");
            }
            finally
            {
                cancellation.Cancel();
                try { await run; } catch (OperationCanceledException) { }
            }
        }

        await StopAtSecondAsync(false, () =>
        {
            Equal(PresetStatus.Ready, first.Status, "First item is committed before the second child starts.");
            Equal(PresetStatus.Ready, store.LoadPresets().Single(p => p.Id == first.Id).Status, "Each committed item is saved immediately, not only at batch completion.");
        });
        string firstKey = first.CacheKey;
        string firstPath = first.PreviewPath;
        DateTime? firstRendered = first.RenderedUtc;
        byte[] firstBytes = File.ReadAllBytes(firstPath);
        Equal(PresetStatus.Pending, second.Status, "An uncommitted stopped item becomes pending.");
        Equal(0, second.Attempts, "A stopped current attempt does not consume the retry budget.");
        Equal(PresetStatus.Pending, third.Status, "A later item is untouched by stop.");
        Equal(PresetStatus.Pending, store.LoadPresets().Single(p => p.Id == second.Id).Status, "Stopped current-item state is persisted immediately.");
        second.Name = "success";
        await queue.RunAsync(settings, presets, TestExecutable, progress, CancellationToken.None);
        True(presets.All(p => p.Status == PresetStatus.Ready), "Resuming completes the remaining items.");
        Equal(firstRendered, first.RenderedUtc, "Resuming skips the already committed first preview.");
        Equal(firstKey, first.CacheKey, "Resume keeps the completed first item's cache mapping.");
        True(File.ReadAllBytes(firstPath).SequenceEqual(firstBytes), "Resume cannot rewrite the completed first item's audio.");
        Equal(4, progress.RenderingCalls, "Resume renders only the two incomplete items after the initial success and stopped attempt.");

        var duplicate = Preset("gate"); duplicate.DuplicateOfId = first.Id;
        int beforeDuplicate = progress.RenderingCalls;
        await queue.RunAsync(settings, [duplicate], TestExecutable, progress, CancellationToken.None);
        Equal(beforeDuplicate, progress.RenderingCalls, "Duplicate records are not separate render tasks.");
        Equal(0, duplicate.Attempts, "Skipping duplicates preserves their attempt history.");

        var oldSecond = JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(second, LibraryJson.Options), LibraryJson.Options)!;
        var oldThird = JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(third, LibraryJson.Options), LibraryJson.Options)!;
        byte[] secondBytes = File.ReadAllBytes(second.PreviewPath);
        settings.Template.Velocity++;
        second.Name = "gate";
        await StopAtSecondAsync(true, () =>
        {
            NotEqual(firstKey, first.CacheKey, "Forced regeneration commits each finished item with the new template.");
            Equal(first.CacheKey, store.LoadPresets().Single(p => p.Id == first.Id).CacheKey, "Forced commits are also durable before the next child starts.");
            True(File.Exists(oldSecond.PreviewPath) && File.Exists(oldThird.PreviewPath), "Force must not clear all previous outputs before processing them.");
            second.Favorite = true; second.Feedback = -1;
        });
        Equal(PresetStatus.Ready, second.Status, "Stopping a forced task preserves its previous complete status.");
        Equal(oldSecond.CacheKey, second.CacheKey, "Stopping force restores the previous cache mapping for the current item.");
        Equal(oldSecond.PreviewPath, second.PreviewPath, "Stopping force restores the previous audio path.");
        Equal(oldSecond.TemplateJson, second.TemplateJson, "Stopping force restores the previous saved template.");
        Equal(oldSecond.Attempts, second.Attempts, "Stopping force restores the previous completed attempt history.");
        Equal(oldSecond.RenderedUtc, second.RenderedUtc, "Stopping force keeps the original completed timestamp.");
        Equal(JsonSerializer.Serialize(oldSecond.Notes, LibraryJson.Options), JsonSerializer.Serialize(second.Notes, LibraryJson.Options), "Stopping force restores the previous note mapping.");
        True(second.Favorite && second.Feedback == -1, "Stopping force must not revert the user's personal feedback.");
        True(File.ReadAllBytes(second.PreviewPath).SequenceEqual(secondBytes), "Stopping force leaves the old committed current-item bytes intact.");
        Equal(oldThird.CacheKey, third.CacheKey, "An unreached forced item retains its old cache mapping.");
        Equal(oldThird.RenderedUtc, third.RenderedUtc, "An unreached forced item retains its old completed timestamp.");
        True(File.ReadAllBytes(firstPath).SequenceEqual(firstBytes), "A new forced cache entry does not remove the previous first-item output.");
        int beforeResume = progress.RenderingCalls;
        second.Name = "success";
        await queue.RunAsync(settings, presets, TestExecutable, progress, CancellationToken.None);
        Equal(beforeResume, progress.RenderingCalls, "A default resume skips all complete outputs, including old-template outputs preserved after force was stopped.");
        Equal(oldSecond.CacheKey, second.CacheKey, "Default resume does not implicitly force a restored old-template preview.");
    }

    private static async Task<int> WaitForStartedAsync(string marker, Task run)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (File.Exists(marker))
            {
                try { return JsonNode.Parse(await File.ReadAllTextAsync(marker))!["Pid"]!.GetValue<int>(); }
                catch (Exception error) when (error is IOException or JsonException) { }
            }
            if (run.IsCompleted) { await run; throw new InvalidOperationException("The TEST ONLY waiting child did not reach its start marker."); }
            await Task.Delay(25);
        }
        throw new TimeoutException("The TEST ONLY waiting child did not start within eight seconds.");
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task<int> FakeWorkerAsync(string[] args)
    {
        string requestPath = Argument(args, "--request");
        string resultPath = Argument(args, "--result");
        var request = JsonSerializer.Deserialize<RenderRequest>(await File.ReadAllTextAsync(requestPath), LibraryJson.Options)!;
        string mode = request.Preset.Name;
        if (mode == "guard-marker-success") await File.WriteAllTextAsync(request.OutputPath + ".external-ran", "TEST ONLY external renderer invocation marker");
        if (mode == "gate")
        {
            await File.WriteAllTextAsync(request.Preset.SourcePath + ".started", JsonSerializer.Serialize(new { Pid = Environment.ProcessId }));
            await Task.Delay(TimeSpan.FromSeconds(30));
            return 0;
        }
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
            NeedsReview = mode == "review",
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
        string pluginDirectory = Path.Combine(directory, "TEST-ONLY-plugins");
        Directory.CreateDirectory(pluginDirectory);
        WritePeFixture(Path.Combine(pluginDirectory, "TEST-ONLY-VST2-x64.dll"), x64: true, "VSTPluginMain");
        var discovery = await new PluginScanService().ScanAsync([pluginDirectory]);
        var settings = new AppSettings
        {
            PreviewDirectory = directory, Template = template,
            Sources = [new SourceFolder { Id = "test-only-ui-fixture", Path = directory, Synth = SynthKind.Unknown, AutoDetect = true }],
            PluginDirectories = [pluginDirectory], DiscoveredPlugins = discovery.Plugins
        };
        var preset = new PresetRecord
        {
            Id = Fingerprints.PresetId(SynthKind.Vital, source), Name = "TEST AUDIO: 440Hz", Synth = SynthKind.Vital,
            SourceId = "test-only-ui-fixture", SourcePath = source, ContentHash = Fingerprints.HashFile(source),
            ContentMd5 = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(source))).ToLowerInvariant(),
            FileSize = new FileInfo(source).Length, LastWriteUtc = File.GetLastWriteTimeUtc(source),
            Package = "TEST ONLY / no synthesizer loaded", Status = PresetStatus.Ready,
            PreviewPath = preview, Metrics = WaveFile.Validate(preview), AdapterVersion = "test-only-ui-fixture-v1",
            PluginVersion = "no synthesizer loaded", RenderedUtc = DateTime.UtcNow,
            Message = "TEST ONLY: generated 440Hz IEEE float32 PCM; no plugin compatibility claim.",
            DetectionReason = "TEST ONLY: .vital suffix; audio is a synthetic fixture, not a rendered preset.",
            Tags = ["Vital", ".vital", "TEST ONLY"],
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
        string copiedSource = Path.Combine(directory, "TEST-ONLY-440Hz-copied.vital");
        File.Copy(source, copiedSource, overwrite: true);
        var duplicate = JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(preset, LibraryJson.Options), LibraryJson.Options)!;
        duplicate.Id = Fingerprints.PresetId(SynthKind.Vital, copiedSource);
        duplicate.SourcePath = copiedSource;
        duplicate.Name = "TEST ONLY: duplicate 440Hz";
        duplicate.DuplicateOfId = preset.Id;
        duplicate.Tags.Add("TEST ONLY duplicate");
        store.Upsert(duplicate);
        string unknownSource = Path.Combine(directory, "TEST-ONLY-UNKNOWN.fxp");
        WriteFxp(unknownSource, 1, pluginId: "nope");
        store.Upsert(new PresetRecord
        {
            Id = Fingerprints.PresetId(SynthKind.Unknown, unknownSource), Name = "TEST ONLY: unidentified FXP",
            Synth = SynthKind.Unknown, SourceId = "test-only-ui-fixture", SourcePath = unknownSource,
            ContentHash = Fingerprints.HashFile(unknownSource), ContentMd5 = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(unknownSource))).ToLowerInvariant(),
            FileSize = new FileInfo(unknownSource).Length,
            LastWriteUtc = File.GetLastWriteTimeUtc(unknownSource), Status = PresetStatus.Unidentified,
            Package = "TEST ONLY / mixed root", DetectionReason = "TEST ONLY: product ID nope is not validated; no synthesizer was loaded.",
            Tags = ["未识别", ".fxp", "TEST ONLY"], Message = "TEST ONLY: unknown fixture must not render."
        });
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

    private static void CompletePreview(PresetRecord preset, string path)
    {
        var template = new PreviewTemplate { HoldSeconds = 0.1, TailSeconds = 0.02 };
        int frames = (int)Math.Round(template.SampleRate * (template.HoldSeconds + template.TailSeconds));
        double duration = frames / (double)template.SampleRate;
        WaveFile.WritePcm16(path, Tone(template.SampleRate, 1, duration * 3));
        preset.PreviewPath = path;
        preset.CacheKey = Fingerprints.HashText("TEST ONLY preview " + preset.Id);
        preset.AttemptedCacheKey = preset.CacheKey;
        preset.TemplateJson = JsonSerializer.Serialize(template, LibraryJson.Options);
        preset.Metrics = WaveFile.Validate(path);
        preset.AdapterVersion = "test-only-complete-fixture-v1";
        preset.PluginVersion = "TEST ONLY: no synthesizer loaded";
        preset.RenderedUtc = DateTime.UtcNow;
        preset.Status = PresetStatus.Ready;
        preset.Notes = template.Pitches.Select((pitch, index) => new NotePreview
        {
            MidiNote = pitch, StartSeconds = index * duration, DurationSeconds = duration,
            ActualHoldSeconds = template.HoldSeconds, ActualTailSeconds = duration - template.HoldSeconds,
            ActualVelocity = template.Velocity
        }).ToList();
    }

    private static void CreateLegacyDatabase(string path, PresetRecord record)
    {
        var type = typeof(LibraryStore).Assembly.GetType("SoundPreviewer.Core.Storage.NativeSqlite", throwOnError: true)!;
        using var database = (IDisposable)Activator.CreateInstance(type, path)!;
        var execute = type.GetMethod("Execute")!;
        void Execute(string sql, params object?[] parameters) => execute.Invoke(database, [sql, parameters]);
        Execute("CREATE TABLE presets (id TEXT PRIMARY KEY NOT NULL, source_id TEXT NOT NULL, synth INTEGER NOT NULL, source_path TEXT NOT NULL, internal_path TEXT NOT NULL, content_hash TEXT NOT NULL, status INTEGER NOT NULL, favorite INTEGER NOT NULL, feedback INTEGER NOT NULL, json TEXT NOT NULL, updated_utc TEXT NOT NULL);");
        Execute("CREATE TABLE settings (key TEXT PRIMARY KEY NOT NULL, json TEXT NOT NULL);");
        Execute("PRAGMA user_version=1;");
        var legacy = JsonNode.Parse(JsonSerializer.Serialize(record, LibraryJson.Options))!.AsObject();
        legacy.Remove(nameof(PresetRecord.ContentMd5)); legacy.Remove(nameof(PresetRecord.DuplicateOfId));
        Execute("INSERT INTO presets (id,source_id,synth,source_path,internal_path,content_hash,status,favorite,feedback,json,updated_utc) VALUES (?,?,?,?,?,?,?,?,?,?,?);",
            record.Id, record.SourceId, (int)record.Synth, record.SourcePath, record.InternalPath, record.ContentHash,
            (int)record.Status, record.Favorite, record.Feedback, legacy.ToJsonString(), DateTime.UtcNow.ToString("O"));
    }

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
    private static void WriteFxp(string path, byte payload, bool totalLength = false, string pluginId = "")
    {
        byte[] bytes = new byte[60];
        "CcnK"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), totalLength ? 60u : 52u);
        "FPCh"u8.CopyTo(bytes.AsSpan(8));
        if (pluginId.Length > 0) Encoding.ASCII.GetBytes(pluginId).CopyTo(bytes.AsSpan(16, 4));
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
    private static void WriteVstBank(string path, string pluginId, int trailingBytes = 0)
    {
        byte[] bytes = new byte[160 + trailingBytes];
        "CcnK"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), 152);
        "FBCh"u8.CopyTo(bytes.AsSpan(8));
        Encoding.ASCII.GetBytes(pluginId).CopyTo(bytes.AsSpan(16, 4));
        File.WriteAllBytes(path, bytes);
    }
    private static void WritePeFixture(string path, bool x64, params string[] exports)
    {
        byte[] bytes = new byte[0x600];
        void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        void U64(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), value);
        "MZ"u8.CopyTo(bytes); U32(0x3c, 0x80); "PE\0\0"u8.CopyTo(bytes.AsSpan(0x80));
        U16(0x84, x64 ? (ushort)0x8664 : (ushort)0x14c); U16(0x86, 1);
        int optionalSize = x64 ? 0xf0 : 0xe0;
        U16(0x94, (ushort)optionalSize); U16(0x96, 0x2022);
        int optional = 0x98;
        U16(optional, x64 ? (ushort)0x20b : (ushort)0x10b);
        U32(optional + 16, 0x1300); U32(optional + 20, 0x1000);
        if (x64) U64(optional + 24, 0x180000000); else U32(optional + 28, 0x10000000);
        U32(optional + 32, 0x1000); U32(optional + 36, 0x200);
        U16(optional + 40, 6); U16(optional + 48, 6);
        U32(optional + 56, 0x2000); U32(optional + 60, 0x200); U16(optional + 68, 3);
        int directories = optional + (x64 ? 112 : 96);
        U32(directories - 4, 16);
        if (exports.Length > 0) { U32(directories, 0x1000); U32(directories + 4, 0x100); }
        int section = optional + optionalSize;
        ".rdata"u8.CopyTo(bytes.AsSpan(section));
        U32(section + 8, 0x400); U32(section + 12, 0x1000);
        U32(section + 16, 0x400); U32(section + 20, 0x200); U32(section + 36, 0x40000040);
        if (exports.Length > 0)
        {
            U32(0x20c, 0x1070); U32(0x210, 1);
            U32(0x214, (uint)exports.Length); U32(0x218, (uint)exports.Length);
            U32(0x21c, 0x1040); U32(0x220, 0x1080); U32(0x224, 0x10c0);
            "test-only-fixture.dll\0"u8.CopyTo(bytes.AsSpan(0x270));
            int nameOffset = 0x300;
            for (int index = 0; index < exports.Length; index++)
            {
                U32(0x240 + index * 4, (uint)(0x1300 + index));
                U32(0x280 + index * 4, (uint)(nameOffset + 0xe00));
                U16(0x2c0 + index * 2, (ushort)index);
                byte[] name = Encoding.ASCII.GetBytes(exports[index] + "\0");
                name.CopyTo(bytes.AsSpan(nameOffset)); nameOffset += name.Length;
            }
        }
        File.WriteAllBytes(path, bytes);
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

    private sealed record ProgressSnapshot(int Completed, int Total, string Stage, bool IsIndeterminate, string Message, string? PresetId);

    private sealed class RecordingProgress(Action<QueueProgress>? onReport = null) : IProgress<QueueProgress>
    {
        private readonly object gate = new();
        private readonly List<ProgressSnapshot> snapshots = [];
        public ProgressSnapshot[] Snapshots { get { lock (gate) return snapshots.ToArray(); } }
        public void Report(QueueProgress value)
        {
            lock (gate) snapshots.Add(new(value.Completed, value.Total, value.Stage, value.IsIndeterminate, value.Message, value.Preset?.Id));
            onReport?.Invoke(value);
        }
    }
}
