using System.Text.Json;
using System.Text.Json.Nodes;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;
using SoundPreviewer.Worker;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestNoteNamesAsync()
    {
        Equal("C2", NoteNames.Format(new PreviewTemplate().LowNote), "The low default uses scientific pitch C2.");
        Equal("C4", NoteNames.Format(new PreviewTemplate().MiddleNote), "Scientific C4 is MIDI 60.");
        Equal("C6", NoteNames.Format(new PreviewTemplate().HighNote), "The high default uses scientific pitch C6.");
        foreach (int pitch in Enumerable.Range(0, 128))
            Equal(pitch, NoteNames.Parse(NoteNames.Format(pitch)), "Every supported MIDI pitch round-trips through its note name.");
        foreach (string name in new[] { "C#4", "Db4", "c♯4", " d♭4 " })
            Equal(61, NoteNames.Parse(name), "Sharp and flat spellings name the same pitch.");
        Equal(60, NoteNames.Parse("B#3"), "An accidental can cross the octave boundary.");
        Equal(59, NoteNames.Parse("Cb4"), "A flat can cross the octave boundary.");
        foreach (string? name in new[] { null, "", "60", "H4", "C", "C##4", "C-2", "G#9", "C2147483647" })
            True(!NoteNames.TryParse(name, out _), "An invalid or out-of-range note name cannot become a MIDI pitch.");
        Throws<ArgumentException>(() => NoteNames.Parse("H4"));
        Throws<ArgumentOutOfRangeException>(() => NoteNames.Format(128));
        return Task.CompletedTask;
    }

    private static Task TestConfigurableQuickPreviewAsync()
    {
        var template = new PreviewTemplate();
        var preset = new PresetRecord { Synth = SynthKind.Vital, ContentHash = "TEST-ONLY-source-hash" };
        var plugin = new PluginConfiguration
        {
            Synth = SynthKind.Vital, Backend = RenderBackend.ExternalWorker, ExecutablePath = "TEST-ONLY", Version = "fixture-v1"
        };
        // Captured from the original pre-duration cache JSON, including its field order and Pitches getter.
        const string historicalKey = "6d8252cdae32c4a9b55e1cb2187529645e47b4ed797376feb8f1c6e8b18e92f3";
        Equal(historicalKey, Fingerprints.CacheKey(preset, plugin, template), "The new default must retain historical half-second cache identities.");
        var legacy = JsonSerializer.Deserialize<PreviewTemplate>("{\"NoteOrder\":1}", LibraryJson.Options)!;
        Near(0.5, legacy.QuickPreviewSeconds, 0, "Older template JSON defaults to a half-second preview.");
        Equal(historicalKey, Fingerprints.CacheKey(preset, plugin, legacy), "An old persisted template retains its original cache key.");

        string directory = Fixture("quick-duration-audio");
        var source = Tone(48000, 2, 6);
        foreach (double seconds in new[] { 0.1, 0.25, 1.25, 4 })
        {
            template.QuickPreviewSeconds = seconds;
            template.Validate();
            NotEqual(historicalKey, Fingerprints.CacheKey(preset, plugin, template), "A non-default duration participates in the cache identity.");
            var analysis = QuickPreviewAudio.Analyze(source, seconds);
            True(!analysis.NeedsMoreAudio, "A sufficiently long real WAV supports the selected preview duration.");
            string wav = Path.Combine(directory, seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".wav");
            WaveFile.WriteFloat32(wav, QuickPreviewAudio.Crop(source, analysis, seconds));
            var readback = WaveFile.Read(wav);
            Equal((int)Math.Round(seconds * source.SampleRate), readback.Frames, "The exported WAV contains the selected number of audio frames.");
            True(WaveFile.Analyze(readback).Rms > 0.05, "A configurable crop retains the captured tone.");
        }
        foreach (double invalid in new[] { 0, 0.09, 4.01, double.NaN, double.PositiveInfinity })
        {
            template.QuickPreviewSeconds = invalid;
            Throws<ArgumentException>(() => template.Validate());
            Throws<ArgumentOutOfRangeException>(() => QuickPreviewAudio.Analyze(source, invalid));
        }
        template.QuickPreviewSeconds = 4;
        var roundTrip = JsonSerializer.Deserialize<PreviewTemplate>(JsonSerializer.Serialize(template, LibraryJson.Options), LibraryJson.Options)!;
        Near(4, roundTrip.QuickPreviewSeconds, 0, "The configured duration survives saved settings and worker JSON.");
        Near(8, QuickPreviewAudio.MaximumCaptureHoldSeconds(4), 0, "A four-second target still permits bounded slow-onset extension.");
        return Task.CompletedTask;
    }

    private static async Task TestConfigurableQuickWorkerAsync()
    {
        string directory = Fixture("quick-duration-worker");
        string worker = Path.ChangeExtension(typeof(RenderDispatcher).Assembly.Location, OperatingSystem.IsWindows() ? ".exe" : null);
        foreach (double seconds in new[] { 0.25, 1.25, 4 })
        {
            string name = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var request = Request("quick-delayed", Path.Combine(directory, name + ".wav"));
            request.Template = new PreviewTemplate { QuickPreviewSeconds = seconds };
            request.Plugin.ExecutablePath = TestExecutable;
            request.Preset.SourcePath = Path.Combine(directory, "TEST-ONLY-" + name);
            var result = await new WorkerProcessClient().RenderAsync(request, worker, CancellationToken.None);
            True(result.Success && result.NeedsReview, "The worker retains real delayed and silent fallback audio at the selected duration: " + result.Message);
            var audio = WaveFile.Read(request.OutputPath);
            Equal((int)Math.Round(3 * seconds * audio.SampleRate), audio.Frames, "The complete worker protocol publishes three selected-duration clips.");
            for (int index = 0; index < 3; index++)
            {
                Near(seconds, result.Notes[index].DurationSeconds, 1.5 / audio.SampleRate, "Each note's map agrees with its selected-duration WAV segment.");
                Near(index * seconds, result.Notes[index].StartSeconds, 1.5 / audio.SampleRate, "The mapped notes have no inter-note gaps.");
            }
            var captures = File.ReadAllLines(request.Preset.SourcePath + ".captures")
                .Select(line => JsonNode.Parse(line)!["Hold"]!.GetValue<double>()).ToArray();
            Near(seconds, captures[0], 0, "The first raw capture starts at the requested hold duration.");
            Near(QuickPreviewAudio.MaximumCaptureHoldSeconds(seconds), captures[^1], 0, "Silent audio stops at the bounded capture limit.");
            if (seconds == 4)
            {
                Near(8, result.Notes[1].ActualHoldSeconds, 0, "The delayed attack extends beyond a four-second target to retain four seconds of actual audio.");
                Throws<InvalidDataException>(() => WorkerProcessClient.ValidateNotes(result, new PreviewTemplate()),
                    "A longer preview cannot be mislabeled as a legacy half-second result.");
            }
        }
    }

    private static async Task TestQuickDurationQueueAsync()
    {
        string directory = Fixture("quick-duration-queue");
        var store = new LibraryStore(Path.Combine(directory, "library.db"));
        store.Initialize();
        var plugin = new PluginConfiguration { Synth = SynthKind.Vital, Backend = RenderBackend.ExternalWorker,
            ExecutablePath = TestExecutable, Version = "TEST ONLY duration queue", TimeoutSeconds = 5 };
        var settings = new AppSettings
        {
            PreviewDirectory = Path.Combine(directory, "previews"), StorageBudgetGb = 300000.0 / (1024 * 1024 * 1024),
            MaxAttempts = 1, Plugins = [plugin], Template = new PreviewTemplate { QuickPreviewSeconds = 4 }
        };
        var preset = Preset("quick-delayed");
        preset.SourcePath = Path.Combine(directory, "TEST-ONLY-preset");
        store.UpsertMany([preset]);
        var queue = new RenderQueue(store);
        string worker = Path.ChangeExtension(typeof(RenderDispatcher).Assembly.Location, OperatingSystem.IsWindows() ? ".exe" : null);
        await ThrowsAsync<IOException>(() => queue.RunAsync(settings, [preset], worker, new InlineProgress(), CancellationToken.None));
        True(!File.Exists(preset.SourcePath + ".captures"), "The selected duration is included in storage budgeting before starting a worker.");
        settings.Template.QuickPreviewSeconds = 0.25;
        var first = await queue.RunAsync(settings, [preset], worker, new InlineProgress(), CancellationToken.None);
        Equal(1, first.Generated, "A shorter duration fits the same storage budget and generates successfully.");
        byte[] audio = File.ReadAllBytes(preset.PreviewPath);
        string key = preset.CacheKey;
        settings.Template.QuickPreviewSeconds = 4;
        var reused = await queue.RunAsync(settings, [preset], worker, new InlineProgress(), CancellationToken.None);
        Equal(1, reused.Reused, "Changing the configured duration keeps the existing valid preview until explicit regeneration.");
        Equal(key, preset.CacheKey, "Default reuse preserves the previous preview identity.");
        True(audio.SequenceEqual(File.ReadAllBytes(preset.PreviewPath)), "Default reuse does not replace committed audio.");
        Near(0.25, JsonSerializer.Deserialize<PreviewTemplate>(preset.TemplateJson, LibraryJson.Options)!.QuickPreviewSeconds, 0,
            "The retained preview keeps its original duration provenance.");
    }
}
