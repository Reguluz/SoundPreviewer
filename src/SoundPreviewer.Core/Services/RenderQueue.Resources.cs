using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Services;

public sealed partial class RenderQueue
{
    private sealed class MemoryLease(long forecast)
    {
        public long Forecast { get; } = forecast;
        public long ObservedPeakResidentBytes { get; set; }
        public bool ObservedChildren { get; set; }
        public HashSet<int> WorkerIds { get; } = [];
        public long ObservedResidentBytes { get; set; }
    }

    private sealed partial class Execution
    {
        private sealed class SynthJobDemand(PluginConfiguration plugin)
        {
            public PluginConfiguration Plugin { get; } = plugin;
            public int Waiting { get; set; }
            public int Leased { get; set; }
            public int Total => Waiting + Leased;
        }

        private const long MinimumJobMemory = 256L * 1024 * 1024;
        private const long GlobalMemoryReserve = 128L * 1024 * 1024;
        private readonly IRenderResourceProbe resourceProbe;
        private readonly object probeGate = new();
        private readonly Dictionary<RenderJob, MemoryLease> memoryLeases = new();
        private readonly Dictionary<SynthKind, long> memoryProfiles = new();
        private long peakRootResidentBytes;
        private readonly int targetConcurrency;
        private RenderMemorySnapshot memorySnapshot = new(0, false, 0, "尚未检测内存。");
        private TaskCompletionSource resourcesChanged = NewSignal();
        private CancellationTokenSource? batchCancellation;
        private string? resourceFailure;
        private string resourceMessage = "正在检查主程序和渲染进程的内存。";
        private int incompleteMemorySamples;
        private int allowedConcurrency = 1;
        private double lastResourceProgress;
        private List<RenderJob>? trackedWaitingJobs;
        private readonly Dictionary<SynthKind, SynthJobDemand> synthJobDemand = new();

        private async Task MonitorResourcesAsync(CancellationToken monitorToken)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(100, monitorToken).ConfigureAwait(false);
                    CaptureResources();
                }
            }
            catch (OperationCanceledException) when (monitorToken.IsCancellationRequested) { }
        }

        private void CaptureResources()
        {
            try { CaptureResourcesCore(); }
            catch (Exception error)
            {
                lock (gate)
                {
                    SetResourceFailure("内存监控未能继续，已停止生成以保护已有记录。" + error.Message);
                    SignalResourcesChanged();
                }
                batchCancellation?.Cancel();
            }
        }

        private void CaptureResourcesCore()
        {
            RenderMemorySnapshot snapshot;
            try
            {
                lock (probeGate) snapshot = resourceProbe.Capture();
            }
            catch (Exception error)
            {
                lock (gate) snapshot = memorySnapshot with { IsComplete = false, Error = error.Message };
            }
            if (RenderResourcePolicy.MustStop(snapshot))
            {
                string reason = $"内存保护已停止生成：主程序及渲染进程合计 {snapshot.ResidentBytes / (1024.0 * 1024 * 1024):F2} GiB，超过 2 GiB 目标。已完成的预览保留，其余可继续。";
                Interlocked.CompareExchange(ref resourceFailure, reason, null);
                // A file/database commit can briefly hold gate. Stop other processes as
                // soon as the sample is known, without waiting for that commit to finish.
                batchCancellation?.Cancel();
            }
            lock (gate)
            {
                if (snapshot.ResidentBytes < 0 || snapshot.RootResidentBytes < 0 || snapshot.RootResidentBytes > snapshot.ResidentBytes)
                    snapshot = snapshot with { ResidentBytes = Math.Max(0, snapshot.ResidentBytes), IsComplete = false, Error = "内存采样数据无效。" };
                memorySnapshot = snapshot;
                summary.PeakWorkingSetBytes = Math.Max(summary.PeakWorkingSetBytes, snapshot.ResidentBytes);
                if (RenderResourcePolicy.MustStop(snapshot))
                    SetResourceFailure(resourceFailure!);
                else if (!snapshot.IsComplete)
                {
                    incompleteMemorySamples++;
                    resourceMessage = "无法完整读取渲染进程树内存，暂停启动新任务。" + snapshot.Error;
                    if (incompleteMemorySamples >= 3)
                        SetResourceFailure("无法完整监测主程序及渲染子进程的内存，已停止生成以保护 2 GiB 内存目标。" + snapshot.Error);
                }
                else
                {
                    incompleteMemorySamples = 0;
                    peakRootResidentBytes = Math.Max(peakRootResidentBytes, snapshot.RootResidentBytes);
                    foreach (var lease in memoryLeases.Values)
                    {
                        long own = RenderResourcePolicy.WorkerTreeResidentBytes(snapshot, lease.WorkerIds);
                        lease.ObservedResidentBytes = own;
                        // The root is already included in total RSS. Its shared growth
                        // must not be multiplied into every worker's future forecast.
                        lease.ObservedPeakResidentBytes = Math.Max(lease.ObservedPeakResidentBytes, own);
                        lease.ObservedChildren |= own > 0;
                    }
                    if (snapshot.ResidentBytes >= RenderResourcePolicy.SoftLimitBytes)
                        resourceMessage = "当前内存接近目标，等待正在生成的任务结束后再启动新任务。";
                }
                UpdateAllowedConcurrency();
                if (memoryLeases.Count > 0 && (elapsed.Elapsed.TotalSeconds - lastResourceProgress >= 0.5 || resourceFailure is not null))
                {
                    lastResourceProgress = elapsed.Elapsed.TotalSeconds;
                    Report(activeWorkers > 0 ? QueueStages.Rendering : QueueStages.Validating, resourceMessage,
                        memoryLeases.Keys.First().Items[0], true);
                }
                SignalResourcesChanged();
            }
            // Cancel outside the coordinator lock: process cancellation may synchronously
            // invoke callbacks. Completed file/DB commits continue through their boundary.
            if (Volatile.Read(ref resourceFailure) is not null) batchCancellation?.Cancel();
        }

        private bool TryStartJob(List<RenderJob> waiting, IEnumerable<RenderJob> running, int runningCount, out RenderJob? next)
        {
            lock (gate)
            {
                TrackWaitingJobs(waiting);
                next = null;
                if (resourceFailure is not null || !memorySnapshot.IsComplete || runningCount >= targetConcurrency) return false;
                bool observing = running.Any(active => !memoryProfiles.ContainsKey(active.Plugin.Synth));
                if (observing)
                {
                    resourceMessage = "先单独完成当前音源的首项，观察实际内存后再增加同时生成数量。";
                    return false;
                }
                int calibrationIndex = -1;
                if (runningCount == 0 && waiting.Count > 0)
                {
                    var preferred = waiting[0];
                    if (!memoryProfiles.ContainsKey(preferred.Plugin.Synth)) calibrationIndex = 0;
                    else if (!preferred.Plugin.AllowParallelRendering && targetConcurrency > 1)
                    {
                        // A synth that explicitly disallows simultaneous instances
                        // cannot fill the other slots. At an idle boundary, measure
                        // only enough later synths to permit useful backfilling.
                        // Otherwise finish the current synth before switching just
                        // to calibrate an unrelated one.
                        int knownCapacity = 0;
                        bool hasUnmeasured = false;
                        foreach (var demand in synthJobDemand.Values)
                        {
                            if (demand.Waiting == 0) continue;
                            if (memoryProfiles.ContainsKey(demand.Plugin.Synth))
                                knownCapacity += demand.Plugin.AllowParallelRendering ? demand.Waiting : 1;
                            else hasUnmeasured = true;
                        }
                        if (knownCapacity < targetConcurrency && hasUnmeasured)
                        {
                            int candidate = waiting.FindIndex(job => !memoryProfiles.ContainsKey(job.Plugin.Synth));
                            // This calibration is an optional backfill optimization.
                            // Its conservative unknown-synth reservation must not stop
                            // a smaller, already measured preferred job that still fits.
                            if (candidate >= 0 && RenderResourcePolicy.CanStart(memorySnapshot,
                                ReservedMemoryBytes, ForecastMemory(waiting[candidate].Plugin.Synth)))
                                calibrationIndex = candidate;
                        }
                    }
                }
                bool hasEligibleSynth = false;
                bool waitingForCalibration = false;
                for (int index = 0; index < waiting.Count; index++)
                {
                    if (calibrationIndex >= 0 && index != calibrationIndex) continue;
                    var job = waiting[index];
                    // Unknown synths still calibrate alone. A later unmeasured
                    // synth must not prevent calibrated jobs of the preferred
                    // synth from filling the CPU / memory slots.
                    if (!memoryProfiles.ContainsKey(job.Plugin.Synth) && runningCount > 0)
                    { waitingForCalibration = true; continue; }
                    if (!job.Plugin.AllowParallelRendering && running.Any(active => active.Plugin.Synth == job.Plugin.Synth)) continue;
                    hasEligibleSynth = true;
                    long forecast = ForecastMemory(job.Plugin.Synth);
                    if (!RenderResourcePolicy.CanStart(memorySnapshot, ReservedMemoryBytes, forecast)) continue;
                    memoryLeases.Add(job, new(forecast));
                    waiting.RemoveAt(index);
                    var demand = synthJobDemand[job.Plugin.Synth];
                    demand.Waiting--;
                    demand.Leased++;
                    next = job;
                    resourceMessage = !memoryProfiles.ContainsKey(job.Plugin.Synth)
                        ? "先单独完成当前音源的首项，观察实际内存后再增加同时生成数量。"
                        : "已根据观测占用预留内存；同时生成数量受当前内存余量和音源设置限制。";
                    UpdateAllowedConcurrency();
                    return true;
                }
                if (hasEligibleSynth)
                {
                    resourceMessage = "内存余量不足，暂不启动新任务，等待正在生成的任务结束。";
                    if (runningCount == 0)
                        SetResourceFailure("内存余量不足以安全启动下一个渲染任务，已停止生成。已完成的预览保留；关闭占用内存的试听或重新打开程序后可继续。");
                }
                else if (waitingForCalibration)
                    resourceMessage = "等待当前音源任务结束后，单独观测下一音源的首项。";
                else if (waiting.Count > 0)
                    resourceMessage = "此音源尚未允许多实例并行，等待当前音色完成。";
                UpdateAllowedConcurrency();
                return false;
            }
        }

        private long ForecastMemory(SynthKind synth) => memoryProfiles.TryGetValue(synth, out long forecast)
            ? forecast : Math.Max(MinimumJobMemory, memoryProfiles.Values.DefaultIfEmpty(MinimumJobMemory).Max());

        private long UnobservedReservations() => memoryLeases.Values.Sum(lease => Math.Max(0, lease.Forecast - lease.ObservedResidentBytes));

        private long MainProcessHeadroom() => Math.Max(0, peakRootResidentBytes - memorySnapshot.RootResidentBytes);

        private long ReservedMemoryBytes => UnobservedReservations() + MainProcessHeadroom() + GlobalMemoryReserve;

        private void RegisterWorker(RenderJob job, int processId)
        {
            lock (gate)
            {
                foreach (var lease in memoryLeases.Values)
                    if (lease.WorkerIds.Remove(processId)) lease.ObservedResidentBytes = 0;
                if (memoryLeases.TryGetValue(job, out var owner))
                {
                    // A retry / next baseline reference has not loaded its plugin yet.
                    // Do not use an exited invocation's last RSS to redeem this reservation.
                    owner.ObservedResidentBytes = 0;
                    owner.WorkerIds.Add(processId);
                }
            }
        }

        private void CompleteResourceLease(RenderJob job)
        {
            if (!memoryLeases.Remove(job, out var lease)) return;
            synthJobDemand[job.Plugin.Synth].Leased--;
            if (lease.ObservedChildren)
            {
                long forecast = Math.Max(64L * 1024 * 1024, (long)Math.Min(RenderResourcePolicy.MemoryLimitBytes,
                    lease.ObservedPeakResidentBytes * 1.25 + 64L * 1024 * 1024));
                memoryProfiles[job.Plugin.Synth] = Math.Max(memoryProfiles.GetValueOrDefault(job.Plugin.Synth), forecast);
            }
            resourceMessage = resourceFailure ?? (lease.ObservedChildren
                ? "已观测此音源的内存占用，后续任务按峰值及安全余量预留。"
                : "尚未观测到此音源宿主的完整内存占用，继续逐项生成和采样。");
            UpdateAllowedConcurrency();
            SignalResourcesChanged();
        }

        private void UpdateAllowedConcurrency()
        {
            if (resourceFailure is not null || !memorySnapshot.IsComplete) { allowedConcurrency = 0; return; }
            if (memoryProfiles.Count == 0 || memoryLeases.Keys.Any(job => !memoryProfiles.ContainsKey(job.Plugin.Synth)))
            { allowedConcurrency = Math.Min(1, targetConcurrency); return; }
            long available = RenderResourcePolicy.SoftLimitBytes - memorySnapshot.ResidentBytes - ReservedMemoryBytes;
            int additional = 0;
            // Follow the pending synth groups rather than the largest synth ever
            // observed. Completed heavy synths cannot restrict a later light group.
            foreach (var demand in synthJobDemand.Values)
            {
                if (demand.Waiting == 0 || !memoryProfiles.ContainsKey(demand.Plugin.Synth) || available <= 0) continue;
                long forecast = ForecastMemory(demand.Plugin.Synth);
                int count = (int)Math.Min(demand.Waiting, Math.Min(targetConcurrency - memoryLeases.Count - additional, available / forecast));
                additional += count;
                available -= count * forecast;
                if (memoryLeases.Count + additional >= targetConcurrency) break;
            }
            if (memoryLeases.Count == 0 && additional == 0)
            {
                var calibration = synthJobDemand.Values.FirstOrDefault(demand => demand.Waiting > 0 && !memoryProfiles.ContainsKey(demand.Plugin.Synth));
                if (calibration is not null && RenderResourcePolicy.CanStart(memorySnapshot, ReservedMemoryBytes, ForecastMemory(calibration.Plugin.Synth)))
                    additional = 1;
            }
            allowedConcurrency = Math.Min(targetConcurrency, memoryLeases.Count + additional);
        }

        private (int Capacity, string Reason) CurrentConcurrency()
        {
            if (resourceFailure is not null) return (activeWorkers, resourceFailure);
            if (!memorySnapshot.IsComplete) return (activeWorkers, "内存采样未完整，暂停启动新任务。");
            int jobCount = 0, permissionCapacity = 0, measuredCapacity = 0;
            bool observing = false;
            var serial = new List<string>();
            foreach (var demand in synthJobDemand.Values)
            {
                int count = demand.Total;
                if (count == 0) continue;
                jobCount += count;
                int permitted = demand.Plugin.AllowParallelRendering ? count : 1;
                permissionCapacity += permitted;
                if (memoryProfiles.ContainsKey(demand.Plugin.Synth)) measuredCapacity += permitted;
                else observing |= demand.Leased > 0;
                if (count > 1 && !demand.Plugin.AllowParallelRendering) serial.Add(demand.Plugin.Synth.ToString());
            }
            if (jobCount == 0) return (activeWorkers, "本批次没有等待生成的任务。");
            string serialSynths = string.Join("、", serial);
            string permissionReason = serialSynths.Length == 0 ? "" : serialSynths + " 未启用同音源多实例并行。";
            if (observing || memoryProfiles.Count == 0)
                return (Math.Max(activeWorkers, Math.Min(1, allowedConcurrency)),
                    "先单独完成首项，测量实际内存。" + permissionReason);
            // An unmeasured later synth starts alone at an idle boundary. It does
            // not count as a usable slot alongside this synth's running tasks.
            if (measuredCapacity == 0) measuredCapacity = 1;
            int capacity = Math.Max(activeWorkers, Math.Min(targetConcurrency, Math.Min(allowedConcurrency, measuredCapacity)));
            var reasons = new List<string>();
            if (permissionReason.Length > 0) reasons.Add(permissionReason);
            if (allowedConcurrency < Math.Min(targetConcurrency, measuredCapacity))
                reasons.Add($"2 GiB 内存目标限制当前最多 {allowedConcurrency} 条（含预留 {ReservedMemoryBytes / 1048576d:0} MiB）。");
            if (measuredCapacity < permissionCapacity)
                reasons.Add("下一音源须等当前任务结束后单独测量内存。");
            if (jobCount < targetConcurrency)
                reasons.Add($"本批次剩余 {jobCount} 个独立渲染任务。");
            return (capacity, reasons.Count == 0 ? "按音源分组连续生成。" : string.Join("", reasons));
        }

        private void TrackWaitingJobs(List<RenderJob> waiting)
        {
            if (ReferenceEquals(trackedWaitingJobs, waiting)) return;
            trackedWaitingJobs = waiting;
            synthJobDemand.Clear();
            // Count once per batch. Dispatch and lease release maintain these
            // small per-synth counters; frequent progress reports never walk
            // or allocate arrays for the potentially thousands of waiting jobs.
            foreach (var job in waiting)
            {
                if (!synthJobDemand.TryGetValue(job.Plugin.Synth, out var demand))
                    synthJobDemand.Add(job.Plugin.Synth, demand = new(job.Plugin));
                demand.Waiting++;
            }
        }

        private void SetResourceFailure(string reason)
        {
            Interlocked.CompareExchange(ref resourceFailure, reason, null);
            resourceMessage = resourceFailure!;
            summary.ResourceStopped = true;
            allowedConcurrency = 0;
        }

        private void ThrowIfResourceStopped()
        {
            if (Volatile.Read(ref resourceFailure) is string reason) throw new RenderResourceLimitException(reason, summary);
        }

        private Task ResourceChangedTask()
        {
            lock (gate) return resourcesChanged.Task;
        }

        private void SignalResourcesChanged()
        {
            var previous = resourcesChanged;
            resourcesChanged = NewSignal();
            previous.TrySetResult();
        }
    }
}
