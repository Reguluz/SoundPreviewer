using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private async Task<bool> CheckAppearanceAsync()
    {
        var original = ThemeManager.Copy(_settings.Appearance);
        var storedSettings = _store.LoadSettings();
        string originalStored = JsonSerializer.Serialize(storedSettings, LibraryJson.Options);
        string originalRecords = JsonSerializer.Serialize(_store.LoadPresets(), LibraryJson.Options);
        var viewport = (Width, Height, Pages.SelectedIndex);
        AppearanceDialog? dialog = null;
        bool saved = false;
        try
        {
            if (JsonSerializer.Deserialize<AppSettings>("{}", LibraryJson.Options)!.Appearance.Accent != "#168571" ||
                JsonSerializer.Deserialize<AppSettings>("{\"Appearance\":null}", LibraryJson.Options)!.Appearance is null ||
                ThemeManager.TryColor("red", out _) || ThemeManager.TryColor("#123", out _) ||
                ThemeManager.TryColor("#12345Z", out _) || ThemeManager.TryColor("#00112233", out _) ||
                !ThemeManager.TryColor(" #abcdef ", out var parsed) || ThemeManager.Hex(parsed) != "#ABCDEF" ||
                ThemeManager.AccentText(Colors.White) != Colors.Black || ThemeManager.AccentText(Colors.Black) != Colors.White)
                throw new InvalidOperationException("旧库默认配色、HEX 验证或强调色文字对比计算错误。");
            var damaged = new AppearanceSettings { Accent = "invalid", Surface = null! };
            if (ThemeManager.Normalize(damaged).Accent != "#168571" || ThemeManager.Normalize(damaged).Surface != "#FFFFFF")
                throw new InvalidOperationException("损坏的单项颜色没有安全回退。");

            Pages.SelectedIndex = 0;
            ThemeManager.Apply(ThemeManager.Preset("slate"));
            foreach (var (width, height, name) in new[] { (1380d, 900d, "desktop"), (1040d, 700d, "compact") })
            {
                Width = width; Height = height;
                await SettleLayoutAsync();
                if (ActualThemeVariant != ThemeVariant.Dark || BrushColor("Surface") != Color.Parse("#1D2735") ||
                    VolumeSlider.ActualThemeVariant != ThemeVariant.Dark ||
                    !VolumeSlider.TryFindResource("SystemAccentColor", out var nativeAccent) ||
                    nativeAccent is not Color accent || accent != Color.Parse("#5DD5B8"))
                    throw new InvalidOperationException("深色预设没有同步原生控件和强调色。");
                CaptureSmokeImage($"ui-appearance-dark-{name}.png");
            }
            ThemeManager.Apply(original);

            dialog = new AppearanceDialog(original, SaveAppearance);
            var close = dialog.ShowDialog<bool>(this);
            await SettleLayoutAsync();
            var input = dialog.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "ColorAccent");
            input.Text = "#8A44CC";
            await SettleLayoutAsync();
            if (BrushColor("Accent") != Color.Parse("#8A44CC") ||
                JsonSerializer.Serialize(_store.LoadSettings(), LibraryJson.Options) != originalStored)
                throw new InvalidOperationException("手动配色未实时预览，或预览提前写入了设置。");
            CaptureAppearanceDialog(dialog, "ui-appearance-dialog.png");
            input.Text = "#GGGGGG";
            await SettleLayoutAsync();
            if (dialog.TryRead(out _) || dialog.Save() || BrushColor("Accent") != Color.Parse("#8A44CC"))
                throw new InvalidOperationException("无效 HEX 改写了配色或允许保存。");
            dialog.Close(false);
            await close;
            dialog = null;
            if (BrushColor("Accent") != Color.Parse(original.Accent) ||
                JsonSerializer.Serialize(_store.LoadSettings(), LibraryJson.Options) != originalStored)
                throw new InvalidOperationException("关闭配色窗口没有还原预览，或修改了已存设置。");

            // Persistence is only exercised in empty or synthetic smoke libraries.
            if (Presets.Any(row => row.Record.SourceId != "test-only-ui-fixture")) return false;
            dialog = new AppearanceDialog(original, SaveAppearance);
            close = dialog.ShowDialog<bool>(this);
            await SettleLayoutAsync();
            var preset = dialog.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "PresetChoice");
            preset.SelectedIndex = 1;
            await SettleLayoutAsync();
            if (dialog.ActualThemeVariant != ThemeVariant.Dark || BrushColor("Accent") != Color.Parse("#5DD5B8"))
                throw new InvalidOperationException("配色窗口中的预设没有立即切换深色控件。");
            CaptureAppearanceDialog(dialog, "ui-appearance-dialog-dark.png");
            input = dialog.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "ColorAccent");
            input.Text = "#DD9911";
            await SettleLayoutAsync();
            if (!dialog.Save()) throw new InvalidOperationException("自定义配色保存失败。");
            saved = true;
            dialog.Close(true);
            await close;
            dialog = null;
            var loaded = _store.LoadSettings();
            var expected = JsonSerializer.Deserialize<AppSettings>(originalStored, LibraryJson.Options)!;
            expected.Appearance = loaded.Appearance;
            if (loaded.Appearance.Accent != "#DD9911" || !loaded.Appearance.IsDark ||
                JsonSerializer.Serialize(expected, LibraryJson.Options) != JsonSerializer.Serialize(loaded, LibraryJson.Options) ||
                JsonSerializer.Serialize(_store.LoadPresets(), LibraryJson.Options) != originalRecords)
                throw new InvalidOperationException("配色没有持久保存，或保存更改了其他设置与音色数据。");
            ThemeManager.Apply(loaded.Appearance);
            if (BrushColor("Accent") != Color.Parse("#DD9911") || BrushColor("AccentForeground") != Colors.Black)
                throw new InvalidOperationException("重载配色没有更新资源或强调色上的文字。");
            return true;
        }
        finally
        {
            dialog?.Close(false);
            if (saved) _store.SaveSettings(storedSettings);
            _settings.Appearance = original;
            ThemeManager.Apply(original);
            Width = viewport.Width; Height = viewport.Height; Pages.SelectedIndex = viewport.SelectedIndex;
            await SettleLayoutAsync();
        }

        static Color BrushColor(string key) => Application.Current!.Resources[key] is ISolidColorBrush brush
            ? brush.Color : throw new InvalidOperationException("配色资源缺失：" + key);

        static void CaptureAppearanceDialog(AppearanceDialog window, string name)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)window.ClientSize.Width), Math.Max(1, (int)window.ClientSize.Height)), new Vector(96, 96));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(App.DataDirectory, name));
        }
    }
}
