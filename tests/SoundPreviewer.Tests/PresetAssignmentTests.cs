using System.Text.Json;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestPresetAssignmentAsync()
    {
        string directory = Fixture("targeted-synth-assignment");
        PresetRecord Record(string id, SynthKind synth, string md5) => new()
        {
            Id = id, Name = id, Synth = synth, SourceId = "test-source",
            SourcePath = Path.Combine(directory, id + ".fxp"), ContentMd5 = md5,
            ContentHash = new string('a', 64), Favorite = true,
            ManualTags = ["keep tag"], Tags = ["keep tag"], ManualCategories = ["keep category"], CategoryTags = ["keep category"]
        };
        string md5 = new('b', 32);
        var selected = Record("selected", SynthKind.Unknown, md5);
        var oldCopy = Record("old-copy", SynthKind.Unknown, md5);
        oldCopy.DuplicateOfId = selected.Id;
        var donor = Record("new-donor", SynthKind.Nexus2, md5);
        foreach (var record in new[] { selected, oldCopy, donor }) File.WriteAllText(record.SourcePath, "TEST ONLY identical source");
        CompletePreview(donor, Path.Combine(directory, "existing.wav"));
        var unrelated = Record("unrelated", SynthKind.Serum1, new string('c', 32));
        unrelated.DuplicateOfId = "keep-existing-link";
        PresetRecord[] records = [selected, oldCopy, donor, unrelated];
        var store = new LibraryStore(Path.Combine(directory, "library.db"));
        store.UpsertMany(records);
        string protectedJson = JsonSerializer.Serialize(store.LoadPresets().Single(r => r.Id == unrelated.Id), LibraryJson.Options);
        string previewHash = Fingerprints.HashFile(donor.PreviewPath);
        var changed = PresetAssignmentService.Assign(store, records, selected.Id, SynthKind.Nexus2,
            [new SourceFolder { Id = "test-source", Path = directory }]);
        Equal(3, changed.Count, "Only the selected MD5 group is examined and written.");
        Equal(SynthKind.Unknown, selected.Synth, "Background work never mutates the UI's live records.");
        var assigned = changed.Single(r => r.Id == selected.Id);
        Equal(donor.Id, assigned.DuplicateOfId, "The assigned preset joins the target synth's verified canonical group.");
        Equal(donor.PreviewPath, assigned.PreviewPath, "A verified identical preview is reused without rendering.");
        Equal("", changed.Single(r => r.Id == oldCopy.Id).DuplicateOfId, "The old synth group no longer points at its reclassified canonical.");
        True(assigned.Favorite && assigned.ManualTags!.Single() == "keep tag" && assigned.ManualCategories!.Single() == "keep category", "Assignment preserves personal metadata.");
        Equal(protectedJson, JsonSerializer.Serialize(store.LoadPresets().Single(r => r.Id == unrelated.Id), LibraryJson.Options), "Unrelated duplicate links and metadata remain unchanged.");
        Equal(previewHash, Fingerprints.HashFile(donor.PreviewPath), "Assignment never changes existing audio.");
        string committed = JsonSerializer.Serialize(store.LoadPresets(), LibraryJson.Options);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Throws<OperationCanceledException>(() => PresetAssignmentService.Assign(store, records, selected.Id, SynthKind.Serum1, [], cancel.Token));
        Throws<ArgumentException>(() => PresetAssignmentService.Assign(store, records, selected.Id, SynthKind.Unknown, []));
        Equal(committed, JsonSerializer.Serialize(store.LoadPresets(), LibraryJson.Options), "Cancellation and invalid choices cannot partially change the library.");
        return Task.CompletedTask;
    }
}
