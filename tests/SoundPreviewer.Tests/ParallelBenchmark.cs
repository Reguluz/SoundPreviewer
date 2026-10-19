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
        if (manifest.PresetPaths.Count is < 2 or > 8 || manifest.WarmRuns is < 1 or > 3 || manifest.TimeoutMinutes is < 1 or > 60)
            throw new ArgumentException("Use 2–8 distinct presets, 1–3 warm runs and a 1–60 minute timeout.");
        if (manifest.Slots.Count != 2 || !manifest.Slots.Order().SequenceEqual(new[] { 1, 2 }))
            throw new ArgumentException("Slots must be [1,2] or [2,1].");
        manifest.Request.Template.Validate();
        if (manifest.Request.Preset.Synth != manifest.Request.Plugin.Synth || manifest.Request.Preset.Synth == SynthKind.Unknown ||
            manifest.Request.Preset.BankSlot is not null || manifest.Request.Preset.InternalPath.Length != 0)
            throw new ArgumentException("The source request must describe one configured synth and independent preset files.");
        var inputs = manifest.PresetPaths.Select(path => new BenchmarkInput(Path.GetFullPath(path), Fingerprints.HashFile(path))).ToArray();
        if (inputs.Select(input => input.Sha256).Distinct(StringComparer.Ordinal).Count() != inputs.Length)
            throw new ArgumentException("Identical-content presets would measure singleflight, not parallel generation.");
        var observedFiles = inputs.Select(input => input.Path).Concat(new[] { manifest.Request.Plugin.PluginPath,
            manifest.Request.Plugin.ExecutablePath, manifest.WorkerPath, typeof(Program).Assembly.Location }).Distinct(ApplicationPaths.PathComparer).ToArray();
        var before = observedFiles.ToDictionary(path => path, path => Fingerprints.HashFile(path), ApplicationPaths.PathComparer);
        var phases = new List<object>();
        var timings = new Dictionary<int, List<double>>();
        string? error = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(manifest.TimeoutMinutes));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; timeout.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            foreach (int slots in manifest.Slots)
            {
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
                timings[slots] = [];
                await MeasureAsync("cold", force: false, expectReused: false);
                for (int warm = 1; warm <= manifest.WarmRuns; warm++)
                    await MeasureAsync("warm-force-" + warm, force: true, expectReused: false);
                await MeasureAsync("cache-reuse", force: false, expectReused: true);

                async Task MeasureAsync(string phase, bool force, bool expectReused)
                {
                    Console.WriteLine($"Benchmark {slots} slot(s): {phase}, {records.Count} different presets.");
                    var progress = new BenchmarkProgress();
                    var watch = Stopwatch.StartNew();
                    QueueResult result;
                    try { result = await new RenderQueue(store).RunAsync(settings, records, manifest.WorkerPath, progress, timeout.Token, force); }
                    catch (Exception exception)
                    {
                        phases.Add(new { Slots = slots, Phase = phase, WallSeconds = watch.Elapsed.TotalSeconds, Error = exception.ToString(), Events = progress.Events });
                        throw;
                    }
                    watch.Stop();
                    if (result.Failed != 0 || result.Unconfigured != 0 || result.RetryLimited != 0 || result.Excluded != 0 ||
                        result.Generated != (expectReused ? 0 : records.Count) || result.Reused != (expectReused ? records.Count : 0))
                    {
                        phases.Add(new { Slots = slots, Phase = phase, WallSeconds = watch.Elapsed.TotalSeconds, Queue = result, Events = progress.Events });
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
                    phases.Add(new { Slots = slots, Phase = phase, WallSeconds = watch.Elapsed.TotalSeconds, Queue = result,
                        OverlapObserved = result.PeakActiveCount > 1, Events = progress.Events, Audio = audio });
                    if (records.Select(record => record.PreviewPath).Distinct(ApplicationPaths.PathComparer).Count() != records.Count)
                        throw new InvalidDataException("Different input contents unexpectedly share an audio path.");
                    if (!expectReused && phase.StartsWith("warm-force-", StringComparison.Ordinal)) timings[slots].Add(watch.Elapsed.TotalSeconds);
                    Console.WriteLine($"{phase}: {watch.Elapsed.TotalSeconds:F3}s, generated={result.Generated}, reused={result.Reused}, peak active={result.PeakActiveCount}.");
                }
            }
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally { Console.CancelKeyPress -= cancel; }
        var hashes = before.Select(pair => new { Path = pair.Key, Before = pair.Value,
            After = File.Exists(pair.Key) ? Fingerprints.HashFile(pair.Key) : "missing" }).ToArray();
        bool unchanged = hashes.All(hash => hash.Before == hash.After);
        if (!unchanged) error = (error ?? "") + " Input, plugin or renderer bytes changed during measurement.";
        double? ratio = timings.TryGetValue(1, out var serial) && serial.Count > 0 && timings.TryGetValue(2, out var parallel) && parallel.Count > 0
            ? serial.Average() / parallel.Average() : null;
        var report = new
        {
            Success = error is null, Error = error, CreatedUtc = DateTimeOffset.UtcNow,
            Environment = new { OS = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Framework = RuntimeInformation.FrameworkDescription },
            manifest.Request.Plugin, manifest.Request.Template, manifest.Slots, Inputs = inputs, SourceFilesUnchanged = unchanged, Hashes = hashes,
            WarmForceSpeedup = ratio, Phases = phases,
            Limitations = "Small sample, not a batch compatibility promise. Cold includes baseline setup; warm-force reuses baseline but creates fresh audio. OS file caches are not flushed. Use reversed Slots in a fresh run before generalizing. PeakActiveCount counts overlapping preview worker calls, not CPU utilization. Audio mapping/metrics do not independently prove commercial preset identity or resource completeness; listen before confirming. Commercial plugins may write their normal user configuration."
        };
        await using (var stream = new FileStream(Path.Combine(root, "report.json"), FileMode.CreateNew, FileAccess.Write))
            await JsonSerializer.SerializeAsync(stream, report, LibraryJson.Options);
        if (error is not null) Console.Error.WriteLine(error);
        return error is null ? 0 : 1;
    }

    private sealed class ParallelBenchmarkManifest
    {
        public ParallelBenchmarkManifest() { }
        public RenderRequest Request { get; set; } = new();
        public List<string> PresetPaths { get; set; } = [];
        public string WorkerPath { get; set; } = "";
        public string ArtifactDirectory { get; set; } = "";
        public List<int> Slots { get; set; } = [1, 2];
        public int WarmRuns { get; set; } = 2;
        public int TimeoutMinutes { get; set; } = 15;
    }

    private sealed record BenchmarkInput(string Path, string Sha256);
    private sealed record BenchmarkEvent(DateTimeOffset Utc, double ElapsedSeconds, string Stage, int ActiveCount, int MaxConcurrency,
        string? PresetId, string Outcome, string Message);
    private sealed class BenchmarkProgress : IProgress<QueueProgress>
    {
        private readonly object gate = new();
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly List<BenchmarkEvent> events = [];
        public BenchmarkEvent[] Events { get { lock (gate) return events.ToArray(); } }
        public void Report(QueueProgress progress)
        {
            lock (gate) events.Add(new(DateTimeOffset.UtcNow, watch.Elapsed.TotalSeconds, progress.Stage, progress.ActiveCount, progress.MaxConcurrency, progress.Preset?.Id,
                progress.Preset?.LastRenderOutcome ?? "", progress.Message));
        }
    }
}
