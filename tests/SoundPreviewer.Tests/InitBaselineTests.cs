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
        Equal(3, referencePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Calibration does not reuse a single file as three independent observations.");
        string validMetadata = await File.ReadAllTextAsync(plugin.InitBaselinePath);
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
        Equal(plugin.InitBaselinePath, store.LoadSettings().Plugins.Single().InitBaselinePath, "An automatically generated locator is persisted for the next application session.");
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
    }
}
