using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoundPreviewer.Core.Models;

public sealed class AppearanceSettings : IJsonOnDeserialized
{
    private string mode = "light";
    private bool modeSpecified;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
    public string Preset { get; set; } = "silver";
    public string Mode
    {
        get => mode;
        set { mode = value; modeSpecified = true; }
    }
    // Describes the saved palette, including a palette saved while following the
    // system. It also reads the first development builds' IsDark-only setting.
    public bool IsDark { get; set; }
    public string Accent { get; set; } = "#287E91";
    public string WindowBackground { get; set; } = "#F1F3F5";
    public string Surface { get; set; } = "#FFFFFF";
    public string SurfaceAlt { get; set; } = "#E9EDF1";
    public string TextPrimary { get; set; } = "#263440";
    public string Muted { get; set; } = "#657481";
    public string Line { get; set; } = "#D1D8DF";
    public string SelectionBackground { get; set; } = "#E0F0F3";
    public string HoverBackground { get; set; } = "#EDF5F7";
    public Dictionary<string, string>? LightColors { get; set; }
    public Dictionary<string, string>? DarkColors { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (!modeSpecified && IsDark) mode = "dark";
    }
}
