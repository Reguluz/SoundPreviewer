using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static async Task TestParallelSchedulingAsync()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{\"Plugins\":[{\"Synth\":0,\"Backend\":2}]}", LibraryJson.Options)!;
        Equal(1, old.MaxParallelRenders, "Old settings retain one worker by default.");
        True(old.Plugins.All(plugin => !plugin.AllowParallelRendering), "Old plugin configurations do not implicitly enable concurrent instances.");
        var cachePreset = Preset("parallel-options-cache-identity");
        var cachePlugin = ParallelPlugin(SynthKind.Vital);
        var cacheTemplate = new PreviewTemplate();
        string originalKey = Fingerprints.CacheKey(cachePreset, cachePlugin, cacheTemplate);
        cachePlugin.AllowParallelRendering = true;
        Equal(originalKey, Fingerprints.CacheKey(cachePreset, cachePlugin, cacheTemplate), "Concurrency opt-in changes scheduling without invalidating audio identity.");
        TestParallelPluginSelection();
        foreach (var scenario in new[] { (Global: 1, OptIn: true, Mixed: false, Expected: 1),
                     (Global: 2, OptIn: false, Mixed: false, Expected: 1),
                     (Global: 2, OptIn: true, Mixed: false, Expected: 2),
                     (Global: 2, OptIn: false, Mixed: true, Expected: 2),
                     (Global: 4, OptIn: true, Mixed: false, Expected: 4),
                     (Global: 8, OptIn: true, Mixed: false, Expected: 6),
                     (Global: 16, OptIn: true, Mixed: false, Expected: 6),
                     (Global: 0, OptIn: true, Mixed: false, Expected: 6) })
        {
            int count = Math.Max(4, scenario.Expected + 2);
            var fixture = CreateParallelFixture($"parallel-schedule-{scenario.Global}-{scenario.OptIn}-{scenario.Mixed}", count);
            int target = RenderResourcePolicy.ResolveMaxConcurrency(scenario.Global, new(16, 8));
            fixture.Settings.MaxParallelRenders = scenario.Global;
            fixture.Settings.Plugins[0].AllowParallelRendering = scenario.OptIn;
            if (scenario.Mixed)
            {
                fixture.Settings.Plugins.Add(ParallelPlugin(SynthKind.Serum1));
                fixture.Presets[1].Synth = fixture.Presets[3].Synth = SynthKind.Serum1;
                fixture.Store.UpsertMany(fixture.Presets);
            }
            if (scenario.Expected > 1) ReleaseParallelCalibration(fixture);
            var progress = new ParallelProgress();
            using var cancellation = new CancellationTokenSource();
            var run = new RenderQueue(fixture.Store, new TestRenderResourceProbe(fixture)).RunAsync(fixture.Settings, fixture.Presets, TestExecutable, progress, cancellation.Token);
            try
            {
                await WaitForParallelActiveAsync(fixture, scenario.Expected, run);
                await Task.Delay(200);
                Equal(scenario.Expected, ParallelActive(fixture).Count, "After one serial calibration, held real children obey both global and per-synth limits.");
                int calibrationCount = scenario.Expected > 1 ? fixture.Presets.Select(preset => preset.Synth).Distinct().Count() : 0;
                Equal(scenario.Expected + calibrationCount, ParallelStarts(fixture).Count, "Each synth calibrates alone before the CPU and reserved-memory limits open more slots.");
                var held = progress.Values[^1];
                Equal(scenario.Expected, held.EffectiveConcurrency, "The running denominator combines memory admission, per-synth permission and remaining independent jobs.");
                Equal(scenario.Expected, held.ActiveCount, "Held workers occupy the effective slots advertised in progress.");
                if (!scenario.OptIn && !scenario.Mixed)
                    True(held.ConcurrencyMessage.Contains("未启用同音源多实例并行", StringComparison.Ordinal),
                        "A serial synth explains its permission limit even when the configured target is higher.");
                if (scenario.Expected < target && scenario.OptIn)
                    True(held.ConcurrencyMessage.Contains("2 GiB", StringComparison.Ordinal), "Memory admission explains why an opted-in synth cannot fill the configured CPU target.");
                var originalTemplate = JsonSerializer.Serialize(fixture.Settings.Template, LibraryJson.Options);
                if (scenario.Expected == 2 && !scenario.Mixed)
                {
                    // Changes made while children are held cannot alter later work in this batch.
                    fixture.Settings.Template.Velocity++;
                    fixture.Settings.Template.HoldSeconds = .2;
                    fixture.Settings.Plugins[0].Version = "TEST ONLY changed while running";
                    fixture.Settings.MaxParallelRenders = 1;
                }
                ReleaseParallel(fixture);
                var result = await run;
                Equal(count, result.Generated, "Every independent key completes exactly once.");
                Equal(scenario.Expected, ParallelPeak(fixture), "Timestamped child lifetimes prove actual concurrency independently of progress counters.");
                var completed = ParallelFinishes(fixture).OrderBy(trace => trace.StartedTicks).ToArray();
                for (int index = 0; index < calibrationCount; index++)
                    True(completed[index].FinishedTicks <= completed[index + 1].StartedTicks, "Every unmeasured synth finishes an isolated calibration before any subsequent worker starts.");
                Equal(scenario.Expected, result.PeakActiveCount, "The result exposes the observed active-worker peak.");
                True(result.ElapsedSeconds > 0 && double.IsFinite(result.ElapsedSeconds), "A completed batch reports finite elapsed time.");
                Equal(scenario.Global, result.RequestedConcurrency, "The result retains the explicit or automatic selection.");
                Equal(target, result.ResolvedConcurrency, "The CPU target remains distinguishable from dynamic memory admission.");
                True(progress.Values.All(value => value.ActiveCount >= 0 && value.ActiveCount <= target && value.MaxConcurrency == target), "Progress never exceeds the CPU target even when memory reduces actual admission.");
                True(progress.Values.All(value => value.EffectiveConcurrency >= value.ActiveCount && value.EffectiveConcurrency <= target),
                    "The effective running denominator never contradicts active workers or the configured ceiling.");
                True(progress.Values.All(value => value.Completed == value.Succeeded + value.Failed + value.Unconfigured + value.RetryLimited),
                    "Live outcome counters account for all completed work independently of record status.");
                Equal(count, progress.Values[^1].Succeeded, "Final progress exposes committed and reused successes.");
                Equal(0, progress.Values[^1].Failed, "Successful batches never infer failures from unconfirmed preview status.");
                Equal(0, progress.Values[^1].ActiveCount, "Terminal progress has no active worker.");
                Equal(0, progress.Values[^1].EffectiveConcurrency, "A finished batch has no available running slots.");
                foreach (var preset in fixture.Presets)
                {
                    Equal(originalTemplate, preset.TemplateJson, "All scheduled items use the template captured at batch start.");
                    Equal("TEST ONLY parallel fixture", preset.PluginVersion, "All scheduled items use the captured plugin configuration.");
                    True(File.Exists(preset.PreviewPath), "Each committed mapping refers to validated audio.");
                }
            }
            finally { await StopParallelFixtureAsync(fixture, run, cancellation); }
        }
        var invalid = CreateParallelFixture("parallel-invalid-setting", 1);
        foreach (int limit in new[] { -1, 3 })
        {
            invalid.Settings.MaxParallelRenders = limit;
            await ThrowsAsync<ArgumentException>(() => new RenderQueue(invalid.Store, new TestRenderResourceProbe(invalid)).RunAsync(invalid.Settings, invalid.Presets, TestExecutable, new ParallelProgress(), CancellationToken.None));
        }
        await TestLiveRenderOutcomeCountersAsync();
    }

    private static async Task TestLiveRenderOutcomeCountersAsync()
    {
        var fixture = CreateParallelFixture("parallel-live-outcomes", 3);
        ReleaseParallel(fixture);
        var queue = new RenderQueue(fixture.Store, new TestRenderResourceProbe(fixture));
        var first = await queue.RunAsync(fixture.Settings, fixture.Presets, TestExecutable, new ParallelProgress(), CancellationToken.None);
        Equal(3, first.Generated, "The counter fixture has three committed previews to protect.");
        var failed = fixture.Presets[1];
        string oldPath = failed.PreviewPath;
        byte[] oldAudio = File.ReadAllBytes(oldPath);
        failed.Name = "parallel-crash";
        var failureControl = JsonSerializer.Deserialize<ParallelChildControl>(File.ReadAllText(failed.SourcePath), LibraryJson.Options)!;
        failureControl.DelayMilliseconds = 10;
        File.WriteAllText(failed.SourcePath, JsonSerializer.Serialize(failureControl, LibraryJson.Options));
        var forcedProgress = new ParallelProgress();
        var forced = await queue.RunAsync(fixture.Settings, fixture.Presets, TestExecutable, forcedProgress, CancellationToken.None, forceRegenerate: true);
        Equal(2, forced.Generated, "Successful forced replacements commit separately from a failed child.");
        Equal(1, forced.Failed, "The failed replacement counts once even though an old usable preview remains.");
        True(failed.Status is PresetStatus.Ready or PresetStatus.NeedsReview, "A failed replacement preserves the previous usable record status.");
        Equal(oldPath, failed.PreviewPath, "A failed replacement retains the previous path.");
        True(File.ReadAllBytes(oldPath).SequenceEqual(oldAudio), "The failed replacement preserves the previous audio bytes.");
        var final = forcedProgress.Values[^1];
        Equal(2, final.Succeeded, "The live success counter does not mistake preserved old audio for a newly successful render.");
        Equal(1, final.Failed, "The live failure counter matches the final batch result.");
        True(forcedProgress.Values.Any(value => value.Completed < value.Total && value.Failed == 1), "Failure counts become visible before all queued tasks finish.");
        True(forcedProgress.Values.All(value => value.Completed == value.Succeeded + value.Failed + value.Unconfigured + value.RetryLimited),
            "Every live snapshot separates successful, failed and skipped outcomes.");
        var reuseProgress = new ParallelProgress();
        var reused = await queue.RunAsync(fixture.Settings, fixture.Presets, TestExecutable, reuseProgress, CancellationToken.None);
        Equal(3, reused.Reused, "The next batch reuses all three preserved previews.");
        Equal(3, reuseProgress.Values[^1].Reused, "Reused audio is explicitly available in progress.");
        Equal(3, reuseProgress.Values[^1].Succeeded, "Success includes valid reused previews.");
        Equal(0, reuseProgress.Values[^1].Failed, "A fresh batch resets the previous failure counter.");
    }

    private static async Task TestParallelSharedCacheAsync()
    {
        var fixture = CreateParallelFixture("parallel-shared-cache", 2);
        fixture.Presets[1].ContentHash = fixture.Presets[0].ContentHash;
        fixture.Presets[0].Tags = ["first personal tag"];
        fixture.Presets[1].Tags = ["second personal tag"];
        fixture.Store.UpsertMany(fixture.Presets);
        ReleaseParallel(fixture);
        var queue = new RenderQueue(fixture.Store, new TestRenderResourceProbe(fixture));
        var initial = await queue.RunAsync(fixture.Settings, fixture.Presets, TestExecutable, new ParallelProgress(), CancellationToken.None);
        Equal(1, initial.Generated, "Two source records with one cache key launch one rendering operation.");
        Equal(1, initial.Reused, "The second source record consumes the single-flight result.");
        Equal(1, ParallelStarts(fixture).Count, "The shared cache is not overwritten by a second child.");
        Equal(fixture.Presets[0].PreviewPath, fixture.Presets[1].PreviewPath, "Both records map to the same committed audio.");
        Equal(JsonSerializer.Serialize(fixture.Presets[0].Notes, LibraryJson.Options), JsonSerializer.Serialize(fixture.Presets[1].Notes, LibraryJson.Options), "Single-flight consumers receive the complete note mapping.");
        var oldPath = fixture.Presets[0].PreviewPath;
        var retained = JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(fixture.Presets[0], LibraryJson.Options), LibraryJson.Options)!;
        retained.Id = Guid.NewGuid().ToString("N");
        retained.Name = "TEST ONLY outside requested batch";
        fixture.Store.Upsert(retained);
        fixture.Settings.Template.Velocity++;
        File.Delete(fixture.GatePath);
        using var cancellation = new CancellationTokenSource();
        var run = queue.RunAsync(fixture.Settings, fixture.Presets, TestExecutable, new ParallelProgress(), cancellation.Token, forceRegenerate: true);
        try
        {
            await WaitForParallelStartsAsync(fixture, 2, run);
            var latest = fixture.Store.LoadPresets().Single(preset => preset.Id == fixture.Presets[0].Id);
            latest.Favorite = true;
            latest.Feedback = -1;
            latest.Tags = ["edited during rendering"];
            latest.CategoryTags = ["edited category"];
            latest.ManualTags = ["edited during rendering"];
            latest.ManualCategories = ["edited category"];
            fixture.Store.Upsert(latest);
            ReleaseParallel(fixture);
            var replaced = await run;
            Equal(1, replaced.Generated, "Forced shared-cache regeneration also launches only one child.");
            Equal(1, replaced.Reused, "A forced consumer shares the newly committed result.");
            Equal(2, ParallelStarts(fixture).Count, "Initial generation plus forced replacement make exactly two child processes.");
            True(File.Exists(oldPath), "A shared old cache referenced outside the batch cannot be reclaimed.");
            NotEqual(oldPath, fixture.Presets[0].PreviewPath, "A changed template commits a distinct shared cache key.");
            var stored = fixture.Store.LoadPresets();
            var edited = stored.Single(preset => preset.Id == latest.Id);
            True(edited.Favorite && edited.Feedback == -1 && edited.Tags.SequenceEqual(latest.Tags) && edited.CategoryTags.SequenceEqual(latest.CategoryTags), "Completion merges the latest persisted personal metadata instead of overwriting it with its starting snapshot.");
            True(edited.ManualTags!.SequenceEqual(latest.ManualTags!) && edited.ManualCategories!.SequenceEqual(latest.ManualCategories!), "Explicit manual label overrides survive completion.");
            True(stored.Single(preset => preset.Id == fixture.Presets[1].Id).Tags.SequenceEqual(["second personal tag"]), "Single-flight consumers retain their independent personal tags.");
            Equal(oldPath, stored.Single(preset => preset.Id == retained.Id).PreviewPath, "A record outside the batch retains its existing mapping.");
        }
        finally { await StopParallelFixtureAsync(fixture, run, cancellation); }
        await TestParallelOutsideSharedMetadataAsync();
        await TestParallelExistingAliasAsync();
    }

    private static async Task TestParallelExistingAliasAsync()
    {
        var fixture = CreateParallelFixture("parallel-default-existing-alias", 1);
        ReleaseParallel(fixture);
        var queue = new RenderQueue(fixture.Store, new TestRenderResourceProbe(fixture));
        var existing = fixture.Presets.Single();
        await queue.RunAsync(fixture.Settings, fixture.Presets, TestExecutable, new ParallelProgress(), CancellationToken.None);
        existing.Favorite = true;
        existing.Feedback = -1;
        existing.Tags = ["existing source personal tag"];
        existing.LastRenderOutcome = "TEST ONLY earlier source outcome";
        existing.LastRenderMessage = "TEST ONLY earlier source diagnostic";
        existing.Message = "TEST ONLY earlier review explanation";
        int previousAttempts = existing.Attempts;
        string previousAttemptedKey = existing.AttemptedCacheKey;
        string previousOutcome = existing.LastRenderOutcome, previousDiagnostic = existing.LastRenderMessage, previousMessage = existing.Message;
        double oldRms = existing.Metrics!.Rms;
        SetParallelOutput(fixture, .25, "TEST ONLY regenerated missing shared mapping");
        var pending = Preset("parallel-render");
        pending.ContentHash = existing.ContentHash;
        pending.SourcePath = Path.Combine(fixture.Root, "TEST-ONLY-second-source.json");
        pending.Tags = ["new source personal tag"];
        File.Copy(existing.SourcePath, pending.SourcePath);
        fixture.Store.UpsertMany([existing, pending]);
        var result = await queue.RunAsync(fixture.Settings, [existing, pending], TestExecutable, new ParallelProgress(), CancellationToken.None);
        Equal(1, result.Reused, "Default generation reuses the source that already has a valid preview.");
        Equal(1, result.Generated, "The same-key source missing its mapping still performs its scheduled generation.");
        Equal(2, ParallelStarts(fixture).Count, "Initial generation plus the unmapped source create exactly two children.");
        Equal(existing.PreviewPath, pending.PreviewPath, "The existing and newly mapped records share the committed canonical audio.");
        var stored = fixture.Store.LoadPresets().Single(preset => preset.Id == existing.Id);
        True(stored.Favorite && stored.Feedback == -1 && stored.Tags.SequenceEqual(["existing source personal tag"]), "Default shared-cache publication retains the existing source's personal metadata.");
        Equal(previousAttempts, stored.Attempts, "Reusing a record does not inherit the newly generated sibling's attempt history.");
        Equal(previousAttemptedKey, stored.AttemptedCacheKey, "A reused record preserves its own attempted identity.");
        Equal(previousOutcome, stored.LastRenderOutcome, "A reused record retains its own last-render outcome.");
        Equal(previousDiagnostic, stored.LastRenderMessage, "A reused record retains its own last-render diagnostic.");
        Equal(previousMessage, stored.Message, "A reused record retains its own review explanation.");
        Equal(previousOutcome, existing.LastRenderOutcome, "The reused in-memory record retains its own outcome too.");
        Equal(previousAttempts, existing.Attempts, "The reused in-memory record retains its own attempt count too.");
        True(stored.Metrics!.Rms < oldRms * .75, "The newly written shared WAV differs enough to require refreshed metrics.");
        Equal(JsonSerializer.Serialize(pending.Metrics, LibraryJson.Options), JsonSerializer.Serialize(stored.Metrics, LibraryJson.Options), "A reused record's persisted aggregate metrics track the replaced shared WAV.");
        Equal(JsonSerializer.Serialize(pending.Notes, LibraryJson.Options), JsonSerializer.Serialize(stored.Notes, LibraryJson.Options), "A reused record's persisted note metrics track the replaced shared WAV.");
        Equal(JsonSerializer.Serialize(pending.Metrics, LibraryJson.Options), JsonSerializer.Serialize(existing.Metrics, LibraryJson.Options), "The reused in-memory record presented by the UI also receives the new shared audio metrics.");
        Equal(JsonSerializer.Serialize(pending.Notes, LibraryJson.Options), JsonSerializer.Serialize(existing.Notes, LibraryJson.Options), "The reused in-memory note mapping also matches the replaced shared audio.");
    }

    private static async Task TestParallelOutsideSharedMetadataAsync()
    {
        var fixture = CreateParallelFixture("parallel-outside-same-key-metadata", 1);
        ReleaseParallel(fixture);
        var queue = new RenderQueue(fixture.Store, new TestRenderResourceProbe(fixture));
        await queue.RunAsync(fixture.Settings, fixture.Presets, TestExecutable, new ParallelProgress(), CancellationToken.None);
        var main = fixture.Presets.Single();
        var outside = JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(main, LibraryJson.Options), LibraryJson.Options)!;
        outside.Id = Guid.NewGuid().ToString("N");
        outside.SourcePath = Path.Combine(fixture.Root, "TEST-ONLY-missing-source.vital");
        outside.Status = PresetStatus.Missing;
        outside.Attempts = 4;
        outside.AttemptedCacheKey = "TEST ONLY previous attempted key";
        outside.LastRenderOutcome = "TEST ONLY previous missing-source outcome";
        outside.LastRenderMessage = "TEST ONLY previous missing-source diagnostic";
        outside.Message = "TEST ONLY original source is missing";
        outside.Favorite = true;
        outside.Feedback = -1;
        outside.Tags = ["outside personal tag"];
        outside.CategoryTags = ["outside personal category"];
        outside.ManualTags = ["outside personal tag"];
        outside.ManualCategories = ["outside personal category"];
        fixture.Store.Upsert(outside);
        double oldRms = outside.Metrics!.Rms;
        SetParallelOutput(fixture, .25, "TEST ONLY changed shared audio");
        var result = await queue.RunAsync(fixture.Settings, fixture.Presets, TestExecutable, new ParallelProgress(), CancellationToken.None, forceRegenerate: true);
        Equal(1, result.Generated, "A forced same-key replacement renders only the requested source.");
        Equal(2, ParallelStarts(fixture).Count, "An outside source sharing the cache never starts its own child.");
        var updated = fixture.Store.LoadPresets().Single(preset => preset.Id == outside.Id);
        Equal(PresetStatus.Missing, updated.Status, "Updating shared audio cannot resurrect a missing outside source.");
        Equal(outside.Attempts, updated.Attempts, "Audio synchronization preserves the outside source's attempt history.");
        Equal(outside.AttemptedCacheKey, updated.AttemptedCacheKey, "Audio synchronization preserves the outside source's last attempted identity.");
        Equal(outside.LastRenderOutcome, updated.LastRenderOutcome, "Audio synchronization does not claim the outside source was rendered successfully.");
        Equal(outside.LastRenderMessage, updated.LastRenderMessage, "The outside source retains its own last-render diagnostic.");
        Equal(outside.Message, updated.Message, "The outside source retains its missing-source explanation.");
        True(updated.Favorite && updated.Feedback == -1 && updated.Tags.SequenceEqual(outside.Tags) && updated.CategoryTags.SequenceEqual(outside.CategoryTags), "An outside shared-cache mapping retains personal metadata.");
        True(updated.ManualTags!.SequenceEqual(outside.ManualTags!) && updated.ManualCategories!.SequenceEqual(outside.ManualCategories!), "Outside manual metadata overrides survive audio synchronization.");
        Equal(main.PreviewPath, updated.PreviewPath, "The outside source still maps to the shared canonical WAV.");
        True(updated.Metrics!.Rms < oldRms * .75, "The fixture changed the shared audio substantially enough to require refreshed metrics.");
        Equal(JsonSerializer.Serialize(main.Metrics, LibraryJson.Options), JsonSerializer.Serialize(updated.Metrics, LibraryJson.Options), "Outside aggregate metrics describe the newly committed shared audio.");
        Equal(JsonSerializer.Serialize(main.Notes, LibraryJson.Options), JsonSerializer.Serialize(updated.Notes, LibraryJson.Options), "Outside note metrics and provenance also describe the newly committed shared audio.");
    }

    private static async Task TestParallelCancellationAndFailureAsync()
    {
        var fixture = CreateParallelFixture("parallel-cancel-all", 5);
        ReleaseParallel(fixture);
        var queue = new RenderQueue(fixture.Store, new TestRenderResourceProbe(fixture));
        await queue.RunAsync(fixture.Settings, fixture.Presets.Take(3).ToArray(), TestExecutable, new ParallelProgress(), CancellationToken.None);
        var old = fixture.Presets.Take(3).ToDictionary(preset => preset.Id, preset => (preset.PreviewPath, Hash: Fingerprints.HashFile(preset.PreviewPath), preset.Attempts));
        int previousStarts = ParallelStarts(fixture).Count;
        File.Delete(fixture.GatePath);
        ReleaseParallelCalibration(fixture);
        fixture.Settings.Template.Velocity++;
        var progress = new ParallelProgress();
        using var cancellation = new CancellationTokenSource();
        var run = queue.RunAsync(fixture.Settings, fixture.Presets, TestExecutable, progress, cancellation.Token, forceRegenerate: true);
        try
        {
            await WaitForParallelActiveAsync(fixture, 2, run);
            var active = ParallelActive(fixture).Select(trace => trace.Pid).ToArray();
            var committedFirst = fixture.Store.LoadPresets().Single(preset => preset.Id == fixture.Presets[0].Id);
            string committedHash = Fingerprints.HashFile(committedFirst.PreviewPath);
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => run);
            True(active.All(pid => !IsRunning(pid)), "Cancellation drains every active child before returning.");
            Equal(previousStarts + 3, ParallelStarts(fixture).Count, "Only the completed calibration and two active children start before cancellation.");
            var stored = fixture.Store.LoadPresets();
            foreach (var preset in fixture.Presets)
            {
                var durable = stored.Single(item => item.Id == preset.Id);
                True(durable.Status != PresetStatus.Rendering, "No durable record remains rendering after cancellation.");
                if (preset.Id == committedFirst.Id)
                {
                    AssertParallelAudioMapping(committedFirst, durable, "Canceling siblings keeps the already committed calibration result.");
                    Equal(committedHash, Fingerprints.HashFile(durable.PreviewPath), "Stopping later work cannot roll back already committed audio.");
                }
                else if (old.TryGetValue(preset.Id, out var previous))
                {
                    Equal(previous.PreviewPath, durable.PreviewPath, "An interrupted forced item retains its previous valid mapping.");
                    Equal(previous.Hash, Fingerprints.HashFile(durable.PreviewPath), "An interrupted forced item retains byte-identical committed audio.");
                    Equal(previous.Attempts, durable.Attempts, "A canceled attempt does not consume retry allowance.");
                }
                else
                {
                    Equal(PresetStatus.Pending, durable.Status, "Unstarted records remain resumable.");
                    Equal(0, durable.Attempts, "Queued cancellation does not count as an attempt.");
                }
            }
            True(!Directory.EnumerateDirectories(fixture.Settings.PreviewDirectory, ".soundpreviewer-*").Any(), "All canceled staging directories are removed.");
            Equal(0, progress.Values[^1].ActiveCount, "Stopped progress clears every active slot.");
        }
        finally { await StopParallelFixtureAsync(fixture, run, cancellation); }
        var resumed = await queue.RunAsync(fixture.Settings, fixture.Presets, TestExecutable, new ParallelProgress(), CancellationToken.None);
        Equal(3, resumed.Reused, "Resume reuses the completed calibration and both preserved interrupted previews.");
        Equal(2, resumed.Generated, "Resume renders only previously queued records.");

        var failed = CreateParallelFixture("parallel-crash-isolation", 3);
        failed.Presets[0].Name = "parallel-crash";
        failed.Store.UpsertMany(failed.Presets);
        ReleaseParallel(failed);
        var result = await new RenderQueue(failed.Store, new TestRenderResourceProbe(failed)).RunAsync(failed.Settings, failed.Presets, TestExecutable, new ParallelProgress(), CancellationToken.None);
        Equal(1, result.Failed, "A child exiting without a result fails only its own item.");
        Equal(2, result.Generated, "Another active child and later queued work still complete after one crash.");
        True(failed.Presets.Skip(1).All(preset => File.Exists(preset.PreviewPath)), "Successful siblings retain valid committed outputs.");
        True(!Directory.EnumerateDirectories(failed.Settings.PreviewDirectory, ".soundpreviewer-*").Any(), "Crash isolation still removes failed staging directories.");
    }

    private static async Task TestParallelBudgetAndBaselineAsync()
    {
        var budget = CreateParallelFixture("parallel-budget-reservation", 3);
        double estimated = (budget.Settings.Template.HoldSeconds + budget.Settings.Template.TailSeconds) * 3 * budget.Settings.Template.SampleRate * 8 + 4096;
        budget.Settings.StorageBudgetGb = (estimated + 1024) / (1024 * 1024 * 1024.0);
        ReleaseParallel(budget);
        try { await new RenderQueue(budget.Store, new TestRenderResourceProbe(budget)).RunAsync(budget.Settings, budget.Presets, TestExecutable, new ParallelProgress(), CancellationToken.None); }
        catch (IOException error) { True(error.Message.Contains("预算", StringComparison.Ordinal), "Exhausting the shared output budget leaves an actionable reason."); }
        True(budget.Presets.Any(preset => File.Exists(preset.PreviewPath)), "A budget fitting one preview still permits useful work before reporting exhaustion.");
        True(ParallelPeak(budget) <= 1, "Two independent jobs cannot both reserve the same remaining output budget.");
        long bytes = Directory.EnumerateFiles(budget.Settings.PreviewDirectory, "*.wav").Sum(path => new FileInfo(path).Length);
        True(bytes <= budget.Settings.StorageBudgetGb * 1024 * 1024 * 1024, "Committed outputs never exceed the shared cache budget.");
        var one = budget.Presets.First(preset => File.Exists(preset.PreviewPath));
        int before = ParallelStarts(budget).Count;
        var samePath = one.PreviewPath;
        var replacement = await new RenderQueue(budget.Store, new TestRenderResourceProbe(budget)).RunAsync(budget.Settings, [one], TestExecutable, new ParallelProgress(), CancellationToken.None, forceRegenerate: true);
        Equal(1, replacement.Generated, "Replacing the same cache path grants its old byte credit once rather than charging both copies to final storage.");
        Equal(before + 1, ParallelStarts(budget).Count, "A same-key forced replacement actually reaches the child.");
        Equal(samePath, one.PreviewPath, "Same-key force commits back to the same owned path.");

        var baseline = CreateParallelFixture("parallel-baseline-single-flight", 2);
        foreach (var preset in baseline.Presets) preset.Synth = SynthKind.Serum1;
        var plugin = ParallelPlugin(SynthKind.Serum1);
        plugin.AllowParallelRendering = true;
        plugin.Backend = RenderBackend.DawDreamer;
        plugin.PluginPath = Path.Combine(baseline.Root, "TEST-ONLY-never-loaded.dll");
        File.WriteAllText(plugin.PluginPath, "TEST ONLY placeholder, never loaded.");
        File.WriteAllText(plugin.PluginPath + ".parallel.json", JsonSerializer.Serialize(new ParallelChildControl
        {
            TraceDirectory = baseline.TraceDirectory, DelayMilliseconds = 50
        }, LibraryJson.Options));
        baseline.Settings.Plugins = [plugin];
        baseline.Settings.Template.HoldSeconds = .5;
        baseline.Settings.Template.TailSeconds = 0;
        baseline.Store.SaveSettings(baseline.Settings);
        baseline.Store.UpsertMany(baseline.Presets);
        ReleaseParallel(baseline);
        var generated = await new RenderQueue(baseline.Store, new TestRenderResourceProbe(baseline)).RunAsync(baseline.Settings, baseline.Presets, TestExecutable, new ParallelProgress(), CancellationToken.None);
        Equal(2, generated.Generated, "Both normal presets use the prepared startup protection.");
        Equal(3, ParallelStarts(baseline).Count(trace => trace.IsBaseline), "One synth prepares one baseline with three reference renders even when two jobs require it.");
        Equal(2, ParallelStarts(baseline).Count(trace => !trace.IsBaseline), "Baseline preparation does not replace or duplicate normal preset work.");
        string locator = baseline.Store.LoadSettings().Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).InitBaselinePath;
        True(File.Exists(locator) && (await InitBaselineService.InspectAsync(baseline.Settings, baseline.Settings.Plugins.Single())).CanCompare, "The single prepared baseline is persisted and remains verifiable.");
        baseline.Settings.MaxParallelRenders = 1;
        baseline.Settings.Plugins.Single().AllowParallelRendering = false;
        True((await InitBaselineService.InspectAsync(baseline.Settings, baseline.Settings.Plugins.Single())).CanCompare, "Changing scheduling options preserves the existing baseline identity.");
    }

    private static async Task TestParallelCommitRollbackAsync()
    {
        const string replacementMarker = "TEST ONLY injected replacement";
        foreach (bool replaceExisting in new[] { false, true })
        {
            var fixture = CreateParallelFixture("parallel-database-rollback-" + replaceExisting, 2);
            fixture.Presets[1].ContentHash = fixture.Presets[0].ContentHash;
            fixture.Store.UpsertMany(fixture.Presets);
            ReleaseParallel(fixture);
            var queue = new RenderQueue(fixture.Store, new TestRenderResourceProbe(fixture));
            if (replaceExisting)
                await queue.RunAsync(fixture.Settings, fixture.Presets, TestExecutable, new ParallelProgress(), CancellationToken.None);
            var before = fixture.Store.LoadPresets().ToDictionary(preset => preset.Id);
            string output = Path.Combine(fixture.Settings.PreviewDirectory,
                Fingerprints.CacheKey(fixture.Presets[0], fixture.Settings.Plugins.Single(), fixture.Settings.Template) + ".wav");
            byte[]? oldBytes = File.Exists(output) ? File.ReadAllBytes(output) : null;
            SetParallelOutput(fixture, .25, replacementMarker);
            string successField = "\"LastRenderOutcome\": " + JsonSerializer.Serialize("成功", LibraryJson.Options);
            using (var database = new CacheTestSqlite(fixture.Store.DatabasePath))
                database.Execute("CREATE TRIGGER reject_parallel_completion BEFORE INSERT ON presets WHEN NEW.id='" + fixture.Presets[1].Id +
                    "' AND NEW.status IN (2,4) AND instr(NEW.json,'" + successField.Replace("'", "''", StringComparison.Ordinal) +
                    "')>0 AND instr(NEW.json,'" + replacementMarker + "')>0 BEGIN SELECT RAISE(ABORT,'TEST_ONLY_DB_COMMIT_FAILURE'); END;");
            await ThrowsAsync<IOException>(() => queue.RunAsync(fixture.Settings, fixture.Presets, TestExecutable,
                new ParallelProgress(), CancellationToken.None, forceRegenerate: replaceExisting));
            var stored = fixture.Store.LoadPresets();
            foreach (var preset in fixture.Presets)
            {
                var durable = stored.Single(record => record.Id == preset.Id);
                True(durable.Status != PresetStatus.Rendering, "A failed shared-key transaction restores every started member from Rendering.");
                AssertParallelAudioMapping(before[preset.Id], durable, "A failure on the second member must roll back the first member's audio mapping too.");
                True(durable.LastRenderOutcome != "成功", "A failed shared transaction cannot report one of its members as successfully committed.");
            }
            if (oldBytes is null)
                True(!File.Exists(output), "A database failure removes a newly published cache with no previous committed audio.");
            else
            {
                True(File.ReadAllBytes(output).SequenceEqual(oldBytes), "Same-key forced replacement restores the exact old WAV bytes after database failure.");
                NotEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(oldBytes)).ToLowerInvariant(),
                    ParallelFinishes(fixture).OrderBy(trace => trace.StartedTicks).Last().AudioSha256,
                    "The injected replacement really produced different WAV bytes, so rollback is not a vacuous same-output check.");
            }
            AssertParallelNoStaging(fixture);
        }

        var conflict = CreateParallelFixture("parallel-source-conflict", 2);
        conflict.Presets[1].ContentHash = conflict.Presets[0].ContentHash;
        conflict.Store.UpsertMany(conflict.Presets);
        ReleaseParallel(conflict);
        var conflictQueue = new RenderQueue(conflict.Store, new TestRenderResourceProbe(conflict));
        await conflictQueue.RunAsync(conflict.Settings, conflict.Presets, TestExecutable, new ParallelProgress(), CancellationToken.None);
        var previousFirst = conflict.Store.LoadPresets().Single(preset => preset.Id == conflict.Presets[0].Id);
        byte[] previousAudio = File.ReadAllBytes(previousFirst.PreviewPath);
        SetParallelOutput(conflict, .25, replacementMarker);
        int previousStarts = ParallelStarts(conflict).Count;
        File.Delete(conflict.GatePath);
        using var cancellation = new CancellationTokenSource();
        var run = conflictQueue.RunAsync(conflict.Settings, conflict.Presets, TestExecutable, new ParallelProgress(), cancellation.Token, forceRegenerate: true);
        try
        {
            await WaitForParallelStartsAsync(conflict, previousStarts + 1, run);
            var latest = conflict.Store.LoadPresets().Single(preset => preset.Id == conflict.Presets[1].Id);
            latest.ContentHash = Fingerprints.HashText("TEST ONLY changed source while an old task was rendering");
            latest.SourcePath = Path.Combine(conflict.Root, "TEST-ONLY-new-source.vital");
            File.WriteAllText(latest.SourcePath, "TEST ONLY newer source identity, never loaded by a plugin.");
            latest.Favorite = true;
            latest.Tags = ["newer source personal metadata"];
            CompletePreview(latest, Path.Combine(conflict.Root, "TEST-ONLY-new-source-preview.wav"), conflict.Settings.Template);
            conflict.Store.Upsert(latest);
            string latestJson = JsonSerializer.Serialize(conflict.Store.LoadPresets().Single(preset => preset.Id == latest.Id), LibraryJson.Options);
            ReleaseParallel(conflict);
            await ThrowsAsync<InvalidOperationException>(() => run);
            var stored = conflict.Store.LoadPresets();
            Equal(latestJson, JsonSerializer.Serialize(stored.Single(preset => preset.Id == latest.Id), LibraryJson.Options),
                "An obsolete render and its failure cleanup cannot overwrite the latest changed source, preview or personal metadata.");
            var restoredFirst = stored.Single(preset => preset.Id == previousFirst.Id);
            AssertParallelAudioMapping(previousFirst, restoredFirst, "A conflicting second member prevents partial publication to the first member.");
            True(restoredFirst.Status != PresetStatus.Rendering, "A source conflict also restores the unaffected started member.");
            True(File.ReadAllBytes(previousFirst.PreviewPath).SequenceEqual(previousAudio), "The old shared WAV bytes survive a changed-source conflict.");
            True(File.Exists(latest.PreviewPath), "Rollback never removes the changed source's separate newer preview.");
            AssertParallelNoStaging(conflict);
        }
        finally
        {
            ReleaseParallel(conflict);
            cancellation.Cancel();
            try { await run; }
            catch (Exception error) when (error is OperationCanceledException or InvalidOperationException) { }
        }
    }

    private static void SetParallelOutput(ParallelFixture fixture, double gain, string marker)
    {
        foreach (string path in fixture.Presets.Select(preset => preset.SourcePath).Distinct(StringComparer.Ordinal))
        {
            var control = JsonSerializer.Deserialize<ParallelChildControl>(File.ReadAllText(path), LibraryJson.Options)!;
            control.OutputGain = gain;
            control.ResultMarker = marker;
            File.WriteAllText(path, JsonSerializer.Serialize(control, LibraryJson.Options));
        }
    }

    private static void AssertParallelAudioMapping(PresetRecord expected, PresetRecord actual, string reason)
    {
        Equal(expected.PreviewPath, actual.PreviewPath, reason);
        Equal(expected.CacheKey, actual.CacheKey, reason);
        Equal(expected.TemplateJson, actual.TemplateJson, reason);
        Equal(expected.RenderedUtc, actual.RenderedUtc, reason);
        Equal(JsonSerializer.Serialize(expected.Notes, LibraryJson.Options), JsonSerializer.Serialize(actual.Notes, LibraryJson.Options), reason);
        Equal(JsonSerializer.Serialize(expected.Metrics, LibraryJson.Options), JsonSerializer.Serialize(actual.Metrics, LibraryJson.Options), reason);
    }

    private static void AssertParallelNoStaging(ParallelFixture fixture)
    {
        True(!Directory.EnumerateDirectories(fixture.Settings.PreviewDirectory, ".soundpreviewer-*").Any(), "Failed publication leaves no live worker staging directory.");
        True(!Directory.EnumerateFiles(fixture.Settings.PreviewDirectory, ".sp-*.wav").Any(), "Failed publication cleans completed scratch WAVs and consumed rollback backups.");
    }

    private static void TestParallelPluginSelection()
    {
        string directory = Fixture("parallel-selection-reset"), binary = Path.Combine(directory, "TEST-ONLY-Vital.exe");
        File.WriteAllText(binary, "TEST ONLY placeholder; selection does not execute it.");
        var selected = new DiscoveredPlugin
        {
            Synth = SynthKind.Vital, FilePath = binary, BinaryPath = binary, Format = PluginBinaryFormat.Standalone,
            Platform = PluginPlatform.Windows, Architecture = PluginArchitecture.X64, Version = "TEST ONLY 1"
        };
        var plugin = new PluginConfiguration
        {
            Synth = SynthKind.Vital, Backend = RenderBackend.VitalCli, ExecutablePath = binary,
            Version = selected.Version, AllowParallelRendering = true
        };
        PluginSelectionService.Assign(plugin, selected, platform: PluginPlatform.Windows, architecture: PluginArchitecture.X64);
        True(plugin.AllowParallelRendering, "Reselecting an identical executable and version retains its explicit concurrency choice.");
        selected.Version = "TEST ONLY 2";
        PluginSelectionService.Assign(plugin, selected, platform: PluginPlatform.Windows, architecture: PluginArchitecture.X64);
        True(!plugin.AllowParallelRendering, "Changing the selected plugin version requires a fresh concurrency opt-in.");
        string host = Path.Combine(directory, "new", "Vst2Host-x86", "SoundPreviewer.Vst2Host.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllText(host, "TEST ONLY replacement host, never executed.");
        var sylenth = new PluginConfiguration
        {
            Synth = SynthKind.Sylenth1, Backend = RenderBackend.ExternalWorker, AllowParallelRendering = true,
            ExecutablePath = Path.Combine(directory, "old", "Vst2Host-x86", "SoundPreviewer.Vst2Host.exe")
        };
        Equal(1, PluginSelectionService.RefreshBundledHost([sylenth], host, PluginPlatform.Windows), "A bundled host upgrade refreshes its installation path.");
        True(!sylenth.AllowParallelRendering, "A replaced bundled host also requires a fresh concurrency opt-in.");
    }

    private sealed record ParallelFixture(string Root, string TraceDirectory, string GatePath, LibraryStore Store, AppSettings Settings, PresetRecord[] Presets);

    private static ParallelFixture CreateParallelFixture(string name, int count)
    {
        string root = Fixture(name), trace = Path.Combine(root, "child-traces"), gate = Path.Combine(root, "release-children");
        Directory.CreateDirectory(trace);
        var settings = new AppSettings
        {
            PreviewDirectory = Path.Combine(root, "previews"), MaxAttempts = 1, MaxParallelRenders = 2,
            Template = new PreviewTemplate { QuickPreview = false, HoldSeconds = .1, TailSeconds = .02 },
            Plugins = [ParallelPlugin(SynthKind.Vital)]
        };
        settings.Plugins[0].AllowParallelRendering = true;
        var presets = Enumerable.Range(0, count).Select(index =>
        {
            var preset = Preset("parallel-render");
            preset.ContentHash = Fingerprints.HashText(name + "/source/" + index);
            preset.SourcePath = Path.Combine(root, "TEST-ONLY-source-" + index + ".json");
            File.WriteAllText(preset.SourcePath, JsonSerializer.Serialize(new ParallelChildControl
            {
                TraceDirectory = trace, GatePath = gate, DelayMilliseconds = 200
            }, LibraryJson.Options));
            return preset;
        }).ToArray();
        var store = new LibraryStore(Path.Combine(root, "library.db"));
        store.SaveSettings(settings);
        store.UpsertMany(presets);
        return new ParallelFixture(root, trace, gate, store, settings, presets);
    }

    private static PluginConfiguration ParallelPlugin(SynthKind synth) => new()
    {
        Synth = synth, Backend = RenderBackend.ExternalWorker, ExecutablePath = TestExecutable,
        Version = "TEST ONLY parallel fixture", TimeoutSeconds = 30
    };

    private sealed class ParallelProgress : IProgress<QueueProgress>
    {
        private readonly List<QueueProgress> values = [];
        public QueueProgress[] Values { get { lock (values) return values.ToArray(); } }
        public void Report(QueueProgress value) { lock (values) values.Add(value); }
    }

    private sealed class ParallelChildControl
    {
        public ParallelChildControl() { }
        public string TraceDirectory { get; set; } = "";
        public string GatePath { get; set; } = "";
        public int DelayMilliseconds { get; set; }
        public double OutputGain { get; set; } = .5;
        public string ResultMarker { get; set; } = "";
    }

    private sealed class ParallelChildTrace
    {
        public ParallelChildTrace() { }
        public int Pid { get; set; }
        public int ParentPid { get; set; }
        public long StartedTicks { get; set; }
        public long FinishedTicks { get; set; }
        public bool IsBaseline { get; set; }
        public SynthKind Synth { get; set; }
        public string PresetId { get; set; } = "";
        public string AudioSha256 { get; set; } = "";
    }

    private static List<ParallelChildTrace> ParallelStarts(ParallelFixture fixture) => Directory.EnumerateFiles(fixture.TraceDirectory, "*.started.json")
        .Select(ReadParallelTrace).OrderBy(trace => trace.StartedTicks).ToList();

    private static ParallelChildTrace ReadParallelTrace(string path)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return JsonSerializer.Deserialize<ParallelChildTrace>(reader.ReadToEnd(), LibraryJson.Options)
                    ?? throw new InvalidDataException("TEST ONLY child trace contains no record: " + path);
            }
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) is 32 or 33 &&
                elapsed.Elapsed < TimeSpan.FromMilliseconds(500))
            {
                Thread.Sleep(10);
            }
        }
    }

    private static int ParallelPeak(ParallelFixture fixture)
    {
        var traces = ParallelFinishes(fixture);
        int active = 0, peak = 0;
        foreach (var item in traces.SelectMany(trace => new[] { (Ticks: trace.StartedTicks, Change: 1), (Ticks: trace.FinishedTicks, Change: -1) }).OrderBy(item => item.Ticks).ThenBy(item => item.Change))
        { active += item.Change; peak = Math.Max(peak, active); }
        return peak;
    }

    private static ParallelChildTrace[] ParallelFinishes(ParallelFixture fixture) => Directory.EnumerateFiles(fixture.TraceDirectory, "*.finished.json")
        .Select(ReadParallelTrace).ToArray();

    private static async Task WaitForParallelStartsAsync(ParallelFixture fixture, int expected, Task run)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (ParallelStarts(fixture).Count >= expected) return;
            if (run.IsCompleted) { await run; throw new InvalidOperationException("TEST ONLY parallel children did not reach the requested barrier."); }
            await Task.Delay(25);
        }
        throw new TimeoutException("TEST ONLY parallel children did not reach the barrier within fifteen seconds.");
    }

    private static void ReleaseParallel(ParallelFixture fixture) => File.WriteAllText(fixture.GatePath, "TEST ONLY release waiting fixture children");

    private static void ReleaseParallelCalibration(ParallelFixture fixture)
    {
        foreach (var preset in fixture.Presets.GroupBy(preset => preset.Synth).Select(group => group.First()))
        {
            string source = preset.SourcePath;
            var control = JsonSerializer.Deserialize<ParallelChildControl>(File.ReadAllText(source), LibraryJson.Options)!;
            control.GatePath = "";
            control.DelayMilliseconds = 350;
            File.WriteAllText(source, JsonSerializer.Serialize(control, LibraryJson.Options));
        }
    }

    private static List<ParallelChildTrace> ParallelActive(ParallelFixture fixture)
    {
        var finished = ParallelFinishes(fixture).Select(trace => (trace.Pid, trace.StartedTicks)).ToHashSet();
        return ParallelStarts(fixture).Where(trace => !trace.IsBaseline && !finished.Contains((trace.Pid, trace.StartedTicks))).ToList();
    }

    private static async Task WaitForParallelActiveAsync(ParallelFixture fixture, int expected, Task run)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (ParallelActive(fixture).Count >= expected) return;
            if (run.IsCompleted) { await run; throw new InvalidOperationException("TEST ONLY calibrated workers did not reach the active barrier."); }
            await Task.Delay(25);
        }
        throw new TimeoutException("TEST ONLY calibrated workers did not reach the active barrier within fifteen seconds.");
    }

    private static async Task StopParallelFixtureAsync(ParallelFixture fixture, Task run, CancellationTokenSource cancellation)
    {
        ReleaseParallel(fixture);
        cancellation.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    private static async Task<int> ParallelFakeWorkerAsync(RenderRequest request, string resultPath)
    {
        string controlPath = request.IsInitializationBaseline ? request.Plugin.PluginPath + ".parallel.json" : request.Preset.SourcePath;
        var control = JsonSerializer.Deserialize<ParallelChildControl>(await File.ReadAllTextAsync(controlPath), LibraryJson.Options)!;
        string stem = Path.Combine(control.TraceDirectory, Guid.NewGuid().ToString("N"));
        var trace = new ParallelChildTrace
        {
            Pid = Environment.ProcessId, ParentPid = OperatingSystem.IsMacOS() ? ParallelParentProcessId() : 0,
            StartedTicks = DateTime.UtcNow.Ticks, IsBaseline = request.IsInitializationBaseline,
            Synth = request.Plugin.Synth, PresetId = request.Preset.Id
        };
        await WriteTraceAsync(stem + ".started.json");
        try
        {
            while (control.GatePath.Length > 0 && !File.Exists(control.GatePath)) await Task.Delay(20);
            await Task.Delay(control.DelayMilliseconds);
            if (request.Preset.Name == "parallel-crash") return 17;
            int frames = (int)Math.Round(request.Template.SampleRate * (request.Template.HoldSeconds + request.Template.TailSeconds));
            var audio = Tone(request.Template.SampleRate, 1, frames * 3 / (double)request.Template.SampleRate);
            if (!request.IsInitializationBaseline)
                for (int index = 0; index < audio.Samples.Length; index++) audio.Samples[index] *= (float)control.OutputGain;
            WaveFile.WritePcm16(request.OutputPath, audio);
            trace.AudioSha256 = Fingerprints.HashFile(request.OutputPath);
            var result = new RenderResult
            {
                Success = true, OutputPath = request.OutputPath, AdapterVersion = "TEST-ONLY-parallel-process-v1",
                Message = "TEST ONLY synthetic PCM; no commercial plugin was loaded. " + control.ResultMarker,
                Notes = request.Template.Pitches.Select((pitch, index) => new NotePreview
                {
                    MidiNote = pitch, StartSeconds = index * frames / (double)request.Template.SampleRate,
                    DurationSeconds = frames / (double)request.Template.SampleRate,
                    ActualHoldSeconds = request.Template.HoldSeconds, ActualTailSeconds = request.Template.TailSeconds,
                    ActualVelocity = request.Template.Velocity, ActualNormalizedVelocity = request.Template.Velocity / 127.0
                }).ToList()
            };
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, LibraryJson.Options));
            return 0;
        }
        finally { trace.FinishedTicks = DateTime.UtcNow.Ticks; await WriteTraceAsync(stem + ".finished.json"); }

        async Task WriteTraceAsync(string path)
        {
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(trace, LibraryJson.Options));
            File.Move(path + ".tmp", path);
        }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getppid")]
    private static extern int ParallelParentProcessId();
}
