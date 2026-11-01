using System.Text.Json;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Core.Services;

public static class PresetAssignmentService
{
    public static IReadOnlyList<PresetRecord> Assign(LibraryStore store, IReadOnlyList<PresetRecord> records,
        string selectedId, SynthKind synth, IReadOnlyList<SourceFolder> sources, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var selected = records.Single(record => record.Id == selectedId);
        // A synth change only alters duplicate groups with the same content. Never
        // probe unrelated source files, decode their audio, or rewrite the library.
        var related = records.Where(record => record.Id == selectedId || PresetIndex.IsMd5(selected.ContentMd5) &&
            string.Equals(record.ContentMd5, selected.ContentMd5, StringComparison.OrdinalIgnoreCase)).ToList();
        var copies = JsonSerializer.Deserialize<List<PresetRecord>>(JsonSerializer.Serialize(related, LibraryJson.Options), LibraryJson.Options)!;
        ScanService.AssignSynth(copies.Single(record => record.Id == selectedId), synth);
        PresetIndex.RebuildDuplicates(copies, sources);
        var (audioUpdates, _) = DuplicatePreviewService.PrepareUpdates(copies, token);
        foreach (var update in audioUpdates) LibraryStore.CopyAudioFields(update.Identity, update.Rendered);
        token.ThrowIfCancellationRequested();
        store.UpsertMany(copies);
        return copies; // No cancellation after the atomic commit; the UI must apply it.
    }
}
