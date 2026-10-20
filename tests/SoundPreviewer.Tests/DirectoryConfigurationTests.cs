using System.Text.Json;
using System.Text.Json.Nodes;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestDirectoryConfigurationAsync()
    {
        string root = Fixture("directory-configuration"), sourcePath = Path.Combine(root, "presets");
        var settings = new AppSettings
        {
            Sources = [new SourceFolder { Id = "retained-source", Path = sourcePath, Synth = SynthKind.Serum1, Enabled = false,
                AdditionalProperties = new() { ["FutureSourceField"] = JsonSerializer.Deserialize<JsonElement>("{\"retained\":true}") } }],
            PluginDirectories = [Path.Combine(root, "plugins")],
            PlaybackVolume = .23,
            MaxParallelRenders = 8
        };
        var service = new DirectoryConfigurationService(root);
        True(service.LoadOrCreate(settings), "The first startup exports legacy database directories to explicit JSON.");
        Equal(Path.Combine(root, "Config", "directories.json"), service.FilePath, "The editable directory configuration is grouped under the installation's Config directory.");
        string initial = File.ReadAllText(service.FilePath);
        True(initial.Contains("\n") && initial.Contains("\"Serum1\""), "The file is indented and names synthesizers for manual editing.");
        True(!initial.Contains("PlaybackVolume") && !initial.Contains("MaxParallelRenders"), "Unrelated rendering and playback settings remain in the compatible database.");
        var first = JsonNode.Parse(initial)!.AsObject();
        Equal("retained-source", first["Sources"]![0]!["Id"]!.GetValue<string>(), "Export retains source IDs linked to existing records.");

        first["FutureDirectoryField"] = new JsonObject { ["value"] = "preserve across old versions" };
        first["Sources"]![0]!.AsObject().Remove("Id");
        first["Sources"]![0]!.AsObject().Remove("FutureSourceField");
        first["Sources"]![0]!["AutoDetect"] = true;
        first["Sources"]![0]!["Enabled"] = true;
        first["PluginDirectories"] = new JsonArray("relative-plugins");
        File.WriteAllText(service.FilePath, first.ToJsonString());
        var reopened = new DirectoryConfigurationService(root);
        True(!reopened.LoadOrCreate(settings), "Existing JSON is authoritative at restart rather than overwritten by database settings.");
        Equal("retained-source", settings.Sources.Single().Id, "Omitted IDs match the old path and synth to preserve indexed record identity.");
        True(settings.Sources.Single().AutoDetect, "Changing automatic detection does not disconnect a unique old source with an omitted ID.");
        True(settings.Sources.Single().Enabled, "Manual source switches override the former database value.");
        Equal(Path.Combine(root, "relative-plugins"), settings.PluginDirectories.Single(), "Relative manual paths resolve beside the program, independent of the working directory.");
        Near(.23, settings.PlaybackVolume, 0, "Loading directory JSON does not reset playback settings.");
        Equal(8, settings.MaxParallelRenders, "Loading directory JSON does not reset rendering settings.");
        settings.Sources.Add(new SourceFolder { Path = Path.Combine(root, "second"), Synth = SynthKind.Vital });
        reopened.Save(settings);
        var saved = JsonNode.Parse(File.ReadAllText(service.FilePath))!;
        Equal("preserve across old versions", saved["FutureDirectoryField"]!["value"]!.GetValue<string>(), "UI writes preserve future root fields.");
        True(saved["Sources"]![0]!["FutureSourceField"]!["retained"]!.GetValue<bool>(), "UI writes retain future database source metadata even when a hand-edited row omitted it.");
        Equal("retained-source", saved["Sources"]![0]!["Id"]!.GetValue<string>(), "A normalized save makes recovered source IDs explicit.");

        var store = new LibraryStore(Path.Combine(root, "Preview", "library.db"));
        store.SaveSettings(settings);
        var databaseSettings = new LibraryStore(store.DatabasePath).LoadSettings();
        Equal("retained-source", databaseSettings.Sources[0].Id, "The caller can retain source identity in the database for previous-version compatibility.");
        Equal(settings.PluginDirectories.Single(), databaseSettings.PluginDirectories.Single(), "The database copy remains compatible with versions that only read library settings.");

        File.WriteAllText(service.FilePath, "{\"FormatVersion\":1,\"Sources\":[],\"PluginDirectories\":[]}");
        var cleared = new DirectoryConfigurationService(root);
        cleared.LoadOrCreate(settings);
        Equal(0, settings.Sources.Count, "An explicit empty source list clears the selected directories.");
        Equal(0, settings.PluginDirectories.Count, "An explicit empty plugin list is distinct from a missing field and must not be reseeded.");
        settings.Sources = [new SourceFolder { Id = "partial-source", Path = sourcePath, Synth = SynthKind.Vital }];
        settings.PluginDirectories = [Path.Combine(root, "fallback-plugins")];
        File.WriteAllText(service.FilePath, "{ /* manual notes */ \"FormatVersion\":1, \"FutureDirectoryField\":17, }");
        var partial = new DirectoryConfigurationService(root);
        partial.LoadOrCreate(settings);
        Equal("partial-source", settings.Sources.Single().Id, "An omitted source section inherits existing database settings.");
        Equal(Path.Combine(root, "fallback-plugins"), settings.PluginDirectories.Single(), "An omitted plugin section inherits existing database settings.");
        partial.Save(settings);
        Equal(17, JsonNode.Parse(File.ReadAllText(service.FilePath))!["FutureDirectoryField"]!.GetValue<int>(), "Comments and trailing commas can be read without losing unknown JSON values.");

        var numeric = JsonNode.Parse(File.ReadAllText(service.FilePath))!;
        numeric["Sources"]![0]!["Synth"] = (int)SynthKind.Vital;
        numeric["Sources"]!.AsArray().Add(new JsonObject { ["Path"] = "new-manual-source", ["Synth"] = "Spire", ["AutoDetect"] = false, ["Enabled"] = true });
        File.WriteAllText(service.FilePath, numeric.ToJsonString());
        var numericService = new DirectoryConfigurationService(root);
        numericService.LoadOrCreate(settings);
        Equal(SynthKind.Vital, settings.Sources[0].Synth, "Numeric enum values from historical settings remain readable.");
        True(settings.Sources[1].Id.Length > 0 && settings.Sources[1].Id != settings.Sources[0].Id, "New manually added sources without IDs receive independent persistent identities.");
        numericService.Save(settings);

        string beforeBom = File.ReadAllText(service.FilePath);
        File.WriteAllText(service.FilePath, beforeBom, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var bomService = new DirectoryConfigurationService(root);
        bomService.LoadOrCreate(settings);
        Equal("partial-source", settings.Sources[0].Id, "UTF-8 BOM files saved by Windows editors retain the existing source identity.");
        Equal(SynthKind.Spire, settings.Sources[1].Synth, "UTF-8 BOM files load all manually edited directory entries.");
        bomService.Save(settings);
        Equal(beforeBom, File.ReadAllText(service.FilePath), "A UI save normalizes a BOM file without changing the directory configuration.");
        var bomConflict = new DirectoryConfigurationService(root);
        bomConflict.LoadOrCreate(settings);
        File.WriteAllText(service.FilePath, beforeBom, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Throws<InvalidOperationException>(() => bomConflict.Save(settings), "Adding a BOM externally is still detected as a manual file change before UI save.");

        string good = File.ReadAllText(service.FilePath);
        foreach (string invalid in new[]
        {
            "{broken",
            "[]",
            "{\"FormatVersion\":2,\"Sources\":[],\"PluginDirectories\":[]}",
            "{\"Sources\":null}",
            "{\"PluginDirectories\":null}",
            "{\"Sources\":[null]}",
            "{\"Sources\":[{\"Path\":\"\"}]}",
            "{\"Sources\":[{\"Path\":\"a\",\"Synth\":99}]}",
            "{\"Sources\":[{\"Id\":\"same\",\"Path\":\"a\"},{\"Id\":\"same\",\"Path\":\"b\"}]}",
            "{\"Sources\":[],\"sources\":[]}",
            "{\"Sources\":[{\"Path\":\"a\",\"path\":\"b\"}]}",
            "{\"PluginDirectories\":[null]}"
        })
        {
            File.WriteAllText(service.FilePath, invalid);
            string before = JsonSerializer.Serialize(settings, LibraryJson.Options);
            Throws<InvalidDataException>(() => new DirectoryConfigurationService(root).LoadOrCreate(settings), "Invalid or newer directory JSON is rejected without replacing it or partially applying settings.");
            Equal(invalid, File.ReadAllText(service.FilePath), "Rejected manually edited content remains byte-for-byte available to fix.");
            Equal(before, JsonSerializer.Serialize(settings, LibraryJson.Options), "Rejected JSON cannot partly change the active directory list.");
        }
        File.WriteAllText(service.FilePath, good);
        var conflict = new DirectoryConfigurationService(root);
        conflict.LoadOrCreate(settings);
        File.WriteAllText(service.FilePath, good + "\n");
        Throws<InvalidOperationException>(() => conflict.Save(settings), "An external edit since startup cannot be silently overwritten by UI changes.");
        Equal(good + "\n", File.ReadAllText(service.FilePath), "Concurrent manual changes are preserved even when they only change whitespace.");
        File.Delete(service.FilePath);
        Throws<InvalidOperationException>(() => conflict.Save(settings), "Deleting the file externally also requires reloading rather than silently recreating stale settings.");
        True(!File.Exists(service.FilePath), "A deleted configuration is not recreated by a stale service instance.");
        True(Directory.GetFiles(Path.GetDirectoryName(service.FilePath)!, "directories.json.*.tmp").Length == 0, "Rejected saves leave no temporary directory configuration files.");

        string legacyRoot = Fixture("directory-configuration-legacy");
        string legacyFile = Path.Combine(legacyRoot, "directories.json");
        string legacyJson = "{\"FormatVersion\":1,\"Sources\":[],\"PluginDirectories\":[],\"LegacyUnknown\":{\"preserve\":true}}";
        File.WriteAllText(legacyFile, legacyJson);
        var migratedSettings = new AppSettings { PluginDirectories = [Path.Combine(legacyRoot, "ignored-database-directory")] };
        var migration = new DirectoryConfigurationService(legacyRoot);
        True(!migration.LoadOrCreate(migratedSettings), "Moving an existing explicit JSON into Config does not reseed deliberately empty directories.");
        Equal(0, migratedSettings.PluginDirectories.Count, "Legacy explicit empty directories win over database fallback.");
        Equal(legacyJson, File.ReadAllText(legacyFile), "Migrating root JSON leaves its original bytes available for previous versions and recovery.");
        True(JsonNode.Parse(File.ReadAllText(migration.FilePath))!["LegacyUnknown"]!["preserve"]!.GetValue<bool>(), "Moving the JSON retains unknown future configuration fields.");
        File.WriteAllText(legacyFile, "{broken old root file");
        new DirectoryConfigurationService(legacyRoot).LoadOrCreate(migratedSettings);
        Equal(0, migratedSettings.PluginDirectories.Count, "Once Config exists, an obsolete or invalid root file cannot override it.");
        string rejectedRoot = Fixture("directory-configuration-invalid-legacy");
        File.WriteAllText(Path.Combine(rejectedRoot, "directories.json"), "{broken");
        Throws<InvalidDataException>(() => new DirectoryConfigurationService(rejectedRoot).LoadOrCreate(new AppSettings()), "An invalid migration source is preserved and must be fixed rather than silently replaced with database defaults.");
        True(!File.Exists(Path.Combine(rejectedRoot, "Config", "directories.json")), "A rejected legacy migration does not publish partial Config data.");
        string originalData = ApplicationPaths.DataDirectory;
        string originalConfigurationRoot = Path.GetDirectoryName(ApplicationPaths.ConfigurationDirectory)!;
        string rulesRoot = Fixture("configuration-rules-migration"), rulesData = Path.Combine(rulesRoot, "Preview");
        string oldRules = Path.Combine(rulesData, "Config", "preset-categories.json");
        const string customRules = "{\"version\":1,\"categories\":[{\"label\":\"Personal category\",\"tokens\":[\"personal\"]}]}";
        Directory.CreateDirectory(Path.GetDirectoryName(oldRules)!);
        File.WriteAllText(oldRules, customRules);
        try
        {
            ApplicationPaths.ConfigureDataDirectory(rulesData);
            ApplicationPaths.ConfigureConfigurationDirectory(rulesRoot);
            var categories = new PresetCategoryService();
            categories.EnsureRulesFile();
            Equal(Path.Combine(rulesRoot, "Config", "preset-categories.json"), categories.RulesPath, "Personal classification rules are consolidated with the other installation Config files.");
            Equal(customRules, File.ReadAllText(categories.RulesPath), "The first Config rules file copies the former Preview rules rather than replacing them with bundled defaults.");
            Equal(customRules, File.ReadAllText(oldRules), "The former Preview rules are retained for compatibility and recovery.");
            True(categories.Load().Success && categories.Classify("Personal sound").Contains("Personal category"), "Migrated personal categories remain usable.");
            File.WriteAllText(oldRules, "{broken obsolete rules");
            new PresetCategoryService().EnsureRulesFile();
            Equal(customRules, File.ReadAllText(categories.RulesPath), "Established Config rules win over obsolete Preview copies.");
        }
        finally
        {
            ApplicationPaths.ConfigureDataDirectory(originalData);
            ApplicationPaths.ConfigureConfigurationDirectory(originalConfigurationRoot);
        }
        return Task.CompletedTask;
    }
}
