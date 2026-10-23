using System.Text.Json;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static async Task TestManualPresetMetadataAsync()
    {
        string directory = Fixture("manual-preset-metadata");
        string sources = Path.Combine(directory, "sources");
        Directory.CreateDirectory(sources);
        string path = Path.Combine(sources, "Bass.vital");
        File.WriteAllText(path, "{\"preset_name\":\"Bass\",\"settings\":{}}");
        string rules = Path.Combine(directory, "rules.json");
        File.WriteAllText(rules, "{\"version\":1,\"categories\":[{\"label\":\"Bass\",\"tokens\":[\"Bass\"]},{\"label\":\"Pad\",\"tokens\":[\"Pad\"]}]}");
        var categories = new PresetCategoryService(rules);
        var scanner = new ScanService(categories);
        var source = new SourceFolder { Path = sources, Synth = SynthKind.Vital };
        var initial = await scanner.ScanAsync([source], []);
        var record = initial.Presets.Single();
        record.Favorite = true;
        record.Feedback = -1;
        record.CacheKey = "existing-cache-identity";
        record.PreviewPath = Path.Combine(directory, "existing-preview.wav");
        using var unknown = JsonDocument.Parse("{\"Keep\":7}");
        record.AdditionalProperties = new() { ["FutureMetadata"] = unknown.RootElement.Clone() };
        string identity = record.Id;
        PresetMetadataService.SetTags(record, ["  Personal  ", "personal", "", "Live"]);
        PresetMetadataService.SetCategories(record, ["My category"]);
        True(record.Tags.SequenceEqual(["Personal", "Live"]), "Manual tags trim whitespace and deduplicate without changing order.");
        True(record.CategoryTags.SequenceEqual(["My category"]), "Legacy category fields mirror the effective manual value.");
        Equal(identity, record.Id, "Metadata edits cannot replace a preset identity.");
        Equal("existing-cache-identity", record.CacheKey, "Metadata edits cannot invalidate an existing preview cache.");
        True(record.Favorite && record.Feedback == -1, "Editing metadata cannot change independent user feedback.");
        Throws<ArgumentException>(() => PresetMetadataService.SetTags(record, [new string('x', 65)]), "Invalid tag input must fail before editing metadata.");
        True(record.Tags.SequenceEqual(["Personal", "Live"]), "Failed metadata validation is atomic.");

        var store = new LibraryStore(Path.Combine(directory, "library.db"));
        store.Upsert(record);
        record = store.LoadPresets().Single();
        Equal(2, LibraryStore.CurrentSchemaVersion, "Manual metadata extends the schema-2 JSON without upgrading the database schema.");
        True(record.ManualTags!.SequenceEqual(["Personal", "Live"]), "Manual overrides round trip through the existing SQLite record.");
        Equal(7, record.AdditionalProperties!["FutureMetadata"].GetProperty("Keep").GetInt32(), "Unknown JSON fields survive storage writes.");
        var rescanned = await scanner.ScanAsync([source], [record]);
        record = rescanned.Presets.Single();
        True(record.Tags.SequenceEqual(["Personal", "Live"]) && record.CategoryTags.SequenceEqual(["My category"]), "Unchanged rescans cannot overwrite manual tags or categories.");
        Equal("existing-cache-identity", record.CacheKey, "Unchanged rescans preserve a manual edit's existing cache mapping.");

        File.WriteAllText(path, "{\"preset_name\":\"Pad\",\"settings\":{\"changed\":true}}");
        record = (await scanner.ScanAsync([source], [record])).Presets.Single();
        Equal(identity, record.Id, "Changed source content retains the edited record's identity.");
        True(record.Tags.SequenceEqual(["Personal", "Live"]) && record.CategoryTags.SequenceEqual(["My category"]), "Source content changes preserve explicit metadata overrides.");
        Equal(7, record.AdditionalProperties!["FutureMetadata"].GetProperty("Keep").GetInt32(), "Rescan replacement preserves unknown JSON fields.");
        PresetMetadataService.ResetCategories(record);
        True(record.CategoryTags.SequenceEqual(["Pad"]), "Reset restores the latest automatic classification rather than the stale pre-edit result.");
        PresetMetadataService.ResetTags(record);
        True(record.Tags.Count == 0 && record.ManualTags is null, "Reset restores empty automatic tags and releases the manual override.");

        PresetMetadataService.SetTags(record, []);
        PresetMetadataService.SetCategories(record, []);
        store.Upsert(record);
        record = store.LoadPresets().Single();
        record = (await scanner.ScanAsync([source], [record])).Presets.Single();
        True(record.ManualTags is { Count: 0 } && record.Tags.Count == 0 && record.ManualCategories is { Count: 0 } && record.CategoryTags.Count == 0,
            "An explicitly cleared override remains empty after persistence and rescanning.");
        True(PresetMetadataService.EffectiveTags(record).Count == 0 && PresetMetadataService.EffectiveCategories(record).Count == 0,
            "Effective metadata distinguishes an explicit empty edit from an untouched record.");
        File.WriteAllText(rules, "{\"version\":1,\"categories\":[{\"label\":\"Revised Pad\",\"tokens\":[\"Pad\"]}]}");
        True(categories.ReloadAndApply([record]).Success && record.CategoryTags.Count == 0, "Rule recalculation cannot refill a manually cleared category.");
        PresetMetadataService.ResetCategories(record);
        True(record.CategoryTags.SequenceEqual(["Revised Pad"]), "Reset after a rule update restores the new automatic category.");

        var legacy = JsonSerializer.Deserialize<PresetRecord>("{\"Id\":\"legacy\",\"Tags\":[\"Old tag\"],\"CategoryTags\":[\"Old category\"],\"FutureField\":42}", LibraryJson.Options)!;
        True(legacy.ManualTags is null && legacy.ManualCategories is null && PresetMetadataService.EffectiveTags(legacy).SequenceEqual(["Old tag"]),
            "Existing schema-2 JSON keeps its stored effective metadata without inventing manual edits.");
        store.Upsert(legacy);
        Equal(42, store.LoadPresets().Single(value => value.Id == "legacy").AdditionalProperties!["FutureField"].GetInt32(), "Older records preserve unknown fields when rewritten.");
    }
}
