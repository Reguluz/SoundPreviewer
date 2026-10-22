using System.Reflection;
using System.Text.Json;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestUpgradeCacheCompatibilityAsync()
    {
        foreach (int oldVersion in new[] { 0, 1 })
        {
            string directory = Fixture("upgrade-cache-v" + oldVersion);
            string databasePath = Path.Combine(directory, "library.db");
            var record = Preset("upgrade-cache");
            CompletePreview(record, Path.Combine(directory, "existing-previews", "legacy.wav"));
            record.Favorite = true;
            record.Feedback = -1;
            record.FeedbackUtc = DateTime.UtcNow;
            record.SynthAssignedManually = true;
            record.CategoryTags = ["User category"];
            record.Tags = ["User tag"];
            record.Attempts = 2;
            CreateLegacyDatabase(databasePath, record);
            var settings = new AppSettings
            {
                PreviewDirectory = Path.Combine(directory, "existing-previews"),
                Sources = [new SourceFolder { Id = "kept-source", Path = Path.Combine(directory, "user-presets"), Synth = SynthKind.Vital }]
            };
            var plugin = settings.Plugins.Single(item => item.Synth == SynthKind.Vital);
            plugin.ExecutablePath = Path.Combine(directory, "custom-host");
            plugin.Version = "legacy-version";
            plugin.InitBaselinePath = Path.Combine(directory, "kept-baseline");
            string settingsJson = JsonSerializer.Serialize(settings, LibraryJson.Options);
            using var connection = new CacheTestSqlite(databasePath);
            connection.Execute("PRAGMA journal_mode=WAL;");
            connection.Execute("PRAGMA wal_autocheckpoint=0;");
            connection.Execute($"PRAGMA user_version={oldVersion};");
            connection.Execute("INSERT INTO settings (key,json) VALUES (?,?);", "application", settingsJson);
            True(new FileInfo(databasePath + "-wal").Length > 0, "The fixture has committed metadata in a live WAL before migration.");
            string originalPreset = connection.Query("SELECT json FROM presets;").Single();

            var store = new LibraryStore(databasePath);
            store.Initialize();
            string backup = store.LastMigrationBackupPath ?? throw new InvalidOperationException("An existing legacy database must be backed up before migration.");
            True(File.Exists(backup) && ApplicationPaths.IsWithin(backup, Path.Combine(directory, "Backups")), "The original library snapshot is retained beside its user data.");
            using (var snapshot = new CacheTestSqlite(backup))
            {
                Equal(oldVersion.ToString(), snapshot.Query("PRAGMA user_version;").Single(), "The backup retains the original schema version.");
                Equal("ok", snapshot.Query("PRAGMA quick_check;").Single(), "The pre-migration backup is a complete SQLite database.");
                Equal(originalPreset, snapshot.Query("SELECT json FROM presets;").Single(), "Backups preserve the exact original preset metadata.");
                Equal(settingsJson, snapshot.Query("SELECT json FROM settings WHERE key='application';").Single(), "SQLite backup includes settings committed only to the WAL.");
                True(!snapshot.Query("SELECT name FROM pragma_table_info('presets');").Contains("content_md5"), "The snapshot predates the schema alteration.");
            }
            var restored = store.LoadPresets().Single();
            Equal(JsonSerializer.Serialize(record, LibraryJson.Options), JsonSerializer.Serialize(restored, LibraryJson.Options), "Migration preserves identity, feedback, tags, attempts, render provenance and old preview mappings.");
            Equal(settingsJson, JsonSerializer.Serialize(store.LoadSettings(), LibraryJson.Options), "Migration retains custom preview directories and plugin/source settings.");
            True(File.Exists(restored.PreviewPath), "An upgrade does not move or delete valid existing audio.");
            Equal(record.CacheKey, restored.CacheKey, "Application releases do not change saved preview cache identities.");
            Equal(LibraryStore.CurrentSchemaVersion.ToString(), connection.Query("PRAGMA user_version;").Single(), "The schema upgrade is committed only after successful migration.");
            var reopened = new LibraryStore(databasePath);
            reopened.Initialize();
            True(reopened.LastMigrationBackupPath is null, "Reopening a current-schema library does not repeatedly migrate or back it up.");
            Equal(1, Directory.GetFiles(Path.Combine(directory, "Backups"), "*.db").Length, "A successful migration creates one pre-upgrade snapshot.");
            Equal(0, Directory.GetFiles(Path.Combine(directory, "Backups"), "*.tmp").Length, "A completed snapshot leaves no partial backup.");
        }

        string futureDirectory = Fixture("upgrade-cache-future");
        string futurePath = Path.Combine(futureDirectory, "library.db");
        var current = new LibraryStore(futurePath);
        current.Upsert(Preset("future-schema"));
        using (var future = new CacheTestSqlite(futurePath)) future.Execute("PRAGMA user_version=99;");
        byte[] originalBytes = File.ReadAllBytes(futurePath);
        Throws<InvalidDataException>(() => new LibraryStore(futurePath).Initialize(), "An older application must refuse an unsupported future schema.");
        True(originalBytes.SequenceEqual(File.ReadAllBytes(futurePath)), "Refusing a future schema does not rewrite its database.");
        True(!Directory.Exists(Path.Combine(futureDirectory, "Backups")), "A refused future schema is never treated as an old migration.");

        string brokenDirectory = Fixture("upgrade-cache-rollback");
        string brokenPath = Path.Combine(brokenDirectory, "library.db");
        CreateLegacyDatabase(brokenPath, Preset("broken-metadata"));
        using (var broken = new CacheTestSqlite(brokenPath)) broken.Execute("UPDATE presets SET json=?;", "{");
        var rejected = new LibraryStore(brokenPath);
        Throws<JsonException>(() => rejected.Initialize(), "Malformed legacy JSON must stop migration instead of discarding the record.");
        using (var unchanged = new CacheTestSqlite(brokenPath))
        {
            Equal("1", unchanged.Query("PRAGMA user_version;").Single(), "A failed migration does not advance the original schema.");
            Equal("{", unchanged.Query("SELECT json FROM presets;").Single(), "The original record remains available for recovery.");
            True(!unchanged.Query("SELECT name FROM pragma_table_info('presets');").Contains("content_md5"), "Schema changes roll back together with a failed metadata migration.");
        }
        True(rejected.LastMigrationBackupPath is not null && File.Exists(rejected.LastMigrationBackupPath), "A failed migration still leaves its valid pre-migration snapshot for recovery.");

        string blockedDirectory = Fixture("upgrade-cache-blocked-backup");
        string blockedPath = Path.Combine(blockedDirectory, "library.db");
        CreateLegacyDatabase(blockedPath, Preset("backup-required"));
        File.WriteAllText(Path.Combine(blockedDirectory, "Backups"), "TEST ONLY: backup directory is unavailable");
        Throws<IOException>(() => new LibraryStore(blockedPath).Initialize(), "Migration cannot proceed when a recoverable snapshot cannot be created.");
        using (var unchanged = new CacheTestSqlite(blockedPath))
            Equal("1", unchanged.Query("PRAGMA user_version;").Single(), "An unavailable backup location leaves the old schema untouched.");

        string rulesDirectory = Fixture("upgrade-category-rules");
        string bundled = Path.Combine(FindRepository(), "src", "SoundPreviewer.Core", "Config", "preset-categories.default.json");
        string legacyRules = Path.Combine(rulesDirectory, "old-release", "Config", "preset-categories.json");
        string newRules = Path.Combine(rulesDirectory, "user-data", "Config", "preset-categories.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyRules)!);
        const string customRules = "{\"version\":1,\"categories\":[{\"label\":\"Personal category\",\"tokens\":[\"personal\"]}]}";
        File.WriteAllText(legacyRules, customRules);
        var categories = new PresetCategoryService(newRules, bundled, legacyRules);
        categories.EnsureRulesFile();
        Equal(customRules, File.ReadAllText(newRules), "The first user-data rules copy preserves existing custom rules rather than replacing them with defaults.");
        True(categories.Load().Success && categories.Classify("Personal sound").Contains("Personal category"), "Imported custom categories remain usable.");
        File.WriteAllText(legacyRules, "{");
        categories.EnsureRulesFile();
        Equal(customRules, File.ReadAllText(newRules), "Later installations never overwrite the established editable rules file.");
        Equal(0, Directory.GetFiles(Path.GetDirectoryName(newRules)!, "*.tmp").Length, "Rules initialization publishes only a complete file.");
        return Task.CompletedTask;
    }

    private sealed class CacheTestSqlite : IDisposable
    {
        private readonly object database;
        private readonly MethodInfo execute;
        private readonly MethodInfo query;

        public CacheTestSqlite(string path)
        {
            var type = typeof(LibraryStore).Assembly.GetType("SoundPreviewer.Core.Storage.NativeSqlite", throwOnError: true)!;
            database = Activator.CreateInstance(type, path)!;
            execute = type.GetMethod("Execute")!;
            query = type.GetMethod("QueryStrings")!;
        }

        public void Execute(string sql, params object?[] parameters) => execute.Invoke(database, [sql, parameters]);
        public List<string> Query(string sql) => (List<string>)query.Invoke(database, [sql, Array.Empty<object?>()])!;
        public void Dispose() => ((IDisposable)database).Dispose();
    }
}
