using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoundPreviewer.Core.Models;

public sealed class AppearanceSettings : IJsonOnDeserialized
{
    private string mode = "light";
    private bool modeSpecified;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
    public string Preset { get; set; } = "sage";
    public string Mode
    {
        get => mode;
        set { mode = value; modeSpecified = true; }
    }
    // Describes the saved palette, including a palette saved while following the
    // system. It also reads the first development builds' IsDark-only setting.
    public bool IsDark { get; set; }
    public string Accent { get; set; } = "#53694D";
    public string WindowBackground { get; set; } = "#F8FAF4";
    public string Surface { get; set; } = "#FFFFFF";
    public string SurfaceAlt { get; set; } = "#F0F3EA";
    public string TextPrimary { get; set; } = "#1A2118";
    public string Muted { get; set; } = "#66705F";
    public string Line { get; set; } = "#E0E5DC";
    public string SelectionBackground { get; set; } = "#D9E8CC";
    public string HoverBackground { get; set; } = "#EAF0E3";
    public Dictionary<string, string>? LightColors { get; set; }
    public Dictionary<string, string>? DarkColors { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (!modeSpecified && IsDark) mode = "dark";
    }
}
