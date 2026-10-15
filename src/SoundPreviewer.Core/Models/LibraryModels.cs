using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoundPreviewer.Core.Models;

public enum SynthKind { Vital, Serum1, Serum2, Sylenth1, Unknown = 4 }
public enum PresetStatus { Pending, Rendering, Ready, Failed, NeedsReview, Unsupported, Missing, Unidentified = 7 }
public enum RenderBackend { Unconfigured, VitalCli, ExternalWorker, DawDreamer }

public sealed class PresetRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public SynthKind Synth { get; set; }
    public string DetectionReason { get; set; } = "";
    public bool SynthAssignedManually { get; set; }
    public List<string> Tags { get; set; } = [];
    public string SourceId { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string ContentMd5 { get; set; } = "";
    public string DuplicateOfId { get; set; } = "";
    public string Package { get; set; } = "";
    public string InternalPath { get; set; } = "";
    public int? BankSlot { get; set; }
    public long FileSize { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public PresetStatus Status { get; set; } = PresetStatus.Pending;
    public int Attempts { get; set; }
    public string Message { get; set; } = "";
    public string PreviewPath { get; set; } = "";
    public string CacheKey { get; set; } = "";
    public string AttemptedCacheKey { get; set; } = "";
    public bool Favorite { get; set; }
    public int Feedback { get; set; }
    public DateTime? FeedbackUtc { get; set; }
    public DateTime? RenderedUtc { get; set; }
    public AudioMetrics? Metrics { get; set; }
    public List<NotePreview> Notes { get; set; } = [];
    public string AdapterVersion { get; set; } = "";
    public string PluginVersion { get; set; } = "";
    public string TemplateJson { get; set; } = "";
}

public sealed class SourceFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public SynthKind Synth { get; set; }
    public bool AutoDetect { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class PluginConfiguration
{
    public SynthKind Synth { get; set; }
    public RenderBackend Backend { get; set; }
    public string ExecutablePath { get; set; } = "";
    public string PluginPath { get; set; } = "";
    public string Version { get; set; } = "";
    public string ExecutableHash { get; set; } = "";
    public string PluginHash { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed class PreviewTemplate
{
    public int LowNote { get; set; } = 36;
    public int MiddleNote { get; set; } = 60;
    public int HighNote { get; set; } = 84;
    public int Velocity { get; set; } = 89;
    public double HoldSeconds { get; set; } = 2;
    public double TailSeconds { get; set; } = 0.6;
    public int Bpm { get; set; } = 120;
    public int SampleRate { get; set; } = 44100;
    public int[] Pitches => [LowNote, MiddleNote, HighNote];

    public void Validate()
    {
        if (Pitches.Any(n => n is < 0 or > 127)) throw new ArgumentException("MIDI 音高须为 0 到 127。");
        if (Velocity is < 1 or > 127) throw new ArgumentException("力度须为 1 到 127。");
        if (!double.IsFinite(HoldSeconds) || HoldSeconds is < 0.1 or > 30) throw new ArgumentException("按键时长须为 0.1 到 30 秒。");
        if (!double.IsFinite(TailSeconds) || TailSeconds is < 0 or > 30) throw new ArgumentException("尾音须为 0 到 30 秒。");
        if (Bpm is < 20 or > 300) throw new ArgumentException("BPM 须为 20 到 300。");
        if (SampleRate is not (44100 or 48000)) throw new ArgumentException("采样率须为 44100 或 48000 Hz。");
    }
}

public sealed class AppSettings
{
    public List<SourceFolder> Sources { get; set; } = [];
    public List<string> PluginDirectories { get; set; } = [];
    public List<DiscoveredPlugin> DiscoveredPlugins { get; set; } = [];
    public List<PluginConfiguration> Plugins { get; set; } = Enum.GetValues<SynthKind>().Where(s => s != SynthKind.Unknown).Select(s => new PluginConfiguration { Synth = s }).ToList();
    public PreviewTemplate Template { get; set; } = new();
    public string PreviewDirectory { get; set; } = "";
    public double StorageBudgetGb { get; set; } = 10;
    public int MaxAttempts { get; set; } = 2;
    public double PlaybackVolume { get; set; } = 0.7;
}

public sealed class AudioMetrics
{
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public double DurationSeconds { get; set; }
    public double Peak { get; set; }
    public double Rms { get; set; }
    public double ZeroCrossingRate { get; set; }
    public bool IsSilent { get; set; }
    public bool HasClipping { get; set; }
    public bool HasNonFinite { get; set; }
    public string Version { get; set; } = "wave-metrics-v1";
}

public sealed class NotePreview
{
    public int MidiNote { get; set; }
    public double StartSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public double ActualHoldSeconds { get; set; }
    public double ActualTailSeconds { get; set; }
    public int ActualVelocity { get; set; }
    public double? ActualNormalizedVelocity { get; set; }
    public AudioMetrics? Metrics { get; set; }
}

public sealed class RenderRequest
{
    public PresetRecord Preset { get; set; } = new();
    public PluginConfiguration Plugin { get; set; } = new();
    public PreviewTemplate Template { get; set; } = new();
    public string OutputPath { get; set; } = "";
    public string CacheKey { get; set; } = "";
    public long MaxOutputBytes { get; set; } = 256L * 1024 * 1024;
}

public sealed class RenderResult
{
    public bool Success { get; set; }
    public string OutputPath { get; set; } = "";
    public string Message { get; set; } = "";
    public bool NeedsReview { get; set; }
    public AudioMetrics? Metrics { get; set; }
    public List<NotePreview> Notes { get; set; } = [];
    public string AdapterVersion { get; set; } = "";
}

public static class LibraryJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}

public static class Fingerprints
{
    public const string AdapterVersion = "soundpreviewer-render-v1";
    public static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static string HashFile(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = stream.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            hash.AppendData(buffer, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    public static string PluginBinaryHash(PluginConfiguration plugin, IReadOnlyList<DiscoveredPlugin> discovered, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(plugin.PluginPath)) return HashFile(plugin.PluginPath, cancellationToken);
        if (!Directory.Exists(plugin.PluginPath)) return "";
        var binaries = discovered.Where(p => string.Equals(p.FilePath, plugin.PluginPath, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.BinaryPath).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => new { Path = p.ToUpperInvariant(), Hash = HashFile(p, cancellationToken) }).ToList();
        cancellationToken.ThrowIfCancellationRequested();
        return binaries.Count == 0 ? "" : HashText(JsonSerializer.Serialize(binaries));
    }
    public static string PresetId(SynthKind synth, string path, string internalPath = "", int? slot = null) =>
        HashText($"{synth}|{System.IO.Path.GetFullPath(path).ToUpperInvariant()}|{internalPath}|{slot}");
    public static string CacheKey(PresetRecord preset, PluginConfiguration plugin, PreviewTemplate template) =>
        HashText(JsonSerializer.Serialize(new { preset.ContentHash, preset.InternalPath, preset.BankSlot, preset.Synth, plugin, template, AdapterVersion }));
}
