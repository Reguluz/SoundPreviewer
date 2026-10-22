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
    // An explicit manual entry point; normal CI tests never load commercial plugins.
    private static async Task<int> TestParallelBenchmarkAsync(string manifestPath)
    {
        var manifest = JsonSerializer.Deserialize<ParallelBenchmarkManifest>(await File.ReadAllTextAsync(manifestPath), LibraryJson.Options)
            ?? throw new InvalidDataException("Benchmark manifest is empty.");
        if (!OperatingSystem.IsWindows() || !Path.GetExtension(manifest.Request.Plugin.PluginPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("This small-sample harness supports Windows DLL plugins only; the application queue remains cross-platform.");
        string repository = FindRepository();
        string root = Path.GetFullPath(manifest.ArtifactDirectory);
        string artifacts = Path.Combine(repository, "artifacts");
        if (!ApplicationPaths.IsWithin(root, artifacts) || Path.GetFileName(root).Length != "parallel-benchmark-".Length + 32 ||
            !Path.GetFileName(root).StartsWith("parallel-benchmark-", StringComparison.Ordinal))
            throw new ArgumentException("Benchmark output must be a new parallel-benchmark-GUID directory under repository artifacts.");
        for (string? path = root; path is not null && !ApplicationPaths.SamePath(path, repository); path = Path.GetDirectoryName(path))
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing a linked benchmark output directory.");
        if (!File.Exists(manifest.WorkerPath)) throw new FileNotFoundException("Build the worker before measuring; this harness does not build it.", manifest.WorkerPath);
        if (manifest.PresetPaths.Count is < 4 or > 16 || manifest.WarmRuns is < 1 or > 3 || manifest.TimeoutMinutes is < 1 or > 60)
            throw new ArgumentException("Use 4–16 distinct presets, 1–3 warm runs and a 1–60 minute timeout.");
        if (manifest.Slots.Count == 0 || manifest.Slots.Distinct().Count() != manifest.Slots.Count ||
            manifest.Slots.Any(value => value is not (0 or 1 or 2 or 4 or 8 or 16)))
            throw new ArgumentException("Slots must contain distinct values from 0 (automatic), 1, 2, 4, 8, and 16.");
        manifest.Request.Template.Validate();
        if (manifest.Request.Preset.Synth != manifest.Request.Plugin.Synth || manifest.Request.Preset.Synth == SynthKind.Unknown ||
            manifest.Request.Preset.BankSlot is not null || manifest.Request.Preset.InternalPath.Length != 0)
            throw new ArgumentException("The source request must describe one configured synth and independent preset files.");
        var inputs = manifest.PresetPaths.Select(path => new BenchmarkInput(Path.GetFullPath(path), Fingerprints.HashFile(path))).ToArray();
        if (inputs.Select(input => input.Sha256).Distinct(StringComparer.Ordinal).Count() != inputs.Length)
            throw new ArgumentException("Identical-content presets would measure singleflight, not parallel generation.");
        var observedFiles = inputs.Select(input => input.Path).Concat(new[] { manifest.Request.Plugin.PluginPath,
            manifest.Request.Plugin.ExecutablePath, manifest.WorkerPath, typeof(Program).Assembly.Location,
            typeof(RenderQueue).Assembly.Location }).Distinct(ApplicationPaths.PathComparer).ToArray();
        var before = observedFiles.ToDictionary(path => path, path => Fingerprints.HashFile(path), ApplicationPaths.PathComparer);
        var phases = new List<object>();
        var configurations = new List<object>();
        var resourceMeasurements = new List<BenchmarkResourceSummary>();
        var resourceLimited = new List<int>();
        var timings = new Dictionary<int, List<double>>();
        string? error = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(manifest.TimeoutMinutes));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; timeout.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            foreach (int slots in manifest.Slots)
            {
                if (slots > 0 && resourceLimited.Any(limited => limited > 0 && slots >= limited))
                {
                    phases.Add(new { RequestedConcurrency = slots, Phase = "skipped", Reason = "An earlier equal/lower requested setting reached the fixed resource limit; no higher setting is attempted." });
                    continue;
                }
                string directory = Path.Combine(root, "slots-" + slots);
                if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Benchmark slot directories must be new; use a fresh manifest.");
                Directory.CreateDirectory(directory);
                ApplicationPaths.ConfigureDataDirectory(directory);
                var plugin = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(manifest.Request.Plugin, LibraryJson.Options), LibraryJson.Options)!;
                plugin.AllowParallelRendering = true;
                plugin.InitBaselinePath = "";
                plugin.ExecutableHash = Fingerprints.HashFile(plugin.ExecutablePath);
                plugin.PluginHash = Fingerprints.HashFile(plugin.PluginPath);
                var settings = new AppSettings
                {
                    PreviewFormat = PreviewAudioFormat.Wav, // Retain the benchmark's original raw-audio snapshot/hash contract.
                    Plugins = [plugin], Template = manifest.Request.Template, PreviewDirectory = directory,
                    MaxParallelRenders = slots, MaxAttempts = 1, StorageBudgetGb = 1
                };
                var records = inputs.Select((input, index) => new PresetRecord
                {
                    Id = Fingerprints.PresetId(plugin.Synth, input.Path), Name = Path.GetFileNameWithoutExtension(input.Path),
                    Synth = plugin.Synth, SourceId = "manual-benchmark", SourcePath = input.Path, ContentHash = input.Sha256,
                    FileSize = new FileInfo(input.Path).Length, LastWriteUtc = File.GetLastWriteTimeUtc(input.Path), Status = PresetStatus.Pending
                }).ToList();
                var store = new LibraryStore(Path.Combine(directory, "library.db"));
                store.UpsertMany(records);
                store.SaveSettings(settings);
                configurations.Add(new { RequestedConcurrency = slots, ActualSettings = JsonSerializer.SerializeToElement(settings, LibraryJson.Options) });
                timings[slots] = [];
                if (!await MeasureAsync("cold", force: false, expectReused: false)) continue;
                for (int warm = 1; warm <= manifest.WarmRuns; warm++)
                    if (!await MeasureAsync("warm-force-" + warm, force: true, expectReused: false)) break;
                if (!resourceLimited.Contains(slots)) await MeasureAsync("cache-reuse", force: false, expectReused: true);

                async Task<bool> MeasureAsync(string phase, bool force, bool expectReused)
                {
                    Console.WriteLine($"Benchmark requested concurrency {slots} (0=automatic): {phase}, {records.Count} different presets.");
                    var progress = new BenchmarkProgress();
                    var sampler = new BenchmarkResourceSampler(new SystemRenderResourceProbe());
                    var watch = Stopwatch.StartNew();
                    QueueResult? result = null;
                    Exception? failure = null;
                    try { result = await new RenderQueue(store).RunAsync(settings, records, manifest.WorkerPath, progress, timeout.Token, force); }
                    catch (RenderResourceLimitException exception) { result = exception.Summary; }
                    catch (Exception exception) { failure = exception; }
                    watch.Stop();
                    var resources = await sampler.StopAsync();
                    resourceMeasurements.Add(resources);
                    var actualSettings = JsonSerializer.SerializeToElement(settings, LibraryJson.Options);
                    if (failure is not null || result is null)
                    {
                        phases.Add(new { RequestedConcurrency = slots, Phase = phase, WallSeconds = watch.Elapsed.TotalSeconds,
                            ActualSettings = actualSettings, Resources = resources, Error = failure?.ToString(), Events = progress.Events });
                        throw new InvalidOperationException("Benchmark queue did not complete.", failure);
                    }
                    bool observedBudgetExceeded = result.MemoryBudgetBytes > 0 && resources.PeakTreeResidentBytes > result.MemoryBudgetBytes;
                    if (result.ResourceStopped || observedBudgetExceeded)
                    {
                        resourceLimited.Add(slots);
                        phases.Add(new { result.RequestedConcurrency, result.ResolvedConcurrency, result.MemoryBudgetBytes,
                            result.PeakWorkingSetBytes, result.ResourceStopped, ObservedBudgetExceeded = observedBudgetExceeded,
                            Phase = phase, WallSeconds = watch.Elapsed.TotalSeconds,
                            ActualSettings = actualSettings, Queue = result, Resources = resources, Events = progress.Events });
                        Console.WriteLine($"Resource-limited result: requested={result.RequestedConcurrency}, resolved={result.ResolvedConcurrency}, peak active={result.PeakActiveCount}, queue stopped={result.ResourceStopped}, sampled RSS over budget={observedBudgetExceeded}. No retry or budget increase.");
                        return false;
                    }
                    if (result.Failed != 0 || result.Unconfigured != 0 || result.RetryLimited != 0 || result.Excluded != 0 ||
                        result.Generated != (expectReused ? 0 : records.Count) || result.Reused != (expectReused ? records.Count : 0))
                    {
                        phases.Add(new { result.RequestedConcurrency, result.ResolvedConcurrency, result.MemoryBudgetBytes,
                            result.PeakWorkingSetBytes, result.ResourceStopped, Phase = phase, WallSeconds = watch.Elapsed.TotalSeconds,
                            ActualSettings = actualSettings, Queue = result, Resources = resources, Events = progress.Events });
                        throw new InvalidDataException("Benchmark did not generate/reuse the complete expected batch: " + result.Summary);
                    }
                    string snapshots = Path.Combine(directory, "evidence", phase);
                    Directory.CreateDirectory(snapshots);
                    var audio = records.Select(record =>
                    {
                        if (!ApplicationPaths.IsWithin(record.PreviewPath, directory) || record.CacheKey != Fingerprints.CacheKey(record, plugin, settings.Template) ||
                            Path.GetFileName(record.PreviewPath) != record.CacheKey + ".wav")
                            throw new InvalidDataException("Preset-to-cache mapping is incorrect: " + record.Name);
                        var metrics = WaveFile.Validate(record.PreviewPath, timeout.Token);
                        WorkerProcessClient.ValidateNotes(new RenderResult { Metrics = metrics, Notes = record.Notes, AdapterVersion = record.AdapterVersion }, settings.Template);
                        string snapshot = Path.Combine(snapshots, record.CacheKey + ".wav");
                        File.Copy(record.PreviewPath, snapshot, overwrite: false);
                        return new { record.Id, record.Name, record.SourcePath, record.ContentHash, record.CacheKey, record.PreviewPath,
                            AudioSnapshotPath = snapshot, AudioSha256 = Fingerprints.HashFile(snapshot, timeout.Token), Metrics = metrics, record.Notes, record.Message };
                    }).ToArray();
                    phases.Add(new { result.RequestedConcurrency, result.ResolvedConcurrency, result.MemoryBudgetBytes,
                        result.PeakWorkingSetBytes, result.ResourceStopped, Phase = phase, WallSeconds = watch.Elapsed.TotalSeconds,
                        ActualSettings = actualSettings, Queue = result, Resources = resources,
                        OverlapObserved = result.PeakActiveCount > 1, Events = progress.Events, Audio = audio });
                    if (records.Select(record => record.PreviewPath).Distinct(ApplicationPaths.PathComparer).Count() != records.Count)
                        throw new InvalidDataException("Different input contents unexpectedly share an audio path.");
                    if (!expectReused && phase.StartsWith("warm-force-", StringComparison.Ordinal)) timings[slots].Add(watch.Elapsed.TotalSeconds);
                    Console.WriteLine($"{phase}: {watch.Elapsed.TotalSeconds:F3}s, generated={result.Generated}, reused={result.Reused}, requested/resolved={result.RequestedConcurrency}/{result.ResolvedConcurrency}, peak active={result.PeakActiveCount}, tree RSS peak={resources.PeakTreeResidentBytes / 1048576d:F1} MiB, observed CPU={resources.ObservedCpuSecondsLowerBound:F3}s.");
                    return true;
                }
            }
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally { Console.CancelKeyPress -= cancel; }
        var hashes = before.Select(pair => new { Path = pair.Key, Before = pair.Value,
            After = File.Exists(pair.Key) ? Fingerprints.HashFile(pair.Key) : "missing" }).ToArray();
        bool unchanged = hashes.All(hash => hash.Before == hash.After);
        if (!unchanged) error = (error ?? "") + " Input, plugin or renderer bytes changed during measurement.";
        var comparisons = timings.Where(pair => pair.Value.Count > 0).Select(pair => new
        {
            RequestedConcurrency = pair.Key, WarmMeanSeconds = pair.Value.Average(), WarmRunCount = pair.Value.Count,
            ResourceLimited = resourceLimited.Contains(pair.Key),
            SpeedupVersusRequestedOne = timings.TryGetValue(1, out var serial) && serial.Count > 0 && !resourceLimited.Contains(pair.Key)
                ? (double?)(serial.Average() / pair.Value.Average()) : null
        }).ToArray();
        var report = new
        {
            Success = error is null && resourceLimited.Count == 0, Error = error, ResourceLimitedRequests = resourceLimited, CreatedUtc = DateTimeOffset.UtcNow,
            Environment = new { OS = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Framework = RuntimeInformation.FrameworkDescription },
            manifest.Request.Template, manifest.Slots, Configurations = configurations, Inputs = inputs, SourceFilesUnchanged = unchanged, Hashes = hashes,
            ResourceMeasurementsComplete = resourceMeasurements.Count > 0 && resourceMeasurements.All(measurement => measurement.IncompleteSampleCount == 0),
            WarmComparisons = comparisons, Phases = phases,
            Limitations = "Small sample, not a batch compatibility promise. Cold includes baseline setup; warm-force reuses baseline but creates fresh audio. The measured root is this benchmark process, not the Avalonia desktop. RSS sums its root and descendants (including shared pages more than once) at 100ms intervals; short peaks and short-lived CPU may be missed. CPU time is a lower bound from observed processes; normalized CPU percent divides by visible logical processors. OS file caches are not flushed. Requested concurrency can exceed resolved or active concurrency due to CPU, calibration, remaining work and the fixed resource budget. Use reversed Slots in a fresh run before generalizing. Audio mapping/metrics do not independently prove commercial preset identity or resource completeness; listen before confirming. Commercial plugins may write their normal user configuration."
        };
        await using (var stream = new FileStream(Path.Combine(root, "report.json"), FileMode.CreateNew, FileAccess.Write))
            await JsonSerializer.SerializeAsync(stream, report, LibraryJson.Options);
        if (error is not null) Console.Error.WriteLine(error);
        return error is null && resourceLimited.Count == 0 ? 0 : 1;
    }

    private sealed class ParallelBenchmarkManifest
    {
        public ParallelBenchmarkManifest() { }
        public RenderRequest Request { get; set; } = new();
        public List<string> PresetPaths { get; set; } = [];
        public string WorkerPath { get; set; } = "";
        public string ArtifactDirectory { get; set; } = "";
        public List<int> Slots { get; set; } = [1, 2, 4];
        public int WarmRuns { get; set; } = 2;
        public int TimeoutMinutes { get; set; } = 15;
    }

    private sealed record BenchmarkInput(string Path, string Sha256);
    private sealed record BenchmarkEvent(DateTimeOffset Utc, double ElapsedSeconds, string Stage, int ActiveCount, int MaxConcurrency,
        int AllowedConcurrency, long WorkingSetBytes, long MemoryBudgetBytes, string ResourceMessage,
        string? PresetId, string Outcome, string Message);
    private sealed class BenchmarkProgress : IProgress<QueueProgress>
    {
        private readonly object gate = new();
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly List<BenchmarkEvent> events = [];
        public BenchmarkEvent[] Events { get { lock (gate) return events.ToArray(); } }
        public void Report(QueueProgress progress)
        {
            lock (gate) events.Add(new(DateTimeOffset.UtcNow, watch.Elapsed.TotalSeconds, progress.Stage, progress.ActiveCount, progress.MaxConcurrency,
                progress.AllowedConcurrency, progress.WorkingSetBytes, progress.MemoryBudgetBytes, progress.ResourceMessage, progress.Preset?.Id,
                progress.Preset?.LastRenderOutcome ?? "", progress.Message));
        }
    }

    private sealed record BenchmarkResourceSample(DateTimeOffset Utc, double ElapsedSeconds,
        double ObservedCpuSecondsLowerBound, double CpuPercentOfLogicalCapacityLowerBound, RenderMemorySnapshot Snapshot);

    private sealed record BenchmarkResourceSummary(RenderHardwareInfo Hardware, int RootProcessId, int SampleIntervalMilliseconds,
        long PeakTreeResidentBytes, long PeakRootResidentBytes, int PeakProcessCount, int SampleCount, int IncompleteSampleCount,
        int MissingCpuTimeObservations, int CpuCounterResetObservations, double MaxSampleGapSeconds,
        double ObservedCpuSecondsLowerBound, double AverageCpuCoresLowerBound, double CpuPercentOfLogicalCapacityLowerBound,
        double PeakCpuPercentOfLogicalCapacityLowerBound, string[] Errors, BenchmarkResourceSample[] Samples);

    // Uses the same public probe as the queue. Keep observed process CPU counters
    // after exit; summing only the final live snapshot would omit child work.
    private sealed class BenchmarkResourceSampler
    {
        private readonly IRenderResourceProbe probe;
        private readonly RenderHardwareInfo hardware;
        private readonly CancellationTokenSource stop = new();
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly List<BenchmarkResourceSample> samples = [];
        private readonly Dictionary<int, double> cpuCounters = [];
        private readonly HashSet<string> errors = new(StringComparer.Ordinal);
        private readonly Task sampling;
        private long peakTree, peakRoot;
        private int peakProcesses, incompleteSamples, missingCpuTimes, cpuResets;
        private double cpuSeconds, peakCpuPercent, maxSampleGap;

        public BenchmarkResourceSampler(IRenderResourceProbe resourceProbe)
        {
            probe = resourceProbe;
            hardware = probe.Hardware;
            Capture();
            sampling = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(100, stop.Token);
                        Capture();
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            });
        }

        private void Capture()
        {
            RenderMemorySnapshot snapshot;
            try { snapshot = probe.Capture(); }
            catch (Exception error) { snapshot = new(0, false, 0, error.Message); }
            double elapsed = watch.Elapsed.TotalSeconds;
            double gap = samples.Count == 0 ? 0 : elapsed - samples[^1].ElapsedSeconds;
            maxSampleGap = Math.Max(maxSampleGap, gap);
            peakTree = Math.Max(peakTree, snapshot.ResidentBytes);
            peakRoot = Math.Max(peakRoot, snapshot.RootResidentBytes);
            peakProcesses = Math.Max(peakProcesses, snapshot.ProcessCount);
            if (!snapshot.IsComplete) incompleteSamples++;
            if (!string.IsNullOrWhiteSpace(snapshot.Error)) errors.Add(snapshot.Error);
            double delta = 0;
            foreach (var process in snapshot.Processes)
            {
                if (process.CpuSeconds is not double current || !double.IsFinite(current) || current < 0)
                {
                    missingCpuTimes++;
                    continue;
                }
                if (cpuCounters.TryGetValue(process.ProcessId, out double previous))
                {
                    if (current < previous) cpuResets++;
                    delta += current >= previous ? current - previous : current;
                }
                else if (samples.Count != 0) delta += current;
                cpuCounters[process.ProcessId] = current;
            }
            cpuSeconds += delta;
            double percent = gap > 0 ? delta / gap / Math.Max(1, hardware.LogicalProcessorCount) * 100 : 0;
            peakCpuPercent = Math.Max(peakCpuPercent, percent);
            samples.Add(new(DateTimeOffset.UtcNow, elapsed, cpuSeconds, percent, snapshot));
        }

        public async Task<BenchmarkResourceSummary> StopAsync()
        {
            stop.Cancel();
            await sampling;
            Capture();
            watch.Stop();
            stop.Dispose();
            double seconds = watch.Elapsed.TotalSeconds;
            double cores = seconds > 0 ? cpuSeconds / seconds : 0;
            return new(hardware, Environment.ProcessId, 100, peakTree, peakRoot, peakProcesses, samples.Count,
                incompleteSamples, missingCpuTimes, cpuResets, maxSampleGap, cpuSeconds, cores,
                cores / Math.Max(1, hardware.LogicalProcessorCount) * 100, peakCpuPercent, errors.ToArray(), samples.ToArray());
        }
    }
}
