using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Scanning;

public sealed class ScanResult
{
    public IReadOnlyList<PresetRecord> Presets { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public int Added { get; init; }
    public int Changed { get; init; }
}
