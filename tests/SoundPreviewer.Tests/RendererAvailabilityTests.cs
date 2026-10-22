using System.Text.Json;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static async Task TestQueueRendererAvailabilityAsync()
    {
        string directory = Fixture("renderer-availability");
        var settings = new AppSettings
        {
            PreviewDirectory = Path.Combine(directory, "Preview"), PreviewFormat = PreviewAudioFormat.Wav, MaxAttempts = 1,
            Template = new PreviewTemplate { QuickPreview = false, HoldSeconds = .1, TailSeconds = .02 },
            Plugins = [new PluginConfiguration { Synth = SynthKind.Vital, Backend = RenderBackend.ExternalWorker,
                ExecutablePath = TestExecutable, PluginPath = Path.Combine(directory, "TEST ONLY selected-plugin.dll"),
                Version = "TEST ONLY independent protocol", TimeoutSeconds = 5 }, new PluginConfiguration { Synth = SynthKind.Serum1 }]
        };
        var store = new LibraryStore(Path.Combine(directory, "library.db"));
        var blocked = Preset("success");
        blocked.Attempts = 2;
        blocked.AttemptedCacheKey = "TEST ONLY last attempt key";
        blocked.Favorite = true; blocked.Feedback = -1; blocked.Tags = ["TEST ONLY retained tag"];
        var originalBlocked = CloneAvailabilityRecord(blocked);
        var cached = Preset("success");
        CompletePreview(cached, Path.Combine(directory, "TEST-ONLY-old-confirmed.wav"), settings.Template);
        cached.Message = "TEST ONLY user confirmed this exact preview";
        string originalCached = JsonSerializer.Serialize(cached, LibraryJson.Options);
        string cachedHash = Fingerprints.HashFile(cached.PreviewPath);
        var notChosen = Preset("success");
        notChosen.Synth = SynthKind.Serum1;
        var states = new List<RenderReadiness>
        {
            new(SynthKind.Vital, "版本缺失", "TEST ONLY selected renderer requires its actual version", false),
            new(SynthKind.Serum1, "需要32位宿主", "TEST ONLY no generation method was selected", false),
            new(SynthKind.Spire, "插件缺失", "TEST ONLY unrelated synth must not enter this queue summary", false)
        };
        string settingsBefore = JsonSerializer.Serialize(settings, LibraryJson.Options);
        store.SaveState([blocked, cached, notChosen], settings);
        string savedSettingsBefore = JsonSerializer.Serialize(store.LoadSettings(), LibraryJson.Options);
        bool changedSnapshotSource = false;
        int lastUnavailable = 0;
        var progress = new RecordingProgress(value =>
        {
            lastUnavailable = value.Unavailable;
            if (changedSnapshotSource) return;
            changedSnapshotSource = true;
            states[0] = states[0] with { CanAttemptRender = true, Detail = "TEST ONLY changed after queue construction" };
        });
        var queue = new RenderQueue(store);
        var result = await queue.RunAsync(settings, [blocked, cached, notChosen], TestExecutable, progress, CancellationToken.None,
            readiness: states);
        Equal(1, result.Unavailable, "An explicitly selected but blocked renderer is unavailable rather than unconfigured.");
        Equal(1, result.Unconfigured, "Only a genuinely unselected generation method is counted as unconfigured.");
        Equal(1, result.Reused, "A verified old preview is reused before the current availability check.");
        Equal(0, result.Generated, "A blocked new preview never starts a rendering process.");
        Equal(0, result.Failed, "An availability check does not consume a render failure.");
        Equal(3, result.Processed, "Reuse, unselected methods and unavailable renderers each account for their input once.");
        Equal(0, result.Remaining, "No blocked candidate silently disappears from the final accounting.");
        var group = result.UnavailableRenderers.Single();
        Equal(SynthKind.Vital, group.Synth, "Only selected candidate synths enter unavailable groups.");
        Equal("版本缺失", group.Status, "A renderer's original readiness status survives queue preparation.");
        Equal(1, group.Count, "A reused preview is excluded from unavailable counts.");
        True(group.Detail.Contains("TEST ONLY selected renderer requires its actual version", StringComparison.Ordinal) &&
            group.Detail.Contains(settings.Plugins[0].ExecutablePath, StringComparison.Ordinal) &&
            group.Detail.Contains(settings.Plugins[0].PluginPath, StringComparison.Ordinal), "A skipped renderer keeps the specific reason and selected executable/plugin paths.");
        True(result.Summary.Contains("已配置，暂不可用", StringComparison.Ordinal) &&
            result.Summary.Contains("未选择生成方式", StringComparison.Ordinal) && result.Summary.Contains(group.Detail, StringComparison.Ordinal) &&
            !result.Summary.Contains("unrelated synth", StringComparison.Ordinal), "The final summary explains only actual skipped candidates, preserving the full cause.");
        Equal(1, lastUnavailable, "Final progress carries the distinct unavailable count.");
        Equal("已配置，暂不可用", blocked.LastRenderOutcome, "The skipped preset has a visible outcome diagnostic.");
        True(blocked.LastRenderMessage.Contains("版本缺失", StringComparison.Ordinal) &&
            blocked.Message == blocked.LastRenderMessage, "Pending presets retain their exact skip reason in the library.");
        originalBlocked.LastRenderOutcome = blocked.LastRenderOutcome;
        originalBlocked.LastRenderMessage = blocked.LastRenderMessage;
        originalBlocked.Message = blocked.Message;
        Equal(JsonSerializer.Serialize(originalBlocked, LibraryJson.Options), JsonSerializer.Serialize(blocked, LibraryJson.Options),
            "Availability changes only diagnostics, preserving source status, attempts, last attempt key and personal edits.");
        Equal(JsonSerializer.Serialize(blocked, LibraryJson.Options),
            JsonSerializer.Serialize(store.LoadPresets().Single(record => record.Id == blocked.Id), LibraryJson.Options),
            "A skipped preset's reason persists across library reloads.");
        Equal("未选择生成方式", notChosen.LastRenderOutcome, "An unselected generation method receives different wording.");
        Equal(originalCached, JsonSerializer.Serialize(cached, LibraryJson.Options), "Default cache reuse keeps an existing confirmation and all provenance intact.");
        Equal(settingsBefore, JsonSerializer.Serialize(settings, LibraryJson.Options), "Execution cannot persistently downgrade a selected renderer.");
        Equal(savedSettingsBefore, JsonSerializer.Serialize(store.LoadSettings(), LibraryJson.Options), "Saved backend selections and paths remain unchanged.");
        Equal(cachedHash, Fingerprints.HashFile(cached.PreviewPath), "Readiness never rewrites existing audio.");

        var unavailable = new[] { new RenderReadiness(SynthKind.Vital, "环境未就绪", "TEST ONLY private environment file is missing", false) };
        int previousAttempts = cached.Attempts;
        var forced = await queue.RunAsync(settings, [cached], TestExecutable, new RecordingProgress(), CancellationToken.None,
            forceRegenerate: true, readiness: unavailable);
        Equal(1, forced.Unavailable, "Explicit regeneration is blocked by a real readiness issue without erasing the old preview.");
        Equal(0, forced.Unconfigured, "A selected backend is never relabeled as unconfigured because its environment is unavailable.");
        Equal(previousAttempts, cached.Attempts, "An unavailable forced render does not consume attempts.");
        Equal(PresetStatus.Ready, cached.Status, "A blocked replacement preserves the user's previous confirmation status.");
        Equal("TEST ONLY user confirmed this exact preview", cached.Message, "A blocked replacement retains the old confirmation text.");
        Equal(cachedHash, Fingerprints.HashFile(cached.PreviewPath), "A blocked forced replacement preserves the exact old bytes.");

        var transientBlocked = Preset("success");
        var transientUnselected = Preset("success");
        transientUnselected.Synth = SynthKind.Serum1;
        string beforeTransient = JsonSerializer.Serialize(store.LoadPresets(), LibraryJson.Options);
        var transient = await queue.RunAsync(settings, [transientBlocked, transientUnselected], TestExecutable,
            new RecordingProgress(), CancellationToken.None, readiness: unavailable);
        Equal(1, transient.Unavailable, "A transient blocked input still receives an unavailable outcome.");
        Equal(1, transient.Unconfigured, "A transient unselected input still receives a distinct outcome.");
        True(transientBlocked.LastRenderMessage.Contains("环境未就绪", StringComparison.Ordinal) &&
            transientUnselected.LastRenderOutcome == "未选择生成方式", "Transient callers receive the diagnostics in memory.");
        Equal(beforeTransient, JsonSerializer.Serialize(store.LoadPresets(), LibraryJson.Options),
            "Skipped inputs absent from the library never become new saved preset rows.");

        // Legacy/custom protocol callers can omit readiness; Core must not infer an
        // additional plugin scanner or Python requirement for their external worker.
        var independent = Preset("success");
        independent.ContentHash = Fingerprints.HashText("TEST ONLY optional availability contract");
        Equal(1, (await queue.RunAsync(settings, [independent], TestExecutable, new RecordingProgress(), CancellationToken.None)).Generated,
            "Omitting availability retains the existing custom external protocol contract.");

        var rollbackA = Preset("success"); rollbackA.Id = "TEST-ONLY-availability-rollback-A";
        var rollbackB = Preset("success"); rollbackB.Id = "TEST-ONLY-availability-rollback-B";
        store.UpsertMany([rollbackA, rollbackB]);
        string beforeFailure = JsonSerializer.Serialize(store.LoadPresets(), LibraryJson.Options);
        using (var database = new CacheTestSqlite(store.DatabasePath))
            database.Execute("CREATE TRIGGER reject_availability_diagnostic BEFORE INSERT ON presets WHEN NEW.id='TEST-ONLY-availability-rollback-B' BEGIN SELECT RAISE(ABORT,'TEST ONLY availability diagnostic commit failure'); END;");
        await ThrowsAsync<IOException>(() => queue.RunAsync(settings, [rollbackA, rollbackB], TestExecutable, new RecordingProgress(),
            CancellationToken.None, readiness: unavailable));
        Equal(beforeFailure, JsonSerializer.Serialize(store.LoadPresets(), LibraryJson.Options), "Skipped diagnostics are saved as one metadata-safe transaction and roll back together.");
        Equal(cachedHash, Fingerprints.HashFile(cached.PreviewPath), "A diagnostic database failure cannot damage a prior preview.");
    }

    private static PresetRecord CloneAvailabilityRecord(PresetRecord record) =>
        JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(record, LibraryJson.Options), LibraryJson.Options)!;
}
