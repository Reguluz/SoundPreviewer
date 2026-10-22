using System.Text.Json;
using System.Text.Json.Nodes;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static async Task TestPreparedStorageStartupAsync()
    {
        TestPortableRelativeStartup();
        TestForeignAbsolutePreviewMigration();
        var local = CreateInstalledStorageFixture("prepared-startup-local", inPlace: true, externalPreview: false);
        var progress = new StartupStorageProgress();
        var originalFiles = InstalledFixtureHashes(local);
        string originalRecords = JsonSerializer.Serialize(local.Store.LoadPresets(), LibraryJson.Options);
        string originalSettings = JsonSerializer.Serialize(local.Store.LoadSettings(), LibraryJson.Options);
        string[] originalFileNames = InstalledLibraryFiles(local.Root);
        for (int launch = 0; launch < 2; launch++)
        {
            True(InstalledStorageService.TryOpenPreparedLibrary(local.Application, local.Root, out var direct), "A relative-path local library can open directly without a new migration marker.");
            Equal(local.Root, direct!.Directory, "The direct startup keeps output in installation-local Preview.");
            Equal(0, InstalledStorageService.Prepare(local.Application, local.UserData, progress: progress).CopiedFiles, "Repeated startup copies no local audio.");
        }
        Equal(0, progress.Messages.Count, "A prepared local library never reports a migration progress pass, including its first launch with this version.");
        True(originalFileNames.SequenceEqual(InstalledLibraryFiles(local.Root)), "Direct startup adds neither backups nor temporary migration files.");
        Equal(originalRecords, JsonSerializer.Serialize(local.Store.LoadPresets(), LibraryJson.Options), "Opening an already local library changes no personal or cached preset metadata.");
        Equal(originalSettings, JsonSerializer.Serialize(local.Store.LoadSettings(), LibraryJson.Options), "The quick check preserves every renderer, source and template setting.");
        VerifyInstalledFixtureHashes(originalFiles);

        var edited = local.Store.LoadPresets().Single();
        edited.Favorite = false;
        edited.Tags.Add("TEST ONLY latest personal tag");
        edited.CategoryTags.Add("TEST ONLY latest personal category");
        local.Store.Upsert(edited);
        var settings = local.Store.LoadSettings();
        settings.PlaybackVolume = .35;
        settings.Plugins.Single(plugin => plugin.Synth == SynthKind.Vital).Version = "TEST ONLY updated renderer version";
        local.Store.SaveSettings(settings);
        string editedJson = JsonSerializer.Serialize(local.Store.LoadPresets(), LibraryJson.Options);
        InstalledStorageService.Prepare(local.Application, local.UserData, progress: progress);
        Equal(0, progress.Messages.Count, "Ordinary metadata and renderer-setting changes do not cause migration on the next launch.");
        Equal(editedJson, JsonSerializer.Serialize(local.Store.LoadPresets(), LibraryJson.Options), "The newest tags, categories and favorite choice survive direct startup.");

        string generatedRoot = Fixture("prepared-startup-after-generation");
        string generatedApplication = Path.Combine(generatedRoot, "application"), generatedUser = Path.Combine(generatedRoot, "user-data");
        string generatedPreview = InstalledStorageService.Prepare(generatedApplication, generatedUser).Directory;
        var generatedStore = new LibraryStore(Path.Combine(generatedPreview, "library.db"));
        var renderSettings = new AppSettings
        {
            PreviewDirectory = generatedPreview, PreviewFormat = PreviewAudioFormat.Wav, MaxParallelRenders = 1,
            Template = new PreviewTemplate { QuickPreview = false, HoldSeconds = .1, TailSeconds = .02 },
            Plugins = [new PluginConfiguration { Synth = SynthKind.Vital, Backend = RenderBackend.ExternalWorker, ExecutablePath = TestExecutable, Version = "TEST ONLY worker", TimeoutSeconds = 30 }]
        };
        var generated = Preset("success");
        generatedStore.SaveState([generated], renderSettings);
        Equal(1, (await new RenderQueue(generatedStore).RunAsync(renderSettings, [generated], TestExecutable, new InlineProgress(), CancellationToken.None)).Generated,
            "The fixture completes a real queue/cache commit using only the synthetic test worker.");
        string generatedHash = Fingerprints.HashFile(generatedStore.LoadPresets().Single().PreviewPath);
        InstalledStorageService.Prepare(generatedApplication, generatedUser, progress: progress);
        Equal(0, progress.Messages.Count, "A completed render does not restart migration on the next launch.");
        Equal(generatedHash, Fingerprints.HashFile(generatedStore.LoadPresets().Single().PreviewPath), "Direct startup preserves the newly rendered preview byte for byte.");

        string external = Path.Combine(Path.GetDirectoryName(local.Application)!, "TEST ONLY old-version-external.wav");
        WaveFile.WritePcm16(external, Tone(44100, 1, .02));
        string externalHash = Fingerprints.HashFile(external);
        edited.PreviewPath = external;
        local.Store.Upsert(edited); // An older version can introduce an external reference after a successful local launch.
        True(!InstalledStorageService.TryOpenPreparedLibrary(local.Application, local.Root, out _), "An available external preview cannot bypass safe normalization just because PreviewDirectory already matches.");
        var migrated = InstalledStorageService.Prepare(local.Application, local.UserData, progress: progress);
        True(migrated.CopiedFiles > 0 && progress.Messages.Count == 1, "Actual external audio still uses the original validated snapshot/copy workflow.");
        True(!progress.Messages.Single().Contains("旧音色库安全复制", StringComparison.Ordinal), "An in-place path correction is not described as importing the whole old library.");
        edited = local.Store.LoadPresets().Single();
        True(ApplicationPaths.IsWithin(edited.PreviewPath, local.Root), "The old-version audio reference is safely made installation-local.");
        Equal(externalHash, Fingerprints.HashFile(edited.PreviewPath), "Normalization retains the complete old preview.");
        Equal(externalHash, Fingerprints.HashFile(external), "Normalization never removes or overwrites the external original.");
        string[] afterMigrationFiles = InstalledLibraryFiles(local.Root);
        progress.Messages.Clear();
        InstalledStorageService.Prepare(local.Application, local.UserData, progress: progress);
        Equal(0, progress.Messages.Count, "Once the actual correction commits, the next launch skips the migration window again.");
        True(afterMigrationFiles.SequenceEqual(InstalledLibraryFiles(local.Root)), "A second launch after true migration produces no additional backup.");

        string missingExternal = Path.Combine(Path.GetDirectoryName(local.Application)!, "TEST ONLY disconnected-preview.wav");
        edited.PreviewPath = missingExternal;
        local.Store.Upsert(edited);
        True(!InstalledStorageService.TryOpenPreparedLibrary(local.Application, local.Root, out _), "A missing historical absolute mapping receives a one-time local relative mapping.");
        InstalledStorageService.Prepare(local.Application, local.UserData);
        True(InstalledStorageService.TryOpenPreparedLibrary(local.Application, local.Root, out _), "A missing historical mapping does not repeat migration on every startup.");
        var missingRecord = local.Store.LoadPresets().Single();
        True(ApplicationPaths.IsWithin(missingRecord.PreviewPath, local.Root), "Missing previews still resolve safely inside the current Preview.");
        Equal(missingExternal, missingRecord.AdditionalProperties!["HistoricalPreviewPath"].GetString(), "The historical missing reference remains recoverable without being the active preview path.");
        File.Copy(external, missingExternal);
        True(!InstalledStorageService.TryOpenPreparedLibrary(local.Application, local.Root, out _), "When an external disk's preview becomes available, its retained mapping triggers safe import again.");
        InstalledStorageService.Prepare(local.Application, local.UserData);

        string nestedExternal = Path.Combine(Path.GetDirectoryName(local.Application)!, "TEST ONLY external-baseline-note.wav");
        File.Copy(external, nestedExternal);
        var baseline = JsonNode.Parse(File.ReadAllText(local.Baseline))!.AsObject();
        baseline["References"]![0]!["WavPath"] = nestedExternal;
        File.WriteAllText(local.Baseline, baseline.ToJsonString(LibraryJson.Options));
        True(!InstalledStorageService.TryOpenPreparedLibrary(local.Application, local.Root, out _), "Local baseline metadata with an external nested note must still receive safe path rewriting.");
        InstalledStorageService.Prepare(local.Application, local.UserData);
        True(InstalledStorageService.TryOpenPreparedLibrary(local.Application, local.Root, out _), "Rewritten baseline references allow normal direct startup.");
        Equal(externalHash, Fingerprints.HashFile(nestedExternal), "Baseline preparation preserves external source audio.");

        var externalBaseline = CreateInstalledStorageFixture("prepared-startup-external-baseline", inPlace: true);
        settings = externalBaseline.Store.LoadSettings();
        settings.PreviewDirectory = externalBaseline.Root;
        externalBaseline.Record.PreviewPath = "";
        externalBaseline.Store.SaveState([externalBaseline.Record], settings);
        True(!InstalledStorageService.TryOpenPreparedLibrary(externalBaseline.Application, externalBaseline.Root, out _), "An external baseline JSON itself is not mistaken for a prepared local reference.");
        InstalledStorageService.Prepare(externalBaseline.Application, externalBaseline.UserData);
        True(InstalledStorageService.TryOpenPreparedLibrary(externalBaseline.Application, externalBaseline.Root, out _), "The baseline-only normalization commits before direct startup becomes available.");

        settings = local.Store.LoadSettings();
        settings.PreviewDirectory = Path.Combine(local.Application, "TEST ONLY previous Preview");
        local.Store.SaveSettings(settings);
        True(!InstalledStorageService.TryOpenPreparedLibrary(local.Application, local.Root, out _), "A portable relocation's previous absolute output root requires the existing safe normalization workflow.");
        InstalledStorageService.Prepare(local.Application, local.UserData);
        True(InstalledStorageService.TryOpenPreparedLibrary(local.Application, local.Root, out _), "Successful root normalization is recognized without creating a new configuration file.");
        True(!InstalledStorageService.TryOpenPreparedLibrary(local.Application, generatedPreview, out _), "A selected external library never takes the installation-local shortcut.");
    }

    private static void TestPortableRelativeStartup()
    {
        var original = CreateInstalledStorageFixture("prepared-portable-relocation", inPlace: true, externalPreview: false);
        string movedApplication = Path.Combine(Path.GetDirectoryName(original.Application)!, "moved-application");
        string movedRoot = Path.Combine(movedApplication, "Preview");
        CopyFixture(original.Root, movedRoot);
        string movedPreview = Path.Combine(movedRoot, Path.GetRelativePath(original.Root, original.Preview));
        string expectedHash = Fingerprints.HashFile(movedPreview);
        True(ApplicationPaths.IsWithin(original.Preview, _fixtureRoot), "Only this synthetic fixture may be replaced to distinguish the two installations.");
        File.Delete(original.Preview);
        WaveFile.WritePcm16(original.Preview, Tone(44100, 1, .04));
        True(expectedHash != Fingerprints.HashFile(original.Preview), "Old and moved installations deliberately hold different audio bytes.");
        string oldHash = Fingerprints.HashFile(original.Preview);
        True(InstalledStorageService.TryOpenPreparedLibrary(movedApplication, movedRoot, out _), "A moved relative library opens immediately without copying the previous installation.");
        var movedStore = new LibraryStore(Path.Combine(movedRoot, "library.db"));
        Equal(expectedHash, Fingerprints.HashFile(movedStore.LoadPresets().Single().PreviewPath), "Playback resolves the moved local audio, even while the old installation still exists.");
        var originalSettings = original.Store.LoadSettings();
        using (var database = new CacheTestSqlite(movedStore.DatabasePath))
        {
            var row = JsonNode.Parse(database.Query("SELECT json FROM presets;").Single())!.AsObject();
            row["PreviewPath"] = original.Preview;
            database.Execute("UPDATE presets SET json=?;", row.ToJsonString());
            database.Execute("UPDATE settings SET json=? WHERE key='application';", JsonSerializer.Serialize(originalSettings, LibraryJson.Options));
        }
        string movedBaseline = Path.Combine(movedRoot, Path.GetRelativePath(original.Root, original.Baseline));
        var metadata = JsonNode.Parse(File.ReadAllText(movedBaseline))!;
        RewriteFixturePaths(metadata, path => PreviewPathStorage.Resolve(path, original.Root));
        File.WriteAllText(movedBaseline, metadata.ToJsonString());
        True(!InstalledStorageService.TryOpenPreparedLibrary(movedApplication, movedRoot, out _), "Old absolute rows and baseline JSON require one compatibility pass.");
        var result = InstalledStorageService.Prepare(movedApplication, original.UserData);
        Equal(expectedHash, Fingerprints.HashFile(movedStore.LoadPresets().Single().PreviewPath), "Legacy absolute references prefer the same relative file in the current installation over the old copy.");
        Equal(oldHash, Fingerprints.HashFile(original.Preview), "Relocation never changes the previous installation's audio.");
        Equal(original.Source, movedStore.LoadPresets().Single().SourcePath, "Preset source paths are not rewritten with preview references.");
        using (var database = new CacheTestSqlite(movedStore.DatabasePath))
        {
            var row = JsonNode.Parse(database.Query("SELECT json FROM presets;").Single())!;
            True(!PreviewPathStorage.IsAbsolute(row["PreviewPath"]!.GetValue<string>()), "Migrated cached audio is stored with a relative path.");
            var settings = JsonNode.Parse(database.Query("SELECT json FROM settings WHERE key='application';").Single())!;
            Equal(".", settings["PreviewDirectory"]!.GetValue<string>(), "The stored output root is relative to this Preview.");
        }
        string[] after = InstalledLibraryFiles(movedRoot);
        True(InstalledStorageService.TryOpenPreparedLibrary(movedApplication, movedRoot, out _), "The compatibility pass leaves a prepared relative library.");
        Equal(0, InstalledStorageService.Prepare(movedApplication, original.UserData).CopiedFiles, "Subsequent launches do not copy or produce a new migration pass.");
        True(after.SequenceEqual(InstalledLibraryFiles(movedRoot)), "Subsequent launches preserve the exact file set including backup count.");
        string baselinePath = movedStore.LoadSettings().Plugins.Single(plugin => plugin.Synth == SynthKind.Vital).InitBaselinePath;
        True(StorageBaselinePaths(JsonNode.Parse(File.ReadAllText(baselinePath))!).All(path => !PreviewPathStorage.IsAbsolute(path) && File.Exists(PreviewPathStorage.Resolve(path, movedRoot))),
            "Every nested baseline audio/result reference migrates to safe relative Preview paths.");

        string disconnected = Path.Combine(Path.GetDirectoryName(movedApplication)!, "TEST ONLY disconnected-before-clear.wav");
        var historical = movedStore.LoadPresets().Single();
        historical.PreviewPath = disconnected;
        movedStore.Upsert(historical);
        InstalledStorageService.Prepare(movedApplication, original.UserData);
        File.Copy(movedPreview, disconnected);
        PreviewStorageService.ClearPreviews(movedStore, movedStore.LoadSettings());
        True(InstalledStorageService.TryOpenPreparedLibrary(movedApplication, movedRoot, out _),
            "Clearing previews disables a prior missing-file recovery marker even if the old external file returns.");
        string[] clearedFiles = InstalledLibraryFiles(movedRoot);
        InstalledStorageService.Prepare(movedApplication, original.UserData);
        True(clearedFiles.SequenceEqual(InstalledLibraryFiles(movedRoot)) && movedStore.LoadPresets().Single().PreviewPath.Length == 0,
            "Restarting after clear neither recreates the cleared audio nor repeats a migration pass.");
        True(File.Exists(disconnected), "Clearing leaves the returned historical external file untouched.");

        void CopyFixture(string source, string target)
        {
            True(ApplicationPaths.IsWithin(source, _fixtureRoot) && ApplicationPaths.IsWithin(target, _fixtureRoot), "Relocation copies only isolated fixtures.");
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            foreach (string directory in Directory.GetDirectories(source)) CopyFixture(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static void RewriteFixturePaths(JsonNode? node, Func<string, string> rewrite)
    {
        if (node is JsonObject obj)
            foreach (string key in obj.Select(entry => entry.Key).ToArray())
            {
                if (key is "WavPath" or "OutputPath") obj[key] = rewrite(obj[key]!.GetValue<string>());
                else RewriteFixturePaths(obj[key], rewrite);
            }
        else if (node is JsonArray array) foreach (var item in array) RewriteFixturePaths(item, rewrite);
    }

    private static void TestForeignAbsolutePreviewMigration()
    {
        foreach (string foreignRoot in new[] { "/Users/TEST ONLY/App/Preview", "Z:/TEST ONLY/App/Preview" })
        {
            var fixture = CreateInstalledStorageFixture("prepared-foreign-path-" + (foreignRoot.StartsWith('/') ? "posix" : "windows"), inPlace: true, externalPreview: false);
            string expectedHash = Fingerprints.HashFile(fixture.Preview);
            using (var database = new CacheTestSqlite(fixture.Store.DatabasePath))
            {
                var row = JsonNode.Parse(database.Query("SELECT json FROM presets;").Single())!.AsObject();
                row["PreviewPath"] = foreignRoot + "/" + Path.GetFileName(fixture.Preview);
                database.Execute("UPDATE presets SET json=?;", row.ToJsonString());
                var settings = JsonNode.Parse(database.Query("SELECT json FROM settings WHERE key='application';").Single())!.AsObject();
                settings["PreviewDirectory"] = foreignRoot;
                var plugin = settings["Plugins"]!.AsArray().OfType<JsonObject>().Single(item => item["InitBaselinePath"]!.GetValue<string>().Length > 0);
                plugin["InitBaselinePath"] = foreignRoot + "/InitializationBaselines/" + Path.GetFileName(fixture.Baseline);
                database.Execute("UPDATE settings SET json=? WHERE key='application';", settings.ToJsonString());
            }
            var prepared = InstalledStorageService.Prepare(fixture.Application, fixture.UserData);
            Equal(0, prepared.CopiedFiles, "Cross-platform absolute references rebind existing local audio without accessing or copying an old disk.");
            var restored = fixture.Store.LoadPresets().Single();
            Equal(fixture.Preview, restored.PreviewPath, "A foreign absolute audio path resolves to the matching current Preview file.");
            Equal(expectedHash, Fingerprints.HashFile(restored.PreviewPath), "Cross-platform rebinding retains exact audio bytes.");
            Equal(fixture.Baseline, fixture.Store.LoadSettings().Plugins.Single(plugin => plugin.Synth == SynthKind.Vital).InitBaselinePath,
                "The foreign absolute baseline locator also resolves to current local metadata.");
            True(InstalledStorageService.TryOpenPreparedLibrary(fixture.Application, fixture.Root, out _), "Both Windows and POSIX historical absolute formats finish as a prepared relative library.");
            using var verified = new CacheTestSqlite(fixture.Store.DatabasePath);
            True(!PreviewPathStorage.IsAbsolute(JsonNode.Parse(verified.Query("SELECT json FROM presets;").Single())!["PreviewPath"]!.GetValue<string>()),
                "A cross-platform migration persists a relative audio path.");
        }
    }

    private sealed class StartupStorageProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];
        public void Report(string message) => Messages.Add(message);
    }
}
