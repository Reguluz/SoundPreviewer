using System.Buffers.Binary;
using System.Text;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static async Task TestSerumFallbackAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = Fixture("serum-fallback");
        string binary = Path.Combine(directory, "TEST-ONLY-Serum.vst3");
        await File.WriteAllTextAsync(binary, "TEST ONLY; not a plugin");
        var serum1 = new PluginConfiguration { Synth = SynthKind.Serum1, Backend = RenderBackend.DawDreamer,
            ExecutablePath = TestExecutable, PluginPath = binary, Version = "TEST-ONLY-serum-routing" };
        var serum2 = new PluginConfiguration { Synth = SynthKind.Serum2, Backend = RenderBackend.DawDreamer,
            ExecutablePath = TestExecutable, PluginPath = binary, Version = "TEST-ONLY-serum-routing", AllowParallelRendering = false };
        var vital = new PluginConfiguration { Synth = SynthKind.Vital, Backend = RenderBackend.ExternalWorker,
            ExecutablePath = TestExecutable, PluginPath = binary, Version = "TEST-ONLY-serum-routing" };
        var settings = new AppSettings { Plugins = [serum1, serum2, vital], PreviewFormat = PreviewAudioFormat.Wav,
            PreviewDirectory = Path.Combine(directory, "previews"), MaxAttempts = 1, MaxParallelRenders = 1,
            Template = new PreviewTemplate { QuickPreview = false, HoldSeconds = 0.5, TailSeconds = 0 } };
        var readiness = new[] { new RenderReadiness(SynthKind.Serum1, "TEST ONLY", "", true),
            new RenderReadiness(SynthKind.Serum2, "TEST ONLY", "", true) };
        var store = new LibraryStore(Path.Combine(directory, "library.db"));
        store.SaveSettings(settings);
        var queue = new RenderQueue(store);
        string log = Path.Combine(directory, "routing.log");

        PresetRecord Preset(string name)
        {
            string source = Path.Combine(directory, name + ".fxp");
            byte[] raw = new byte[92];
            "CcnK"u8.CopyTo(raw);
            BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(4), (uint)raw.Length - 8);
            "FPCh"u8.CopyTo(raw.AsSpan(8));
            "XfsX"u8.CopyTo(raw.AsSpan(16));
            BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(56), (uint)raw.Length - 60);
            Encoding.ASCII.GetBytes("TEST ONLY " + name).AsSpan(0, Math.Min(28, 10 + name.Length)).CopyTo(raw.AsSpan(28));
            File.WriteAllBytes(source, raw);
            return new PresetRecord { Id = name, Name = name, Synth = SynthKind.Serum1, SourcePath = source,
                ContentHash = Fingerprints.HashFile(source), Favorite = true, Tags = ["keep custom tag"] };
        }

        var target = Preset("serum-success");
        var normal = new PresetRecord { Id = "normal", Name = "normal", Synth = SynthKind.Vital, ContentHash = "TEST ONLY normal" };
        store.UpsertMany([target, normal]);
        var generated = await queue.RunAsync(settings, [target, normal], TestExecutable, new InlineProgress(), CancellationToken.None, readiness: readiness);
        Equal(2, generated.Generated, "Normal jobs and Serum fallback each count as one successful item.");
        Equal(0, generated.Failed, "A successful fallback does not count the primary failure twice.");
        var calls = File.ReadAllLines(log);
        True(calls[0].Contains("Serum1") && calls[1].StartsWith("normal|") && calls[2].EndsWith("Serum2|True"),
            "All normal work drains before the explicitly marked Serum 2 fallback request.");
        Equal(SynthKind.Serum1, target.Synth, "Fallback preserves indexed Serum 1 identity.");
        True(target.Favorite && target.Tags.SequenceEqual(["keep custom tag"]), "Fallback preserves user metadata.");
        Equal(target.ContentHash, Fingerprints.HashFile(target.SourcePath), "The original FXP is never converted in place.");
        string primaryKey = Fingerprints.CacheKey(target, serum1, settings.Template);
        True(target.CacheKey != primaryKey && File.Exists(target.PreviewPath), "Fallback uses a different cache identity and valid output.");
        var reused = await queue.RunAsync(settings, [target], "missing-worker", new InlineProgress(), CancellationToken.None);
        Equal(1, reused.Reused, "A valid fallback preview is reused without starting either synthesizer.");
        Equal(3, File.ReadAllLines(log).Length, "Reuse does not invoke an importer.");

        string previousPath = target.PreviewPath, previousHash = Fingerprints.HashFile(previousPath);
        target.Name = "serum-fail";
        var failed = await queue.RunAsync(settings, [target], TestExecutable, new InlineProgress(), CancellationToken.None, true, readiness);
        Equal(1, failed.Failed, "Failure of both backends is one failed preset.");
        Equal(5, File.ReadAllLines(log).Length, "Fallback obeys the one-attempt limit and cannot loop back to Serum 1.");
        Equal(previousPath, target.PreviewPath, "Failed forced fallback keeps the previous mapping.");
        Equal(previousHash, Fingerprints.HashFile(previousPath), "Failed forced fallback preserves the previous audio bytes.");

        target.Name = "serum-cancel";
        using (var cancellation = new CancellationTokenSource())
        {
            var run = queue.RunAsync(settings, [target], TestExecutable, new InlineProgress(), cancellation.Token, true, readiness);
            await WaitForStartedAsync(target.SourcePath + ".fallback-started", run);
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => run);
        }
        Equal(previousHash, Fingerprints.HashFile(previousPath), "Cancelling the fallback preserves old audio.");
        True(!Directory.EnumerateDirectories(settings.PreviewDirectory, ".soundpreviewer-*").Any(), "Cancelled fallback staging directories are cleaned.");

        var startup = Preset("serum-startup");
        var startupResult = await queue.RunAsync(settings, [startup], TestExecutable, new InlineProgress(), CancellationToken.None, readiness: readiness);
        Equal(1, startupResult.Generated, "Default-startup rejection triggers the same guarded fallback.");
        serum1.Backend = RenderBackend.Unconfigured;
        var missingPrimary = Preset("serum-unconfigured");
        var missingResult = await queue.RunAsync(settings, [missingPrimary], TestExecutable, new InlineProgress(), CancellationToken.None, readiness: readiness);
        Equal(1, missingResult.Generated, "An unavailable Serum 1 can fall back when its FXP identity is confirmed.");
        serum1.Backend = RenderBackend.DawDreamer;

        foreach (string invalid in new[] { "wrong-id", "changed-source", "unavailable-serum2" })
        {
            var record = Preset(invalid);
            var states = readiness;
            if (invalid == "wrong-id")
            {
                byte[] raw = File.ReadAllBytes(record.SourcePath);
                "syl1"u8.CopyTo(raw.AsSpan(16));
                File.WriteAllBytes(record.SourcePath, raw);
                record.ContentHash = Fingerprints.HashFile(record.SourcePath);
            }
            if (invalid == "changed-source") await File.AppendAllTextAsync(record.SourcePath, "changed");
            if (invalid == "unavailable-serum2") states = [readiness[0], readiness[1] with { CanAttemptRender = false }];
            int count = File.ReadAllLines(log).Length;
            var blocked = await queue.RunAsync(settings, [record], TestExecutable, new InlineProgress(), CancellationToken.None, readiness: states);
            Equal(1, blocked.Failed, "An unsafe or unavailable fallback retains the primary failure.");
            Equal(count + 1, File.ReadAllLines(log).Length, "Invalid source evidence or unavailable Serum 2 never invokes fallback.");
        }
    }
}
