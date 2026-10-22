using System.Text.Json;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Core.Services;

/// <summary>Shares verified preview mappings with identical copies while retaining each source record.</summary>
public static class DuplicatePreviewService
{
    public static int Synchronize(LibraryStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        cancellationToken.ThrowIfCancellationRequested();
        var records = store.LoadPresets();
        var view = new PresetDuplicateView(records);
        var verified = new Dictionary<string, bool>(StringComparer.Ordinal);
        var donors = new Dictionary<string, PresetRecord>(StringComparer.Ordinal);
        var updates = new List<(PresetRecord Rendered, PresetRecord Identity, bool AudioOnly)>();
        foreach (var copy in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (copy.DuplicateOfId.Length == 0 || copy.Status == PresetStatus.Rendering) continue;
            var donor = view.ResolveCanonical(copy);
            if (donor.Id == copy.Id || donor.Status == PresetStatus.Rendering || donor.Synth == SynthKind.Unknown || !Enum.IsDefined(donor.Synth) ||
                donor.ContentHash.Length != 64 || !donor.ContentHash.All(char.IsAsciiHexDigit) ||
                !string.Equals(donor.ContentHash, copy.ContentHash, StringComparison.OrdinalIgnoreCase) ||
                donor.Synth != copy.Synth || donor.InternalPath != copy.InternalPath || donor.BankSlot != copy.BankSlot)
                continue;
            // Identical persisted mappings need no I/O, including when both refer to
            // a cache the user has removed. Reopening a repaired library is cheap.
            if (SameAudioMapping(copy, donor) || HasValidPreview(copy) || !HasValidPreview(donor)) continue;
            var changed = JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(copy, LibraryJson.Options), LibraryJson.Options)!;
            LibraryStore.CopyAudioFields(changed, donor);
            // AudioOnly preserves source/render states. Ready prevents its optional
            // NeedsReview downgrade signal from changing an independently confirmed copy.
            changed.Status = PresetStatus.Ready;
            updates.Add((changed, copy, true));
            donors.TryAdd(donor.Id, donor);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (updates.Count == 0) return 0;
        var merged = store.MergeRenderStates(updates, donors.Values.ToList());
        // No cancellation after the transaction: all published mappings are durable.
        return merged.Where((record, index) => SameAudioMapping(record, updates[index].Rendered)).Count();

        bool HasValidPreview(PresetRecord record)
        {
            if (string.IsNullOrWhiteSpace(record.PreviewPath) || string.IsNullOrWhiteSpace(record.CacheKey) ||
                string.IsNullOrWhiteSpace(record.TemplateJson) || !File.Exists(record.PreviewPath)) return false;
            string key = AudioMapping(record);
            if (!verified.TryGetValue(key, out bool valid))
                verified[key] = valid = PresetIndex.HasRecoverablePreview(record, cancellationToken);
            return valid;
        }
    }

    private static bool SameAudioMapping(PresetRecord first, PresetRecord second) =>
        AudioMapping(first) == AudioMapping(second);

    private static string AudioMapping(PresetRecord record) => LibraryStore.AudioFieldsIdentity(record);
}
