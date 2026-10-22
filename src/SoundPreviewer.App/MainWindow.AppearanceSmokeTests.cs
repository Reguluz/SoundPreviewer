using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
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
            if (JsonSerializer.Deserialize<AppSettings>("{}", LibraryJson.Options)!.Appearance.Accent != "#53694D" ||
                JsonSerializer.Deserialize<AppSettings>("{\"Appearance\":null}", LibraryJson.Options)!.Appearance is null ||
                JsonSerializer.Deserialize<AppSettings>("{\"Appearance\":{\"IsDark\":true}}", LibraryJson.Options)!.Appearance.Mode != "dark" ||
                ThemeManager.TryColor("red", out _) || ThemeManager.TryColor("#123", out _) ||
                ThemeManager.TryColor("#12345Z", out _) || ThemeManager.TryColor("#00112233", out _) ||
                !ThemeManager.TryColor(" #abcdef ", out var parsed) || ThemeManager.Hex(parsed) != "#ABCDEF" ||
                ThemeManager.AccentText(Colors.White) != Colors.Black || ThemeManager.AccentText(Colors.Black) != Colors.White)
                throw new InvalidOperationException("旧库默认配色、旧明暗设置、HEX 验证或文字对比计算错误。");
            var damaged = new AppearanceSettings { Accent = "invalid", Surface = null! };
            if (ThemeManager.Normalize(damaged).Accent != "#53694D" || ThemeManager.Normalize(damaged).Surface != "#FFFFFF")
                throw new InvalidOperationException("损坏的单项颜色没有安全回退。");

            Pages.SelectedIndex = 0;
            ThemeManager.Apply(ThemeManager.Preset("sage", "dark"));
            foreach (var (width, height, name) in new[] { (1380d, 900d, "desktop"), (1040d, 700d, "compact") })
            {
                Width = width; Height = height;
                await SettleLayoutAsync();
                if (ActualThemeVariant != ThemeVariant.Dark || BrushColor("Surface") != Color.Parse("#1D251B") ||
                    VolumeSlider.ActualThemeVariant != ThemeVariant.Dark ||
                    !VolumeSlider.TryFindResource("SystemAccentColor", out var nativeAccent) ||
                    nativeAccent is not Color accent || accent != Color.Parse("#B4CDA7"))
                    throw new InvalidOperationException("深色主题没有同步原生控件和强调色。");
                CaptureSmokeImage($"ui-appearance-dark-{name}.png");
            }
            ThemeManager.Apply(original);

            dialog = new AppearanceDialog(original, SaveAppearance);
            var close = dialog.ShowDialog<bool>(this);
            await SettleLayoutAsync();
            CheckDialogFits(dialog);
            var input = Named<TextBox>(dialog, "ColorAccent");
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
            if (BrushColor("Accent") != Color.Parse(ThemeManager.SwitchMode(original, original.Mode).Accent) ||
                JsonSerializer.Serialize(_store.LoadSettings(), LibraryJson.Options) != originalStored)
                throw new InvalidOperationException("取消配色没有还原预览，或修改了已存设置。");

            // Save tests only operate on the synthetic smoke library, never a real user's library.
            if (Presets.Any(row => row.Record.SourceId != "test-only-ui-fixture")) return false;
            var futureSettings = ThemeManager.Copy(original);
            futureSettings.AdditionalProperties ??= [];
            futureSettings.AdditionalProperties["FutureAppearance"] = JsonSerializer.SerializeToElement(new { Tone = 123 });
            futureSettings.LightColors ??= [];
            futureSettings.LightColors["FutureColorSlot"] = "#010203";
            dialog = new AppearanceDialog(futureSettings, SaveAppearance);
            close = dialog.ShowDialog<bool>(this);
            await SettleLayoutAsync();
            CheckDialogFits(dialog);
            Click(dialog, "LavenderPreset");
            await SettleLayoutAsync();
            if (BrushColor("Accent") != Color.Parse("#746087")) throw new InvalidOperationException("雾紫预设没有生效。");
            Click(dialog, "BluePreset");
            await SettleLayoutAsync();
            if (BrushColor("Accent") != Color.Parse("#376681")) throw new InvalidOperationException("海蓝预设没有生效。");
            Click(dialog, "ResetDefaultsButton");
            await SettleLayoutAsync();
            if (BrushColor("Accent") != Color.Parse("#53694D") || ActualThemeVariant != ThemeVariant.Light)
                throw new InvalidOperationException("恢复默认没有还原苔绿浅色主题。");

            Click(dialog, "PickAccent");
            Named<Slider>(dialog, "HueSlider").Value = 210;
            Named<Slider>(dialog, "SaturationSlider").Value = 50;
            Named<Slider>(dialog, "ValueSlider").Value = 50;
            await SettleLayoutAsync();
            input = Named<TextBox>(dialog, "ColorAccent");
            if (!ThemeManager.TryColor(input.Text, out var picked) || picked != Color.FromRgb(64, 96, 128) || BrushColor("Accent") != picked)
                throw new InvalidOperationException("色彩选择器没有同步 HEX 和实时预览。");
            input.Text = "#336699";
            Click(dialog, "DarkMode");
            await SettleLayoutAsync();
            if (dialog.ActualThemeVariant != ThemeVariant.Dark || BrushColor("TextPrimary") != Color.Parse("#E7EDE0"))
                throw new InvalidOperationException("深色主题文字或原生控件没有切换。");
            input.Text = "#DD9911";
            Click(dialog, "SystemMode");
            await SettleLayoutAsync();
            if (Application.Current!.RequestedThemeVariant != ThemeVariant.Default || !dialog.TryRead(out var system) || system.Mode != "system")
                throw new InvalidOperationException("跟随系统模式没有生效。");
            Click(dialog, "LightMode");
            await SettleLayoutAsync();
            if (input.Text != "#336699") throw new InvalidOperationException("切换明暗模式丢失了浅色自定义颜色。");
            Click(dialog, "DarkMode");
            await SettleLayoutAsync();
            if (input.Text != "#DD9911") throw new InvalidOperationException("切换明暗模式丢失了深色自定义颜色。");
            CaptureAppearanceDialog(dialog, "ui-appearance-dialog-dark.png");
            if (!dialog.Save()) throw new InvalidOperationException("自定义配色保存失败。");
            saved = true;
            dialog.Close(true);
            await close;
            dialog = null;
            var loaded = _store.LoadSettings();
            var expected = JsonSerializer.Deserialize<AppSettings>(originalStored, LibraryJson.Options)!;
            expected.Appearance = loaded.Appearance;
            if (loaded.Appearance.Accent != "#DD9911" || loaded.Appearance.Mode != "dark" || !loaded.Appearance.IsDark ||
                loaded.Appearance.LightColors?[nameof(AppearanceSettings.Accent)] != "#336699" ||
                loaded.Appearance.LightColors?["FutureColorSlot"] != "#010203" ||
                loaded.Appearance.AdditionalProperties?["FutureAppearance"].GetProperty("Tone").GetInt32() != 123 ||
                JsonSerializer.Serialize(expected, LibraryJson.Options) != JsonSerializer.Serialize(loaded, LibraryJson.Options) ||
                JsonSerializer.Serialize(_store.LoadPresets(), LibraryJson.Options) != originalRecords)
                throw new InvalidOperationException("配色未持久保存、未知字段丢失，或保存更改了其他设置与音色数据。");
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

        static T Named<T>(AppearanceDialog window, string name) where T : Control =>
            window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

        static void Click(AppearanceDialog window, string name) => Named<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        static Color BrushColor(string key) => Application.Current!.Resources[key] is ISolidColorBrush brush
            ? brush.Color : throw new InvalidOperationException("配色资源缺失：" + key);

        static void CheckDialogFits(AppearanceDialog window)
        {
            var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
            if (screen is not null && (window.FrameSize ?? window.ClientSize).Height * screen.Scaling > screen.WorkingArea.Height + 1)
                throw new InvalidOperationException("主题配色窗口超出了屏幕工作区。");
            var cancel = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "取消");
            foreach (var button in new[] { Named<Button>(window, "SaveButton"), cancel })
                if (!button.IsEffectivelyVisible || button.TranslatePoint(new Point(0, button.Bounds.Height), window) is not Point bottom ||
                    bottom.Y > window.ClientSize.Height || bottom.Y - button.Bounds.Height < 0)
                    throw new InvalidOperationException("配色窗口的应用或取消按钮被截断。");
        }

        static void CaptureAppearanceDialog(AppearanceDialog window, string name)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)window.ClientSize.Width), Math.Max(1, (int)window.ClientSize.Height)), new Vector(96, 96));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(App.DataDirectory, name));
        }
    }
}
