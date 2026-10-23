using System.Text.Json;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestMetadataCatalogAsync()
    {
        string root = Fixture("metadata-catalog");
        var categories = new MetadataCatalog(Path.Combine(root, "categories.json"), true);
        var tags = new MetadataCatalog(Path.Combine(root, "tags.json"), false);
        Equal(0, tags.Items.Count, "New tag catalogs contain no generated categories, synth names or extensions.");
        var record = new PresetRecord { Id = "legacy", CategoryTags = ["Arp / 琶音"], Tags = ["Warm"], PreviewPath = "keep.m4a", CacheKey = "keep-cache" };
        True(categories.Synchronize(record, true) && tags.Synchronize(record, false), "Legacy strings receive numeric references.");
        int arp = record.CategoryIds!.Single();
        Equal("Arp / 琶音 / 点缀", categories.Label(arp, "zh-CN"), "Canonical name and optional aliases display together.");
        Equal("Arp", categories.Label(arp, "en-US"), "English metadata resolves the same numeric ID.");
        var before = JsonSerializer.Serialize(record, LibraryJson.Options);
        categories.Edit(arp, "Arpeggio", ["装饰"], "");
        Equal("Arpeggio / 装饰", categories.Label(arp, "zh-CN"), "Editing canonical text changes the display without replacing an ID.");
        Equal(arp, categories.Resolve("Arp / 琶音"), "Legacy automatic rules still resolve after renaming.");
        True(categories.Matches(record.CategoryIds, "装饰") && categories.Matches(record.CategoryIds, "Arp"), "Search includes aliases and previous names.");
        Equal(before, JsonSerializer.Serialize(record, LibraryJson.Options), "Translations and renames never rewrite the preset or audio mapping.");
        Throws<ArgumentException>(() => categories.Edit(arp, "  ", [], ""), "Names are required.");
        var reopened = new MetadataCatalog(Path.Combine(root, "categories.json"), true);
        Equal("Arpeggio / 装饰", reopened.Label(arp, "zh-CN"), "Editable catalog changes survive reopening.");
        True(!reopened.Synchronize(record, true), "An existing numeric reference is authoritative.");
        var recovered = new MetadataCatalog(Path.Combine(root, "recovered-categories.json"), true, reopened.ExportJson());
        Equal(reopened.Id, recovered.Id, "Missing root metadata files recover their stable identity from the library snapshot.");
        var otherLibrary = new MetadataCatalog(Path.Combine(root, "other-categories.json"), true);
        otherLibrary.Edit(arp, "Other category", [], "");
        True(otherLibrary.Synchronize(record, true, reopened.ExportJson()), "Importing into another catalog remaps references instead of confusing equal numeric IDs.");
        Equal("Arpeggio / 装饰", otherLibrary.Label(record.CategoryIds!.Single(), "zh-CN"), "Cross-library import preserves the source catalog's edited name and aliases.");
        string recoveryFile = Path.Combine(root, "recovered-categories.json");
        File.AppendAllText(recoveryFile, "\n");
        string externalEdit = File.ReadAllText(recoveryFile);
        Throws<IOException>(() => recovered.Edit(arp, "Cannot overwrite external edit", [], ""), "Concurrent manual JSON edits are protected.");
        Equal(externalEdit, File.ReadAllText(recoveryFile), "External file changes survive rejected GUI edits.");
        Equal("Arpeggio / 装饰", recovered.Label(arp, "zh-CN"), "A failed catalog write rolls back in-memory names.");
        for (int index = 0; index < 200; index++) tags.Resolve("Mood " + index);
        Equal(201, new MetadataCatalog(Path.Combine(root, "tags.json"), false).Items.Count, "Catalogs support large reusable tag collections.");
        string original = File.ReadAllText(Path.Combine(root, "categories.json"));
        File.WriteAllText(Path.Combine(root, "categories.json"), "{broken");
        Throws<JsonException>(() => new MetadataCatalog(Path.Combine(root, "categories.json"), true), "Invalid user JSON must not be overwritten by defaults.");
        Equal("{broken", File.ReadAllText(Path.Combine(root, "categories.json")), "Damaged custom JSON remains available for repair.");
        File.WriteAllText(Path.Combine(root, "categories.json"), original);

        string rules = Path.Combine(root, "Config", "preset-categories.json");
        var service = new PresetCategoryService(rules);
        service.EnsureRulesFile();
        True(service.Load().Success, "Bundled classification rules load.");
        foreach (string name in new[] { "CHORD NAH 03", "chord lush", "Warm Chords", "CH_001", "和弦音色" })
            True(service.Classify(name).Contains("Chord / 和弦"), "Chord detection: " + name);
        foreach (string name in new[] { "Chorus", "Chordophone", "Beach", "Chart" })
            True(!service.Classify(name).Contains("Chord / 和弦"), "Token and prefix boundaries avoid false positives: " + name);
        using var old = JsonDocument.Parse(File.ReadAllText(rules));
        var previous = new { version = 1, categories = old.RootElement.GetProperty("categories").EnumerateArray().Where(item => item.GetProperty("label").GetString() != "Chord / 和弦").Select(item => item.Clone()).ToList() };
        File.WriteAllText(rules, JsonSerializer.Serialize(previous));
        string originalRules = File.ReadAllText(rules);
        service.EnsureRulesFile();
        True(service.Load().Success && service.Classify("CHORD NAH 03").Contains("Chord / 和弦"), "Existing default installations gain Chord recognition.");
        Equal(originalRules, File.ReadAllText(rules + ".before-chord.bak"), "The previous editable rules are backed up before extension.");
        var manual = new PresetRecord { Name = "CHORD NAH 03", ManualCategories = ["Custom"], CategoryTags = ["Custom"], CategoryIds = [123] };
        service.Apply([manual]);
        True(manual.CategoryIds!.SequenceEqual([123]) && manual.CategoryTags.SequenceEqual(["Custom"]), "Chord recognition respects manual categories and numeric references.");
        return Task.CompletedTask;
    }

    private static Task TestLanguagesAsync()
    {
        var language = new LanguageService();
        language.Select("en-US");
        Equal("Library", language.Translate("音色库"), "English UI text comes from the JSON dictionary.");
        Equal("Processed 7 / 12", language.Translate("已处理 7 / 12"), "Parameterized progress messages retain values.");
        Equal(@"File path copied: E:\音色库\喜欢\Arp.fxp", language.Translate(@"已复制文件地址：E:\音色库\喜欢\Arp.fxp"), "Translation preserves Chinese user paths rather than translating their components.");
        Equal("Update Serum 2: current plugin version 2.0.23 is older than preset version 2.1.0. Update and retry.", language.Translate("需要更新 Serum 2：当前插件版本 2.0.23 低于音色制作版本 2.1.0，请更新后重试。"), "Python bridge version guidance uses the same editable language dictionary.");
        Equal("Filtered 7 / 12 · Generated 3 (1 awaiting checks) · 2 duplicate copies", language.Translate("筛选 7 / 12 · 已生成 3（待确认 1） · 2 个重复副本"), "Long progress templates translate coherently.");
        language.Select("zh-CN");
        Equal("音色库", language.Translate("音色库"), "Switching back restores Chinese.");
        string directory = Fixture("community-language");
        File.WriteAllText(Path.Combine(directory, "test.json"), "{\"languageName\":\"Community\",\"strings\":{\"音色库\":\"Collection\"}}");
        Equal("Community", language.Languages(directory).Single().Name, "The first JSON property supplies the language selector label.");
        language.Select("test", directory);
        Equal("Collection", language.Translate("音色库"), "Community JSON loads without recompiling.");
        Equal("未翻译", language.Translate("未翻译"), "Missing translations preserve readable source text.");
        Throws<ArgumentException>(() => language.Select("../test", directory), "Language identifiers cannot escape the language folder.");
        return Task.CompletedTask;
    }
}
