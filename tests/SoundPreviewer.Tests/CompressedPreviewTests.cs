using System.Text.Json;
using System.Text.Json.Nodes;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static async Task TestWindowsAacCrossPlatformAsync()
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "NativeAac");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "windows-mf-fixtures.json")));
        var files = manifest.RootElement.GetProperty("Files").EnumerateArray().ToArray();
        Equal(2, files.Length, "Both 44.1 and 48 kHz Windows-produced AAC fixtures must ship with the test executable.");
        var reports = new List<object>();
        foreach (var entry in files)
        {
            string path = Path.Combine(fixtures, entry.GetProperty("File").GetString()!);
            Equal(entry.GetProperty("Sha256").GetString(), Fingerprints.HashFile(path), "The cross-platform sample must remain the exact checked-in Windows AAC bytes.");
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            {
                Throws<PlatformNotSupportedException>(() => PreviewAudioFile.Read(path));
                continue;
            }
            // Windows and macOS must both actually decode these Windows-produced bytes; failures are not skipped.
            int rate = entry.GetProperty("SampleRate").GetInt32(), frames = entry.GetProperty("OriginalFrames").GetInt32();
            try
            {
                var audio = PreviewAudioFile.Read(path);
                Equal(rate, audio.SampleRate, "A cache copied between platforms keeps its original sample rate.");
                Equal(2, audio.Channels, "A cache copied between platforms retains stereo.");
                Equal(frames, audio.Frames, "Cross-platform AAC decoding cannot lose frames or expose codec padding to the note map.");
                var reference = WindowsAacFixtureReference(rate);
                var regions = new List<object>();
                for (int region = 0; region < 3; region++)
                {
                    int first = region * (rate / 2) + 256, end = first + 7744, bestShift = int.MaxValue;
                    double bestScore = double.NegativeInfinity;
                    for (int shift = -4096; shift <= 4096; shift++)
                    {
                        double dot = 0, referenceEnergy = 0, decodedEnergy = 0;
                        for (int frame = first; frame < end; frame += 3)
                        {
                            int candidate = frame + shift;
                            if (candidate < 0 || candidate >= audio.Frames) continue;
                            double expected = reference.Samples[frame * 2], actual = audio.Samples[candidate * 2];
                            dot += expected * actual; referenceEnergy += expected * expected; decodedEnergy += actual * actual;
                        }
                        double score = referenceEnergy > 0 && decodedEnergy > 0 ? dot / Math.Sqrt(referenceEnergy * decodedEnergy) : -1;
                        if (score > bestScore) { bestScore = score; bestShift = shift; }
                    }
                    True(bestScore >= .85, $"Windows AAC region {region} must retain its recognizable waveform on this platform (score {bestScore:0.000}).");
                    True(Math.Abs(bestShift) / (double)rate <= .001,
                        $"Windows AAC region {region} start drift exceeds 1 ms on this platform: {bestShift} frames at {rate} Hz.");
                    regions.Add(new { Region = region, ShiftFrames = bestShift, ShiftSeconds = bestShift / (double)rate, Correlation = bestScore });
                }
                reports.Add(new { File = Path.GetFileName(path), SampleRate = rate, audio.Channels, audio.Frames, Regions = regions });
            }
            catch (Exception error) when (OperatingSystem.IsMacOS())
            {
                string diagnostic = await MacAacDecodeDiagnosticsAsync(path, rate);
                throw new InvalidOperationException("Windows AAC fixture decode failed: " + error.Message + "\n" + diagnostic, error);
            }
        }
        string report = Path.Combine(Fixture("windows-aac-cross-platform"), "windows-aac-compatibility.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { Platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            WindowsProducedFilesDecoded = reports.Count, Files = reports }, LibraryJson.Options));
        Console.WriteLine("Windows-produced AAC compatibility report: " + report);
    }

    private static WaveData WindowsAacFixtureReference(int rate)
    {
        int frames = rate * 3 / 2;
        float[] samples = new float[frames * 2];
        var random = new Random(9917);
        for (int frame = 0; frame < frames; frame++)
        {
            double time = frame / (double)rate;
            double fade = Math.Min(1, frame / 64.0) * Math.Min(1, (frames - frame) / 64.0);
            double localTime = frame % (rate / 2) / (double)rate;
            int frequency = frame < rate / 2 ? 523 : frame < rate ? 733 : 1031;
            samples[frame * 2] = (float)(fade * .25 * (Math.Sin(2 * Math.PI * (frequency * localTime + 5000 * Math.Pow(localTime, 2))) + .15 * (random.NextDouble() * 2 - 1)));
            samples[frame * 2 + 1] = (float)(fade * .2 * Math.Cos(2 * Math.PI * 351 * time));
        }
        return new WaveData { SampleRate = rate, Channels = 2, Samples = samples };
    }

    private static async Task TestCompressedPreviewCodecAsync()
    {
        string directory = Fixture("native-aac-three-registers");
        var rates = new[] { 44100, 48000 };
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            string unsupported = Path.Combine(directory, "unsupported.m4a");
            await ThrowsAsync<PlatformNotSupportedException>(() => PreviewAudioFile.EncodeAsync(ThreeRegisterAudio(44100, 2), unsupported));
            True(!File.Exists(unsupported), "An unsupported platform must reject AAC rather than publish a mislabeled WAV.");
            return;
        }

        foreach (int rate in rates)
            foreach (int channels in new[] { 1, 2 })
            {
                var original = ThreeRegisterAudio(rate, channels);
                string m4a = Path.Combine(directory, $"TEST ONLY 三音区-{rate}-{channels}.m4a");
                string wav = Path.ChangeExtension(m4a, ".wav");
                WaveFile.WritePcm16(wav, original);
                await PreviewAudioFile.EncodeAsync(original, m4a);
                True(PreviewAudioFile.IsCompressed(m4a) && PreviewAudioFile.IsSupportedExtension(m4a), "AAC is an explicitly supported preview format.");
                var decoded = PreviewAudioFile.Read(m4a);
                Equal(rate, decoded.SampleRate, "AAC does not downsample 44.1 or 48 kHz input.");
                Equal(channels, decoded.Channels, "AAC retains mono/stereo channel layout.");
                Equal(original.Frames, decoded.Frames, "AAC priming and terminal padding cannot shift the stored note timeline.");
                True(new FileInfo(m4a).Length < new FileInfo(wav).Length * .5, "Native AAC meaningfully reduces the PCM preview size.");
                var metrics = PreviewAudioFile.Validate(m4a);
                True(!metrics.IsSilent && !metrics.HasNonFinite && metrics.SampleRate == rate && metrics.Channels == channels,
                    "The stored AAC is decoded and validated rather than trusted by its extension.");
                AssertThreeRegisterAudio(original, decoded);
                var template = new PreviewTemplate { SampleRate = rate, QuickPreview = false, HoldSeconds = .4, TailSeconds = .1,
                    NoteOrder = PreviewNoteOrder.LowMiddleHigh };
                var notes = template.Pitches.Select((pitch, index) => new NotePreview { MidiNote = pitch, StartSeconds = index * .5,
                    DurationSeconds = .5, ActualHoldSeconds = .4, ActualTailSeconds = .1, ActualVelocity = template.Velocity }).ToList();
                WorkerProcessClient.ValidateNotes(new RenderResult { Success = true, OutputPath = m4a, Metrics = metrics, Notes = notes,
                    AdapterVersion = "TEST ONLY AAC roundtrip" }, template);

                byte[] committed = File.ReadAllBytes(m4a);
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => PreviewAudioFile.EncodeAsync(original, m4a, canceled.Token));
                Throws<OperationCanceledException>(() => PreviewAudioFile.Read(m4a, canceled.Token));
                True(File.ReadAllBytes(m4a).SequenceEqual(committed), "Canceled encoding preserves an existing committed AAC byte for byte.");
                string damaged = Path.Combine(directory, $"damaged-{rate}-{channels}.m4a");
                File.WriteAllBytes(damaged, committed[..^1]);
                Throws<InvalidDataException>(() => PreviewAudioFile.Validate(damaged), "A truncated timing/integrity marker is not accepted as an aligned preview.");
                True(PreviewAudioFile.IsSupportedExtension(wav) && !PreviewAudioFile.IsCompressed(wav), "Old WAV previews remain supported.");
                Equal(original.Frames, PreviewAudioFile.Read(wav).Frames, "Unified preview reading preserves existing WAV frame counts.");
            }
        True(!PreviewAudioFile.IsSupportedExtension("not-a-preview.mp3"), "Unknown preview formats are rejected explicitly.");
        True(!Directory.EnumerateFiles(directory, ".aac-*", SearchOption.AllDirectories).Any(), "AAC success and cancellation leave no temporary audio behind.");
    }

    private static async Task TestCompressedPreviewQueueAsync()
    {
        Equal(PreviewAudioFormat.Aac, new AppSettings().PreviewFormat, "New settings select compact AAC previews.");
        Equal(PreviewAudioFormat.Aac, JsonSerializer.Deserialize<AppSettings>("{}", LibraryJson.Options)!.PreviewFormat,
            "Older settings without a format field adopt AAC without changing their existing cache records.");
        Equal(0, (int)PreviewAudioFormat.Aac, "The serialized AAC format value remains fixed.");
        Equal(1, (int)PreviewAudioFormat.Wav, "The serialized WAV opt-out remains fixed.");
        string directory = Fixture("aac-queue-and-legacy-reuse");
        var store = new LibraryStore(Path.Combine(directory, "library.db"));
        var settings = CompressedQueueSettings(directory);
        var queue = new RenderQueue(store);
        var preset = Preset("stereo");
        preset.ContentHash = Fingerprints.HashText("TEST ONLY independent stereo source");
        var progress = new InlineProgress();
        var first = await queue.RunAsync(settings, [preset], TestExecutable, progress, CancellationToken.None);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Equal(0, first.Generated, "An unsupported AAC platform cannot report generated audio.");
            True(first.Failed == 1 && preset.PreviewPath.Length == 0, "Unavailable native AAC leaves an explicit failed record without a fake output.");
            return;
        }
        Equal(1, first.Generated, "The default queue commits an actual AAC preview.");
        Equal(".m4a", Path.GetExtension(preset.PreviewPath), "New queue cache names use the compressed extension.");
        var decoded = PreviewAudioFile.Read(preset.PreviewPath);
        Equal(48000, decoded.SampleRate, "Queue AAC retains the selected 48 kHz sample rate.");
        Equal(2, decoded.Channels, "The queue retains a stereo worker output.");
        string firstPath = preset.PreviewPath, firstKey = preset.CacheKey;
        byte[] firstBytes = File.ReadAllBytes(firstPath);
        var legacy = Preset("guard-marker-success");
        legacy.ContentHash = Fingerprints.HashText("TEST ONLY independent legacy WAV");
        string legacyPath = Path.Combine(directory, "TEST ONLY old standalone.wav");
        CompletePreview(legacy, legacyPath, settings.Template);
        legacy.Favorite = true; legacy.ManualTags = ["TEST ONLY personal tag"]; legacy.Tags = ["TEST ONLY personal tag"];
        store.Upsert(legacy);
        byte[] legacyBytes = File.ReadAllBytes(legacyPath);
        int calls = progress.RenderingCalls;
        var reused = await queue.RunAsync(settings, [legacy], Path.Combine(directory, "missing-worker"), progress, CancellationToken.None);
        Equal(1, reused.Reused, "A complete legacy WAV is reused before checking a worker or attempting compression.");
        Equal(calls, progress.RenderingCalls, "Choosing AAC never implicitly renders a complete legacy WAV again.");
        True(File.ReadAllBytes(legacyPath).SequenceEqual(legacyBytes), "Default reuse leaves the old WAV bytes unchanged.");
        Equal(legacyPath, legacy.PreviewPath, "Default reuse retains the old WAV path.");
        True(legacy.Favorite && legacy.Tags.SequenceEqual(["TEST ONLY personal tag"]), "Legacy reuse preserves personal metadata.");
        string legacyJson = JsonSerializer.Serialize(legacy, LibraryJson.Options);

        settings.PreviewFormat = PreviewAudioFormat.Wav;
        Equal(firstKey, Fingerprints.CacheKey(preset, settings.Plugins.Single(), settings.Template), "Storage format is independent of the rendered audio cache identity.");
        var keep = await queue.RunAsync(settings, [preset], TestExecutable, progress, CancellationToken.None);
        Equal(1, keep.Reused, "Changing the format preference keeps existing complete AAC by default.");
        True(File.ReadAllBytes(firstPath).SequenceEqual(firstBytes), "A format preference change does not rewrite current audio.");
        var forcedWav = await queue.RunAsync(settings, [preset], TestExecutable, progress, CancellationToken.None, forceRegenerate: true);
        Equal(1, forcedWav.Generated, "Explicit regeneration can opt back into WAV.");
        Equal(".wav", Path.GetExtension(preset.PreviewPath), "The explicit WAV preference is honored on new output.");
        Equal(firstKey, preset.CacheKey, "Changing only the stored format preserves the render identity.");
        True(!File.Exists(firstPath), "After a committed replacement only an unshared owned AAC is reclaimed.");
        string forcedWavPath = preset.PreviewPath;
        settings.PreviewFormat = PreviewAudioFormat.Aac;
        Equal(1, (await queue.RunAsync(settings, [preset], TestExecutable, progress, CancellationToken.None, forceRegenerate: true)).Generated,
            "Explicit regeneration can move the same identity back to AAC.");
        True(!File.Exists(forcedWavPath), "A successfully replaced unshared owned WAV is reclaimed too.");
        string committedPath = preset.PreviewPath;
        byte[] committed = File.ReadAllBytes(committedPath);
        preset.Name = "failure";
        Equal(1, (await queue.RunAsync(settings, [preset], TestExecutable, progress, CancellationToken.None, forceRegenerate: true)).Failed,
            "A failed AAC replacement is recorded as a failed attempt.");
        Equal(committedPath, preset.PreviewPath, "Failed replacement retains the previous AAC mapping.");
        True(File.ReadAllBytes(committedPath).SequenceEqual(committed), "Failed replacement cannot overwrite the committed AAC bytes.");
        preset.Name = "gate";
        using var cancellation = new CancellationTokenSource();
        var run = queue.RunAsync(settings, [preset], TestExecutable, progress, cancellation.Token, forceRegenerate: true);
        try
        {
            await WaitForStartedAsync(preset.SourcePath + ".started", run);
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => run);
        }
        finally { cancellation.Cancel(); try { await run; } catch (OperationCanceledException) { } }
        Equal(committedPath, preset.PreviewPath, "Stopping a replacement restores its existing AAC path.");
        True(File.ReadAllBytes(committedPath).SequenceEqual(committed), "Stopping a replacement preserves the old AAC bytes.");
        Equal(JsonSerializer.Serialize(legacy, LibraryJson.Options), legacyJson, "Other records remain untouched by AAC regeneration and cancellation.");
        True(!Directory.EnumerateDirectories(settings.PreviewDirectory, ".soundpreviewer-*").Any() &&
            !Directory.EnumerateFiles(settings.PreviewDirectory, ".sp-*", SearchOption.TopDirectoryOnly).Any(),
            "Completed and stopped AAC work removes private staging audio.");

        var raw = Request("stereo", Path.Combine(directory, "direct-worker-contract.wav"));
        var direct = await new WorkerProcessClient().RenderAsync(raw, TestExecutable, CancellationToken.None);
        True(direct.Success && File.ReadAllBytes(raw.OutputPath).AsSpan(0, 4).SequenceEqual("RIFF"u8),
            "A caller explicitly asking WorkerProcessClient for WAV still receives a real WAV.");
    }

    private static async Task TestCompressedPreviewStorageAsync()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return; // Native rejection is asserted in the codec and queue tests.
        foreach (var mode in Enum.GetValues<PreviewDirectoryChangeMode>())
        {
            var fixture = CreatePreviewStorageFixture("aac-storage-" + mode);
            var record = fixture.Store.LoadPresets().Single();
            string oldWav = record.PreviewPath;
            string compressed = Path.ChangeExtension(oldWav, ".m4a");
            await PreviewAudioFile.EncodeAsync(WaveFile.Read(oldWav), compressed);
            record.PreviewPath = compressed;
            fixture.Store.Upsert(record);
            File.Delete(oldWav);
            string unowned = Path.Combine(fixture.Root, Fingerprints.HashText("TEST ONLY unowned AAC") + ".m4a");
            File.Copy(compressed, unowned);
            byte[] bytes = File.ReadAllBytes(compressed), unownedBytes = File.ReadAllBytes(unowned);
            if (mode == PreviewDirectoryChangeMode.Migrate)
            {
                string target = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "new-library");
                PreviewStorageService.RelocateLibrary(fixture.Store, fixture.Settings, target, mode);
                var movedStore = new LibraryStore(Path.Combine(target, "library.db"));
                var moved = movedStore.LoadPresets().Single();
                True(moved.PreviewPath.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) && File.Exists(moved.PreviewPath), "Migration retains compressed audio and its extension.");
                True(File.ReadAllBytes(moved.PreviewPath).SequenceEqual(bytes) && !File.Exists(compressed), "Migration copies AAC byte for byte before retiring its owned old file.");
                Equal(record.CacheKey, moved.CacheKey, "Migration retains the compressed preview's existing render identity.");
                True(moved.Favorite && moved.Tags.SequenceEqual(record.Tags), "AAC migration preserves personal metadata.");
                Equal(fixture.Key, moved.CacheKey, "Migration never requests a new render identity just for AAC.");
                string baseline = movedStore.LoadSettings().Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).InitBaselinePath;
                True(StorageBaselinePaths(JsonNode.Parse(File.ReadAllText(baseline))!).All(path => Path.GetExtension(path) == ".wav" && File.Exists(PreviewPathStorage.Resolve(path, target))),
                    "Migration retains initialization comparison audio as WAV even beside compressed previews.");
            }
            else
            {
                PreviewStorageService.ClearPreviews(fixture.Store, fixture.Settings);
                True(!File.Exists(compressed) && fixture.Store.LoadPresets().Single().PreviewPath.Length == 0,
                    "Clear removes a referenced owned AAC and revokes its mapping.");
            }
            True(File.ReadAllBytes(unowned).SequenceEqual(unownedBytes) && File.Exists(fixture.Source) && File.Exists(fixture.Unrelated),
                "AAC cleanup and migration preserve unowned audio, presets and unrelated WAVs.");
        }
    }

    private static async Task TestPreviewCompressionServiceAsync()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return; // Native unsupported behavior is explicitly asserted above.
        var fixture = CompressionFixture("compress-owned-shared", shared: true);
        var before = fixture.Store.LoadPresets().ToDictionary(record => record.Id);
        string sourceHash = Fingerprints.HashFile(fixture.Records[0].SourcePath);
        string unowned = Path.Combine(fixture.Root, Fingerprints.HashText("TEST ONLY unreferenced cache") + ".wav");
        WaveFile.WritePcm16(unowned, ThreeRegisterAudio(44100, 1));
        string unownedM4a = Path.ChangeExtension(unowned, ".m4a");
        File.WriteAllText(unownedM4a, "TEST ONLY another library's private AAC sentinel");
        string baseline = Path.Combine(fixture.Root, "InitializationBaselines", "TEST-ONLY-baseline.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
        WaveFile.WritePcm16(baseline, ThreeRegisterAudio(44100, 1));
        string baselineHash = Fingerprints.HashFile(baseline), unownedHash = Fingerprints.HashFile(unowned), unownedM4aHash = Fingerprints.HashFile(unownedM4a);
        var progressValues = new List<PreviewCompressionProgress>();
        var result = await PreviewCompressionService.CompressAsync(fixture.Store, fixture.Settings, new CompressionTestProgress(progressValues.Add), CancellationToken.None);
        Equal(1, result.Compressed, "Several DB references to the same owned WAV encode only one file.");
        Equal(0, result.Failed, "A valid owned WAV compresses successfully.");
        True(result.FreedBytes > 0 && !File.Exists(fixture.Wav), "The old owned WAV is reclaimed only after the smaller AAC is committed.");
        var after = fixture.Store.LoadPresets();
        Equal(1, after.Select(record => record.PreviewPath).Distinct().Count(), "All references to one WAV move to the same AAC.");
        foreach (var record in after)
        {
            var previous = before[record.Id];
            Equal(".m4a", Path.GetExtension(record.PreviewPath), "Explicit compression publishes AAC cache mappings.");
            var expected = JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(previous, LibraryJson.Options), LibraryJson.Options)!;
            expected.PreviewPath = record.PreviewPath;
            Equal(JsonSerializer.Serialize(expected, LibraryJson.Options), JsonSerializer.Serialize(record, LibraryJson.Options),
                "Only the audio path changes; cache identity, notes, status, feedback and render provenance remain unchanged.");
            Equal(previous.Metrics!.SampleRate, PreviewAudioFile.Read(record.PreviewPath).SampleRate, "Explicit compression preserves the old preview sample rate.");
        }
        True(progressValues.Any(value => value.Compressed == 1), "Compression progress counts completed files independently of database references.");
        Equal(sourceHash, Fingerprints.HashFile(fixture.Records[0].SourcePath), "Compression never rewrites the source preset.");
        Equal(baselineHash, Fingerprints.HashFile(baseline), "Startup comparison WAVs are never converted into AAC.");
        Equal(unownedHash, Fingerprints.HashFile(unowned), "An unreferenced hash-shaped WAV is not claimed by compression.");
        Equal(unownedM4aHash, Fingerprints.HashFile(unownedM4a), "Unreferenced AAC remains private and unchanged.");
        Equal(0, (await PreviewCompressionService.CompressAsync(fixture.Store, fixture.Settings, null, CancellationToken.None)).Compressed,
            "Running compression again does not re-encode existing AAC.");

        foreach (string protection in new[] { "external", "source", "source-root", "nested-library", "existing-target" })
        {
            var guarded = CompressionFixture("compress-protected-" + protection);
            var record = guarded.Store.LoadPresets().Single();
            string protectedPath = guarded.Wav;
            if (protection == "external")
            {
                protectedPath = Path.Combine(Path.GetDirectoryName(guarded.Root)!, record.CacheKey + ".wav");
                File.Move(guarded.Wav, protectedPath);
                record.PreviewPath = protectedPath;
            }
            else if (protection == "source") record.SourcePath = protectedPath;
            else if (protection == "source-root") guarded.Settings.Sources = [new SourceFolder { Path = guarded.Root, Synth = SynthKind.Vital }];
            else if (protection == "nested-library")
            {
                string nested = Path.Combine(guarded.Root, "other-library");
                Directory.CreateDirectory(nested);
                protectedPath = Path.Combine(nested, record.CacheKey + ".wav");
                File.Move(guarded.Wav, protectedPath);
                record.PreviewPath = protectedPath;
                new LibraryStore(Path.Combine(nested, "library.db")).Initialize();
            }
            string destination = Path.ChangeExtension(protectedPath, ".m4a");
            if (protection == "existing-target") File.WriteAllText(destination, "TEST ONLY unrelated existing M4A; do not overwrite");
            guarded.Store.SaveState([record], guarded.Settings);
            string originalJson = JsonSerializer.Serialize(guarded.Store.LoadPresets(), LibraryJson.Options);
            byte[] protectedBytes = File.ReadAllBytes(protectedPath);
            byte[]? destinationBytes = File.Exists(destination) ? File.ReadAllBytes(destination) : null;
            var skipped = await PreviewCompressionService.CompressAsync(guarded.Store, guarded.Settings, null, CancellationToken.None);
            Equal(0, skipped.Compressed, "Protected or conflicting files cannot be compressed: " + protection);
            True(File.ReadAllBytes(protectedPath).SequenceEqual(protectedBytes), "Protected WAV bytes remain intact: " + protection);
            Equal(originalJson, JsonSerializer.Serialize(guarded.Store.LoadPresets(), LibraryJson.Options), "Protected mappings and metadata stay untouched: " + protection);
            True(destinationBytes is null ? !File.Exists(destination) : File.ReadAllBytes(destination).SequenceEqual(destinationBytes),
                "Protected compression cannot publish or replace a conflicting AAC: " + protection);
        }

        var rollback = CompressionFixture("compress-database-rollback", shared: true);
        string rollbackJson = JsonSerializer.Serialize(rollback.Store.LoadPresets(), LibraryJson.Options);
        byte[] rollbackAudio = File.ReadAllBytes(rollback.Wav);
        using (var database = new CacheTestSqlite(rollback.Store.DatabasePath))
            database.Execute("CREATE TRIGGER reject_compressed_mapping BEFORE INSERT ON presets WHEN instr(NEW.json,'.m4a')>0 BEGIN SELECT RAISE(ABORT,'TEST ONLY compression commit failure'); END;");
        var failed = await PreviewCompressionService.CompressAsync(rollback.Store, rollback.Settings, null, CancellationToken.None);
        Equal(1, failed.Failed, "A database commit failure is reported as one failed source WAV.");
        True(failed.Warnings.Count > 0 && File.ReadAllBytes(rollback.Wav).SequenceEqual(rollbackAudio), "A failed commit leaves an actionable reason and all old WAV bytes.");
        Equal(rollbackJson, JsonSerializer.Serialize(rollback.Store.LoadPresets(), LibraryJson.Options), "Shared-reference changes roll back together on database failure.");
        True(!File.Exists(Path.ChangeExtension(rollback.Wav, ".m4a")), "A failed commit does not leave a newly claimed AAC cache behind.");

        var canceled = CompressionFixture("compress-canceled-before-start");
        string canceledJson = JsonSerializer.Serialize(canceled.Store.LoadPresets(), LibraryJson.Options);
        byte[] canceledAudio = File.ReadAllBytes(canceled.Wav);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => PreviewCompressionService.CompressAsync(canceled.Store, canceled.Settings, null, cancellation.Token));
        }
        Equal(canceledJson, JsonSerializer.Serialize(canceled.Store.LoadPresets(), LibraryJson.Options), "Cancel before compression leaves every record untouched.");
        True(File.ReadAllBytes(canceled.Wav).SequenceEqual(canceledAudio), "Canceled compression retains its original WAV.");

        var interrupted = CompressionFixture("compress-stop-between-files");
        var second = Preset("TEST ONLY next compression file");
        string secondPath = Path.Combine(interrupted.Root, Fingerprints.HashText("TEST ONLY preview " + second.Id) + ".wav");
        CompletePreview(second, secondPath, new PreviewTemplate { QuickPreview = false, HoldSeconds = .4, TailSeconds = .1 });
        second.SourcePath = interrupted.Records[0].SourcePath;
        interrupted.Store.Upsert(second);
        var originals = interrupted.Store.LoadPresets().ToDictionary(record => record.Id,
            record => (Record: record, Bytes: File.ReadAllBytes(record.PreviewPath)));
        using (var cancellation = new CancellationTokenSource())
        {
            var reporter = new CompressionTestProgress(value => { if (value.Compressed == 1) cancellation.Cancel(); });
            await ThrowsAsync<OperationCanceledException>(() => PreviewCompressionService.CompressAsync(interrupted.Store, interrupted.Settings, reporter, cancellation.Token));
        }
        var partial = interrupted.Store.LoadPresets();
        Equal(1, partial.Count(record => record.PreviewPath.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)), "Stopping keeps the already committed compressed file.");
        Equal(1, partial.Count(record => record.PreviewPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)), "Stopping retains the not-yet-processed old WAV mapping.");
        foreach (var record in partial)
        {
            True(File.Exists(record.PreviewPath), "Every mapping remains playable after a stopped compression batch.");
            var original = originals[record.Id];
            if (record.PreviewPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                True(File.ReadAllBytes(record.PreviewPath).SequenceEqual(original.Bytes), "A pending compression keeps the exact old bytes.");
            var expected = JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(original.Record, LibraryJson.Options), LibraryJson.Options)!;
            expected.PreviewPath = record.PreviewPath;
            Equal(JsonSerializer.Serialize(expected, LibraryJson.Options), JsonSerializer.Serialize(record, LibraryJson.Options), "Stopping never rewrites metadata or provenance.");
        }
        True(!Directory.EnumerateFiles(interrupted.Root, ".sp-compress-*").Any() && !Directory.EnumerateFiles(interrupted.Root, ".aac-*").Any(),
            "A stopped explicit compression leaves no uncommitted scratch audio.");
    }

    private sealed record CompressionFixtureData(string Root, LibraryStore Store, AppSettings Settings, PresetRecord[] Records, string Wav);

    private static CompressionFixtureData CompressionFixture(string name, bool shared = false)
    {
        string directory = Fixture(name), root = Path.Combine(directory, "library"), sources = Path.Combine(directory, "sources");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sources);
        var record = Preset("TEST ONLY compression fixture");
        string wav = Path.Combine(root, Fingerprints.HashText("TEST ONLY preview " + record.Id) + ".wav");
        CompletePreview(record, wav, new PreviewTemplate { QuickPreview = false, HoldSeconds = .4, TailSeconds = .1 });
        record.SourcePath = Path.Combine(sources, "TEST-ONLY-preset.vital");
        File.WriteAllText(record.SourcePath, "TEST ONLY original source; never loaded by a synth");
        record.Favorite = true; record.Feedback = -1; record.Tags = ["TEST ONLY retained tag"]; record.ManualTags = ["TEST ONLY retained tag"];
        record.CategoryTags = ["TEST ONLY retained category"]; record.ManualCategories = ["TEST ONLY retained category"];
        var records = new List<PresetRecord> { record };
        if (shared)
        {
            var copy = JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(record, LibraryJson.Options), LibraryJson.Options)!;
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Favorite = false; copy.Tags = ["TEST ONLY other reference tag"]; copy.ManualTags = ["TEST ONLY other reference tag"];
            records.Add(copy);
        }
        var settings = new AppSettings { PreviewDirectory = root, Sources = [new SourceFolder { Path = sources, Synth = SynthKind.Vital }] };
        var store = new LibraryStore(Path.Combine(root, "library.db"));
        store.SaveState(records, settings);
        return new(root, store, settings, records.ToArray(), wav);
    }

    private sealed class CompressionTestProgress(Action<PreviewCompressionProgress> report) : IProgress<PreviewCompressionProgress>
    {
        public void Report(PreviewCompressionProgress value) => report(value);
    }

    private static AppSettings CompressedQueueSettings(string directory) => new()
    {
        PreviewDirectory = Path.Combine(directory, "previews"), MaxAttempts = 1,
        Template = new PreviewTemplate { SampleRate = 48000, QuickPreview = false, HoldSeconds = .4, TailSeconds = .1 },
        Plugins = [new PluginConfiguration { Synth = SynthKind.Vital, Backend = RenderBackend.ExternalWorker,
            ExecutablePath = TestExecutable, Version = "TEST ONLY AAC cache", TimeoutSeconds = 5 }]
    };

    private static WaveData ThreeRegisterAudio(int rate, int channels)
    {
        int perNote = rate / 2;
        float[] samples = new float[perNote * 3 * channels];
        int[] midi = [36, 60, 84];
        for (int note = 0; note < midi.Length; note++)
            for (int frame = 0; frame < perNote; frame++)
            {
                double time = frame / (double)rate;
                double envelope = Math.Min(1, Math.Min(time / .02, (.5 - time) / .02));
                double frequency = 440 * Math.Pow(2, (midi[note] - 69) / 12.0);
                for (int channel = 0; channel < channels; channel++)
                    samples[((note * perNote + frame) * channels) + channel] =
                        (float)(envelope * (channel == 0 ? .4 : .18) * Math.Sin(2 * Math.PI * frequency * (channel == 0 ? 1 : 1.5) * time));
            }
        return new WaveData { SampleRate = rate, Channels = channels, Samples = samples };
    }

    private static void AssertThreeRegisterAudio(WaveData original, WaveData decoded)
    {
        int perNote = original.SampleRate / 2;
        foreach (int note in Enumerable.Range(0, 3))
            foreach (int channel in Enumerable.Range(0, original.Channels))
            {
                int start = note * perNote + original.SampleRate / 10, count = original.SampleRate / 4;
                double dot = 0, referenceEnergy = 0, decodedEnergy = 0;
                for (int frame = start; frame < start + count; frame++)
                {
                    float a = original.Samples[frame * original.Channels + channel], b = decoded.Samples[frame * decoded.Channels + channel];
                    dot += a * b; referenceEnergy += a * a; decodedEnergy += b * b;
                }
                True(decodedEnergy > 0 && dot / Math.Sqrt(referenceEnergy * decodedEnergy) > .96,
                    "Every note and channel keeps the original timing and waveform instead of leaking another register or codec priming.");
            }
        if (decoded.Channels == 2)
        {
            double difference = Enumerable.Range(0, decoded.Frames).Average(frame => Math.Abs(decoded.Samples[frame * 2] - decoded.Samples[frame * 2 + 1]));
            True(difference > .1, "Stereo content cannot be silently collapsed to duplicated mono.");
        }
    }
}
