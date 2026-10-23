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
        var layoutState = (_smokeRequestedViewport, _smokeLayoutPhase, PresetGrid.SelectedItem);
        AppearanceDialog? dialog = null;
        bool saved = false;
        try
        {
            if (JsonSerializer.Deserialize<AppSettings>("{}", LibraryJson.Options)!.Appearance.Accent != "#287E91" ||
                JsonSerializer.Deserialize<AppSettings>("{\"Appearance\":null}", LibraryJson.Options)!.Appearance is null ||
                JsonSerializer.Deserialize<AppSettings>("{\"Appearance\":{\"IsDark\":true}}", LibraryJson.Options)!.Appearance.Mode != "dark" ||
                ThemeManager.TryColor("red", out _) || ThemeManager.TryColor("#123", out _) ||
                ThemeManager.TryColor("#12345Z", out _) || ThemeManager.TryColor("#00112233", out _) ||
                !ThemeManager.TryColor(" #abcdef ", out var parsed) || ThemeManager.Hex(parsed) != "#ABCDEF" ||
                ThemeManager.AccentText(Colors.White) != Colors.Black || ThemeManager.AccentText(Colors.Black) != Colors.White)
                throw new InvalidOperationException("旧库默认配色、旧明暗设置、HEX 验证或文字对比计算错误。");
            var damaged = new AppearanceSettings { Accent = "invalid", Surface = null! };
            if (ThemeManager.Normalize(damaged).Accent != "#287E91" || ThemeManager.Normalize(damaged).Surface != "#FFFFFF")
                throw new InvalidOperationException("损坏的单项颜色没有安全回退。");

            Pages.SelectedIndex = 0;
            PresetGrid.SelectedItem = Presets.FirstOrDefault(row => row.Record.DuplicateOfId.Length == 0);
            var appearances = new List<object>();
            foreach (var (width, height, name) in new[] { (1380d, 900d, "desktop"), (1040d, 700d, "compact") })
            {
                _smokeRequestedViewport = new Size(width, height);
                _smokeLayoutPhase = $"appearance-{name}-baseline";
                Width = width; Height = height;
                ThemeManager.Apply(ThemeManager.Preset("silver"));
                await SettleLayoutAsync();
                var fixedLayout = CaptureLibraryLayout();
                foreach (string preset in new[] { "silver", "sage", "blue", "lavender" })
                foreach (string mode in new[] { "light", "dark", "system" })
                {
                    _smokeLayoutPhase = $"appearance-{name}-{preset}-{mode}";
                    var appearance = ThemeManager.Preset(preset, mode);
                    ThemeManager.Apply(appearance);
                    await SettleLayoutAsync();
                    CheckAppearanceResources(appearance);
                    string expectedAccent = (preset, ThemeManager.IsDarkMode(mode)) switch
                    {
                        ("sage", false) => "#53694D", ("sage", true) => "#B4CDA7",
                        ("blue", false) => "#376681", ("blue", true) => "#A0CFEC",
                        ("lavender", false) => "#746087", ("lavender", true) => "#D6B7EC",
                        ("silver", false) => "#287E91", _ => "#79C1D1"
                    };
                    if (BrushColor("Accent") != Color.Parse(expectedAccent))
                        throw new InvalidOperationException("预设配色不符合默认设计或原有已保存配色：" + preset);
                    CheckSmokeLayout();
                    CheckFixedAppearanceBands();
                    CheckLibraryLayoutUnchanged(fixedLayout);
                    appearances.Add(new { Viewport = name, Preset = preset, Mode = mode,
                        ActualTheme = ActualThemeVariant.ToString(), Width = ClientSize.Width, Height = ClientSize.Height });
                    if (preset == "silver" && mode != "system")
                        CaptureSmokeImage($"ui-appearance-{mode}-{name}.png");
                }

                // The visual surface resources must follow user colors, without changing geometry.
                var custom = ThemeManager.Preset("silver");
                ThemeManager.Apply(custom);
                var before = CaptureSurfaceColors();
                custom.WindowBackground = "#D8C2C9";
                custom.Surface = "#E6E0D7";
                custom.SurfaceAlt = "#B9C7BA";
                custom.Accent = "#8A44CC";
                ThemeManager.Apply(custom);
                await SettleLayoutAsync();
                CheckAppearanceResources(custom);
                if (CaptureSurfaceColors().Any(pair => pair.Value.SequenceEqual(before[pair.Key])))
                    throw new InvalidOperationException("自定义背景与面板颜色没有更新控件层次资源。");
                CheckSmokeLayout();
                CheckLibraryLayoutUnchanged(fixedLayout);
            }
            if (JsonSerializer.Serialize(_store.LoadSettings(), LibraryJson.Options) != originalStored ||
                JsonSerializer.Serialize(_store.LoadPresets(), LibraryJson.Options) != originalRecords)
                throw new InvalidOperationException("切换主题或自定义预览改变了已保存设置或音色数据。");
            await File.WriteAllTextAsync(Path.Combine(App.DataDirectory, "ui-appearance.json"),
                JsonSerializer.Serialize(new { Presets = 4, Modes = 3, Viewports = 2, SurfaceResourcesChecked = true, Cases = appearances }, LibraryJson.Options));
            ThemeManager.Apply(original);
            await SettleLayoutAsync();
            CheckAppearanceResources(original);

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
            Click(dialog, "SagePreset");
            await SettleLayoutAsync();
            if (BrushColor("Accent") != Color.Parse("#53694D")) throw new InvalidOperationException("原有苔绿预设没有保留。");
            Click(dialog, "SilverPreset");
            await SettleLayoutAsync();
            if (BrushColor("Accent") != Color.Parse("#287E91")) throw new InvalidOperationException("银灰蓝青预设没有生效。");
            Click(dialog, "ResetDefaultsButton");
            await SettleLayoutAsync();
            if (BrushColor("Accent") != Color.Parse("#287E91") || ActualThemeVariant != ThemeVariant.Light)
                throw new InvalidOperationException("恢复默认没有还原银灰蓝青浅色主题。");

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
            if (dialog.ActualThemeVariant != ThemeVariant.Dark || BrushColor("TextPrimary") != Color.Parse("#E6EDF2"))
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
            CheckAppearanceResources(loaded.Appearance);
            return true;
        }
        finally
        {
            dialog?.Close(false);
            if (saved) _store.SaveSettings(storedSettings);
            _settings.Appearance = original;
            ThemeManager.Apply(original);
            Width = viewport.Width; Height = viewport.Height; Pages.SelectedIndex = viewport.SelectedIndex;
            (_smokeRequestedViewport, _smokeLayoutPhase, PresetGrid.SelectedItem) = layoutState;
            await SettleLayoutAsync();
        }

        void CheckAppearanceResources(AppearanceSettings appearance)
        {
            var palette = ThemeManager.SwitchMode(appearance, appearance.Mode);
            foreach (var (key, _) in ThemeManager.ColorFields)
                if (BrushColor(key) != Color.Parse((string)typeof(AppearanceSettings).GetProperty(key)!.GetValue(palette)!))
                    throw new InvalidOperationException($"{appearance.Preset}/{appearance.Mode} 配色资源未更新：{key}");
            var variant = palette.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
            var requested = appearance.Mode == "system" ? ThemeVariant.Default : variant;
            if (Application.Current!.RequestedThemeVariant != requested || ActualThemeVariant != variant ||
                VolumeSlider.ActualThemeVariant != variant)
                throw new InvalidOperationException("主题没有同步窗口和原生控件。");
            foreach (string suffix in new[] { "", "Light1", "Light2", "Light3", "Dark1", "Dark2", "Dark3" })
                if (!VolumeSlider.TryFindResource("SystemAccentColor" + suffix, out var nativeAccent) ||
                    nativeAccent is not Color accent || accent != Color.Parse(palette.Accent))
                    throw new InvalidOperationException("原生控件强调色没有同步当前配色。");
            if (BrushColor("AccentForeground") != ThemeManager.AccentText(Color.Parse(palette.Accent)))
                throw new InvalidOperationException("强调色上的文字对比没有随配色更新。");
            _ = CaptureSurfaceColors();
        }

        void CheckFixedAppearanceBands()
        {
            var root = (Grid)Content!;
            var header = root.Children.OfType<Border>().Single(border => Grid.GetRow(border) == 0);
            var footer = root.Children.OfType<Border>().Single(border => Grid.GetRow(border) == 4);
            if (Math.Abs(SmokeBounds(header).Height - 64) > .5 || Math.Abs(SmokeBounds(footer).Height - 24) > .5 ||
                Math.Abs(SmokeBounds(FilterOptionsBand).Height - 96) > .5 ||
                Math.Abs(SmokeBounds(PlaybackBar).Height - 84) > .5 || Math.Abs(SmokeBounds(TaskStatusBand).Height - 118) > .5)
                throw SmokeLayoutFailure("主题切换改变了顶部工具栏、筛选选项带或底部固定区域的高度。");
        }

        static Dictionary<string, Color[]> CaptureSurfaceColors()
        {
            var result = new Dictionary<string, Color[]>();
            foreach (string key in new[] { "HeaderBackground", "ControlBackground", "OptionsBandBackground" })
            {
                if (Application.Current!.Resources[key] is not LinearGradientBrush brush || brush.GradientStops.Count < 2 ||
                    brush.GradientStops.Any(stop => stop.Color.A != 255))
                    throw new InvalidOperationException("面板或控件层次资源缺失或无效：" + key);
                result[key] = brush.GradientStops.Select(stop => stop.Color).ToArray();
            }
            result["SurfaceHighlight"] = [BrushColor("SurfaceHighlight")];
            return result;
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
