using System.Text.Json;
using System.Text.Json.Nodes;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static async Task TestRelativePreviewPathsAsync()
    {
        string fixture = Fixture("relative-preview-paths");
        string original = Path.Combine(fixture, "original", "Preview");
        string moved = Path.Combine(fixture, "moved", "Preview");
        Directory.CreateDirectory(original);
        Directory.CreateDirectory(moved);
        var store = new LibraryStore(Path.Combine(original, "library.db"));
        var preset = Preset("portable-relative");
        CompletePreview(preset, Path.Combine(original, "nested", "kept.wav"));
        preset.Favorite = true;
        preset.ManualTags = ["personal"];
        string hash = Fingerprints.HashFile(preset.PreviewPath);
        var settings = new AppSettings { PreviewDirectory = original };
        var plugin = settings.Plugins.Single(item => item.Synth == SynthKind.Vital);
        plugin.InitBaselinePath = Path.Combine(original, "InitializationBaselines", "kept.json");
        plugin.PluginPath = Path.Combine(fixture, "external-plugins", "test.dll");
        store.SaveState([preset], settings);
        using (var database = new CacheTestSqlite(store.DatabasePath))
        {
            var saved = JsonNode.Parse(database.Query("SELECT json FROM presets;").Single())!;
            Equal("nested/kept.wav", saved["PreviewPath"]!.GetValue<string>(), "Database preview paths are portable relative paths.");
            var savedSettings = JsonSerializer.Deserialize<AppSettings>(database.Query("SELECT json FROM settings WHERE key='application';").Single(), LibraryJson.Options)!;
            Equal(".", savedSettings.PreviewDirectory, "The saved preview root is the library directory itself.");
            Equal("InitializationBaselines/kept.json", savedSettings.Plugins.Single(item => item.Synth == SynthKind.Vital).InitBaselinePath, "Baseline references are relative to Preview too.");
            Equal(plugin.PluginPath, savedSettings.Plugins.Single(item => item.Synth == SynthKind.Vital).PluginPath, "Installed plugin paths are not rewritten as preview paths.");
        }
        Equal(Path.Combine(original, "nested", "kept.wav"), preset.PreviewPath, "Persistence does not mutate a caller's live playback path.");
        Equal(JsonSerializer.Serialize(preset, LibraryJson.Options), JsonSerializer.Serialize(store.LoadPresets().Single(), LibraryJson.Options), "Loading restores the same preview and personal metadata.");
        store.CreateBackup(Path.Combine(moved, "library.db"));
        Directory.CreateDirectory(Path.Combine(moved, "nested"));
        File.Copy(preset.PreviewPath, Path.Combine(moved, "nested", "kept.wav"));
        var movedStore = new LibraryStore(Path.Combine(moved, "library.db"));
        var relocated = movedStore.LoadPresets().Single();
        Equal(Path.Combine(moved, "nested", "kept.wav"), relocated.PreviewPath, "A moved library resolves against its own Preview even while the original exists.");
        Equal(hash, Fingerprints.HashFile(relocated.PreviewPath), "Moving the directory keeps cached audio byte for byte.");
        Equal(moved, movedStore.LoadSettings().PreviewDirectory, "A moved library never resumes output in the old installation.");
        True(relocated.Favorite && relocated.ManualTags!.SequenceEqual(["personal"]), "Moving preserves personal metadata.");
        relocated.Feedback = 1;
        movedStore.Upsert(relocated);
        using (var database = new CacheTestSqlite(movedStore.DatabasePath))
            Equal("nested/kept.wav", JsonNode.Parse(database.Query("SELECT json FROM presets;").Single())!["PreviewPath"]!.GetValue<string>(), "Editing metadata retains the relative reference.");

        Equal(Path.Combine(moved, "nested", "kept.wav"), PreviewPathStorage.Resolve("nested\\kept.wav", moved), "Legacy relative separators resolve independently of the platform.");
        foreach (string path in new[] { "../escape.wav", "nested/../../escape.wav", "..\\escape.wav", "C:escape.wav" })
            Throws<InvalidDataException>(() => PreviewPathStorage.Resolve(path, moved), "Relative preview references cannot escape Preview: " + path);
        using (var database = new CacheTestSqlite(movedStore.DatabasePath))
        {
            var node = JsonNode.Parse(database.Query("SELECT json FROM presets;").Single())!;
            node["PreviewPath"] = "../outside.wav";
            database.Execute("UPDATE presets SET json=?;", node.ToJsonString(LibraryJson.Options));
        }
        Throws<InvalidDataException>(() => movedStore.LoadPresets(), "Unsafe stored paths are rejected before playback or cleanup can access another directory.");
        True(File.Exists(preset.PreviewPath), "Invalid mappings cannot remove the original audio.");
        var regenerated = CreateParallelFixture("relative-replaced-legacy-reference", 1);
        ReleaseParallel(regenerated);
        var queue = new RenderQueue(regenerated.Store, new TestRenderResourceProbe(regenerated));
        var first = await queue.RunAsync(regenerated.Settings, regenerated.Presets, TestExecutable, new ParallelProgress(), CancellationToken.None);
        Equal(1, first.Generated, "The replacement fixture starts with one complete cached preview.");
        var previous = regenerated.Store.LoadPresets().Single();
        string previousPath = previous.PreviewPath;
        previous.AdditionalProperties = new()
        {
            ["HistoricalPreviewPath"] = JsonSerializer.SerializeToElement(Path.Combine(fixture, "old-missing", Path.GetFileName(previousPath))),
            ["HistoricalPreviewPathLocalPath"] = JsonSerializer.SerializeToElement(PreviewPathStorage.Store(previousPath, regenerated.Settings.PreviewDirectory))
        };
        regenerated.Store.Upsert(previous);
        var replacement = await queue.RunAsync(regenerated.Settings, [previous], TestExecutable, new ParallelProgress(), CancellationToken.None, forceRegenerate: true);
        Equal(1, replacement.Generated, "Explicit same-key regeneration publishes a new preview successfully.");
        var committed = regenerated.Store.LoadPresets().Single();
        Equal(previousPath, committed.PreviewPath, "The regression exercises replacement at the same cache filename.");
        True(committed.AdditionalProperties is null || !committed.AdditionalProperties.ContainsKey("HistoricalPreviewPath"),
            "A new committed preview retires obsolete historical recovery even when its relative filename stays the same.");
    }
}
