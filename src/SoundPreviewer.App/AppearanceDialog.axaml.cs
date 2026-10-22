using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

internal partial class AppearanceDialog : Window
{
    private readonly AppearanceSettings _original;
    private readonly Action<AppearanceSettings> _save;
    private AppearanceSettings _working;
    private readonly Dictionary<string, (TextBox Input, Button Swatch)> _fields = [];
    private string? _pickerKey;
    private bool _updating;
    private bool _updatingPicker;
    private bool _saved;

    public AppearanceDialog(AppearanceSettings appearance, Action<AppearanceSettings> save)
    {
        _original = ThemeManager.Normalize(appearance);
        _working = ThemeManager.SwitchMode(_original, _original.Mode);
        _save = save;
        _updating = true;
        InitializeComponent();
        for (int index = 0; index < ThemeManager.ColorFields.Length; index++)
        {
            var (key, label) = ThemeManager.ColorFields[index];
            var input = new TextBox { Name = "Color" + key, MinWidth = 132, MaxLength = 7, Watermark = "#RRGGBB", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(input, label + " HEX 色值");
            var swatch = new Button { Name = "Pick" + key, Width = 32, Height = 32, MinHeight = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0) };
            swatch.Bind(BorderBrushProperty, new DynamicResourceExtension("Line"));
            AutomationProperties.SetName(swatch, "选择" + label);
            ToolTip.SetTip(swatch, "选择" + label);
            swatch.Click += (_, _) => OpenPicker(key, label);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,152") };
            var caption = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(caption);
            Grid.SetColumn(swatch, 1);
            Grid.SetColumn(input, 2);
            row.Children.Add(swatch);
            row.Children.Add(input);
            (index < 4 ? ColorFieldsPanel : AdvancedColorFieldsPanel).Children.Add(row);
            _fields.Add(key, (input, swatch));
            input.TextChanged += (_, _) => { Preview(); if (_pickerKey == key) UpdatePicker(); };
        }
        foreach (var slider in new[] { HueSlider, SaturationSlider, ValueSlider })
            slider.PropertyChanged += (_, args) => { if (args.Property == RangeBase.ValueProperty) PickColor(); };
        Populate(_working);
        _updating = false;
        Opened += (_, _) => FitToWorkingArea();
        Closed += (_, _) => { if (!_saved) ThemeManager.Apply(_original); };
    }

    private void FitToWorkingArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        double scaling = screen.Scaling;
        var area = screen.WorkingArea;
        double frameHeight = Math.Max(0, (FrameSize?.Height ?? ClientSize.Height) - ClientSize.Height);
        MaxHeight = Math.Max(MinHeight, Math.Min(MaxHeight, area.Height / scaling - frameHeight - 32));
        Height = Math.Clamp(Height, MinHeight, MaxHeight);

        // Recenter after the native frame has received its constrained size.
        Dispatcher.UIThread.Post(() =>
        {
            var size = FrameSize ?? ClientSize;
            int width = (int)Math.Ceiling(size.Width * scaling);
            int height = (int)Math.Ceiling(size.Height * scaling);
            Position = new PixelPoint(area.X + Math.Max(0, (area.Width - width) / 2),
                area.Y + Math.Max(0, (area.Height - height) / 2));
        }, DispatcherPriority.Loaded);
    }

    private void Populate(AppearanceSettings appearance)
    {
        bool wasUpdating = _updating;
        _updating = true;
        _working = ThemeManager.Copy(appearance);
        LightMode.IsChecked = appearance.Mode == "light";
        DarkMode.IsChecked = appearance.Mode == "dark";
        SystemMode.IsChecked = appearance.Mode == "system";
        SagePreset.IsChecked = appearance.Preset == "sage";
        LavenderPreset.IsChecked = appearance.Preset == "lavender";
        BluePreset.IsChecked = appearance.Preset == "blue";
        foreach (var (key, field) in _fields)
        {
            string value = (string)typeof(AppearanceSettings).GetProperty(key)!.GetValue(appearance)!;
            field.Input.Text = value;
            field.Swatch.Background = new SolidColorBrush(Color.Parse(value));
        }
        ValidationMessage.Text = "";
        SaveButton.IsEnabled = true;
        _updating = wasUpdating;
        UpdatePicker();
    }

    internal bool TryRead(out AppearanceSettings appearance)
    {
        appearance = ThemeManager.Copy(_working);
        foreach (var (key, label) in ThemeManager.ColorFields)
        {
            var field = _fields[key];
            if (!ThemeManager.TryColor(field.Input.Text, out var color))
            {
                ValidationMessage.Text = label + "：请输入完整的 #RRGGBB 色值。";
                SaveButton.IsEnabled = false;
                return false;
            }
            typeof(AppearanceSettings).GetProperty(key)!.SetValue(appearance, ThemeManager.Hex(color));
            field.Swatch.Background = new SolidColorBrush(color);
        }
        ThemeManager.RememberColors(appearance);
        ValidationMessage.Text = "";
        SaveButton.IsEnabled = true;
        return true;
    }

    private void Preview()
    {
        if (!_updating && TryRead(out var appearance))
        {
            _working = appearance;
            ThemeManager.Apply(appearance);
        }
    }

    private void Mode_Click(object? sender, RoutedEventArgs e)
    {
        if (_updating || sender is not ToggleButton { Tag: string mode }) return;
        if (!TryRead(out var appearance)) { Populate(_working); return; }
        Populate(ThemeManager.SwitchMode(appearance, mode));
        Preview();
    }

    private void Preset_Click(object? sender, RoutedEventArgs e)
    {
        if (_updating || sender is not ToggleButton { Tag: string preset }) return;
        Populate(ThemeManager.ChangePreset(_working, preset));
        Preview();
    }

    private void ResetDefaults_Click(object? sender, RoutedEventArgs e)
    {
        var defaults = ThemeManager.ChangePreset(_working, "sage");
        Populate(ThemeManager.SwitchMode(defaults, "light"));
        Preview();
    }

    private void OpenPicker(string key, string label)
    {
        _pickerKey = key;
        PickerLabel.Text = label;
        PickerPanel.IsVisible = true;
        UpdatePicker();
        PickerPanel.BringIntoView();
    }

    private void UpdatePicker()
    {
        if (_updatingPicker || _pickerKey is null || !ThemeManager.TryColor(_fields[_pickerKey].Input.Text, out var color)) return;
        _updatingPicker = true;
        try
        {
            double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
            double maximum = Math.Max(r, Math.Max(g, b)), minimum = Math.Min(r, Math.Min(g, b)), delta = maximum - minimum;
            double hue = delta == 0 ? HueSlider.Value : maximum == r ? 60 * ((g - b) / delta % 6) : maximum == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
            HueSlider.Value = (hue + 360) % 360;
            SaturationSlider.Value = maximum == 0 ? 0 : delta / maximum * 100;
            ValueSlider.Value = maximum * 100;
            PickerSwatch.Background = new SolidColorBrush(color);
        }
        finally { _updatingPicker = false; }
    }

    private void PickColor()
    {
        if (_updating || _updatingPicker || _pickerKey is null) return;
        _updatingPicker = true;
        try
        {
            double hue = HueSlider.Value / 60, saturation = SaturationSlider.Value / 100, value = ValueSlider.Value / 100;
            double c = value * saturation, x = c * (1 - Math.Abs(hue % 2 - 1)), m = value - c;
            var (r, g, b) = hue switch
            {
                < 1 => (c, x, 0d), < 2 => (x, c, 0d), < 3 => (0d, c, x),
                < 4 => (0d, x, c), < 5 => (x, 0d, c), _ => (c, 0d, x)
            };
            var color = Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
            _fields[_pickerKey].Input.Text = ThemeManager.Hex(color);
            PickerSwatch.Background = new SolidColorBrush(color);
        }
        finally { _updatingPicker = false; }
    }

    private void ClosePicker_Click(object? sender, RoutedEventArgs e) => PickerPanel.IsVisible = false;

    internal bool Save()
    {
        if (!TryRead(out var appearance)) return false;
        try
        {
            _save(appearance);
            _saved = true;
            return true;
        }
        catch (Exception error)
        {
            ValidationMessage.Text = "配色暂未保存：" + error.Message;
            return false;
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e) { if (Save()) Close(true); }
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
