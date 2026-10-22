using System.Text.Json;
using System.Text.Json.Nodes;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestInitAudioComparisonAsync()
    {
        var template = new PreviewTemplate { QuickPreview = false, HoldSeconds = 0.5, TailSeconds = 0 };
        var notes = template.Pitches.Select((pitch, index) => new NotePreview
        {
            MidiNote = pitch, StartSeconds = index * 0.5, DurationSeconds = 0.5,
            ActualHoldSeconds = 0.5, ActualVelocity = template.Velocity
        }).ToList();
        WaveData Signal(double phase = 0, double gain = 1, bool different = false, bool envelope = false, double lateDrift = 0)
        {
            int frames = template.SampleRate / 2;
            var samples = new float[frames * 3 * 2];
            for (int note = 0; note < 3; note++)
                for (int frame = 0; frame < frames; frame++)
                {
                    double angle = frame * 2 * Math.PI * 440 * Math.Pow(2, (template.Pitches[note] - 69) / 12.0) / template.SampleRate + phase;
                    double seconds = frame / (double)template.SampleRate;
                    if (seconds > 0.1) angle += lateDrift * (seconds - 0.1) / 0.4;
                    double value = 0.2 * Math.Sin(angle);
                    if (different) value += 0.08 * Math.Sin(angle * 3);
                    if (envelope) value *= 1 - frame / (double)frames * 0.8;
                    samples[((note * frames + frame) * 2)] = (float)(value * gain);
                    samples[((note * frames + frame) * 2) + 1] = (float)(value * gain * 0.9);
                }
            return new WaveData { SampleRate = template.SampleRate, Channels = 2, Samples = samples };
        }
        var baseline = InitAudioComparer.CreateSignature(Signal(), notes);
        True(InitAudioComparer.Compare(baseline, Signal(), notes).IsMatch, "Exact three-note startup audio is rejected as an initialization match.");
        True(InitAudioComparer.Compare(baseline, Signal(phase: 0.7), notes).IsMatch, "A global random oscillator phase does not bypass all three waveform checks.");
        True(InitAudioComparer.Compare(baseline, Signal(phase: 1.1, gain: 1.025), notes).IsMatch, "Small gain and phase drift remains a strict near-match.");
        True(!InitAudioComparer.Compare(baseline, Signal(different: true), notes).IsMatch, "A different harmonic timbre is not rejected merely for its fundamental.");
        True(!InitAudioComparer.Compare(baseline, Signal(envelope: true), notes).IsMatch, "A different envelope is not rejected by a similar spectrum.");
        True(!InitAudioComparer.Compare(baseline, Signal(gain: 0.5), notes).IsMatch, "Material gain differences are not treated as an almost-identical preset.");
        True(!InitAudioComparer.Compare(baseline, Signal(gain: 0), notes).IsMatch, "A silent candidate is not matched to an audible baseline.");
        var quiet = InitAudioComparer.CreateSignature(Signal(gain: 0), notes);
        Throws<InvalidDataException>(() => InitAudioComparer.Compare(quiet, Signal(), notes));
        var references = new[] { 0.0, 0.5, 0.8 }.Select(drift => InitAudioComparer.CreateSignature(Signal(lateDrift: drift), notes)).ToArray();
        var calibrated = InitAudioComparer.CreateCalibratedSignature(references);
        var drifted = Signal(lateDrift: 1.0);
        True(references.Count(reference => InitAudioComparer.Compare(reference, drifted, notes).IsMatch) < 2,
            "The TEST ONLY evolving-phase fixture is not already a two-reference match under the old strict rule.");
        var acceptedDrift = InitAudioComparer.Compare(calibrated, drifted, notes);
        True(acceptedDrift.IsMatch && acceptedDrift.MatchedReferences >= 2,
            "Bounded three-reference calibration accepts time-varying phase only with two complete three-note matches.");
        True(!InitAudioComparer.Compare(calibrated, Signal(different: true), notes).IsMatch,
            "Common harmonics or saw-like spectra cannot replace the calibrated initial waveform constraint.");
        var earlyDifferent = Signal(lateDrift: 0.5);
        var different = Signal(different: true);
        int halfSecond = template.SampleRate / 2;
        for (int note = 0; note < 3; note++)
            Array.Copy(different.Samples, note * halfSecond * 2, earlyDifferent.Samples, note * halfSecond * 2, template.SampleRate / 10 * 2);
        True(!InitAudioComparer.Compare(calibrated, earlyDifferent, notes).IsMatch, "A different first 100 ms cannot be excused by similar later audio.");
        var lateDifferent = Signal(lateDrift: 0.5);
        for (int note = 0; note < 3; note++)
        {
            int start = (note * halfSecond + template.SampleRate / 10) * 2;
            Array.Copy(different.Samples, start, lateDifferent.Samples, start, (halfSecond - template.SampleRate / 10) * 2);
        }
        True(!InitAudioComparer.Compare(calibrated, lateDifferent, notes).IsMatch,
            "A copied initial 100 ms cannot hide a materially different full segment.");
        var twoNotesOnly = Signal(lateDrift: 0.5);
        Array.Copy(different.Samples, halfSecond * 4, twoNotesOnly.Samples, halfSecond * 4, halfSecond * 2);
        True(!InitAudioComparer.Compare(calibrated, twoNotesOnly, notes).IsMatch, "Two matching pitches are not a complete three-note reference match.");
        Throws<InvalidDataException>(() => InitAudioComparer.CreateCalibratedSignature([baseline, baseline]));
        Throws<InvalidDataException>(() => InitAudioComparer.CreateCalibratedSignature([baseline, baseline, InitAudioComparer.CreateSignature(Signal(gain: 1.2), notes)]));
        Throws<InvalidDataException>(() => InitAudioComparer.CreateCalibratedSignature([baseline, baseline, InitAudioComparer.CreateSignature(different, notes)]));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Throws<OperationCanceledException>(() => InitAudioComparer.CreateSignature(Signal(), notes, canceled.Token));
        Throws<OperationCanceledException>(() => InitAudioComparer.CreateCalibratedSignature(references, canceled.Token));
        return Task.CompletedTask;
    }

    private static async Task TestInitBaselineWorkflowAsync()
    {
        string directory = Fixture("TEST-ONLY-startup-baseline");
        string binary = Path.Combine(directory, "TEST-ONLY-placeholder.dll");
        await File.WriteAllTextAsync(binary, "TEST ONLY metadata; never loaded as a commercial plugin.");
        var plugin = new PluginConfiguration
        {
            Synth = SynthKind.Serum1, Backend = RenderBackend.DawDreamer, Version = "TEST-ONLY-1",
            ExecutablePath = TestExecutable, PluginPath = binary
        };
        var settings = new AppSettings
        {
            Plugins = [plugin], PreviewDirectory = Path.Combine(directory, "previews"), MaxAttempts = 2,
            Template = new PreviewTemplate { QuickPreview = false, HoldSeconds = 0.5, TailSeconds = 0 }
        };
        True(InitBaselineService.Supports(plugin), "The Daw Serum backend has an explicit fresh-instance baseline implementation.");
        True(!(await InitBaselineService.InspectAsync(settings, plugin)).CanCompare, "No baseline is an explicit not-ready diagnostic, not silent protection.");
        var record = new PresetRecord { Id = "TEST-ONLY-guard", Name = "alternate", Synth = SynthKind.Serum1, ContentHash = "TEST-ONLY-source" };
        string beforeKey = Fingerprints.CacheKey(record, plugin, settings.Template);
        // TestExecutable returns deterministic TEST ONLY PCM fixtures and never loads a DLL.
        var generated = await InitBaselineService.GenerateAsync(settings, plugin, TestExecutable);
        True(generated.CanCompare && generated.Notes.Count == 3 && File.Exists(generated.WavPath), "Explicit baseline generation publishes audio and three-note metadata only on success.");
        True(!generated.CanConfirm, "The user-selected default-startup target does not require a second confirmation gate.");
        Equal(beforeKey, Fingerprints.CacheKey(record, plugin, settings.Template), "Publishing a baseline locator does not change the normal audio cache key.");
        True((await InitBaselineService.InspectAsync(settings, plugin)).CanCompare, "A baseline with current fingerprints remains reusable.");
        var calibrationMetadata = JsonNode.Parse(await File.ReadAllTextAsync(plugin.InitBaselinePath))!;
        Equal(3, calibrationMetadata["References"]!.AsArray().Count, "Published startup metadata contains exactly three separately captured references.");
        var referencePaths = calibrationMetadata["References"]!.AsArray().Select(reference => reference!["WavPath"]!.GetValue<string>()).ToArray();
        True(referencePaths.All(path => !PreviewPathStorage.IsAbsolute(path) && File.Exists(PreviewPathStorage.Resolve(path, settings.PreviewDirectory))),
            "New baseline JSON stores relative references rooted in Preview.");
        Equal(3, referencePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Calibration does not reuse a single file as three independent observations.");
        string validMetadata = await File.ReadAllTextAsync(plugin.InitBaselinePath);
        string relocatedPreview = Path.Combine(directory, "TEST ONLY relocated preview");
        Directory.CreateDirectory(Path.Combine(relocatedPreview, "InitializationBaselines"));
        foreach (string path in Directory.GetFiles(Path.GetDirectoryName(plugin.InitBaselinePath)!))
            File.Copy(path, Path.Combine(relocatedPreview, "InitializationBaselines", Path.GetFileName(path)));
        var relocatedSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, LibraryJson.Options), LibraryJson.Options)!;
        relocatedSettings.PreviewDirectory = relocatedPreview;
        var relocatedPlugin = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(plugin, LibraryJson.Options), LibraryJson.Options)!;
        relocatedPlugin.InitBaselinePath = Path.Combine(relocatedPreview, "InitializationBaselines", Path.GetFileName(plugin.InitBaselinePath));
        var relocatedBaseline = await InitBaselineService.InspectAsync(relocatedSettings, relocatedPlugin);
        True(relocatedBaseline.CanCompare && ApplicationPaths.IsWithin(relocatedBaseline.WavPath, relocatedPreview),
            "Relocating Preview preserves calibrated comparison and resolves audio from the new local copy.");
        var legacyMetadata = JsonNode.Parse(validMetadata)!;
        RewriteFixturePaths(legacyMetadata, path => PreviewPathStorage.Resolve(path, settings.PreviewDirectory));
        await File.WriteAllTextAsync(plugin.InitBaselinePath, legacyMetadata.ToJsonString());
        True((await InitBaselineService.InspectAsync(settings, plugin)).CanCompare, "Old absolute baseline metadata stays readable for compatibility before startup migration.");
        await File.WriteAllTextAsync(plugin.InitBaselinePath, validMetadata);
        calibrationMetadata["References"]![1]!["WavSha256"] = new string('0', 64);
        await File.WriteAllTextAsync(plugin.InitBaselinePath, calibrationMetadata.ToJsonString());
        True(!(await InitBaselineService.InspectAsync(settings, plugin)).CanCompare, "A damaged secondary reference disables the entire baseline guard.");
        await File.WriteAllTextAsync(plugin.InitBaselinePath, validMetadata);

        var store = new LibraryStore(Path.Combine(directory, "library.db"));
        var queue = new RenderQueue(store);
        var success = await queue.RunAsync(settings, [record], TestExecutable, new InlineProgress(), CancellationToken.None);
        Equal(1, success.Generated, "An audibly different test-only preset passes the startup guard.");
        Equal("成功", record.LastRenderOutcome, "A committed preview records the most recent successful attempt.");
        string oldPath = record.PreviewPath;
        string oldHash = Fingerprints.HashFile(oldPath);
        record.Name = "TEST-ONLY-exact-default";
        var rejected = await queue.RunAsync(settings, [record], TestExecutable, new InlineProgress(), CancellationToken.None, forceRegenerate: true);
        Equal(1, rejected.Failed, "An exact default-startup match is a failed render, not generated audio.");
        Equal(oldPath, record.PreviewPath, "Rejected forced regeneration retains the old valid preview mapping.");
        Equal(oldHash, Fingerprints.HashFile(oldPath), "The before-commit comparison cannot overwrite old valid audio.");
        Equal("失败", record.LastRenderOutcome, "Restoring an old ready preview does not erase the latest failed outcome.");
        True(record.LastRenderMessage.StartsWith(InitBaselineService.RejectionPrefix, StringComparison.Ordinal), "The latest failure explains initialization matching rather than reporting success.");

        string locator = plugin.InitBaselinePath;
        settings.Template.Velocity++;
        True(!(await InitBaselineService.InspectAsync(settings, plugin)).CanCompare, "Template changes invalidate the comparison reference.");
        settings.Template.Velocity--;
        await File.AppendAllTextAsync(binary, " changed");
        True(!(await InitBaselineService.InspectAsync(settings, plugin)).CanCompare, "Replacing a plugin at the same path invalidates its baseline.");
        await File.WriteAllTextAsync(binary, "TEST ONLY metadata; never loaded as a commercial plugin.");
        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(locator))!;
        metadata["Kind"] = "UnverifiedFactoryInit";
        await File.WriteAllTextAsync(locator, metadata.ToJsonString());
        True(!(await InitBaselineService.InspectAsync(settings, plugin)).CanCompare, "A different or unverified baseline kind cannot masquerade as no-preset startup.");
        var unavailable = await queue.RunAsync(settings, [record], Path.Combine(directory, "missing-worker.exe"), new InlineProgress(), CancellationToken.None, forceRegenerate: true);
        Equal(1, unavailable.Failed, "A supported backend with an invalid baseline fails closed if rebuilding is unavailable.");
        Equal(oldHash, Fingerprints.HashFile(oldPath), "A failed baseline rebuild leaves previously committed preview bytes untouched.");
        True(record.LastRenderOutcome == "失败" && record.LastRenderMessage.Contains("基准"), "Baseline preparation failures remain visible despite a retained ready preview.");
        var reused = await queue.RunAsync(settings, [record], Path.Combine(directory, "missing-worker.exe"), new InlineProgress(), CancellationToken.None);
        Equal(1, reused.Reused, "Default resume reuses old valid previews before any baseline/backend requirement.");
        plugin.InitBaselinePath = "";
        record.Name = "alternate";
        var rebuilt = await queue.RunAsync(settings, [record], TestExecutable, new InlineProgress(), CancellationToken.None, forceRegenerate: true);
        Equal(1, rebuilt.Generated, "A supported backend automatically creates a missing baseline before normal rendering.");
        Equal(plugin.InitBaselinePath, store.LoadSettings().Plugins.Single(item => item.Synth == plugin.Synth).InitBaselinePath, "An automatically generated locator is persisted for the next application session.");
        var wrongPath = JsonNode.Parse(await File.ReadAllTextAsync(plugin.InitBaselinePath))!;
        wrongPath["Result"]!["OutputPath"] = Path.Combine(directory, "unrelated.wav");
        await File.WriteAllTextAsync(plugin.InitBaselinePath, wrongPath.ToJsonString());
        True(!(await InitBaselineService.InspectAsync(settings, plugin)).CanCompare, "Baseline result/output path disagreement is rejected.");
        string nativeDirectory = Path.Combine(directory, "TEST-ONLY-native-host");
        Directory.CreateDirectory(nativeDirectory);
        string nativeExecutable = Path.Combine(nativeDirectory, "SoundPreviewer.Vst2Host.exe");
        await File.WriteAllTextAsync(nativeExecutable, "TEST ONLY placeholder; never executed.");
        var native = new PluginConfiguration { Synth = SynthKind.Sylenth1, Backend = RenderBackend.ExternalWorker,
            ExecutablePath = nativeExecutable, PluginPath = binary, Version = "TEST-ONLY-native" };
        True((await InitBaselineService.GenerateAsync(settings, native, TestExecutable)).CanCompare, "TEST ONLY native metadata can be verified without executing its placeholder host.");
        await File.WriteAllTextAsync(Path.Combine(nativeDirectory, "SoundPreviewer.Vst2Host.dll"), "TEST ONLY updated managed entry assembly.");
        True(!(await InitBaselineService.InspectAsync(settings, native)).CanCompare, "Replacing a native managed sidecar invalidates the baseline even when apphost bytes are unchanged.");
        plugin.Synth = SynthKind.Vital;
        plugin.Backend = RenderBackend.VitalCli;
        True(!InitBaselineService.Supports(plugin) && !(await InitBaselineService.InspectAsync(settings, plugin)).CanCompare,
            "Vital CLI has no fabricated Init-file fallback and clearly reports unsupported protection.");
        await TestVerifiedPresetLoadFallbackAsync();
    }

    private static async Task TestVerifiedPresetLoadFallbackAsync()
    {
        string directory = Fixture("TEST-ONLY-verified-state-fallback");
        string executable = Path.Combine(directory, "SoundPreviewer.Vst2Host.exe");
        string binary = Path.Combine(directory, "TEST-ONLY-plugin.dll");
        string source = Path.Combine(directory, "TEST-ONLY-target.fxp");
        await File.WriteAllTextAsync(executable, "TEST ONLY placeholder; never executed.");
        await File.WriteAllTextAsync(binary, "TEST ONLY placeholder; never loaded.");
        await File.WriteAllTextAsync(source, "TEST ONLY source bytes; the fake worker generates synthetic PCM.");
        var plugin = new PluginConfiguration { Synth = SynthKind.Sylenth1, Backend = RenderBackend.ExternalWorker,
            ExecutablePath = executable, PluginPath = binary, Version = "TEST-ONLY-unstable-state-proof" };
        var settings = new AppSettings { Plugins = [plugin], PreviewDirectory = Path.Combine(directory, "Preview"), MaxAttempts = 1,
            Template = new PreviewTemplate { QuickPreview = false, HoldSeconds = 0.5, TailSeconds = 0 } };
        var state = await InitBaselineService.GenerateAsync(settings, plugin, TestExecutable);
        True(!state.CanCompare && state.CanRenderWithVerifiedPresetLoad && state.Notes.Count == 3,
            "An unstable native startup is explicitly unavailable for audio comparison but requires exact per-preset load proof.");
        var inspected = await InitBaselineService.InspectAsync(settings, plugin);
        True(!inspected.CanCompare && inspected.CanRenderWithVerifiedPresetLoad, "The alternate mode reopens only after all original reference hashes and instability are verified.");
        string locator = plugin.InitBaselinePath;
        string originalMetadata = await File.ReadAllTextAsync(locator);
        var metadata = JsonNode.Parse(originalMetadata)!;
        Equal("VerifiedPresetLoad", metadata["Mode"]!.GetValue<string>(), "The persisted fallback is a distinct mode and never masquerades as stable audio calibration.");
        Equal(3, metadata["References"]!.AsArray().Count, "Alternate mode retains three independent, hashed startup observations.");
        metadata["References"]![1]!["WavSha256"] = new string('0', 64);
        await File.WriteAllTextAsync(locator, metadata.ToJsonString());
        var corrupt = await InitBaselineService.InspectAsync(settings, plugin);
        True(!corrupt.CanCompare && !corrupt.CanRenderWithVerifiedPresetLoad, "A corrupted fallback reference fails closed rather than enabling unverified rendering.");
        await File.WriteAllTextAsync(locator, originalMetadata);
        var unknownMode = JsonNode.Parse(originalMetadata)!;
        unknownMode["Mode"] = "TEST-ONLY-future-mode";
        await File.WriteAllTextAsync(locator, unknownMode.ToJsonString());
        var future = await InitBaselineService.InspectAsync(settings, plugin);
        True(!future.CanCompare && !future.CanRenderWithVerifiedPresetLoad, "Unknown persisted modes cannot enable alternate protection.");
        var unknownAdapter = JsonNode.Parse(originalMetadata)!;
        foreach (var reference in unknownAdapter["References"]!.AsArray())
            reference!["Result"]!["AdapterVersion"] = "TEST-ONLY-future-native-adapter";
        await File.WriteAllTextAsync(locator, unknownAdapter.ToJsonString());
        var foreign = await InitBaselineService.InspectAsync(settings, plugin);
        True(!foreign.CanCompare && !foreign.CanRenderWithVerifiedPresetLoad, "An unknown native reference adapter cannot authorize exact-state fallback.");
        await File.WriteAllTextAsync(locator, originalMetadata);

        var record = new PresetRecord { Id = "TEST-ONLY-state-proof-target", Name = "TEST-ONLY-state-proof-valid", Synth = SynthKind.Sylenth1,
            SourcePath = source, ContentHash = Fingerprints.HashFile(source) };
        var store = new LibraryStore(Path.Combine(directory, "library.db"));
        var queue = new RenderQueue(store);
        var generated = await queue.RunAsync(settings, [record], TestExecutable, new InlineProgress(), CancellationToken.None);
        Equal(1, generated.Generated, "An unstable native startup permits generation only with the matching exact-state proof.");
        True(record.Status == PresetStatus.NeedsReview && record.Message.Contains("逐音区核对目标预制状态"),
            "A state-verified result still requires listening and does not claim stable audio comparison.");
        string path = record.PreviewPath, hash = Fingerprints.HashFile(path);
        foreach (string invalid in new[] { "missing", "wrong-source", "wrong-pitch", "wrong-adapter", "wrong-scheme" })
        {
            record.Name = "TEST-ONLY-state-proof-" + invalid;
            var rejected = await queue.RunAsync(settings, [record], TestExecutable, new InlineProgress(), CancellationToken.None, forceRegenerate: true);
            Equal(1, rejected.Failed, "Unstable audio must not excuse an invalid target-load proof: " + invalid);
            Equal(path, record.PreviewPath, "Rejected proof preserves the prior committed preview mapping: " + invalid);
            Equal(hash, Fingerprints.HashFile(path), "Rejected proof preserves the prior committed WAV bytes: " + invalid);
        }
        var reused = await queue.RunAsync(settings, [record], Path.Combine(directory, "missing-worker.exe"), new InlineProgress(), CancellationToken.None);
        Equal(1, reused.Reused, "Existing valid caches remain reusable before the new state-proof requirement.");

        var nativeProof = new RenderResult { Success = true, AdapterVersion = Fingerprints.QuickPreviewPolicy + "/" + PresetLoadVerification.NativeSylenthAdapterVersion,
            PresetLoadProof = new PresetLoadProof { Scheme = PresetLoadVerification.Scheme, SourceSha256 = record.ContentHash, Pitches = settings.Template.Pitches.ToList() } };
        True(PresetLoadVerification.IsValid(nativeProof, record, settings.Template), "The explicitly known quick-preview wrapper preserves native proof provenance.");
        nativeProof.AdapterVersion = "untrusted/" + nativeProof.AdapterVersion;
        True(!PresetLoadVerification.IsValid(nativeProof, record, settings.Template), "An arbitrary adapter cannot claim the native exact-state proof scheme.");
        await TestQuickPresetProofPropagationAsync(directory, source, record.ContentHash);

        var previousBackend = plugin.Backend;
        plugin.Backend = RenderBackend.DawDreamer;
        var unsupported = await InitBaselineService.GenerateAsync(settings, plugin, TestExecutable);
        True(!unsupported.CanCompare && !unsupported.CanRenderWithVerifiedPresetLoad,
            "An unstable unsupported host stays fail-closed even when its result claims the native adapter version.");
        plugin.Backend = previousBackend;
        plugin.InitBaselinePath = "";
        var unavailable = await InitBaselineService.GenerateAsync(settings, plugin, Path.Combine(directory, "missing-worker.exe"));
        True(!unavailable.CanCompare && !unavailable.CanRenderWithVerifiedPresetLoad && plugin.InitBaselinePath.Length == 0,
            "Missing renderer output cannot be treated as ordinary startup variation or publish an alternate mode.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool wasCanceled = false;
        try { await InitBaselineService.GenerateAsync(settings, plugin, TestExecutable, canceled.Token); }
        catch (OperationCanceledException) { wasCanceled = true; }
        True(wasCanceled && plugin.InitBaselinePath.Length == 0, "Cancellation preserves the no-published-baseline boundary for alternate protection too.");
    }

    private static async Task TestQuickPresetProofPropagationAsync(string directory, string source, string sourceHash)
    {
        // Rename only an isolated copy of our synthetic process fixture, never a real host.
        // Its entry assembly and dependencies stay beside it so the native host contract
        // is exercised through the actual Worker without loading a commercial plugin.
        string hostDirectory = Path.Combine(directory, "TEST-ONLY-quick-host");
        Directory.CreateDirectory(hostDirectory);
        string executable = Path.Combine(hostDirectory, "SoundPreviewer.Vst2Host.exe");
        File.Copy(TestExecutable, executable);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, File.GetUnixFileMode(TestExecutable));
        foreach (string name in new[] { "SoundPreviewer.Tests.dll", "SoundPreviewer.Tests.deps.json", "SoundPreviewer.Tests.runtimeconfig.json",
            "SoundPreviewer.Core.dll", "SoundPreviewer.Worker.dll" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(hostDirectory, name));
        var request = new RenderRequest
        {
            Preset = new PresetRecord { Name = "TEST-ONLY-state-proof-quick-valid", Synth = SynthKind.Sylenth1, SourcePath = source, ContentHash = sourceHash },
            Plugin = new PluginConfiguration { Synth = SynthKind.Sylenth1, Backend = RenderBackend.ExternalWorker, ExecutablePath = executable,
                Version = "TEST-ONLY-quick-proof", TimeoutSeconds = 5 },
            Template = new PreviewTemplate { QuickPreview = true },
            OutputPath = Path.Combine(directory, "TEST-ONLY-quick-proven.wav")
        };
        string worker = Path.ChangeExtension(typeof(SoundPreviewer.Worker.RenderDispatcher).Assembly.Location, OperatingSystem.IsWindows() ? ".exe" : null);
        // Production clears generic DOTNET_ROOT for its self-contained x86 host. Our renamed
        // framework-dependent fixture must still find the exact runtime already running tests,
        // including CI SDKs installed outside the system default location.
        string architectureRoot = "DOTNET_ROOT_" + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
        string? previousRoot = Environment.GetEnvironmentVariable(architectureRoot);
        string activeRuntimeRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", ".."));
        Environment.SetEnvironmentVariable(architectureRoot, activeRuntimeRoot);
        try
        {
            var valid = await new WorkerProcessClient().RenderAsync(request, worker, CancellationToken.None);
            True(valid.Success && PresetLoadVerification.IsValid(valid, request.Preset, request.Template),
                "Actual quick Worker propagation retains a proof when every synthetic capture round verified the requested source and all pitches.");
            True(valid.Notes.Any(note => note.ActualHoldSeconds == 4), "The synthetic quiet pitch genuinely requires multiple contributing capture rounds.");
            request.Preset.Name = "TEST-ONLY-state-proof-quick-mixed";
            request.OutputPath = Path.Combine(directory, "TEST-ONLY-quick-mixed.wav");
            var mixed = await new WorkerProcessClient().RenderAsync(request, worker, CancellationToken.None);
            True(mixed.Success && mixed.PresetLoadProof is null && !PresetLoadVerification.IsValid(mixed, request.Preset, request.Template),
                "A missing proof in the first contributing round cannot be blessed by valid later rounds.");
            request.Preset.Name = "TEST-ONLY-state-proof-quick-permanent";
            request.OutputPath = Path.Combine(directory, "TEST-ONLY-quick-incompatible.wav");
            var incompatible = await new WorkerProcessClient().RenderAsync(request, worker, CancellationToken.None);
            True(!incompatible.Success && incompatible.NonRetryable && !File.Exists(request.OutputPath),
                "Native compatibility failures retain their no-retry classification through the actual quick Worker and publish no WAV.");
        }
        finally { Environment.SetEnvironmentVariable(architectureRoot, previousRoot); }
    }

    private static async Task<int> StateProofFakeWorkerAsync(RenderRequest request, string resultPath)
    {
        if (request.Preset.Name.EndsWith("-permanent", StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new RenderResult { NonRetryable = true,
                Message = "TEST ONLY incompatible preset state." }, LibraryJson.Options));
            return 1;
        }
        double duration = request.Template.HoldSeconds + request.Template.TailSeconds;
        int frames = (int)Math.Round(request.Template.SampleRate * duration);
        var audio = Tone(request.Template.SampleRate, 1, frames * 3 / (double)request.Template.SampleRate);
        if (request.IsInitializationBaseline)
        {
            string counterPath = request.Plugin.PluginPath + ".TEST-ONLY-baseline-count";
            int count = File.Exists(counterPath) ? int.Parse(await File.ReadAllTextAsync(counterPath)) : 0;
            await File.WriteAllTextAsync(counterPath, (count + 1).ToString());
            if (count % 3 == 1)
                for (int index = 0; index < audio.Samples.Length; index++) audio.Samples[index] *= 0.5f;
        }
        if (request.Preset.Name.Contains("-quick-", StringComparison.Ordinal)) Array.Clear(audio.Samples, frames * 2, frames);
        WaveFile.WritePcm16(request.OutputPath, audio);
        string mode = request.Preset.Name;
        var result = new RenderResult { Success = true, OutputPath = request.OutputPath,
            AdapterVersion = mode.EndsWith("wrong-adapter", StringComparison.Ordinal) ? "TEST-ONLY-foreign-adapter" : PresetLoadVerification.NativeSylenthAdapterVersion,
            Message = "TEST ONLY unstable startup/state proof fixture; no synthesizer was loaded.",
            Notes = request.Template.Pitches.Select((pitch, index) => new NotePreview { MidiNote = pitch,
                StartSeconds = index * frames / (double)request.Template.SampleRate, DurationSeconds = frames / (double)request.Template.SampleRate,
                ActualHoldSeconds = request.Template.HoldSeconds, ActualTailSeconds = frames / (double)request.Template.SampleRate - request.Template.HoldSeconds,
                ActualVelocity = request.Template.Velocity }).ToList() };
        if (!request.IsInitializationBaseline && !mode.EndsWith("missing", StringComparison.Ordinal) &&
            !(mode.EndsWith("quick-mixed", StringComparison.Ordinal) && request.Template.HoldSeconds == 0.5))
            result.PresetLoadProof = new PresetLoadProof { Scheme = mode.EndsWith("wrong-scheme", StringComparison.Ordinal) ? "TEST-ONLY-untrusted-scheme" : PresetLoadVerification.Scheme,
                SourceSha256 = mode.EndsWith("wrong-source", StringComparison.Ordinal) ? new string('0', 64) : request.Preset.ContentHash,
                Pitches = mode.EndsWith("wrong-pitch", StringComparison.Ordinal) ? [36, 60, 85] : request.Template.Pitches.ToList() };
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, LibraryJson.Options));
        return 0;
    }
}
