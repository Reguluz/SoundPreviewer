using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestInstalledStorageAsync()
    {
        string fresh = Fixture("installed-storage-new");
        string freshApplication = Path.Combine(fresh, "portable"), freshUser = Path.Combine(fresh, "user-data");
        string freshTarget = Path.Combine(freshApplication, "Preview");
        var created = InstalledStorageService.Prepare(freshApplication, freshUser);
        Equal(freshTarget, created.Directory, "A new application always stores its library beside the executable in Preview.");
        True(Directory.Exists(freshTarget), "The installation-local Preview directory is available for subsequent output.");
        True(!Directory.Exists(freshUser), "Preparing a new installation creates no user-directory fallback data.");
        string freshDatabaseHash = InstalledDatabaseHash(Path.Combine(freshTarget, "library.db"));
        Throws<DirectoryNotFoundException>(() => InstalledStorageService.Prepare(freshApplication, freshUser, Path.Combine(fresh, "missing-old-library")), "An explicitly requested missing library is refused rather than silently replaced with an empty one.");
        Equal(freshDatabaseHash, InstalledDatabaseHash(Path.Combine(freshTarget, "library.db")), "An unavailable explicit source never changes the established installation-local database.");

        var legacy = CreateInstalledStorageFixture("installed-storage-legacy-wal", legacySchema: true);
        string legacyDatabase = Path.Combine(legacy.Root, "library.db");
        using (var committedWal = new CacheTestSqlite(legacyDatabase))
        {
            committedWal.Execute("PRAGMA journal_mode=WAL;");
            committedWal.Execute("PRAGMA wal_autocheckpoint=0;");
            var presetJson = JsonNode.Parse(committedWal.Query("SELECT json FROM presets;").Single())!.AsObject();
            presetJson["FuturePresetMetadata"] = new JsonObject { ["description"] = "TEST ONLY preserve unknown preset data" };
            legacy.Record.AdditionalProperties = new() { ["FuturePresetMetadata"] = JsonSerializer.Deserialize<JsonElement>(presetJson["FuturePresetMetadata"]!.ToJsonString()) };
            var settingsJson = JsonNode.Parse(committedWal.Query("SELECT json FROM settings WHERE key='application';").Single())!.AsObject();
            settingsJson["FutureApplicationMetadata"] = JsonNode.Parse("[\"TEST ONLY\",7]");
            settingsJson["Sources"]![0]!["FutureSourceMetadata"] = "TEST ONLY preserve source metadata";
            settingsJson["Plugins"]![0]!["FuturePluginMetadata"] = new JsonObject { ["version"] = 7 };
            settingsJson["Template"]!["FutureTemplateMetadata"] = JsonNode.Parse("[\"TEST ONLY template\",7]");
            committedWal.Execute("UPDATE presets SET json=?;", presetJson.ToJsonString());
            committedWal.Execute("UPDATE settings SET json=? WHERE key='application';", settingsJson.ToJsonString());
            True(new FileInfo(legacyDatabase + "-wal").Length > 0, "The old fixture contains committed state that exists in its live WAL.");
            string oldPresetJson = committedWal.Query("SELECT json FROM presets;").Single();
            string oldSettingsJson = committedWal.Query("SELECT json FROM settings WHERE key='application';").Single();
            string originalDatabaseHash = InstalledDatabaseHash(legacyDatabase);
            var originalFiles = InstalledFixtureHashes(legacy);
            ApplicationPaths.RememberDataDirectory(legacy.Root, legacy.Application);
            Equal(legacy.Root, InstalledStorageService.GetSourceDirectory(legacy.Application, legacy.UserData), "A historical locator supplies an import source, rather than the active output directory.");
            var imported = InstalledStorageService.Prepare(legacy.Application, legacy.UserData);
            string target = Path.Combine(legacy.Application, "Preview");
            Equal(target, imported.Directory, "A remembered old library is imported into installation-local Preview.");
            True(imported.CopiedFiles > 0, "The import copies valid cached audio and supporting metadata.");
            VerifyInstalledFixture(legacy, target);
            Equal(originalDatabaseHash, InstalledDatabaseHash(legacyDatabase), "Import never initializes, upgrades or rewrites the old database file.");
            Equal("wal", committedWal.Query("PRAGMA journal_mode;").Single(), "Making the private snapshot portable never changes the real source library's WAL mode.");
            Equal("1", committedWal.Query("PRAGMA user_version;").Single(), "Only the installation-local snapshot receives a schema upgrade.");
            Equal(oldPresetJson, committedWal.Query("SELECT json FROM presets;").Single(), "The source's WAL-backed preset JSON stays exactly unchanged.");
            Equal(oldSettingsJson, committedWal.Query("SELECT json FROM settings WHERE key='application';").Single(), "The source's WAL-backed settings JSON stays exactly unchanged.");
            VerifyInstalledFixtureHashes(originalFiles);
            string installedBackup = Directory.GetFiles(Path.Combine(target, "Backups"), "library-before-install-*.db").Single();
            VerifyInstalledSnapshotMainFile(installedBackup, [oldPresetJson], oldSettingsJson, 1);
            AssertInstalledSnapshotStandalone(installedBackup, "The retained old-library snapshot has rollback headers and no WAL; a main-file-only copy has passed original schema and JSON checks.");
            using (var snapshot = new CacheTestSqlite(installedBackup))
            {
                Equal("delete", snapshot.Query("PRAGMA journal_mode;").Single(), "Only the private snapshot normalizes its journal mode for portable read-only reopening.");
                Equal("1", snapshot.Query("PRAGMA user_version;").Single(), "The retained pre-import snapshot keeps the original schema version.");
                Equal("ok", snapshot.Query("PRAGMA quick_check;").Single(), "The normalized snapshot remains a complete SQLite database.");
                Equal(oldPresetJson, snapshot.Query("SELECT json FROM presets;").Single(), "Snapshot normalization retains the exact original preset JSON committed through the live WAL.");
                Equal(oldSettingsJson, snapshot.Query("SELECT json FROM settings WHERE key='application';").Single(), "Snapshot normalization retains the exact original settings JSON committed through the live WAL.");
            }
            AssertInstalledSnapshotStandalone(installedBackup, "Reopening the complete retained snapshot retains rollback headers and requires no WAL.");
            using var destination = new CacheTestSqlite(Path.Combine(target, "library.db"));
            Equal(LibraryStore.CurrentSchemaVersion.ToString(), destination.Query("PRAGMA user_version;").Single(), "The imported copy upgrades the old schema before use.");
            var importedPreset = JsonNode.Parse(destination.Query("SELECT json FROM presets;").Single())!;
            var importedSettings = JsonNode.Parse(destination.Query("SELECT json FROM settings WHERE key='application';").Single())!;
            Equal(presetJson["FuturePresetMetadata"]!.ToJsonString(), importedPreset["FuturePresetMetadata"]!.ToJsonString(), "Path migration preserves unknown preset fields written by other versions.");
            Equal(settingsJson["FutureApplicationMetadata"]!.ToJsonString(), importedSettings["FutureApplicationMetadata"]!.ToJsonString(), "Path migration preserves unknown settings fields written by other versions.");
            var loaded = new LibraryStore(Path.Combine(target, "library.db"));
            var typedSettings = loaded.LoadSettings();
            typedSettings.PlaybackVolume = .25;
            loaded.SaveSettings(typedSettings);
            var savedSettings = JsonNode.Parse(destination.Query("SELECT json FROM settings WHERE key='application';").Single())!;
            Equal(.25, savedSettings["PlaybackVolume"]!.GetValue<double>(), "An ordinary typed settings update remains effective after migration.");
            Equal(settingsJson["FutureApplicationMetadata"]!.ToJsonString(), savedSettings["FutureApplicationMetadata"]!.ToJsonString(), "Saving or closing the normal application preserves unknown top-level settings.");
            Equal(settingsJson["Sources"]![0]!["FutureSourceMetadata"]!.ToJsonString(), savedSettings["Sources"]![0]!["FutureSourceMetadata"]!.ToJsonString(), "Ordinary settings persistence preserves unknown source fields.");
            Equal(settingsJson["Plugins"]![0]!["FuturePluginMetadata"]!.ToJsonString(), savedSettings["Plugins"]![0]!["FuturePluginMetadata"]!.ToJsonString(), "Ordinary settings persistence preserves unknown plugin fields.");
            Equal(settingsJson["Template"]!["FutureTemplateMetadata"]!.ToJsonString(), savedSettings["Template"]!["FutureTemplateMetadata"]!.ToJsonString(), "Ordinary settings persistence preserves unknown preview-template fields.");
        }
        TestInstalledClosedWalSnapshot();

        var local = CreateInstalledStorageFixture("installed-storage-local-precedence", inPlace: true, externalPreview: false);
        string otherRoot = Path.Combine(Path.GetDirectoryName(local.Root)!, "other-library");
        var other = new LibraryStore(Path.Combine(otherRoot, "library.db"));
        other.SaveState([Preset("TEST ONLY other library")], new AppSettings { PreviewDirectory = otherRoot });
        ApplicationPaths.RememberDataDirectory(otherRoot, local.Application);
        Equal(local.Root, InstalledStorageService.GetSourceDirectory(local.Application, local.UserData), "An existing installation-local database takes precedence over an obsolete external locator.");
        string originalLocalJson = JsonSerializer.Serialize(local.Store.LoadPresets(), LibraryJson.Options);
        var firstOpen = InstalledStorageService.Prepare(local.Application, local.UserData);
        Equal(local.Root, firstOpen.Directory, "A current local library remains the active library.");
        Equal(originalLocalJson, JsonSerializer.Serialize(local.Store.LoadPresets(), LibraryJson.Options), "An established local library is not replaced with the historical locator's records.");
        string[] originalLocalFiles = InstalledLibraryFiles(local.Root);
        var reopened = InstalledStorageService.Prepare(local.Application, local.UserData);
        Equal(0, reopened.CopiedFiles, "Repeated normal startup does not copy a local library or regenerate previews.");
        string[] reopenedLocalFiles = InstalledLibraryFiles(local.Root);
        True(originalLocalFiles.SequenceEqual(reopenedLocalFiles), "Repeated startup leaves no extra migration copies or backups.\nOnly before: " +
            string.Join(", ", originalLocalFiles.Except(reopenedLocalFiles, ApplicationPaths.PathComparer)) + "\nOnly after: " +
            string.Join(", ", reopenedLocalFiles.Except(originalLocalFiles, ApplicationPaths.PathComparer)));
        string targetDatabaseHash = InstalledDatabaseHash(local.Store.DatabasePath);
        string otherDatabaseHash = InstalledDatabaseHash(other.DatabasePath);
        Throws<IOException>(() => InstalledStorageService.Prepare(local.Application, local.UserData, otherRoot), "An explicitly selected different library cannot overwrite an established local database.");
        Equal(targetDatabaseHash, InstalledDatabaseHash(local.Store.DatabasePath), "An import conflict leaves the active database unchanged.");
        Equal(otherDatabaseHash, InstalledDatabaseHash(other.DatabasePath), "An import conflict leaves the selected source database unchanged.");

        var replaced = CreateInstalledStorageFixture("installed-storage-replace-existing", inPlace: true, externalPreview: false);
        var incoming = CreateInstalledStorageFixture("installed-storage-replace-source");
        incoming.Record.PreviewPath = replaced.Preview;
        incoming.Store.Upsert(incoming.Record);
        var expectedIncoming = incoming with { Preview = replaced.Preview };
        var retainedHashes = Directory.GetFiles(replaced.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(replaced.Root, path), path => Fingerprints.HashFile(path), ApplicationPaths.PathComparer);
        string incomingDatabaseHash = InstalledDatabaseHash(incoming.Store.DatabasePath);
        var replacedResult = InstalledStorageService.Prepare(replaced.Application, replaced.UserData, incoming.Root, replaceTarget: true);
        string retained = replacedResult.RetainedDirectory ?? throw new InvalidOperationException("Replacing an existing library must report its complete retained directory.");
        True(ApplicationPaths.IsWithin(retained, replaced.Application) && !ApplicationPaths.IsWithin(retained, replaced.Root), "A confirmed replacement retains the old Preview in a separate directory beside the application.");
        foreach (var (relative, hash) in retainedHashes)
            Equal(hash, Fingerprints.HashFile(Path.Combine(retained, relative)), "A confirmed replacement retains every old local file byte for byte: " + relative);
        VerifyInstalledFixture(expectedIncoming, replaced.Root);
        Equal(incomingDatabaseHash, InstalledDatabaseHash(incoming.Store.DatabasePath), "Replacing the local library leaves the selected source database unchanged.");
        True(File.Exists(Path.Combine(retained, Path.GetFileName(replaced.Preview))), "The replaced library's original WAV remains recoverable in its retained directory.");
        string importedDatabaseHash = InstalledDatabaseHash(Path.Combine(replaced.Root, "library.db"));
        string retainedDatabaseHash = InstalledDatabaseHash(Path.Combine(retained, "library.db"));
        Throws<IOException>(() => InstalledStorageService.GetSourceDirectory(replaced.Application, replaced.UserData, retained), "A retained whole-directory backup cannot be treated as an ordinary import source while its absolute paths refer to the current Preview.");
        Throws<IOException>(() => InstalledStorageService.Prepare(replaced.Application, replaced.UserData, retained, replaceTarget: true), "Even a confirmed replacement refuses retained directories until their original Preview name is restored.");
        Equal(importedDatabaseHash, InstalledDatabaseHash(Path.Combine(replaced.Root, "library.db")), "Rejecting a retained backup as an import source leaves the active imported library unchanged.");
        Equal(retainedDatabaseHash, InstalledDatabaseHash(Path.Combine(retained, "library.db")), "Rejecting an unsafe backup import leaves the recoverable backup unchanged.");
        string heldImport = Path.Combine(replaced.Application, "TEST ONLY imported-library-held");
        True(ApplicationPaths.IsWithin(replaced.Root, _fixtureRoot) && ApplicationPaths.IsWithin(retained, _fixtureRoot) && ApplicationPaths.IsWithin(heldImport, _fixtureRoot), "Only isolated fixture directories may be renamed to exercise the documented rollback.");
        Directory.Move(replaced.Root, heldImport);
        Directory.Move(retained, replaced.Root);
        var restoredDirectory = InstalledStorageService.Prepare(replaced.Application, replaced.UserData);
        Equal(replaced.Root, restoredDirectory.Directory, "Restoring the retained directory to its original Preview name makes it usable again.");
        VerifyInstalledFixture(replaced, replaced.Root);

        var malformedTarget = CreateInstalledStorageFixture("installed-storage-invalid-types-target", inPlace: true, externalPreview: false);
        var malformedSource = CreateInstalledStorageFixture("installed-storage-invalid-types-source");
        using (var database = new CacheTestSqlite(malformedSource.Store.DatabasePath))
        {
            var validObject = JsonNode.Parse(database.Query("SELECT json FROM presets;").Single())!.AsObject();
            validObject["Attempts"] = "TEST ONLY wrong JSON type";
            database.Execute("UPDATE presets SET json=?;", validObject.ToJsonString());
        }
        string malformedSourceHash = InstalledDatabaseHash(malformedSource.Store.DatabasePath);
        string malformedTargetHash = InstalledDatabaseHash(malformedTarget.Store.DatabasePath);
        var malformedTargetFiles = Directory.GetFiles(malformedTarget.Application, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, ApplicationPaths.PathComparer).ToArray();
        Throws<JsonException>(() => InstalledStorageService.Prepare(malformedTarget.Application, malformedTarget.UserData, malformedSource.Root, replaceTarget: true), "A current-schema JSON object with an invalid typed field is rejected before replacing the usable local library.");
        Equal(malformedTargetHash, InstalledDatabaseHash(malformedTarget.Store.DatabasePath), "Rejecting malformed typed metadata leaves the active database unchanged.");
        Equal(malformedSourceHash, InstalledDatabaseHash(malformedSource.Store.DatabasePath), "Rejecting malformed typed metadata leaves the selected source available for repair.");
        True(malformedTargetFiles.SequenceEqual(Directory.GetFiles(malformedTarget.Application, "*", SearchOption.AllDirectories).OrderBy(path => path, ApplicationPaths.PathComparer)), "A failed replacement neither renames the active Preview nor publishes copied files or a retained backup.");

        var inPlace = CreateInstalledStorageFixture("installed-storage-local-external-audio", inPlace: true);
        string originalExternalPreviewHash = Fingerprints.HashFile(inPlace.Preview);
        string collidingLocalWave = Path.Combine(inPlace.Root, Path.GetFileName(inPlace.Preview));
        WaveFile.WritePcm16(collidingLocalWave, Tone(44100, 1, .031));
        string collidingHash = Fingerprints.HashFile(collidingLocalWave);
        var beforeInPlace = InstalledFixtureHashes(inPlace);
        var normalized = InstalledStorageService.Prepare(inPlace.Application, inPlace.UserData);
        Equal(inPlace.Root, normalized.Directory, "A database already in Preview normalizes its external output mappings in place.");
        VerifyInstalledFixture(inPlace, inPlace.Root);
        VerifyInstalledFixtureHashes(beforeInPlace);
        var normalizedRecord = new LibraryStore(inPlace.Store.DatabasePath).LoadPresets().Single();
        Equal(originalExternalPreviewHash, Fingerprints.HashFile(normalizedRecord.PreviewPath), "An external preview is copied without changing its audio bytes.");
        Equal(collidingHash, Fingerprints.HashFile(collidingLocalWave), "A same-named pre-existing local WAV is never overwritten by imported audio.");
        True(!ApplicationPaths.SamePath(collidingLocalWave, normalizedRecord.PreviewPath), "Name collisions receive a distinct local path instead of replacing an unrelated file.");
        Equal(0, InstalledStorageService.Prepare(inPlace.Application, inPlace.UserData).CopiedFiles, "An in-place normalization is complete and idempotent after the first commit.");

        var missing = CreateInstalledStorageFixture("installed-storage-missing-old-preview");
        byte[] missingWaveBytes = File.ReadAllBytes(missing.Preview);
        File.Delete(missing.Preview);
        var missingImport = InstalledStorageService.Prepare(missing.Application, missing.UserData, missing.Root);
        var missingStore = new LibraryStore(Path.Combine(missingImport.Directory, "library.db"));
        Equal(JsonSerializer.Serialize(missing.Record, LibraryJson.Options), JsonSerializer.Serialize(missingStore.LoadPresets().Single(), LibraryJson.Options), "A missing historical WAV retains its old mapping, cache identity, review state and personal metadata for later recovery.");
        Equal(missingImport.Directory, missingStore.LoadSettings().PreviewDirectory, "A missing old WAV does not keep the old directory as the location for future generation.");
        True(missingImport.Warnings.Any(message => message.Contains(missing.Preview, ApplicationPaths.PathComparison)), "A missing historical WAV produces an actionable warning with its original path.");
        True(!File.Exists(missing.Preview), "Import never creates or regenerates audio at a missing historical external path.");
        File.WriteAllBytes(missing.Preview, missingWaveBytes);
        InstalledStorageService.Prepare(missing.Application, missing.UserData);
        VerifyInstalledFixture(missing, missingImport.Directory);

        var recovery = CreateInstalledStorageFixture("installed-storage-recover-missing-mappings", inPlace: true);
        File.Delete(recovery.Preview);
        var review = JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(recovery.Record, LibraryJson.Options), LibraryJson.Options)!;
        review.Id = Guid.NewGuid().ToString("N");
        review.Status = PresetStatus.NeedsReview;
        var renderingWithAudio = Preset("TEST ONLY interrupted with committed audio");
        CompletePreview(renderingWithAudio, Path.Combine(recovery.Root, "TEST ONLY interrupted-existing.wav"));
        renderingWithAudio.Status = PresetStatus.Rendering;
        var renderingMissingAudio = Preset("TEST ONLY interrupted without audio");
        CompletePreview(renderingMissingAudio, Path.Combine(recovery.Root, "TEST ONLY interrupted-missing.wav"));
        File.Delete(renderingMissingAudio.PreviewPath);
        renderingMissingAudio.Status = PresetStatus.Rendering;
        renderingMissingAudio.Favorite = true;
        recovery.Store.UpsertMany([review, renderingWithAudio, renderingMissingAudio]);
        Equal(2, recovery.Store.RecoverInterrupted(preserveMissingPreviewMappings: true), "Installed startup restores true interrupted jobs while retaining unavailable historical ready/review mappings.");
        var recovered = recovery.Store.LoadPresets().ToDictionary(record => record.Id);
        Equal(JsonSerializer.Serialize(recovery.Record, LibraryJson.Options), JsonSerializer.Serialize(recovered[recovery.Record.Id], LibraryJson.Options), "Installed startup preserves every field of a ready historical record whose old WAV is currently unavailable.");
        Equal(JsonSerializer.Serialize(review, LibraryJson.Options), JsonSerializer.Serialize(recovered[review.Id], LibraryJson.Options), "Installed startup preserves unavailable historical review mappings as well as ready mappings.");
        Equal(PresetStatus.NeedsReview, recovered[renderingWithAudio.Id].Status, "A genuinely interrupted render with complete audio still enters review.");
        Equal(renderingWithAudio.CacheKey, recovered[renderingWithAudio.Id].CacheKey, "Restoring an interrupted render preserves its complete committed audio identity.");
        Equal(PresetStatus.Pending, recovered[renderingMissingAudio.Id].Status, "A genuinely interrupted render without complete audio still returns to pending.");
        True(recovered[renderingMissingAudio.Id].Favorite && recovered[renderingMissingAudio.Id].PreviewPath.Length == 0, "Interrupted recovery preserves personal metadata while revoking its incomplete derived mapping.");
        Equal(2, recovery.Store.RecoverInterrupted(), "Non-installed callers retain the existing default policy for missing ready/review previews.");

        var rollback = CreateInstalledStorageFixture("installed-storage-in-place-rollback", inPlace: true);
        string oldRecords = JsonSerializer.Serialize(rollback.Store.LoadPresets(), LibraryJson.Options);
        string oldSettings = JsonSerializer.Serialize(rollback.Store.LoadSettings(), LibraryJson.Options);
        var beforeRollback = InstalledFixtureHashes(rollback);
        string[] beforeRollbackWaves = Directory.GetFiles(rollback.Root, "*.wav", SearchOption.AllDirectories);
        using (var database = new CacheTestSqlite(rollback.Store.DatabasePath))
        {
            database.Execute("CREATE TRIGGER reject_installed_settings_update BEFORE UPDATE ON settings BEGIN SELECT RAISE(ABORT, 'TEST ONLY reject normalization'); END;");
            database.Execute("CREATE TRIGGER reject_installed_settings_insert BEFORE INSERT ON settings BEGIN SELECT RAISE(ABORT, 'TEST ONLY reject normalization'); END;");
        }
        Throws<IOException>(() => InstalledStorageService.Prepare(rollback.Application, rollback.UserData), "A failed normalization commit is reported instead of falling back to an external output path.");
        Equal(oldRecords, JsonSerializer.Serialize(rollback.Store.LoadPresets(), LibraryJson.Options), "A failed in-place commit rolls all preview mappings back together.");
        Equal(oldSettings, JsonSerializer.Serialize(rollback.Store.LoadSettings(), LibraryJson.Options), "A failed in-place commit preserves the previous settings.");
        VerifyInstalledFixtureHashes(beforeRollback);
        True(beforeRollbackWaves.OrderBy(path => path, ApplicationPaths.PathComparer).SequenceEqual(Directory.GetFiles(rollback.Root, "*.wav", SearchOption.AllDirectories).OrderBy(path => path, ApplicationPaths.PathComparer)), "A failed in-place commit removes newly copied audio while preserving all pre-existing local WAV files.");
        True(!Directory.Exists(rollback.UserData), "Failure never silently creates an AppData output directory.");
        using (var database = new CacheTestSqlite(rollback.Store.DatabasePath))
        {
            database.Execute("DROP TRIGGER reject_installed_settings_update;");
            database.Execute("DROP TRIGGER reject_installed_settings_insert;");
        }
        InstalledStorageService.Prepare(rollback.Application, rollback.UserData);
        VerifyInstalledFixture(rollback, rollback.Root);

        var failedImport = CreateInstalledStorageFixture("installed-storage-import-rollback");
        using (var database = new CacheTestSqlite(failedImport.Store.DatabasePath))
        {
            database.Execute("CREATE TRIGGER reject_imported_settings_update BEFORE UPDATE ON settings BEGIN SELECT RAISE(ABORT, 'TEST ONLY reject imported path commit'); END;");
            database.Execute("CREATE TRIGGER reject_imported_settings_insert BEFORE INSERT ON settings BEGIN SELECT RAISE(ABORT, 'TEST ONLY reject imported path commit'); END;");
        }
        string failedImportDatabaseHash = InstalledDatabaseHash(failedImport.Store.DatabasePath);
        var beforeFailedImport = InstalledFixtureHashes(failedImport);
        Throws<IOException>(() => InstalledStorageService.Prepare(failedImport.Application, failedImport.UserData, failedImport.Root), "A path-update failure in an imported snapshot stops publication.");
        Equal(failedImportDatabaseHash, InstalledDatabaseHash(failedImport.Store.DatabasePath), "A failed import never alters the source database.");
        VerifyInstalledFixtureHashes(beforeFailedImport);
        True(!File.Exists(Path.Combine(failedImport.Application, "Preview", "library.db")), "A failed import publishes no database with partially rewritten paths.");

        var rejected = CreateInstalledStorageFixture("installed-storage-future-schema");
        using (var future = new CacheTestSqlite(rejected.Store.DatabasePath)) future.Execute("PRAGMA user_version=99;");
        string futureHash = InstalledDatabaseHash(rejected.Store.DatabasePath);
        Throws<InvalidDataException>(() => InstalledStorageService.Prepare(rejected.Application, rejected.UserData, rejected.Root), "An unsupported future library cannot become an empty current library.");
        Equal(futureHash, InstalledDatabaseHash(rejected.Store.DatabasePath), "Refusing a future database leaves its bytes untouched.");
        True(!File.Exists(Path.Combine(rejected.Application, "Preview", "library.db")), "A rejected source never publishes a partially imported database.");

        string blocked = Fixture("installed-storage-target-blocked");
        string blockedApplication = Path.Combine(blocked, "application"), blockedUser = Path.Combine(blocked, "user-data");
        Directory.CreateDirectory(blockedApplication);
        string obstacle = Path.Combine(blockedApplication, "Preview");
        File.WriteAllText(obstacle, "TEST ONLY occupied Preview path");
        Throws<IOException>(() => InstalledStorageService.Prepare(blockedApplication, blockedUser), "An unavailable installation-local directory stops startup rather than choosing another storage root.");
        Equal("TEST ONLY occupied Preview path", File.ReadAllText(obstacle), "A blocked output path does not overwrite the obstructing user's file.");
        True(!Directory.Exists(blockedUser), "An unavailable installation directory has no hidden AppData fallback.");
        return Task.CompletedTask;
    }

    private sealed record InstalledStorageFixture(string Root, string Application, string UserData, LibraryStore Store,
        PresetRecord Record, string Preview, string Baseline, string Config, string Source, string Unrelated);

    private static void TestInstalledClosedWalSnapshot()
    {
        var fixture = CreateInstalledStorageFixture("installed-storage-closed-wal", legacySchema: true);
        string builder = fixture.Store.DatabasePath;
        List<string> originalRecords;
        string originalSettings;
        using (var database = new CacheTestSqlite(builder))
        {
            database.Execute("PRAGMA journal_mode=WAL;");
            var second = JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(fixture.Record, LibraryJson.Options), LibraryJson.Options)!;
            second.Id = Guid.NewGuid().ToString("N");
            second.Name = "TEST ONLY second closed-WAL record";
            second.Tags = ["TEST ONLY closed WAL personal tag"];
            database.Execute("INSERT INTO presets (id,source_id,synth,source_path,internal_path,content_hash,status,favorite,feedback,json,updated_utc) VALUES (?,?,?,?,?,?,?,?,?,?,?);",
                second.Id, second.SourceId, (int)second.Synth, second.SourcePath, second.InternalPath, second.ContentHash,
                (int)second.Status, second.Favorite, second.Feedback, JsonSerializer.Serialize(second, LibraryJson.Options), DateTime.UtcNow.ToString("O"));
            originalRecords = database.Query("SELECT json FROM presets ORDER BY id;");
            originalSettings = database.Query("SELECT json FROM settings WHERE key='application';").Single();
            Equal("1", database.Query("PRAGMA user_version;").Single(), "The closed-WAL fixture retains its original legacy schema.");
            Equal("0", database.Query("PRAGMA main.wal_checkpoint(TRUNCATE);").Single(), "Building the closed-WAL fixture requires a successful, unblocked TRUNCATE checkpoint.");
            string builderWal = builder + "-wal";
            True(!File.Exists(builderWal) || new FileInfo(builderWal).Length == 0, "The completed TRUNCATE checkpoint leaves no uncheckpointed WAL data in the builder.");
        }
        // macOS may retain the builder's SHM after close. Copy its fully checkpointed main
        // file into a separate source path to construct the no-sidecar case without deleting auxiliaries.
        string sourceDirectory = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "readonly-source");
        Directory.CreateDirectory(sourceDirectory);
        string source = Path.Combine(sourceDirectory, "library.db");
        File.Copy(builder, source);
        byte[] originalBytes = File.ReadAllBytes(source);
        Equal((byte)2, originalBytes[18], "A checkpointed closed WAL database still carries WAL write-version metadata.");
        Equal((byte)2, originalBytes[19], "A checkpointed closed WAL database still carries WAL read-version metadata.");
        True(!File.Exists(source + "-wal") && !File.Exists(source + "-shm"), "All closed-WAL fixture data is committed to its main file without sidecars.");
        string originalHash = InstalledDatabaseHash(source);
        string snapshots = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "private-snapshots");
        string snapshot = Path.Combine(snapshots, "closed-wal.db");
        FileAttributes originalAttributes = File.GetAttributes(source);
        try
        {
            File.SetAttributes(source, originalAttributes | FileAttributes.ReadOnly);
            LibraryStore.SnapshotExistingDatabase(source, snapshot);
            True(originalBytes.SequenceEqual(File.ReadAllBytes(source)), "Snapshotting a closed WAL library preserves every source database byte, including its original schema and JSON.");
            Equal(originalHash, InstalledDatabaseHash(source), "Snapshotting a read-only closed WAL library never rewrites the original database.");
            True(!File.Exists(source + "-wal") && !File.Exists(source + "-shm"), "Reading a closed WAL library never creates auxiliary files beside the original.");
            True((File.GetAttributes(snapshot) & FileAttributes.ReadOnly) == 0, "The private snapshot does not inherit the source's read-only attribute.");
            VerifyInstalledSnapshotMainFile(snapshot, originalRecords, originalSettings, 1);
            using (var database = new CacheTestSqlite(snapshot))
            {
                Equal("delete", database.Query("PRAGMA journal_mode;").Single(), "A closed-WAL snapshot is an independent DELETE-mode database.");
                Equal("1", database.Query("PRAGMA user_version;").Single(), "A closed-WAL snapshot keeps its source schema version before application migration.");
                Equal("ok", database.Query("PRAGMA quick_check;").Single(), "The normalized closed-WAL snapshot passes SQLite integrity checks.");
                True(originalRecords.SequenceEqual(database.Query("SELECT json FROM presets ORDER BY id;")), "Every closed-WAL record and its original JSON survives the private-copy snapshot path.");
                Equal(originalSettings, database.Query("SELECT json FROM settings WHERE key='application';").Single(), "The closed-WAL snapshot retains the exact original settings JSON.");
            }
            AssertInstalledSnapshotStandalone(snapshot, "Opening the independent snapshot retains rollback headers and requires no WAL.");
            True(Directory.GetFiles(snapshots).Where(path => !path.Equals(snapshot + "-shm", ApplicationPaths.PathComparison)).SequenceEqual(new[] { snapshot }),
                "A successful snapshot removes its private input copy and leaves only the final database plus an optional inert SHM index.");
            True(originalBytes.SequenceEqual(File.ReadAllBytes(source)) && !File.Exists(source + "-wal") && !File.Exists(source + "-shm"), "Inspecting the completed private snapshot leaves the source bytes and directory unchanged.");
        }
        finally { File.SetAttributes(source, originalAttributes); }
    }

    private static void VerifyInstalledSnapshotMainFile(string snapshot, IReadOnlyList<string> expectedRecords, string expectedSettings, int schemaVersion)
    {
        string directory = Path.Combine(Path.GetDirectoryName(snapshot)!, "main-only-" + Guid.NewGuid().ToString("N")[..8]);
        True(ApplicationPaths.IsWithin(directory, _fixtureRoot), "A main-file-only snapshot proof must stay in the isolated fixture.");
        Directory.CreateDirectory(directory);
        string copy = Path.Combine(directory, "snapshot.db");
        File.Copy(snapshot, copy);
        True(!File.Exists(copy + "-wal") && !File.Exists(copy + "-shm"), "The independent proof copies only the database main file.");
        using var database = new CacheTestSqlite(copy);
        Equal("delete", database.Query("PRAGMA journal_mode;").Single(), "An independently copied main file reopens in DELETE journal mode. " + DescribeInstalledSnapshot(snapshot));
        Equal(schemaVersion.ToString(), database.Query("PRAGMA user_version;").Single(), "The independent main file retains its original schema.");
        Equal("ok", database.Query("PRAGMA quick_check;").Single(), "The independent main file passes SQLite integrity checks.");
        True(expectedRecords.SequenceEqual(database.Query("SELECT json FROM presets ORDER BY id;")), "The independent main file contains every exact original record, including WAL-committed metadata.");
        Equal(expectedSettings, database.Query("SELECT json FROM settings WHERE key='application';").Single(), "The independent main file contains the exact original application settings.");
    }

    private static void AssertInstalledSnapshotStandalone(string snapshot, string message)
    {
        // Some macOS system SQLite builds retain an inert SHM index after leaving WAL mode.
        // Exact schema/JSON/integrity checks on an independent main-file-only copy prove the
        // committed data needs no sidecars. Require rollback headers and no WAL; leave SHM untouched.
        using var stream = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[20];
        stream.ReadExactly(header);
        if (header[18] == 1 && header[19] == 1 && !File.Exists(snapshot + "-wal")) return;
        throw new InvalidOperationException(message + " " + DescribeInstalledSnapshot(snapshot));
    }

    private static string DescribeInstalledSnapshot(string snapshot)
    {
        var facts = new List<string> { "Snapshot=" + snapshot };
        try
        {
            using var stream = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[20];
            stream.ReadExactly(header);
            facts.Add($"Header18={header[18]},Header19={header[19]}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { facts.Add("HeaderReadError=" + error.Message); }
        foreach (string suffix in new[] { "-wal", "-shm" })
        {
            string path = snapshot + suffix;
            try { facts.Add(suffix + "=" + (File.Exists(path) ? new FileInfo(path).Length + " bytes" : "absent")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { facts.Add(suffix + "ReadError=" + error.Message); }
        }
        return string.Join("; ", facts);
    }

    private static string InstalledDatabaseHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string[] InstalledLibraryFiles(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !path.Equals(Path.Combine(root, "library.db-wal"), ApplicationPaths.PathComparison) &&
            !path.Equals(Path.Combine(root, "library.db-shm"), ApplicationPaths.PathComparison))
        .OrderBy(path => path, ApplicationPaths.PathComparer).ToArray();

    private static InstalledStorageFixture CreateInstalledStorageFixture(string name, bool inPlace = false, bool externalPreview = true, bool legacySchema = false)
    {
        string directory = Fixture(name), application = Path.Combine(directory, "application"), userData = Path.Combine(directory, "user-data");
        string root = inPlace ? Path.Combine(application, "Preview") : Path.Combine(directory, "old-library");
        string audioRoot = externalPreview ? Path.Combine(directory, "old-external-audio") : root;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(audioRoot);
        string source = Path.Combine(directory, "original-presets", "TEST ONLY #原音色.vital");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "{\"settings\":{},\"preset_name\":\"TEST ONLY original\"}");
        var record = Preset(name);
        record.SourcePath = source;
        record.ContentHash = Fingerprints.HashFile(source);
        CompletePreview(record, Path.Combine(audioRoot, "TEST ONLY 旧预览.wav"));
        record.Favorite = true;
        record.Feedback = -1;
        record.FeedbackUtc = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        record.Tags = ["Personal tag", "自定义标签"];
        record.CategoryTags = ["Personal category", "自定义分类"];
        record.Attempts = 3;
        string baseline = Path.Combine(audioRoot, "InitializationBaselines", "TEST ONLY baseline.json");
        Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
        string[] baselineWaves = Enumerable.Range(1, 3).Select(index => Path.Combine(Path.GetDirectoryName(baseline)!, "TEST ONLY note-" + index + ".wav")).ToArray();
        foreach (string wave in baselineWaves) WaveFile.WritePcm16(wave, Tone(44100, 1, .02));
        File.WriteAllText(baseline, JsonSerializer.Serialize(new
        {
            Kind = InitBaselineService.BaselineKind, WavPath = baselineWaves[0], Result = new { OutputPath = baselineWaves[0] },
            References = baselineWaves.Select(wave => new { WavPath = wave, Result = new { OutputPath = wave } })
        }, LibraryJson.Options));
        var settings = new AppSettings { PreviewDirectory = audioRoot, Sources = [new SourceFolder { Id = "kept-source", Synth = SynthKind.Vital, Path = Path.GetDirectoryName(source)! }] };
        settings.Plugins.Single(plugin => plugin.Synth == SynthKind.Vital).InitBaselinePath = baseline;
        string config = Path.Combine(root, "Config", "preset-categories.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "{\"version\":1,\"categories\":[{\"label\":\"Personal category\",\"tokens\":[\"personal\"]}]}");
        string unrelated = Path.Combine(root, "TEST ONLY unrelated user audio.wav");
        WaveFile.WritePcm16(unrelated, Tone(44100, 1, .01));
        var store = new LibraryStore(Path.Combine(root, "library.db"));
        if (legacySchema)
        {
            CreateLegacyDatabase(store.DatabasePath, record);
            using var database = new CacheTestSqlite(store.DatabasePath);
            database.Execute("INSERT INTO settings (key,json) VALUES (?,?);", "application", JsonSerializer.Serialize(settings, LibraryJson.Options));
        }
        else store.SaveState([record], settings);
        return new(root, application, userData, store, record, record.PreviewPath, baseline, config, source, unrelated);
    }

    private static Dictionary<string, string> InstalledFixtureHashes(InstalledStorageFixture fixture)
    {
        var paths = new[] { fixture.Preview, fixture.Baseline, fixture.Config, fixture.Source, fixture.Unrelated }
            .Concat(StorageBaselinePaths(JsonNode.Parse(File.ReadAllText(fixture.Baseline))!));
        return paths.Distinct(ApplicationPaths.PathComparer).ToDictionary(path => path, path => Fingerprints.HashFile(path), ApplicationPaths.PathComparer);
    }

    private static void VerifyInstalledFixtureHashes(Dictionary<string, string> hashes)
    {
        foreach (var (path, hash) in hashes)
        {
            True(File.Exists(path), "Installing or upgrading preserves the original source/cache/config file: " + path);
            Equal(hash, Fingerprints.HashFile(path), "Installing or upgrading preserves the original file bytes: " + path);
        }
    }

    private static void VerifyInstalledFixture(InstalledStorageFixture fixture, string target)
    {
        var targetStore = new LibraryStore(Path.Combine(target, "library.db"));
        var record = targetStore.LoadPresets().Single();
        var settings = targetStore.LoadSettings();
        Equal(target, settings.PreviewDirectory, "New output settings always point at the installation-local Preview directory.");
        True(ApplicationPaths.IsWithin(record.PreviewPath, target) && File.Exists(record.PreviewPath), "An imported preview resolves to an existing audio file under installation-local Preview.");
        Equal(Fingerprints.HashFile(fixture.Preview), Fingerprints.HashFile(record.PreviewPath), "Import preserves audio bytes and avoids regeneration.");
        var expected = JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(fixture.Record, LibraryJson.Options), LibraryJson.Options)!;
        expected.PreviewPath = record.PreviewPath;
        Equal(JsonSerializer.Serialize(expected, LibraryJson.Options), JsonSerializer.Serialize(record, LibraryJson.Options), "Import changes only audio location, preserving identity, source, metadata, cache keys, note maps and render provenance.");
        Equal(File.ReadAllText(fixture.Config), File.ReadAllText(Path.Combine(target, "Config", "preset-categories.json")), "Editable classification rules remain beside the installation-local library.");
        string baseline = settings.Plugins.Single(plugin => plugin.Synth == SynthKind.Vital).InitBaselinePath;
        True(ApplicationPaths.IsWithin(baseline, target) && File.Exists(baseline), "The active baseline metadata follows the local Preview directory.");
        foreach (string wave in StorageBaselinePaths(JsonNode.Parse(File.ReadAllText(baseline))!))
            True(ApplicationPaths.IsWithin(wave, target) && File.Exists(wave), "All nested baseline audio and result references point at existing local files.");
        Equal(Path.GetDirectoryName(fixture.Source), settings.Sources.Single().Path, "Import never redirects the user's original preset sources.");
    }
}
