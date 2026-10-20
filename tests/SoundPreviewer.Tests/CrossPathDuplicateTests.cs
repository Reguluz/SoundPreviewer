using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static async Task TestCrossSourceDuplicateViewAsync()
    {
        string root = Fixture("cross-source-duplicates");
        string first = Path.Combine(root, "A");
        string second = Path.Combine(root, "B");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "Original.vital"), "{\"settings\":{}}");
        File.Copy(Path.Combine(first, "Original.vital"), Path.Combine(second, "Copy.vital"));
        File.Copy(Path.Combine(first, "Original.vital"), Path.Combine(second, "Another-copy.vital"));
        File.WriteAllText(Path.Combine(second, "Unique.vital"), "{\"settings\":{\"different\":1}}");
        var sources = new[]
        {
            new SourceFolder { Id = "A", Path = first, Synth = SynthKind.Vital },
            new SourceFolder { Id = "B", Path = second, Synth = SynthKind.Vital }
        };
        var records = (await new ScanService().ScanAsync(sources, [])).Presets;
        var canonical = records.Single(record => record.SourceId == "A");
        Equal("", canonical.DuplicateOfId, "The deterministic first source retains the global representative.");
        Equal(2, records.Count(record => record.DuplicateOfId == canonical.Id), "Identical presets form one duplicate group across source folders.");
        var copy = records.First(record => record.SourceId == "B" && record.DuplicateOfId.Length > 0);
        copy.ManualTags = ["my local copy"];
        copy.Favorite = true;
        string before = System.Text.Json.JsonSerializer.Serialize(records, LibraryJson.Options);
        var view = new PresetDuplicateView(records);
        Equal(new PresetSourceCounts(4, 2, 2, 3), view.TotalCounts, "Global counts include every physical copy while retaining two distinct sounds.");
        Equal(new PresetSourceCounts(1, 1, 0, 1), view.GetSourceCounts("A"), "The canonical source also reports its cross-source match.");
        Equal(new PresetSourceCounts(3, 2, 1, 2), view.GetSourceCounts("B"), "A source distinguishes local extra copies from matches in another source.");
        Equal(2, records.Count(record => view.IsRepresentative(record)), "All sources hide global duplicate copies.");
        var visibleB = records.Where(record => view.IsRepresentative(record, "B")).ToList();
        Equal(2, visibleB.Count, "Selecting a copied source still shows both sounds stored in that source.");
        True(visibleB.All(record => record.SourceId == "B"), "Source filtering retains each source's own record and metadata.");
        True(visibleB.Any(record => record.DuplicateOfId == canonical.Id), "A source-local representative may be a global duplicate.");
        var renderSelection = view.ResolveCanonicalSelection(visibleB.Concat([canonical, copy]));
        Equal(2, renderSelection.Count, "Generating a filtered source resolves duplicate copies once per canonical preset.");
        True(renderSelection.Contains(canonical) && renderSelection.All(record => record.DuplicateOfId.Length == 0), "Rendering uses canonical records accepted by the queue.");
        Equal(before, System.Text.Json.JsonSerializer.Serialize(records, LibraryJson.Options), "Building views and resolving generation cannot alter personal metadata, cache fields or duplicate identities.");

        // A source disabled or removed later can promote its available copy without changing the view rules.
        sources[0].Enabled = false;
        PresetIndex.RebuildDuplicates(records, sources);
        view = new PresetDuplicateView(records);
        Equal(1, records.Count(record => record.SourceId == "A" && view.IsRepresentative(record, "A")), "An inactive canonical source remains inspectable after promotion elsewhere.");
        Equal(2, records.Count(record => view.IsRepresentative(record, "B")), "Promotion keeps one local representative per content group.");
        True(view.ResolveCanonicalSelection([canonical]).Single().SourceId == "B", "Generation follows the newly available global representative.");

        var dangling = new PresetRecord { Id = "dangling", SourceId = "legacy", DuplicateOfId = "missing" };
        var cyclicA = new PresetRecord { Id = "cyclic-A", SourceId = "legacy", DuplicateOfId = "cyclic-B" };
        var cyclicB = new PresetRecord { Id = "cyclic-B", SourceId = "legacy", DuplicateOfId = "cyclic-A" };
        var legacy = new PresetDuplicateView([dangling, cyclicA, cyclicB]);
        Equal(dangling, legacy.ResolveCanonical(dangling), "Missing legacy canonical links never resolve to a different preset.");
        Equal(cyclicA, legacy.ResolveCanonical(cyclicA), "Malformed cyclic duplicate links cannot loop during filtering or rendering.");
        True(legacy.IsRepresentative(dangling, "legacy"), "A source-local view can still inspect a dangling legacy record.");
    }
}
