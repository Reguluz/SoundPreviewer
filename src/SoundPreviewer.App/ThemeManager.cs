using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

internal static class ThemeManager
{
    internal static readonly (string Key, string Label)[] ColorFields =
    [
        (nameof(AppearanceSettings.Accent), "强调色"),
        (nameof(AppearanceSettings.WindowBackground), "窗口背景"),
        (nameof(AppearanceSettings.Surface), "面板背景"),
        (nameof(AppearanceSettings.SurfaceAlt), "次级面板"),
        (nameof(AppearanceSettings.TextPrimary), "主要文字"),
        (nameof(AppearanceSettings.Muted), "辅助文字"),
        (nameof(AppearanceSettings.Line), "边框与分隔线"),
        (nameof(AppearanceSettings.SelectionBackground), "选中背景"),
        (nameof(AppearanceSettings.HoverBackground), "悬停背景")
    ];

    internal static AppearanceSettings Copy(AppearanceSettings appearance) =>
        JsonSerializer.Deserialize<AppearanceSettings>(JsonSerializer.Serialize(appearance, LibraryJson.Options), LibraryJson.Options)!;

    internal static AppearanceSettings Preset(string key) => key switch
    {
        "slate" => new AppearanceSettings
        {
            Preset = "slate", IsDark = true, Accent = "#5DD5B8", WindowBackground = "#141B26",
            Surface = "#1D2735", SurfaceAlt = "#243142", TextPrimary = "#E5ECF6", Muted = "#A0B0C7",
            Line = "#38485D", SelectionBackground = "#284F50", HoverBackground = "#2D3B4E"
        },
        "indigo" => new AppearanceSettings
        {
            Preset = "indigo", Accent = "#6755C7", WindowBackground = "#F3F2F9", Surface = "#FFFFFF",
            SurfaceAlt = "#F8F7FC", TextPrimary = "#29263D", Muted = "#746E89", Line = "#E4E0EE",
            SelectionBackground = "#EEE9FC", HoverBackground = "#F0EDF7"
        },
        _ => new AppearanceSettings()
    };

    internal static bool TryColor(string? value, out Color color)
    {
        string text = value?.Trim() ?? "";
        color = default;
        if (text.Length != 7 || text[0] != '#' ||
            !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    internal static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    // Old libraries have no appearance section. A damaged individual color falls
    // back to its preset without preventing the library from opening.
    internal static AppearanceSettings Normalize(AppearanceSettings? appearance)
    {
        var fallback = Preset(appearance?.Preset ?? "teal");
        if (appearance is null) return fallback;
        var result = Copy(appearance);
        foreach (var (key, _) in ColorFields)
        {
            var property = typeof(AppearanceSettings).GetProperty(key)!;
            property.SetValue(result, TryColor((string?)property.GetValue(result), out var color)
                ? Hex(color) : property.GetValue(fallback));
        }
        return result;
    }

    internal static Color AccentText(Color accent)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        double luminance = 0.2126 * Linear(accent.R) + 0.7152 * Linear(accent.G) + 0.0722 * Linear(accent.B);
        return (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05) ? Colors.Black : Colors.White;
    }

    internal static void Apply(AppearanceSettings? appearance)
    {
        if (Application.Current is not { } application) return;
        var palette = Normalize(appearance);
        foreach (var (key, _) in ColorFields)
            SetBrush(key, Color.Parse((string)typeof(AppearanceSettings).GetProperty(key)!.GetValue(palette)!));
        var accent = Color.Parse(palette.Accent);
        SetBrush("AccentForeground", AccentText(accent));
        SetBrush("Warning", Color.Parse(palette.IsDark ? "#F3BE70" : "#B97528"));
        SetBrush("Success", Color.Parse(palette.IsDark ? "#5DD5B8" : "#168571"));
        SetBrush("Danger", Color.Parse(palette.IsDark ? "#FF8794" : "#D04F5F"));

        // Fluent's sliders, checks and tab indicators share the chosen accent.
        foreach (var theme in application.Styles.OfType<FluentTheme>())
        {
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                if (!theme.Palettes.TryGetValue(variant, out var colors))
                    theme.Palettes[variant] = colors = new ColorPaletteResources();
                colors.Accent = accent;
            }
        }
        application.Resources["SystemAccentColor"] = accent;
        foreach (var suffix in new[] { "Light1", "Light2", "Light3", "Dark1", "Dark2", "Dark3" })
            application.Resources["SystemAccentColor" + suffix] = accent;
        application.RequestedThemeVariant = palette.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;

        void SetBrush(string key, Color color) => application.Resources[key] = new SolidColorBrush(color);
    }
}
