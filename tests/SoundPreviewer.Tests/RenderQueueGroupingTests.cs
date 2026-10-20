using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static async Task TestRenderQueueSynthGroupingAsync()
    {
        var serial = CreateInterleavedSynthFixture("synth-grouping-serial", 4, 1, true);
        ReleaseParallel(serial);
        var serialResult = await new RenderQueue(serial.Store, new TestRenderResourceProbe(serial)).RunAsync(
            serial.Settings, serial.Presets, TestExecutable, new ParallelProgress(), CancellationToken.None);
        Equal(4, serialResult.Generated, "Interleaved source records all generate once.");
        var serialStarts = ParallelStarts(serial);
        var serialOrder = new[] { 0, 2, 1, 3 };
        for (int index = 0; index < serialOrder.Length; index++)
            Equal(serial.Presets[serialOrder[index]].Id, serialStarts[index].PresetId,
                "A/B/A/B renders as A/A/B/B, retaining first-seen synth order and within-synth source order.");

        var parallel = CreateInterleavedSynthFixture("synth-grouping-parallel", 10, 4, true);
        ReleaseParallelCalibration(parallel);
        using (var cancellation = new CancellationTokenSource())
        {
            var run = new RenderQueue(parallel.Store, new TestRenderResourceProbe(parallel)).RunAsync(
                parallel.Settings, parallel.Presets, TestExecutable, new ParallelProgress(), cancellation.Token);
            try
            {
                await WaitForParallelActiveAsync(parallel, 4, run);
                await Task.Delay(150);
                var starts = ParallelStarts(parallel);
                Equal(5, starts.Count, "One calibration followed by four same-synth jobs fills the four rendering slots.");
                True(starts.All(trace => trace.Synth == SynthKind.Vital),
                    "An unmeasured later synth does not interrupt a calibrated synth's parallel batch.");
                ReleaseParallel(parallel);
                var result = await run;
                Equal(10, result.Generated, "Both synth groups finish every source once.");
                Equal(4, ParallelPeak(parallel), "Real child lifetimes prove the preferred group fills four slots.");
                var finished = ParallelFinishes(parallel);
                var first = finished.Where(trace => trace.Synth == SynthKind.Vital).ToArray();
                var second = finished.Where(trace => trace.Synth == SynthKind.Serum1).OrderBy(trace => trace.StartedTicks).ToArray();
                True(first.Max(trace => trace.FinishedTicks) <= second[0].StartedTicks,
                    "The later synth first calibrates at the completed group boundary.");
                True(second.Skip(1).All(trace => trace.StartedTicks >= second[0].FinishedTicks),
                    "The second synth's first measurement stays isolated before its parallel jobs start.");
            }
            finally { await StopParallelFixtureAsync(parallel, run, cancellation); }
        }

        var serialSynths = CreateInterleavedSynthFixture("synth-grouping-backfill", 8, 2, false);
        ReleaseParallelCalibration(serialSynths);
        using (var cancellation = new CancellationTokenSource())
        {
            var run = new RenderQueue(serialSynths.Store, new TestRenderResourceProbe(serialSynths)).RunAsync(
                serialSynths.Settings, serialSynths.Presets, TestExecutable, new ParallelProgress(), cancellation.Token);
            try
            {
                await WaitForParallelActiveAsync(serialSynths, 2, run);
                var active = ParallelActive(serialSynths);
                Equal(2, active.Select(trace => trace.Synth).Distinct().Count(),
                    "Per-synth serial permission still allows different calibrated synths to fill otherwise idle slots.");
                var starts = ParallelStarts(serialSynths);
                Equal(SynthKind.Vital, starts[0].Synth, "The first-seen synth calibrates first.");
                Equal(SynthKind.Serum1, starts[1].Synth, "A later synth calibrates only at the idle boundary needed for backfilling.");
                ReleaseParallel(serialSynths);
                var result = await run;
                Equal(8, result.Generated, "Backfilling preserves every source job.");
                var finished = ParallelFinishes(serialSynths);
                foreach (var group in finished.GroupBy(trace => trace.Synth))
                {
                    var ordered = group.OrderBy(trace => trace.StartedTicks).ToArray();
                    for (int index = 1; index < ordered.Length; index++)
                        True(ordered[index - 1].FinishedTicks <= ordered[index].StartedTicks,
                            "Backfilling never overlaps two instances of a synth without explicit permission.");
                }
            }
            finally { await StopParallelFixtureAsync(serialSynths, run, cancellation); }
        }
        await TestSynthGroupingCalibrationMemoryFallbackAsync();
    }

    private static async Task TestSynthGroupingCalibrationMemoryFallbackAsync()
    {
        var fixture = CreateInterleavedSynthFixture("synth-grouping-calibration-memory-fallback", 4, 2, false);
        ReleaseParallelCalibration(fixture);
        var probe = new CalibrationFallbackResourceProbe(fixture);
        using var cancellation = new CancellationTokenSource();
        var run = new RenderQueue(fixture.Store, probe).RunAsync(
            fixture.Settings, fixture.Presets, TestExecutable, new ParallelProgress(), cancellation.Token);
        try
        {
            await WaitForParallelStartsAsync(fixture, 2, run);
            var starts = ParallelStarts(fixture);
            Equal(2, starts.Count, "The measured preferred synth can start its second real worker under pressure.");
            True(starts.All(trace => trace.Synth == SynthKind.Vital),
                "An optional unknown-synth calibration that does not fit falls back to the smaller measured synth.");
            True(probe.PressureObserved, "The fallback is exercised after the injected memory pressure was captured.");
            ReleaseParallel(fixture);
            RenderResourceLimitException? stopped = null;
            try { await run.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (RenderResourceLimitException error) { stopped = error; }
            True(stopped is not null && stopped.Summary.ResourceStopped,
                "Once no measured work remains, insufficient room for the unknown synth stops safely instead of waiting forever.");
            Equal(2, stopped!.Summary.Generated, "Both smaller measured jobs commit before the genuine memory stop.");
            Equal(2, ParallelStarts(fixture).Count, "The unsafe unknown synth never starts under pressure.");
            True(fixture.Store.LoadPresets().Where(preset => preset.Synth == SynthKind.Serum1)
                .All(preset => preset.Status == PresetStatus.Pending && preset.Attempts == 0),
                "Unstarted later-synth records remain resumable without consuming attempts.");
        }
        finally
        {
            ReleaseParallel(fixture);
            cancellation.Cancel();
            try { await run; }
            catch (Exception error) when (error is OperationCanceledException or RenderResourceLimitException) { }
        }
        probe.RelaxPressure = true;
        var resumed = await new RenderQueue(fixture.Store, probe).RunAsync(
            fixture.Settings, fixture.Presets, TestExecutable, new ParallelProgress(), CancellationToken.None);
        Equal(2, resumed.Generated, "The remaining synth resumes after memory becomes available.");
        Equal(2, resumed.Reused, "Resuming retains both committed preferred-synth previews.");
        Equal(1, ParallelPeak(fixture), "Fallback and resumption never overlap an unknown calibration with another job.");
    }

    private sealed class CalibrationFallbackResourceProbe(ParallelFixture fixture) : IRenderResourceProbe
    {
        public volatile bool RelaxPressure;
        public volatile bool PressureObserved;
        public RenderHardwareInfo Hardware { get; } = new(4, 4) { DetectionMethod = "TEST ONLY calibration fallback" };

        public RenderMemorySnapshot Capture()
        {
            const long mib = 1024L * 1024;
            var processes = new List<RenderProcessUsage> { new(Environment.ProcessId, 0, 64 * mib, null) };
            var starts = ParallelStarts(fixture);
            var finished = ParallelFinishes(fixture).Select(trace => (trace.Pid, trace.StartedTicks)).ToHashSet();
            foreach (var trace in starts.Where(trace => !finished.Contains((trace.Pid, trace.StartedTicks)) && IsRunning(trace.Pid)))
            {
                int parent = trace.ParentPid > 0 ? trace.ParentPid : Environment.ProcessId;
                if (parent != Environment.ProcessId && processes.All(process => process.ProcessId != parent))
                    processes.Add(new(parent, Environment.ProcessId, 0, null));
                processes.Add(new(trace.Pid, parent, 32 * mib, null));
            }
            if (starts.Count > 0 && !RelaxPressure)
            {
                // Keep the root stable and model another application-owned child.
                // It contributes to total RSS but cannot inflate this worker's
                // measured 32 MiB footprint through parent-growth calibration.
                // Leave 224 MiB after the root and global margin under the
                // current budget: above measured 104 MiB, below unknown 256 MiB.
                long pressure = RenderResourcePolicy.SoftLimitBytes - (64 + 128 + 224) * mib;
                processes.Add(new(int.MaxValue - 1, Environment.ProcessId, pressure, null));
                PressureObserved = true;
            }
            return RenderMemorySnapshot.FromProcesses(Environment.ProcessId, processes);
        }
    }

    private static ParallelFixture CreateInterleavedSynthFixture(string name, int count, int concurrency, bool sameSynthParallel)
    {
        var fixture = CreateParallelFixture(name, count);
        fixture.Settings.MaxParallelRenders = concurrency;
        fixture.Settings.Plugins[0].AllowParallelRendering = sameSynthParallel;
        var second = ParallelPlugin(SynthKind.Serum1);
        second.AllowParallelRendering = sameSynthParallel;
        fixture.Settings.Plugins.Add(second);
        for (int index = 1; index < fixture.Presets.Length; index += 2)
            fixture.Presets[index].Synth = SynthKind.Serum1;
        fixture.Store.SaveSettings(fixture.Settings);
        fixture.Store.UpsertMany(fixture.Presets);
        return fixture;
    }
}
