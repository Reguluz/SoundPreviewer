using System.Text.Json;
using System.Text.Json.Nodes;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestPreviewStorageAsync()
    {
        var clear = CreatePreviewStorageFixture("clear-owned-previews");
        var cleared = PreviewStorageService.ClearPreviews(clear.Store, clear.Settings);
        True(cleared.DeletedFiles >= 5 && cleared.FreedBytes > 0, "Clearing removes referenced previews and the current library's known initialization audio.");
        True(File.Exists(clear.Store.DatabasePath) && File.Exists(clear.Config), "Clearing audio never deletes the colocated database or editable classification rules.");
        True(File.Exists(clear.Unrelated) && File.Exists(clear.Source), "Unrelated WAV files and original source files remain untouched.");
        True(!File.Exists(clear.Preview) && !File.Exists(clear.Baseline), "Referenced preview and baseline paths no longer remain after clearing.");
        True(File.Exists(clear.Orphan) && cleared.Warnings.Any(message => message.Contains("无法确认归属", StringComparison.Ordinal)), "A hash filename alone cannot authorize deleting an unreferenced file.");
        var clearedRecord = clear.Store.LoadPresets().Single();
        Equal("", clearedRecord.PreviewPath, "Clearing revokes the persisted preview mapping.");
        Equal(PresetStatus.Pending, clearedRecord.Status, "A cleared generated record can be generated again.");
        True(clearedRecord.Favorite && clearedRecord.Feedback == -1 && clearedRecord.Tags.Contains("Personal tag") && clearedRecord.CategoryTags.Contains("Personal category"), "Clearing preserves favorites, feedback and user tags/categories.");
        True(clear.Settings.Plugins.All(plugin => plugin.InitBaselinePath.Length == 0), "Clearing updates baseline locators after the database commit succeeds.");
        Equal(0, PreviewStorageService.ClearPreviews(clear.Store, clear.Settings).DeletedFiles, "Repeated clearing does not delete unrelated files.");

        foreach (var mode in Enum.GetValues<PreviewDirectoryChangeMode>())
        {
            var fixture = CreatePreviewStorageFixture("relocate-previews-" + mode);
            string originalJson = JsonSerializer.Serialize(fixture.Store.LoadPresets(), LibraryJson.Options);
            string target = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "new-root");
            Directory.CreateDirectory(target);
            var result = PreviewStorageService.RelocateLibrary(fixture.Store, fixture.Settings, target, mode);
            var relocated = new LibraryStore(Path.Combine(target, "library.db"));
            var record = relocated.LoadPresets().Single();
            var settings = relocated.LoadSettings();
            Equal(target, settings.PreviewDirectory, "The new library, preview output and user metadata use one selected root.");
            Equal("TEST ONLY custom category configuration", File.ReadAllText(Path.Combine(target, "Config", "preset-categories.json")), "Custom editable rules move with the library.");
            True(record.Favorite && record.Feedback == -1 && record.Tags.Contains("Personal tag") && record.CategoryTags.Contains("Personal category"), "Directory changes preserve personal record metadata.");
            True(File.Exists(fixture.Store.DatabasePath), "The original database remains an explicitly reported recovery copy.");
            True(result.Warnings.Any(message => message.Contains(fixture.Root, StringComparison.Ordinal)), "The result reports the retained original database location.");
            True(!File.Exists(fixture.Preview) && !File.Exists(fixture.Baseline), "Neither migration choice leaves this library's referenced audio in the old library root.");
            True(File.Exists(fixture.Orphan), "Unreferenced audio remains protected even when another database is not visible nearby.");
            True(File.Exists(fixture.Unrelated) && File.Exists(fixture.Source), "Directory changes do not remove original sources or unrelated audio.");
            string backup = Directory.GetFiles(Path.Combine(target, "Backups"), "*.db").Single();
            Equal(originalJson, JsonSerializer.Serialize(new LibraryStore(backup, fixture.Root).LoadPresets(), LibraryJson.Options), "The consistent pre-relocation snapshot retains every original record field before path changes.");
            if (mode == PreviewDirectoryChangeMode.Migrate)
            {
                True(ApplicationPaths.IsWithin(record.PreviewPath, target) && File.Exists(record.PreviewPath), "Migrated preview paths point to existing audio in the new root.");
                Equal(fixture.Key, record.CacheKey, "Migration retains the existing validated cache identity and render provenance.");
                True(!File.Exists(Path.Combine(target, Path.GetFileName(fixture.Orphan))), "Unreferenced audio is not claimed or moved based only on its filename.");
                string baseline = settings.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).InitBaselinePath;
                True(File.Exists(baseline) && ApplicationPaths.IsWithin(baseline, target), "The active initialization baseline is relocated.");
                var metadata = JsonNode.Parse(File.ReadAllText(baseline))!;
                foreach (string path in StorageBaselinePaths(metadata)) True(!PreviewPathStorage.IsAbsolute(path) && File.Exists(PreviewPathStorage.Resolve(path, target)), "Every initialization WAV and result locator is rewritten to an existing new file with a relative path.");
            }
            else
            {
                Equal("", record.PreviewPath, "Choosing clear keeps the record without its old audio mapping.");
                Equal(PresetStatus.Pending, record.Status, "Choosing clear enables subsequent regeneration.");
                True(settings.Plugins.All(plugin => plugin.InitBaselinePath.Length == 0), "Choosing clear does not leave stale baseline references.");
                Equal(0, Directory.GetFiles(target, "*.wav", SearchOption.AllDirectories).Length, "Choosing clear copies no preview audio to the new library.");
            }
        }

        var rollback = CreatePreviewStorageFixture("clear-transaction-rollback");
        string originalRecord = JsonSerializer.Serialize(rollback.Store.LoadPresets(), LibraryJson.Options);
        string originalSettings = JsonSerializer.Serialize(rollback.Settings, LibraryJson.Options);
        using (var database = new CacheTestSqlite(rollback.Store.DatabasePath))
            database.Execute("CREATE TRIGGER reject_test_clear BEFORE INSERT ON presets WHEN NEW.status=0 BEGIN SELECT RAISE(ABORT, 'TEST ONLY failed clear'); END;");
        Throws<IOException>(() => PreviewStorageService.ClearPreviews(rollback.Store, rollback.Settings), "A failed database commit must roll back staged audio removal.");
        True(File.Exists(rollback.Preview) && File.Exists(rollback.Orphan) && File.Exists(rollback.Baseline), "All original audio and baseline metadata return to their old paths after a failed clear.");
        Equal(originalRecord, JsonSerializer.Serialize(rollback.Store.LoadPresets(), LibraryJson.Options), "A failed clear preserves all database record fields.");
        Equal(originalSettings, JsonSerializer.Serialize(rollback.Settings, LibraryJson.Options), "A failed clear does not mutate in-memory settings.");

        foreach (bool migrate in new[] { false, true })
        {
            var shared = CreatePreviewStorageFixture("shared-external-cache-" + migrate, externalPreview: true);
            string otherWave = Path.Combine(shared.Settings.PreviewDirectory, Fingerprints.HashText("TEST ONLY another library cache") + ".wav");
            WaveFile.WritePcm16(otherWave, Tone(44100, 1, .02));
            string secondRoot = Path.Combine(Path.GetDirectoryName(shared.Root)!, "second-library");
            var secondStore = new LibraryStore(Path.Combine(secondRoot, "library.db"));
            var secondRecord = Preset("shared-other-record");
            secondRecord.PreviewPath = otherWave;
            secondStore.Upsert(secondRecord);
            if (migrate)
            {
                string target = Path.Combine(Path.GetDirectoryName(shared.Root)!, "migrated-root");
                var moved = PreviewStorageService.RelocateLibrary(shared.Store, shared.Settings, target, PreviewDirectoryChangeMode.Migrate);
                var relocated = new LibraryStore(Path.Combine(target, "library.db")).LoadPresets().Single();
                True(File.Exists(relocated.PreviewPath) && ApplicationPaths.IsWithin(relocated.PreviewPath, target), "An indexed external preview can be copied safely into the new exclusive root.");
                True(moved.Warnings.Any(message => message.Contains(shared.Preview, StringComparison.Ordinal)), "Migration explicitly reports protected external originals left in place.");
            }
            else
            {
                var clearedShared = PreviewStorageService.ClearPreviews(shared.Store, shared.Settings);
                True(clearedShared.Warnings.Any(message => message.Contains(shared.Preview, StringComparison.Ordinal)), "Clear explicitly reports protected external originals left in place.");
            }
            True(File.Exists(otherWave) && File.Exists(shared.Preview) && File.Exists(shared.Baseline), "Neither operation deletes indexed or orphan audio in a potentially shared external folder.");
            True(File.Exists(shared.Orphan), "An unreferenced hash file in this library's directory can still belong to another external library.");
        }

        foreach (bool migrate in new[] { false, true })
        {
            var ownRootShared = CreatePreviewStorageFixture("own-root-used-by-external-library-" + migrate);
            string otherRoot = Path.Combine(Path.GetDirectoryName(ownRootShared.Root)!, "outside-library");
            var otherStore = new LibraryStore(Path.Combine(otherRoot, "library.db"));
            var otherRecord = Preset("outside-library-record");
            otherRecord.PreviewPath = ownRootShared.Orphan;
            string otherBaseline = Path.Combine(ownRootShared.Root, "InitializationBaselines", "Serum1-" + new string('b', 16) + "-" + Guid.NewGuid().ToString("N") + ".json");
            string otherBaselineWave = Path.ChangeExtension(otherBaseline, null) + "-1.wav";
            WaveFile.WritePcm16(otherBaselineWave, Tone(44100, 1, .02));
            File.WriteAllText(otherBaseline, JsonSerializer.Serialize(new { Kind = InitBaselineService.BaselineKind, WavPath = otherBaselineWave, Result = new { OutputPath = otherBaselineWave } }, LibraryJson.Options));
            var otherSettings = new AppSettings { PreviewDirectory = ownRootShared.Root };
            otherSettings.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).InitBaselinePath = otherBaseline;
            otherStore.SaveState([otherRecord], otherSettings);
            string originalOtherWaveHash = Fingerprints.HashFile(ownRootShared.Orphan), originalOtherBaseline = File.ReadAllText(otherBaseline);
            if (migrate)
                PreviewStorageService.RelocateLibrary(ownRootShared.Store, ownRootShared.Settings, Path.Combine(Path.GetDirectoryName(ownRootShared.Root)!, "new-root"), PreviewDirectoryChangeMode.Migrate);
            else PreviewStorageService.ClearPreviews(ownRootShared.Store, ownRootShared.Settings);
            Equal(originalOtherWaveHash, Fingerprints.HashFile(ownRootShared.Orphan), "Another external library's cached audio survives even beside the current database.");
            Equal(originalOtherBaseline, File.ReadAllText(otherBaseline), "Unreferenced baseline metadata is never claimed just because its kind and filename match this application.");
            True(File.Exists(otherBaselineWave), "Another external library's unreferenced initialization audio remains intact.");
        }

        var nestedLibrary = CreatePreviewStorageFixture("nested-library-protection");
        string nestedRoot = Path.Combine(nestedLibrary.Root, "other-library");
        var nestedStore = new LibraryStore(Path.Combine(nestedRoot, "library.db"));
        string nestedWave = Path.Combine(nestedRoot, Fingerprints.HashText("TEST ONLY nested other cache") + ".wav");
        WaveFile.WritePcm16(nestedWave, Tone(44100, 1, .02));
        nestedStore.Upsert(Preset("nested-other-record"));
        var pointingIntoOtherLibrary = nestedLibrary.Store.LoadPresets().Single();
        pointingIntoOtherLibrary.PreviewPath = nestedWave;
        nestedLibrary.Store.Upsert(pointingIntoOtherLibrary);
        PreviewStorageService.ClearPreviews(nestedLibrary.Store, nestedLibrary.Settings);
        True(File.Exists(nestedWave) && File.Exists(nestedStore.DatabasePath), "An indexed path inside another library nested beneath this root remains protected.");

        var failingMove = CreatePreviewStorageFixture("relocate-transaction-rollback");
        using (var database = new CacheTestSqlite(failingMove.Store.DatabasePath))
            database.Execute("CREATE TRIGGER reject_test_relocation BEFORE INSERT ON presets BEGIN SELECT RAISE(ABORT, 'TEST ONLY failed relocation'); END;");
        string failedTarget = Path.Combine(Path.GetDirectoryName(failingMove.Root)!, "failed-target");
        Throws<IOException>(() => PreviewStorageService.RelocateLibrary(failingMove.Store, failingMove.Settings, failedTarget, PreviewDirectoryChangeMode.Migrate), "A destination database write failure must leave the old library usable.");
        True(!Directory.Exists(failedTarget) && File.Exists(failingMove.Preview) && File.Exists(failingMove.Orphan), "A failed relocation publishes no destination and removes no original audio.");

        var protectedFixture = CreatePreviewStorageFixture("relocate-protected-destinations");
        string occupied = Path.Combine(Path.GetDirectoryName(protectedFixture.Root)!, "occupied");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "library.db"), "TEST ONLY other library");
        Throws<IOException>(() => PreviewStorageService.RelocateLibrary(protectedFixture.Store, protectedFixture.Settings, occupied, PreviewDirectoryChangeMode.Migrate), "Another library is never overwritten.");
        Throws<ArgumentException>(() => PreviewStorageService.RelocateLibrary(protectedFixture.Store, protectedFixture.Settings, Path.Combine(protectedFixture.Root, "nested"), PreviewDirectoryChangeMode.Migrate), "A library cannot move inside its own root.");
        Throws<ArgumentException>(() => PreviewStorageService.RelocateLibrary(protectedFixture.Store, protectedFixture.Settings, protectedFixture.Settings.Sources[0].Path, PreviewDirectoryChangeMode.Migrate), "A library cannot move into a source preset directory.");
        string link = Path.Combine(Path.GetDirectoryName(protectedFixture.Root)!, "linked-target");
        try
        {
            Directory.CreateSymbolicLink(link, occupied);
            Throws<IOException>(() => PreviewStorageService.RelocateLibrary(protectedFixture.Store, protectedFixture.Settings, link, PreviewDirectoryChangeMode.Migrate), "A linked destination cannot redirect migration to another directory.");
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { /* Some Windows accounts cannot create fixture symlinks. */ }
        catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 1314) { /* ERROR_PRIVILEGE_NOT_HELD on non-developer Windows accounts. */ }
        return Task.CompletedTask;
    }

    private static (string Root, LibraryStore Store, AppSettings Settings, string Preview, string Key, string Orphan, string Baseline, string Config, string Unrelated, string Source) CreatePreviewStorageFixture(string name, bool externalPreview = false)
    {
        string directory = Fixture(name), root = Path.Combine(directory, "library");
        string external = externalPreview ? Path.Combine(directory, "old-external-previews") : root;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(external);
        var record = Preset(name);
        CompletePreview(record, Path.Combine(external, "legacy-preview.wav"));
        record.Favorite = true; record.Feedback = -1; record.Tags = ["Personal tag"]; record.CategoryTags = ["Personal category"];
        string sourceDirectory = Path.Combine(directory, "original-presets");
        Directory.CreateDirectory(sourceDirectory);
        record.SourcePath = Path.Combine(sourceDirectory, "Original.vital");
        File.WriteAllText(record.SourcePath, "{\"settings\":{}}");
        var settings = new AppSettings { PreviewDirectory = external, Sources = [new SourceFolder { Path = sourceDirectory, Synth = SynthKind.Vital }] };
        string orphan = Path.Combine(root, Fingerprints.HashText("TEST ONLY orphan " + name) + ".wav");
        WaveFile.WritePcm16(orphan, Tone(44100, 1, .02));
        string baselineDirectory = Path.Combine(external, "InitializationBaselines");
        Directory.CreateDirectory(baselineDirectory);
        string stem = "Serum1-" + new string('a', 16) + "-" + Guid.NewGuid().ToString("N");
        string[] waves = Enumerable.Range(1, 3).Select(index => Path.Combine(baselineDirectory, stem + "-" + index + ".wav")).ToArray();
        foreach (string wav in waves) WaveFile.WritePcm16(wav, Tone(44100, 1, .02));
        string baseline = Path.Combine(baselineDirectory, stem + ".json");
        File.WriteAllText(baseline, JsonSerializer.Serialize(new
        {
            Kind = InitBaselineService.BaselineKind, WavPath = waves[0], Result = new { OutputPath = waves[0] },
            References = waves.Select(wav => new { WavPath = wav, Result = new { OutputPath = wav } })
        }, LibraryJson.Options));
        settings.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).InitBaselinePath = baseline;
        string config = Path.Combine(root, "Config", "preset-categories.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "TEST ONLY custom category configuration");
        string unrelated = Path.Combine(root, "unrelated-user-audio.wav");
        WaveFile.WritePcm16(unrelated, Tone(44100, 1, .02));
        var store = new LibraryStore(Path.Combine(root, "library.db"));
        store.SaveState([record], settings);
        return (root, store, settings, record.PreviewPath, record.CacheKey, orphan, baseline, config, unrelated, record.SourcePath);
    }

    private static IEnumerable<string> StorageBaselinePaths(JsonNode node)
    {
        if (node is JsonObject obj)
            foreach (var (key, value) in obj)
            {
                if (key is "WavPath" or "OutputPath") yield return value!.GetValue<string>();
                else if (value is not null) foreach (string nested in StorageBaselinePaths(value)) yield return nested;
            }
        else if (node is JsonArray array)
            foreach (var value in array) if (value is not null) foreach (string nested in StorageBaselinePaths(value)) yield return nested;
    }
}
