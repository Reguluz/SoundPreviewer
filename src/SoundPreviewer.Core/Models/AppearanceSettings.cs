using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoundPreviewer.Core.Models;

public sealed class AppearanceSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
    public string Preset { get; set; } = "teal";
    public bool IsDark { get; set; }
    public string Accent { get; set; } = "#168571";
    public string WindowBackground { get; set; } = "#F3F5F8";
    public string Surface { get; set; } = "#FFFFFF";
    public string SurfaceAlt { get; set; } = "#F7F9FC";
    public string TextPrimary { get; set; } = "#202B3D";
    public string Muted { get; set; } = "#67758A";
    public string Line { get; set; } = "#E2E7EF";
    public string SelectionBackground { get; set; } = "#E1F3EE";
    public string HoverBackground { get; set; } = "#EDF2F7";
}
