using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

internal static class ThemeManager
{
    internal static readonly (string Key, string Label)[] ColorFields =
    [
        (nameof(AppearanceSettings.Accent), "强调色"),
        (nameof(AppearanceSettings.WindowBackground), "背景色"),
        (nameof(AppearanceSettings.SurfaceAlt), "面板色"),
        (nameof(AppearanceSettings.TextPrimary), "文字色"),
        (nameof(AppearanceSettings.Surface), "卡片背景"),
        (nameof(AppearanceSettings.Muted), "辅助文字"),
        (nameof(AppearanceSettings.Line), "边框与分隔线"),
        (nameof(AppearanceSettings.SelectionBackground), "选中背景"),
        (nameof(AppearanceSettings.HoverBackground), "悬停背景")
    ];

    private static AppearanceSettings? _applied;
    private static IPlatformSettings? _platform;

    internal static AppearanceSettings Copy(AppearanceSettings appearance) =>
        JsonSerializer.Deserialize<AppearanceSettings>(JsonSerializer.Serialize(appearance, LibraryJson.Options), LibraryJson.Options)!;

    private static string PresetKey(string? key) => key switch
    {
        "silver" => "silver",
        "lavender" or "indigo" => "lavender",
        "blue" => "blue",
        _ => "sage"
    };

    internal static bool IsDarkMode(string mode) => mode == "dark" ||
        mode == "system" && Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;

    internal static AppearanceSettings Preset(string key, string mode = "light")
    {
        bool dark = key == "slate" || IsDarkMode(mode);
        key = PresetKey(key);
        var palette = (key, dark) switch
        {
            ("silver", false) => new AppearanceSettings(),
            ("silver", true) => new AppearanceSettings
            {
                Accent = "#79C1D1", WindowBackground = "#171D22", Surface = "#20282E", SurfaceAlt = "#29333B",
                TextPrimary = "#E6EDF2", Muted = "#A8B7C3", Line = "#44515C",
                SelectionBackground = "#244852", HoverBackground = "#2B3C45"
            },
            ("lavender", false) => new AppearanceSettings
            {
                Accent = "#746087", WindowBackground = "#FCF8FE", SurfaceAlt = "#F3EDF7",
                TextPrimary = "#241D29", Muted = "#706577", Line = "#E8DEED",
                SelectionBackground = "#EBDCF5", HoverBackground = "#F2E7F8"
            },
            ("blue", false) => new AppearanceSettings
            {
                Accent = "#376681", WindowBackground = "#F5FAFD", SurfaceAlt = "#EAF2F7",
                TextPrimary = "#17212A", Muted = "#5E6F7C", Line = "#DDE7EE",
                SelectionBackground = "#D0E6F4", HoverBackground = "#E3F0F8"
            },
            ("lavender", true) => new AppearanceSettings
            {
                Accent = "#D6B7EC", WindowBackground = "#19151D", Surface = "#211D26", SurfaceAlt = "#2A2430",
                TextPrimary = "#EEE6F2", Muted = "#BCADCA", Line = "#49404F",
                SelectionBackground = "#493854", HoverBackground = "#352C3D"
            },
            ("blue", true) => new AppearanceSettings
            {
                Accent = "#A0CFEC", WindowBackground = "#111A20", Surface = "#19242C", SurfaceAlt = "#222F39",
                TextPrimary = "#E2ECF4", Muted = "#A4B8C7", Line = "#3B4C59",
                SelectionBackground = "#24485F", HoverBackground = "#2A3B49"
            },
            (_, true) => new AppearanceSettings
            {
                Accent = "#B4CDA7", WindowBackground = "#151C14", Surface = "#1D251B", SurfaceAlt = "#273024",
                TextPrimary = "#E7EDE0", Muted = "#AEBBA5", Line = "#404D3B",
                SelectionBackground = "#3D5335", HoverBackground = "#303D2B"
            },
            _ => new AppearanceSettings
            {
                Accent = "#53694D", WindowBackground = "#F8FAF4", SurfaceAlt = "#F0F3EA",
                TextPrimary = "#1A2118", Muted = "#66705F", Line = "#E0E5DC",
                SelectionBackground = "#D9E8CC", HoverBackground = "#EAF0E3"
            }
        };
        palette.Preset = key;
        palette.IsDark = dark;
        palette.Mode = mode == "system" ? "system" : dark ? "dark" : "light";
        return palette;
    }

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

    internal static AppearanceSettings Normalize(AppearanceSettings? appearance)
    {
        if (appearance is null) return Preset("silver");
        var result = Copy(appearance);
        result.Preset = PresetKey(result.Preset);
        result.Mode = result.Mode is "light" or "dark" or "system" ? result.Mode : "light";
        var fallback = Preset(result.Preset, result.IsDark ? "dark" : "light");
        foreach (var (key, _) in ColorFields)
        {
            var property = typeof(AppearanceSettings).GetProperty(key)!;
            property.SetValue(result, TryColor((string?)property.GetValue(result), out var color)
                ? Hex(color) : property.GetValue(fallback));
        }
        return result;
    }

    // Each mode keeps its custom colors. A system-mode change selects the other
    // palette without writing the library or discarding the user's other mode.
    internal static AppearanceSettings SwitchMode(AppearanceSettings appearance, string mode)
    {
        var result = Normalize(appearance);
        RememberColors(result);
        bool dark = IsDarkMode(mode);
        if (dark != result.IsDark)
        {
            var saved = dark ? result.DarkColors : result.LightColors;
            var fallback = Preset(result.Preset, dark ? "dark" : "light");
            foreach (var (key, _) in ColorFields)
            {
                var property = typeof(AppearanceSettings).GetProperty(key)!;
                property.SetValue(result, saved is not null && saved.TryGetValue(key, out var value) && TryColor(value, out var color)
                    ? Hex(color) : property.GetValue(fallback));
            }
        }
        result.Mode = mode;
        result.IsDark = dark;
        RememberColors(result);
        return result;
    }

    internal static void RememberColors(AppearanceSettings appearance)
    {
        var colors = appearance.IsDark ? appearance.DarkColors ??= [] : appearance.LightColors ??= [];
        foreach (var (key, _) in ColorFields)
            colors[key] = (string)typeof(AppearanceSettings).GetProperty(key)!.GetValue(appearance)!;
    }

    internal static AppearanceSettings ChangePreset(AppearanceSettings appearance, string key)
    {
        var result = Copy(appearance);
        var preset = Preset(key, result.Mode);
        result.Preset = preset.Preset;
        result.IsDark = preset.IsDark;
        // Retain unknown palette entries as well as unknown settings fields.
        foreach (var dark in new[] { false, true })
        {
            var colors = dark ? result.DarkColors ??= [] : result.LightColors ??= [];
            var defaults = Preset(key, dark ? "dark" : "light");
            foreach (var (field, _) in ColorFields)
                colors[field] = (string)typeof(AppearanceSettings).GetProperty(field)!.GetValue(defaults)!;
        }
        foreach (var (field, _) in ColorFields)
            typeof(AppearanceSettings).GetProperty(field)!.SetValue(result,
                typeof(AppearanceSettings).GetProperty(field)!.GetValue(preset));
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
        _applied = Normalize(appearance);
        if (_platform != application.PlatformSettings)
        {
            if (_platform is not null) _platform.ColorValuesChanged -= SystemColorsChanged;
            _platform = application.PlatformSettings;
            if (_platform is not null) _platform.ColorValuesChanged += SystemColorsChanged;
        }
        var palette = SwitchMode(_applied, _applied.Mode);
        foreach (var (key, _) in ColorFields)
            SetBrush(key, Color.Parse((string)typeof(AppearanceSettings).GetProperty(key)!.GetValue(palette)!));
        var accent = Color.Parse(palette.Accent);
        var surface = Color.Parse(palette.Surface);
        var surfaceAlt = Color.Parse(palette.SurfaceAlt);
        // Surface depth follows the user's palette in every mode. These are
        // derived brushes, so existing custom colors need no settings migration.
        SetGradient("HeaderBackground", surface, Blend(surface, Color.Parse(palette.WindowBackground), 0.35));
        SetGradient("ControlBackground", surface, Blend(surface, surfaceAlt, 0.3));
        SetGradient("OptionsBandBackground", Blend(surfaceAlt, surface, 0.2), surfaceAlt);
        SetBrush("SurfaceHighlight", Blend(surface, Colors.White, palette.IsDark ? 0.16 : 0.6));
        SetBrush("AccentForeground", AccentText(accent));
        var warning = Color.Parse(palette.IsDark ? "#F2C978" : "#94632B");
        var success = Color.Parse(palette.IsDark ? "#A0D9AD" : "#397665");
        var danger = Color.Parse(palette.IsDark ? "#FFB4B8" : "#AC515B");
        SetBrush("Warning", warning);
        SetBrush("Success", success);
        SetBrush("Danger", danger);
        double statusTint = palette.IsDark ? 0.14 : 0.1;
        SetBrush("SuccessBackground", Blend(surface, success, statusTint));
        SetBrush("WarningBackground", Blend(surface, warning, statusTint));
        SetBrush("DangerBackground", Blend(surface, danger, statusTint));
        SetBrush("NeutralBackground", Color.Parse(palette.SurfaceAlt));
        SetBrush("NeutralForeground", Color.Parse(palette.Muted));

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
        application.RequestedThemeVariant = palette.Mode == "system" ? ThemeVariant.Default : palette.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;

        void SetBrush(string key, Color color) => application.Resources[key] = new SolidColorBrush(color);
        void SetGradient(string key, Color top, Color bottom) => application.Resources[key] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = [new GradientStop(top, 0), new GradientStop(bottom, 1)]
        };
    }

    private static Color Blend(Color first, Color second, double amount) => Color.FromRgb(
        (byte)Math.Round(first.R + (second.R - first.R) * amount),
        (byte)Math.Round(first.G + (second.G - first.G) * amount),
        (byte)Math.Round(first.B + (second.B - first.B) * amount));

    private static void SystemColorsChanged(object? sender, PlatformColorValues values)
    {
        Dispatcher.UIThread.Post(() => { if (_applied?.Mode == "system") Apply(_applied); });
    }
}
