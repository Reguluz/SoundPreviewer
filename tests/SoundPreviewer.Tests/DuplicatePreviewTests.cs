using System.Text.Json;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static async Task TestDuplicatePreviewSynchronizationAsync()
    {
        string directory = Fixture("duplicate-preview-mappings");
        string first = Path.Combine(directory, "first"), second = Path.Combine(directory, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var canonical = Preset("success");
        canonical.SourcePath = Path.Combine(first, "Original.vital");
        File.WriteAllText(canonical.SourcePath, "TEST ONLY identical preset content");
        canonical.ContentHash = Fingerprints.HashFile(canonical.SourcePath);
        var copy = CloneDuplicatePreviewRecord(canonical);
        copy.Id = "TEST ONLY cross-path copy";
        copy.SourcePath = Path.Combine(second, "Copy.vital");
        File.Copy(canonical.SourcePath, copy.SourcePath);
        copy.SourceId = "TEST ONLY second source";
        copy.DuplicateOfId = canonical.Id;
        copy.Favorite = true; copy.Feedback = -1; copy.Attempts = 2;
        copy.Tags = ["TEST ONLY local tag"]; copy.ManualTags = ["TEST ONLY local tag"];
        copy.CategoryTags = ["TEST ONLY local category"]; copy.ManualCategories = ["TEST ONLY local category"];
        copy.LastRenderOutcome = "TEST ONLY own history";
        copy.LastRenderMessage = "TEST ONLY own diagnostic";
        var originalCopy = CloneDuplicatePreviewRecord(copy);
        var settings = new AppSettings
        {
            PreviewDirectory = Path.Combine(directory, "Preview"), PreviewFormat = PreviewAudioFormat.Wav,
            Template = new PreviewTemplate { QuickPreview = false, HoldSeconds = .1, TailSeconds = .02 }, MaxAttempts = 1,
            Plugins = [new PluginConfiguration { Synth = SynthKind.Vital, Backend = RenderBackend.ExternalWorker,
                ExecutablePath = TestExecutable, Version = "TEST ONLY duplicate preview", TimeoutSeconds = 5 }]
        };
        var store = new LibraryStore(Path.Combine(directory, "library.db"));
        store.SaveState([canonical, copy], settings);
        var queue = new RenderQueue(store);
        Equal(1, (await queue.RunAsync(settings, [canonical], TestExecutable, new InlineProgress(), CancellationToken.None)).Generated,
            "Generating the canonical sound renders only one worker item.");
        var stored = store.LoadPresets();
        var donor = stored.Single(record => record.Id == canonical.Id);
        var linked = stored.Single(record => record.Id == copy.Id);
        AssertDuplicatePreviewMapping(originalCopy, donor, linked);
        True(File.Exists(linked.PreviewPath), "A cross-path copy has a real playable path rather than a display-only generated label.");
        Equal(PreviewAudioFile.Read(donor.PreviewPath).Frames, PreviewAudioFile.Read(linked.PreviewPath).Frames,
            "The copy and canonical play the same complete three-register audio.");
        WorkerProcessClient.ValidateNotes(new RenderResult { Metrics = PreviewAudioFile.Validate(linked.PreviewPath),
            Notes = linked.Notes, AdapterVersion = linked.AdapterVersion }, settings.Template);
        Equal(0, DuplicatePreviewService.Synchronize(store), "A repaired library has no further mapping writes.");

        string originalAudioHash = Fingerprints.HashFile(donor.PreviewPath);
        store.Upsert(originalCopy);
        var noRenderer = new AppSettings { PreviewDirectory = settings.PreviewDirectory, Plugins = [] };
        var reused = await queue.RunAsync(noRenderer, [donor], Path.Combine(directory, "missing-worker"), new InlineProgress(), CancellationToken.None);
        Equal(1, reused.Reused, "Reusing a historical preview also repairs a missing duplicate mapping without a renderer.");
        linked = store.LoadPresets().Single(record => record.Id == copy.Id);
        AssertDuplicatePreviewMapping(originalCopy, donor, linked);
        Equal(originalAudioHash, Fingerprints.HashFile(donor.PreviewPath), "Mapping repair never rewrites or recompresses cached audio.");

        var ownPreview = CloneDuplicatePreviewRecord(originalCopy);
        ownPreview.Id = "TEST ONLY copy with its own valid preview";
        CompletePreview(ownPreview, Path.Combine(directory, "TEST-ONLY-retained-own-preview.wav"));
        string ownJson = JsonSerializer.Serialize(ownPreview, LibraryJson.Options);
        store.Upsert(ownPreview);
        var guarded = new List<PresetRecord>();
        foreach (string reason in new[] { "sha", "synth", "slot", "internal", "dangling", "cycle" })
        {
            var record = CloneDuplicatePreviewRecord(originalCopy);
            record.Id = "TEST ONLY guarded " + reason;
            if (reason == "sha") record.ContentHash = Fingerprints.HashText("TEST ONLY different SHA256");
            if (reason == "synth") record.Synth = SynthKind.Serum1;
            if (reason == "slot") record.BankSlot = 1;
            if (reason == "internal") record.InternalPath = "different-entry.vital";
            if (reason == "dangling") record.DuplicateOfId = "TEST ONLY missing canonical";
            if (reason == "cycle") record.DuplicateOfId = record.Id;
            guarded.Add(record);
        }
        store.UpsertMany(guarded);
        var protectedJson = guarded.ToDictionary(record => record.Id, record => JsonSerializer.Serialize(record, LibraryJson.Options));
        Equal(0, DuplicatePreviewService.Synchronize(store), "Unrelated identities and already valid previews cannot borrow another mapping.");
        foreach (var record in store.LoadPresets().Where(record => protectedJson.ContainsKey(record.Id)))
            Equal(protectedJson[record.Id], JsonSerializer.Serialize(record, LibraryJson.Options), "A mismatched or broken duplicate remains unchanged.");
        Equal(ownJson, JsonSerializer.Serialize(store.LoadPresets().Single(record => record.Id == ownPreview.Id), LibraryJson.Options),
            "An existing valid copy preview and its personal state are retained.");

        foreach (var status in new[] { PresetStatus.Missing, PresetStatus.Unsupported, PresetStatus.Unidentified })
        {
            var record = CloneDuplicatePreviewRecord(originalCopy);
            record.Id = "TEST ONLY preserved state " + status;
            record.Status = status;
            store.Upsert(record);
            Equal(1, DuplicatePreviewService.Synchronize(store), "One missing mapping can be repaired without reclassifying its source.");
            AssertDuplicatePreviewMapping(record, donor, store.LoadPresets().Single(item => item.Id == record.Id));
        }
        var rendering = CloneDuplicatePreviewRecord(originalCopy);
        rendering.Id = "TEST ONLY copy with an in-flight render";
        rendering.Status = PresetStatus.Rendering;
        store.Upsert(rendering);
        Equal(0, DuplicatePreviewService.Synchronize(store), "A copy being rendered cannot acquire a competing preview mapping.");
        Equal(JsonSerializer.Serialize(rendering, LibraryJson.Options),
            JsonSerializer.Serialize(store.LoadPresets().Single(record => record.Id == rendering.Id), LibraryJson.Options),
            "An in-flight copy retains all its current fields.");

        var invalidDonor = CloneDuplicatePreviewRecord(donor);
        invalidDonor.Id = "TEST ONLY canonical with invalid note mapping";
        invalidDonor.Notes.RemoveAt(2);
        var invalidCopy = CloneDuplicatePreviewRecord(originalCopy);
        invalidCopy.Id = "TEST ONLY copy of invalid canonical";
        invalidCopy.DuplicateOfId = invalidDonor.Id;
        store.UpsertMany([invalidDonor, invalidCopy]);
        Equal(0, DuplicatePreviewService.Synchronize(store), "Existing bytes without a complete validated three-register map cannot be borrowed.");
        Equal(JsonSerializer.Serialize(invalidCopy, LibraryJson.Options),
            JsonSerializer.Serialize(store.LoadPresets().Single(record => record.Id == invalidCopy.Id), LibraryJson.Options),
            "A malformed donor leaves a missing copy preview untouched.");

        var rollbackA = CloneDuplicatePreviewRecord(originalCopy);
        var rollbackB = CloneDuplicatePreviewRecord(originalCopy);
        rollbackA.Id = "TEST-ONLY-duplicate-rollback-A";
        rollbackB.Id = "TEST-ONLY-duplicate-rollback-B";
        store.UpsertMany([rollbackA, rollbackB]);
        string beforeFailure = JsonSerializer.Serialize(store.LoadPresets(), LibraryJson.Options);
        using (var database = new CacheTestSqlite(store.DatabasePath))
            database.Execute("CREATE TRIGGER reject_duplicate_mapping BEFORE INSERT ON presets WHEN NEW.id='TEST-ONLY-duplicate-rollback-B' AND instr(NEW.json,'.wav')>0 BEGIN SELECT RAISE(ABORT,'TEST ONLY duplicate mapping failure'); END;");
        Throws<IOException>(() => DuplicatePreviewService.Synchronize(store));
        Equal(beforeFailure, JsonSerializer.Serialize(store.LoadPresets(), LibraryJson.Options), "A mapping transaction failure rolls every copy back together.");
        Equal(originalAudioHash, Fingerprints.HashFile(donor.PreviewPath), "Failed mapping repair cannot change or delete cached audio.");
        using (var database = new CacheTestSqlite(store.DatabasePath))
        {
            database.Execute("DROP TRIGGER reject_duplicate_mapping;");
            database.Execute($"CREATE TRIGGER change_duplicate_donor AFTER INSERT ON presets WHEN NEW.id='TEST-ONLY-duplicate-rollback-A' AND instr(NEW.json,'.wav')>0 BEGIN UPDATE presets SET json=replace(json,'{donor.ContentHash}','{new string('0', 64)}') WHERE id='{donor.Id}'; END;");
        }
        Throws<InvalidOperationException>(() => DuplicatePreviewService.Synchronize(store));
        Equal(beforeFailure, JsonSerializer.Serialize(store.LoadPresets(), LibraryJson.Options),
            "Donor identity is checked inside the copy transaction; an intervening canonical change rolls every update back.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => DuplicatePreviewService.Synchronize(store, cancellation.Token));
        Equal(beforeFailure, JsonSerializer.Serialize(store.LoadPresets(), LibraryJson.Options), "Canceled repair preserves all library records.");
    }

    private static PresetRecord CloneDuplicatePreviewRecord(PresetRecord record) =>
        JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(record, LibraryJson.Options), LibraryJson.Options)!;

    private static void AssertDuplicatePreviewMapping(PresetRecord original, PresetRecord donor, PresetRecord actual)
    {
        var expected = CloneDuplicatePreviewRecord(original);
        expected.PreviewPath = donor.PreviewPath;
        expected.CacheKey = donor.CacheKey;
        expected.RenderedUtc = donor.RenderedUtc;
        expected.Metrics = donor.Metrics;
        expected.Notes = donor.Notes;
        expected.AdapterVersion = donor.AdapterVersion;
        expected.PluginVersion = donor.PluginVersion;
        expected.TemplateJson = donor.TemplateJson;
        Equal(JsonSerializer.Serialize(expected, LibraryJson.Options), JsonSerializer.Serialize(actual, LibraryJson.Options),
            "Only verified audio fields are shared; each copy keeps its source, status, edits, feedback and attempts.");
    }
}
