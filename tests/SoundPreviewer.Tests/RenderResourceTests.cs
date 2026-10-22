using System.Text.Json;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Audio;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestRenderResourcePolicyAsync()
    {
        Equal(0, new AppSettings().MaxParallelRenders, "A newly created library starts in automatic mode.");
        Equal(1, JsonSerializer.Deserialize<AppSettings>("{}", LibraryJson.Options)!.MaxParallelRenders, "An old settings document keeps its one-worker behavior.");
        Equal(0, JsonSerializer.Deserialize<AppSettings>("{\"MaxParallelRenders\":0}", LibraryJson.Options)!.MaxParallelRenders, "An explicitly saved automatic selection survives loading.");
        Equal(4, new AppSettings().MemoryBudgetGiB, "New libraries default to four GiB.");
        Equal(4, JsonSerializer.Deserialize<AppSettings>("{}", LibraryJson.Options)!.MemoryBudgetGiB, "Old settings without a budget retain the four-GiB default.");
        Equal(16, JsonSerializer.Deserialize<AppSettings>("{\"MemoryBudgetGiB\":16}", LibraryJson.Options)!.MemoryBudgetGiB, "A saved sixteen-GiB budget survives loading.");
        foreach (int invalid in new[] { int.MinValue, -1, 0, 17, int.MaxValue })
            Throws<ArgumentException>(() => RenderResourcePolicy.ResolveMemoryLimitBytes(invalid));
        foreach (var item in new (int Cores, int Expected)[] { (1, 1), (2, 2), (3, 2), (4, 4), (7, 4), (8, 8), (15, 8), (16, 16), (32, 16) })
            Equal(item.Expected, RenderResourcePolicy.ResolveMaxConcurrency(0, new(Math.Max(2, item.Cores * 2), item.Cores)), "Automatic concurrency follows physical cores and rounds down to a supported tier.");
        Equal(4, RenderResourcePolicy.ResolveMaxConcurrency(0, new(8, null)), "Unknown physical topology uses a conservative half-logical fallback.");
        Equal(1, RenderResourcePolicy.ResolveMaxConcurrency(0, new(1, null)), "Fallback always permits at least one worker target.");
        Equal(2, RenderResourcePolicy.ResolveMaxConcurrency(0, new(2, 16)), "Automatic concurrency respects available logical processors under affinity or CPU quotas.");
        foreach (int requested in new[] { 1, 2, 4, 8, 16 })
            Equal(requested, RenderResourcePolicy.ResolveMaxConcurrency(requested, new(2, 1)), "Explicit supported targets remain targets; memory admission is checked separately.");
        foreach (int invalid in new[] { -1, 3, 6, 32 })
            Throws<ArgumentException>(() => RenderResourcePolicy.ResolveMaxConcurrency(invalid, new(16, 8)));

        const long mib = 1024L * 1024;
        Equal(4096 * mib, RenderResourcePolicy.MemoryLimitBytes, "The user-approved total process-tree budget is four GiB.");
        Equal(256 * mib, RenderResourcePolicy.MemoryLimitBytes - RenderResourcePolicy.SoftLimitBytes, "Admission retains a 256 MiB buffer below the stop boundary.");
        var normal = new RenderMemorySnapshot(256 * mib, true, 1) { RootResidentBytes = 256 * mib };
        True(RenderResourcePolicy.CanStart(normal, 128 * mib, 256 * mib), "A complete low-RSS snapshot permits a reserved new job.");
        True(!RenderResourcePolicy.CanStart(normal, RenderResourcePolicy.SoftLimitBytes, 0), "Outstanding forecasts cannot share the same remaining memory.");
        True(!RenderResourcePolicy.CanStart(normal, long.MaxValue, long.MaxValue), "Reservation overflow cannot wrap around into apparent free memory.");
        True(!RenderResourcePolicy.CanStart(normal with { IsComplete = false }, 0, 0), "An incomplete observation cannot admit another worker.");
        foreach (long resident in new[] { 2048 * mib, 3072 * mib, 3584 * mib })
        {
            var approved = normal with { ResidentBytes = resident };
            True(!RenderResourcePolicy.MustStop(approved), "Real usage between two and three-and-a-half GiB stays within the approved budget.");
            True(RenderResourcePolicy.CanStart(approved, 128 * mib, 64 * mib), "Known small work can continue above the former two-GiB limit while retaining all admission margins.");
        }
        var reported = normal with { ResidentBytes = 581 * mib };
        True(RenderResourcePolicy.CanStart(reported, 1139 * mib, 256 * mib), "The user's 581 MiB actual plus 1139 MiB reservation can admit another forecast under the new budget.");
        var soft = normal with { ResidentBytes = RenderResourcePolicy.SoftLimitBytes };
        True(!RenderResourcePolicy.CanStart(soft, 0, 0) && !RenderResourcePolicy.MustStop(soft), "Soft pressure pauses starts without mislabeling it as a hard overage.");
        True(!RenderResourcePolicy.MustStop(normal with { ResidentBytes = RenderResourcePolicy.MemoryLimitBytes }), "The hard boundary is exceeded only above the configured budget.");
        True(RenderResourcePolicy.MustStop(normal with { ResidentBytes = RenderResourcePolicy.MemoryLimitBytes + 1, IsComplete = false }), "Even an incomplete sample must stop when its already known RSS exceeds the approved four-GiB budget.");
        foreach (int budgetGiB in Enumerable.Range(1, 16))
        {
            long limit = RenderResourcePolicy.ResolveMemoryLimitBytes(budgetGiB);
            Equal(budgetGiB * 1024L * mib, limit, "All selectable budgets resolve without a four-GiB cap or integer overflow.");
            var boundary = normal with { ResidentBytes = limit - RenderResourcePolicy.AdmissionHeadroomBytes };
            True(!RenderResourcePolicy.CanStart(boundary, 0, 0, limit), "Admission pauses at the selected budget's soft boundary.");
            True(RenderResourcePolicy.CanStart(boundary with { ResidentBytes = boundary.ResidentBytes - 128 * mib }, 64 * mib, 64 * mib, limit), "Admission uses the selected budget while honoring all reservations.");
            True(!RenderResourcePolicy.CanStart(boundary with { ResidentBytes = boundary.ResidentBytes - 128 * mib }, 64 * mib, 64 * mib + 1, limit), "A single byte above the combined selected-budget allowance is refused.");
            True(!RenderResourcePolicy.MustStop(normal with { ResidentBytes = limit }, limit) &&
                RenderResourcePolicy.MustStop(normal with { ResidentBytes = limit + 1 }, limit), "Hard stopping follows the selected budget exactly.");
        }

        var tree = RenderMemorySnapshot.FromProcesses(10,
        [
            new(99, 98, 10L * 1024 * mib, 500), // An unrelated synth may have the same executable name.
            new(12, 11, 256 * mib, 3), new(10, 1, 64 * mib, 1), new(11, 10, 128 * mib, 2)
        ]);
        Equal(448 * mib, tree.ResidentBytes, "RSS includes the application, worker and host grandchild, with shared pages conservatively summed.");
        Equal(64 * mib, tree.RootResidentBytes, "The root's RSS is available separately for calibration.");
        Equal(3, tree.ProcessCount, "Unrelated processes are excluded by parent relationships.");
        True(tree.IsComplete && tree.Processes.Select(process => process.ProcessId).Order().SequenceEqual([10, 11, 12]), "Process-table order does not change ancestry selection.");
        True(!RenderMemorySnapshot.FromProcesses(10, [new(11, 1, 1, null)]).IsComplete, "A missing root is a failed observation, never a zero-memory success.");
        True(!RenderMemorySnapshot.FromProcesses(10, [new(10, 1, 1, null)], false, "TEST ONLY denied enumeration").IsComplete, "Enumeration failure remains explicit even if some counters were obtained.");
        Equal(384 * mib, RenderResourcePolicy.WorkerTreeResidentBytes(tree, [11, 12, 11]), "Overlapping worker and host roots count their descendants once.");
        var orphan = tree with { Processes = [new(10, 1, 64 * mib, 1), new(12, 11, 256 * mib, 3)] };
        Equal(256 * mib, RenderResourcePolicy.WorkerTreeResidentBytes(orphan, [11]), "A known worker PID remains an ancestry anchor when it exits before its host.");
        var heavy = RenderMemorySnapshot.FromProcesses(10,
        [new(10, 1, 64 * mib, null), new(11, 10, 32 * mib, null), new(12, 11, 768 * mib, null), new(13, 10, 0, null)]);
        long firstReservation = Math.Max(0, 256 * mib - RenderResourcePolicy.WorkerTreeResidentBytes(heavy, [11]));
        long secondReservation = Math.Max(0, 256 * mib - RenderResourcePolicy.WorkerTreeResidentBytes(heavy, [13]));
        Equal(256 * mib, firstReservation + secondReservation, "A heavy worker may consume its own forecast but cannot pay another unloaded worker's reservation.");
        var supervised = RenderMemorySnapshot.FromProcesses(10,
        [new(10, 1, 64 * mib, null), new(11, 10, 0, null), new(12, 11, 160 * mib, null)]);
        Equal(160 * mib, RenderResourcePolicy.WorkerTreeResidentBytes(supervised, [11]), "A supervisor callback PID includes its actual worker child, even when the supervisor's normalized RSS is zero.");
        return Task.CompletedTask;
    }

    private static async Task TestSystemRenderResourceProbeAsync()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;
        string directory = Fixture("resource-process-tree");
        using var cancellation = new CancellationTokenSource();
        var run = ProcessRunner.RunAsync(TestExecutable, ["--resource-probe-child", directory], directory, TimeSpan.FromSeconds(40), cancellation.Token);
        try
        {
            int parent = await WaitForStartedAsync(Path.Combine(directory, "parent.started"), run);
            int leaf = await WaitForStartedAsync(Path.Combine(directory, "leaf.started"), run);
            var probe = new SystemRenderResourceProbe();
            var sample = probe.Capture();
            True(sample.IsComplete, "The platform probe can read its own live process tree: " + sample.Error);
            True(sample.Processes.Any(process => process.ProcessId == Environment.ProcessId) && sample.Processes.Any(process => process.ProcessId == parent) &&
                sample.Processes.Any(process => process.ProcessId == leaf), "The real platform probe includes this application, child worker and a grandchild host.");
            Equal(sample.Processes.Sum(process => process.ResidentBytes), sample.ResidentBytes, "Reported total RSS equals the retained process counters.");
            True(sample.RootResidentBytes > 0 && sample.ResidentBytes >= sample.RootResidentBytes, "The live root's memory is known and included.");
            True(probe.Hardware.LogicalProcessorCount > 0 && probe.Hardware.DetectionMethod.Length > 0, "Hardware detection states its source or explicit conservative fallback.");
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "release"), "TEST ONLY finish child and grandchild");
            cancellation.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }
    }

    private static async Task TestRenderResourceStopAsync()
    {
        await TestSelectedBudgetResourceStopAsync(4);
        await TestSelectedBudgetResourceStopAsync(16);
        var unknown = CreateParallelFixture("resource-unreadable-probe", 1);
        await ThrowsAsync<InvalidOperationException>(() => new RenderQueue(unknown.Store, new TestRenderResourceProbe { Incomplete = true })
            .RunAsync(unknown.Settings, unknown.Presets, TestExecutable, new ParallelProgress(), CancellationToken.None));
        Equal(0, ParallelStarts(unknown).Count, "Unobservable memory fails closed before creating any worker.");

        var unobserved = CreateParallelFixture("resource-unobserved-calibration", 3);
        unobserved.Settings.MaxParallelRenders = 4;
        ReleaseParallel(unobserved);
        var serial = await new RenderQueue(unobserved.Store, new TestRenderResourceProbe()).RunAsync(unobserved.Settings,
            unobserved.Presets, TestExecutable, new ParallelProgress(), CancellationToken.None);
        Equal(3, serial.Generated, "Short or unobserved workers still finish without inventing a measured memory profile.");
        Equal(1, serial.PeakActiveCount, "Without a complete worker-tree observation the queue continues one worker at a time.");
        Equal(1, ParallelPeak(unobserved), "Real child lifetimes confirm that a missing calibration never opens extra slots.");
        await TestCalibratedMemoryAdmissionAsync();
        await TestSelectedBudgetMemoryAdmissionAsync();
        await TestBaselineResourceStopAsync();
    }

    private static async Task TestSelectedBudgetResourceStopAsync(int budgetGiB)
    {
        var fixture = CreateParallelFixture("resource-pressure-stop-" + budgetGiB, 3);
        fixture.Settings.MemoryBudgetGiB = budgetGiB;
        long budgetBytes = RenderResourcePolicy.ResolveMemoryLimitBytes(budgetGiB);
        var probe = new TestRenderResourceProbe(fixture) { OverBudgetLimitBytes = budgetBytes };
        using var cancellation = new CancellationTokenSource();
        var run = new RenderQueue(fixture.Store, probe).RunAsync(fixture.Settings, fixture.Presets, TestExecutable, new ParallelProgress(), cancellation.Token);
        try
        {
            await WaitForParallelStartsAsync(fixture, 1, run);
            int pid = ParallelStarts(fixture).Single().Pid;
            const long mib = 1024L * 1024;
            foreach (long resident in new[] { budgetBytes / 2, budgetBytes - 512 * mib })
            {
                probe.AdditionalResidentBytes = resident - (64 + 160) * mib;
                var observed = System.Diagnostics.Stopwatch.StartNew();
                while (probe.LastResidentBytes < resident && observed.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(25);
                Equal(resident, probe.LastResidentBytes, "The live complete resource probe observes the approved higher RSS.");
                True(!run.IsCompleted && IsRunning(pid), "An already-running worker remains alive below the chosen budget, including above the former four-GiB cap.");
            }
            probe.OverBudget = true;
            RenderResourceLimitException? stopped = null;
            try { await run; }
            catch (RenderResourceLimitException error) { stopped = error; }
            True(stopped is not null, "An injected over-budget observation safely stops the actual held worker.");
            True(stopped!.Summary.ResourceStopped && stopped.Summary.MemoryBudgetBytes == budgetBytes &&
                stopped.Summary.PeakWorkingSetBytes > budgetBytes, "The stop result states the chosen budget and measured overage rather than claiming an OS-enforced cap.");
            True(stopped.Message.Contains(RenderResourcePolicy.FormatMemoryLimit(budgetBytes), StringComparison.Ordinal), "The failure message describes the chosen limit.");
            True(!IsRunning(pid), "Resource stopping drains the held child process before returning.");
            Equal(1, ParallelStarts(fixture).Count, "Resource stopping never launches the queued children.");
            True(fixture.Store.LoadPresets().All(preset => preset.Status == PresetStatus.Pending && preset.Attempts == 0), "Resource stopping preserves resumable state without consuming attempts.");
        }
        finally
        {
            ReleaseParallel(fixture);
            cancellation.Cancel();
            try { await run; }
            catch (Exception error) when (error is OperationCanceledException or RenderResourceLimitException) { }
        }
    }

    private static async Task TestSelectedBudgetMemoryAdmissionAsync()
    {
        const long mib = 1024L * 1024;
        var fixture = CreateParallelFixture("resource-sixteen-gib-admission", 10);
        fixture.Settings.MaxParallelRenders = 8;
        fixture.Settings.MemoryBudgetGiB = 16;
        ReleaseParallelCalibration(fixture);
        var progress = new ParallelProgress();
        var probe = new TestRenderResourceProbe(fixture) { WorkerResidentBytes = 900 * mib };
        using var cancellation = new CancellationTokenSource();
        var run = new RenderQueue(fixture.Store, probe).RunAsync(fixture.Settings, fixture.Presets, TestExecutable, progress, cancellation.Token);
        try
        {
            await WaitForParallelActiveAsync(fixture, 8, run);
            fixture.Settings.MemoryBudgetGiB = 1; // The caller cannot change a running batch's budget snapshot.
            // Worker creation precedes its next RSS sample, and resource progress is
            // throttled to 500 ms. Wait for that observation, not an arbitrary short delay.
            var observation = System.Diagnostics.Stopwatch.StartNew();
            while (!progress.Values.Any(value => value.ActiveCount == 8 && value.AllowedConcurrency == 8 &&
                value.WorkingSetBytes > RenderResourcePolicy.MemoryLimitBytes) && observation.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (run.IsCompleted) { await run; break; }
                await Task.Delay(25);
            }
            True(!run.IsCompleted && ParallelActive(fixture).Count == 8, "A sixteen-GiB budget admits eight measured large workers and ignores caller edits until the next batch.");
            True(progress.Values.Any(value => value.ActiveCount == 8 && value.AllowedConcurrency == 8 &&
                value.WorkingSetBytes > RenderResourcePolicy.MemoryLimitBytes), "Live progress advertises eight slots above the old four-GiB limit.");
            True(progress.Values.All(value => value.MemoryBudgetBytes == 16 * 1024 * mib), "Every progress update reports the immutable chosen budget.");
            ReleaseParallel(fixture);
            var result = await run;
            Equal(10, result.Generated, "The larger budget commits every job.");
            Equal(8, result.PeakActiveCount, "Real held worker processes prove eight-way admission with larger memory forecasts.");
            Equal(16 * 1024 * mib, result.MemoryBudgetBytes, "The batch summary retains its original sixteen-GiB budget.");
            True(result.PeakWorkingSetBytes > RenderResourcePolicy.MemoryLimitBytes, "The injected measured process tree exceeds four GiB without an accidental default-limit stop.");
        }
        finally { await StopParallelFixtureAsync(fixture, run, cancellation); }
    }

    private static async Task TestCalibratedMemoryAdmissionAsync()
    {
        const long mib = 1024L * 1024;
        var light = CreateParallelFixture("resource-shared-root-growth", 10);
        light.Settings.MaxParallelRenders = 8;
        ReleaseParallelCalibration(light);
        var lightProbe = new TestRenderResourceProbe(light)
        {
            WorkerResidentBytes = 64 * mib, RootGrowthAfterStartBytes = 192 * mib
        };
        var lightProgress = new ParallelProgress();
        using (var cancellation = new CancellationTokenSource())
        {
            var run = new RenderQueue(light.Store, lightProbe).RunAsync(light.Settings, light.Presets,
                TestExecutable, lightProgress, cancellation.Token);
            try
            {
                await WaitForParallelActiveAsync(light, 8, run);
                await Task.Delay(150);
                Equal(8, ParallelActive(light).Count, "Measured light workers fill all eight requested slots despite shared main-process growth.");
                True(lightProgress.Values.Any(value => value.ActiveCount == 8 && value.AllowedConcurrency == 8),
                    "Memory admission advertises all eight affordable slots instead of multiplying the root's shared RSS into every worker.");
                ReleaseParallel(light);
                var result = await run;
                Equal(10, result.Generated, "Every light worker commits after the calibrated eight-slot batch.");
                Equal(8, result.PeakActiveCount, "Real held children prove eight-way concurrency.");
                True(result.PeakWorkingSetBytes <= 800 * mib, "The deterministic process tree stays below 800 MiB while safely using eight slots.");
            }
            finally { await StopParallelFixtureAsync(light, run, cancellation); }
        }

        var mixed = CreateInterleavedSynthFixture("resource-completed-heavy-synth", 20, 8, true);
        ReleaseParallelCalibration(mixed);
        foreach (var preset in mixed.Presets.Where(preset => preset.Synth == SynthKind.Vital))
        {
            var control = JsonSerializer.Deserialize<ParallelChildControl>(File.ReadAllText(preset.SourcePath), LibraryJson.Options)!;
            control.GatePath = "";
            control.DelayMilliseconds = 350;
            File.WriteAllText(preset.SourcePath, JsonSerializer.Serialize(control, LibraryJson.Options));
        }
        var mixedProgress = new ParallelProgress();
        var mixedProbe = new TestRenderResourceProbe(mixed)
        {
            WorkerMemorySelector = synth => (synth == SynthKind.Vital ? 320 : 64) * mib
        };
        using (var cancellation = new CancellationTokenSource())
        {
            var run = new RenderQueue(mixed.Store, mixedProbe).RunAsync(mixed.Settings, mixed.Presets,
                TestExecutable, mixedProgress, cancellation.Token);
            try
            {
                await WaitForParallelActiveAsync(mixed, 8, run);
                True(ParallelActive(mixed).All(trace => trace.Synth == SynthKind.Serum1),
                    "The light synth fills eight slots after the heavy synth has finished.");
                True(mixedProgress.Values.Any(value => value.Preset?.Synth == SynthKind.Serum1 && value.ActiveCount is > 0 and < 8 && value.AllowedConcurrency == 8),
                    "While the light group is filling, the displayed memory limit uses its forecast rather than the completed heavy synth's historical maximum.");
                ReleaseParallel(mixed);
                Equal(20, (await run).Generated, "Both heavy and light synth groups preserve every requested job.");
            }
            finally { await StopParallelFixtureAsync(mixed, run, cancellation); }
        }
    }

    private static async Task TestBaselineResourceStopAsync()
    {
        var fixture = CreateParallelFixture("resource-manual-baseline-stop", 1);
        var plugin = ParallelPlugin(SynthKind.Serum1);
        plugin.Backend = RenderBackend.DawDreamer;
        plugin.PluginPath = Path.Combine(fixture.Root, "TEST-ONLY-never-loaded.dll");
        File.WriteAllText(plugin.PluginPath, "TEST ONLY placeholder, never loaded.");
        File.WriteAllText(plugin.PluginPath + ".parallel.json", JsonSerializer.Serialize(new ParallelChildControl
        {
            TraceDirectory = fixture.TraceDirectory, GatePath = fixture.GatePath, DelayMilliseconds = 200
        }, LibraryJson.Options));
        string previous = Path.Combine(fixture.Root, "TEST-ONLY-existing-baseline.json");
        File.WriteAllText(previous, "TEST ONLY previously saved baseline locator; never parsed for forced generation.");
        plugin.InitBaselinePath = previous;
        fixture.Settings.Plugins = [plugin];
        fixture.Store.SaveSettings(fixture.Settings);
        var probe = new TestRenderResourceProbe(fixture);
        using var cancellation = new CancellationTokenSource();
        var run = new RenderQueue(fixture.Store, probe).GenerateInitializationBaselineAsync(fixture.Settings, plugin,
            TestExecutable, new ParallelProgress(), cancellation.Token);
        try
        {
            await WaitForParallelStartsAsync(fixture, 1, run);
            int pid = ParallelStarts(fixture).Single().Pid;
            probe.OverBudget = true;
            await ThrowsAsync<RenderResourceLimitException>(() => run);
            True(!IsRunning(pid), "Manual baseline resource stopping drains its reference worker before returning.");
            Equal(previous, plugin.InitBaselinePath, "Stopping a new manual baseline keeps the previous plugin locator.");
            Equal(previous, fixture.Store.LoadSettings().Plugins.Single(item => item.Synth == plugin.Synth).InitBaselinePath, "The persisted previous baseline survives a resource stop.");
            True(File.Exists(previous), "Resource cleanup retains the previous baseline file.");
            string generated = Path.Combine(fixture.Settings.PreviewDirectory, "InitializationBaselines");
            True(!Directory.EnumerateFileSystemEntries(generated).Any(), "An interrupted baseline removes its own unfinished WAV and staging files.");
        }
        finally
        {
            ReleaseParallel(fixture);
            cancellation.Cancel();
            try { await run; }
            catch (Exception error) when (error is OperationCanceledException or RenderResourceLimitException) { }
        }
    }

    private sealed class TestRenderResourceProbe(ParallelFixture? fixture = null) : IRenderResourceProbe
    {
        public volatile bool OverBudget;
        public long OverBudgetLimitBytes { get; init; } = RenderResourcePolicy.MemoryLimitBytes;
        private long additionalResidentBytes;
        private long lastResidentBytes;
        public long AdditionalResidentBytes { get => Interlocked.Read(ref additionalResidentBytes); set => Interlocked.Exchange(ref additionalResidentBytes, value); }
        public long LastResidentBytes => Interlocked.Read(ref lastResidentBytes);
        public bool Incomplete { get; init; }
        public long WorkerResidentBytes { get; init; } = 160L * 1024 * 1024;
        public long RootGrowthAfterStartBytes { get; init; }
        public Func<SynthKind, long>? WorkerMemorySelector { get; init; }
        public RenderHardwareInfo Hardware { get; } = new(16, 8) { DetectionMethod = "TEST ONLY injected topology" };
        public RenderMemorySnapshot Capture()
        {
            const long mib = 1024L * 1024;
            var starts = fixture is null ? [] : ParallelStarts(fixture);
            long root = 64 * mib + (starts.Count > 0 ? RootGrowthAfterStartBytes : 0);
            var processes = new List<RenderProcessUsage> { new(Environment.ProcessId, 0, root, null) };
            if (fixture is not null)
            {
                var finished = ParallelFinishes(fixture).Select(trace => (trace.Pid, trace.StartedTicks)).ToHashSet();
                foreach (var trace in starts.Where(trace => !finished.Contains((trace.Pid, trace.StartedTicks)) && IsRunning(trace.Pid)))
                {
                    int parent = trace.ParentPid > 0 ? trace.ParentPid : Environment.ProcessId;
                    // macOS registers the shell supervisor as the controlled process root.
                    // Keep its real ancestry while normalizing only the test RSS counters.
                    if (parent != Environment.ProcessId && processes.All(process => process.ProcessId != parent))
                        processes.Add(new(parent, Environment.ProcessId, 0, null));
                    processes.Add(new(trace.Pid, parent, WorkerMemorySelector?.Invoke(trace.Synth) ?? WorkerResidentBytes, null));
                }
            }
            if (AdditionalResidentBytes > 0)
                processes.Add(new(int.MaxValue - 2, Environment.ProcessId, AdditionalResidentBytes, null));
            var snapshot = RenderMemorySnapshot.FromProcesses(Environment.ProcessId, processes, !Incomplete, Incomplete ? "TEST ONLY access denied" : null);
            if (OverBudget) snapshot = snapshot with { ResidentBytes = OverBudgetLimitBytes + 1 };
            Interlocked.Exchange(ref lastResidentBytes, snapshot.ResidentBytes);
            return snapshot;
        }
    }

    private static async Task<int> ResourceProbeChildAsync(string directory, bool leaf)
    {
        string marker = Path.Combine(directory, leaf ? "leaf.started" : "parent.started");
        await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new { Pid = Environment.ProcessId }));
        if (!leaf)
        {
            var result = await ProcessRunner.RunAsync(TestExecutable, ["--resource-probe-leaf", directory], directory, TimeSpan.FromSeconds(35));
            return result.ExitCode;
        }
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(Path.Combine(directory, "release")) && DateTime.UtcNow < deadline) await Task.Delay(25);
        return 0;
    }
}
