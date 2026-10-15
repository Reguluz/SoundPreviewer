using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static async Task TestPresetCategoriesAsync()
    {
        string directory = Fixture("preset-categories");
        string bundled = Path.Combine(FindRepository(), "src", "SoundPreviewer.Core", "Config", "preset-categories.default.json");
        string path = Path.Combine(directory, "Config", "preset-categories.json");
        var categories = new PresetCategoryService(path, bundled);
        categories.EnsureRulesFile();
        byte[] original = File.ReadAllBytes(path);
        var loaded = categories.Load();
        True(loaded.Success && loaded.RuleCount >= 10, "Bundled common categories load from the isolated editable rules copy.");
        True(categories.Classify("BS_01 Deep").Contains("Bass / 贝斯"), "A conventional bass prefix matches without case sensitivity.");
        True(categories.Classify("Bass01").Contains("Bass / 贝斯"), "Full category-name prefixes accept a numeric preset suffix.");
        True(categories.Classify("[LD] Bright").Contains("Lead / 主音"), "Leading brackets do not hide a conventional prefix.");
        True(categories.Classify("ld02 Bright").Contains("Lead / 主音"), "Numeric suffixes are accepted after a prefix.");
        True(categories.Classify("Bassoon").Count == 0 && categories.Classify("monkey").Count == 0 && categories.Classify("BSquare").Count == 0,
            "English token and prefix boundaries cannot match unrelated longer words.");
        True(categories.Classify("Arp Pluck Bass").ToHashSet().SetEquals(["Bass / 贝斯", "Pluck / 拨弦", "Arp / 琶音"]), "A preset can belong to several independent categories.");
        True(categories.Classify("梦幻钢琴与电钢琴").ToHashSet().SetEquals(["Keys / 键盘", "Piano / 钢琴"]), "Chinese category words match inside continuous Chinese names.");

        var record = new PresetRecord
        {
            Id = "category-feedback", Name = "BS_01 Deep", CategoryTags = ["Old category"], Tags = ["User tag", "Serum 1"],
            Favorite = true, Feedback = -1, FeedbackUtc = DateTime.UtcNow, Status = PresetStatus.Ready, Attempts = 2,
            PreviewPath = "TEST-ONLY-kept.wav", CacheKey = "TEST-ONLY-cache", TemplateJson = "TEST-ONLY-template",
            Notes = [new NotePreview { MidiNote = 36, DurationSeconds = 1 }], AdapterVersion = "TEST-ONLY-adapter"
        };
        var expected = JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(record, LibraryJson.Options), LibraryJson.Options)!;
        expected.CategoryTags = categories.Classify(record.Name);
        var applied = categories.Apply([record]);
        True(applied.Success && applied.Processed == 1 && applied.Changed == 1, "Apply reports only actual category changes.");
        Equal(JsonSerializer.Serialize(expected, LibraryJson.Options), JsonSerializer.Serialize(record, LibraryJson.Options), "Reclassification changes no user tags, feedback, preview, attempts or status.");
        Equal(0, categories.Apply([record]).Changed, "Applying an unchanged ruleset is idempotent.");
        var store = new LibraryStore(Path.Combine(directory, "categories.db"));
        store.Upsert(record);
        True(store.LoadPresets().Single().CategoryTags.SequenceEqual(record.CategoryTags), "Categories persist in the existing SQLite metadata without a schema reset.");
        True(JsonSerializer.Deserialize<PresetRecord>("{\"Name\":\"legacy\"}", LibraryJson.Options)!.CategoryTags.Count == 0, "Old library JSON defaults to an empty category list.");

        File.WriteAllText(path, "{", new UTF8Encoding(false));
        categories.EnsureRulesFile();
        Equal("{", File.ReadAllText(path), "Ensure never overwrites an existing invalid user-edited rules file.");
        var invalid = categories.Load();
        True(!invalid.Success && invalid.UsedPreviousRules && invalid.Error.Length > 0, "A bad replacement reports failure while retaining the last successfully compiled rules.");
        True(categories.Classify("BS_01").Contains("Bass / 贝斯"), "The last valid in-memory snapshot remains available after reload fails.");
        string preserved = JsonSerializer.Serialize(record, LibraryJson.Options);
        var rejected = categories.ReloadAndApply([record]);
        True(!rejected.Success && rejected.Processed == 0 && rejected.Changed == 0, "A bad new file cannot silently apply stale or empty rules to the library.");
        Equal(preserved, JsonSerializer.Serialize(record, LibraryJson.Options), "A failed reload leaves every record field unchanged.");
        var neverLoaded = new PresetCategoryService(path, bundled);
        True(!neverLoaded.Load().Success && !neverLoaded.Apply([record]).Success, "A new process with invalid rules cannot clear saved categories.");
        Throws<InvalidOperationException>(() => neverLoaded.Classify("Bass"));

        foreach (string bad in new[]
        {
            "{\"version\":\"1\",\"categories\":[]}",
            "{\"version\":2,\"categories\":[]}",
            "{\"version\":1,\"version\":1,\"categories\":[]}",
            "{\"version\":1,\"categories\":[{\"label\":\"A\",\"regex\":\"(a+)+$\"}]}",
            "{\"version\":1,\"categories\":[{\"label\":\"A\",\"tokens\":[]}]}",
            "{\"version\":1,\"categories\":[{\"label\":\"A\",\"tokens\":[\"x\"]},{\"label\":\"a\",\"prefixes\":[\"y\"]}]}"
        }) True(!PresetCategoryService.Validate(bad).Success, "Invalid versions, duplicate properties/categories and regex rules must be rejected explicitly.");
        True(!PresetCategoryService.Validate(new string(' ', PresetCategoryService.MaximumRulesBytes + 1) + "{}").Success, "Oversized JSON is rejected before parsing.");
        string tooMany = JsonSerializer.Serialize(new { version = 1, categories = Enumerable.Range(0, 65).Select(i => new { label = i.ToString(), tokens = new[] { "x" } }) });
        True(!PresetCategoryService.Validate(tooMany).Success, "The number of categories is bounded.");
        string tooLong = JsonSerializer.Serialize(new { version = 1, categories = new[] { new { label = "A", tokens = new[] { new string('x', 65) } } } });
        True(!PresetCategoryService.Validate(tooLong).Success, "The length of every literal matching term is bounded.");

        File.WriteAllBytes(path, original);
        True(categories.Load().Success, "A fixed rules file can replace a failed reload.");
        var first = new PresetRecord { Name = "Bass", CategoryTags = ["Keep first"] };
        var second = new PresetRecord { Name = "Lead", CategoryTags = ["Keep second"] };
        using (var cancellation = new CancellationTokenSource())
        {
            IEnumerable<PresetRecord> CancelDuringAnalysis()
            {
                yield return first;
                cancellation.Cancel();
                yield return second;
            }
            Throws<OperationCanceledException>(() => categories.Apply(CancelDuringAnalysis(), cancellation.Token));
        }
        True(first.CategoryTags.SequenceEqual(["Keep first"]) && second.CategoryTags.SequenceEqual(["Keep second"]), "Cancellation during analysis cannot leave half of a library reclassified.");

        string sourcePath = Path.Combine(directory, "source");
        Directory.CreateDirectory(sourcePath);
        string presetPath = Path.Combine(sourcePath, "Synth.vital");
        File.WriteAllText(presetPath, "{\"settings\":{},\"preset_name\":\"LD_01 Sky\"}");
        var source = new SourceFolder { Path = sourcePath, Synth = SynthKind.Vital };
        var scanner = new ScanService(categories);
        var scan = await scanner.ScanAsync([source], []);
        var scanned = scan.Presets.Single();
        True(scanned.CategoryTags.Contains("Lead / 主音"), "Initial scanning classifies the actual preset name, not its filename or parent folder.");
        scanned.Tags.Add("User scan tag");
        scanned.Favorite = true;
        scanned.Feedback = 1;
        scanned.PreviewPath = "TEST-ONLY-preserved-preview.wav";
        scanned.CacheKey = "TEST-ONLY-preserved-key";
        scanned.Status = PresetStatus.Ready;
        var edited = JsonNode.Parse(Encoding.UTF8.GetString(original))!;
        foreach (var category in edited["categories"]!.AsArray())
            if (category!["label"]!.GetValue<string>() == "Lead / 主音") category["label"] = "Lead / 旋律";
        File.WriteAllText(path, edited.ToJsonString());
        var rescanned = (await scanner.ScanAsync([source], scan.Presets)).Presets.Single();
        True(rescanned.CategoryTags.SequenceEqual(["Lead / 旋律"]), "A rules edit updates categories even when preset bytes are unchanged.");
        True(rescanned.Tags.Contains("User scan tag") && rescanned.Favorite && rescanned.Feedback == 1 &&
            rescanned.PreviewPath == scanned.PreviewPath && rescanned.CacheKey == scanned.CacheKey && rescanned.Status == PresetStatus.Ready,
            "Category-only rescanning preserves custom tags, feedback and generated preview metadata.");
        File.WriteAllText(path, "{");
        File.WriteAllText(presetPath, "{\"settings\":{},\"preset_name\":\"BS_Changed\"}");
        var failedRulesScan = await scanner.ScanAsync([source], [rescanned]);
        True(failedRulesScan.Warnings.Any(warning => warning.Contains("分类规则", StringComparison.Ordinal)) &&
            failedRulesScan.Presets.Single().CategoryTags.SequenceEqual(["Lead / 旋律"]), "Invalid rules retain the previous categories even when scanned content changes.");
        File.WriteAllBytes(path, original);
        var fixedRulesScan = await scanner.ScanAsync([source], failedRulesScan.Presets);
        True(fixedRulesScan.Presets.Single().CategoryTags.SequenceEqual(["Bass / 贝斯"]), "Fixing the rules lets a later scan refresh previously preserved categories.");
    }
}
