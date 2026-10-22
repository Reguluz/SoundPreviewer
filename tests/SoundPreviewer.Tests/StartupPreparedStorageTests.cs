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
        var local = CreateInstalledStorageFixture("prepared-startup-local", inPlace: true, externalPreview: false);
        var progress = new StartupStorageProgress();
        var originalFiles = InstalledFixtureHashes(local);
        string originalRecords = JsonSerializer.Serialize(local.Store.LoadPresets(), LibraryJson.Options);
        string originalSettings = JsonSerializer.Serialize(local.Store.LoadSettings(), LibraryJson.Options);
        string[] originalFileNames = InstalledLibraryFiles(local.Root);
        for (int launch = 0; launch < 2; launch++)
        {
            True(InstalledStorageService.TryOpenPreparedLibrary(local.Application, local.Root, out var direct), "An existing 0.8.0/0.8.1 local library can open directly without a new migration marker.");
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
        True(InstalledStorageService.TryOpenPreparedLibrary(local.Application, local.Root, out _), "Missing historical media is retained without endlessly repeating a copy that cannot run.");
        Equal(missingExternal, local.Store.LoadPresets().Single().PreviewPath, "The check never clears a missing historical mapping.");
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

    private sealed class StartupStorageProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];
        public void Report(string message) => Messages.Add(message);
    }
}
