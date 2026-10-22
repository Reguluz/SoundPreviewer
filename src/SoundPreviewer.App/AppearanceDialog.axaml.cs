using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

internal partial class AppearanceDialog : Window
{
    private readonly AppearanceSettings _original;
    private readonly Action<AppearanceSettings> _save;
    private AppearanceSettings _working;
    private readonly Dictionary<string, (TextBox Input, Border Swatch)> _fields = [];
    private bool _updating;
    private bool _saved;

    public AppearanceDialog(AppearanceSettings appearance, Action<AppearanceSettings> save)
    {
        _original = ThemeManager.Normalize(appearance);
        _working = ThemeManager.Copy(_original);
        _save = save;
        _updating = true;
        InitializeComponent();
        PresetChoice.ItemsSource = new[] { "薄荷白 · 清爽", "深海灰 · 深色", "鸢尾紫 · 柔和" };
        ModeChoice.ItemsSource = new[] { "浅色", "深色" };
        PresetChoice.SelectedIndex = appearance.Preset switch { "slate" => 1, "indigo" => 2, _ => 0 };
        for (int index = 0; index < ThemeManager.ColorFields.Length; index++)
        {
            var (key, label) = ThemeManager.ColorFields[index];
            if (index % 2 == 0) ColorGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var input = new TextBox { Name = "Color" + key, MinWidth = 100, MaxLength = 7, Watermark = "#RRGGBB" };
            ToolTip.SetTip(input, label + "，格式 #RRGGBB");
            var swatch = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            swatch.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("Line"));
            var valueRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,10,*") };
            valueRow.Children.Add(swatch);
            Grid.SetColumn(input, 2);
            valueRow.Children.Add(input);
            var field = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 13) };
            var caption = new TextBlock { Text = label, FontSize = 12 };
            caption.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Muted"));
            field.Children.Add(caption);
            field.Children.Add(valueRow);
            Grid.SetColumn(field, index % 2 * 2);
            Grid.SetRow(field, index / 2);
            ColorGrid.Children.Add(field);
            _fields.Add(key, (input, swatch));
            input.TextChanged += (_, _) => Preview();
        }
        Populate(_working);
        _updating = false;
        Closed += (_, _) => { if (!_saved) ThemeManager.Apply(_original); };
    }

    private string SelectedPreset => PresetChoice.SelectedIndex switch { 1 => "slate", 2 => "indigo", _ => "teal" };

    private void Populate(AppearanceSettings appearance)
    {
        bool wasUpdating = _updating;
        _updating = true;
        _working = ThemeManager.Copy(appearance);
        ModeChoice.SelectedIndex = appearance.IsDark ? 1 : 0;
        foreach (var (key, field) in _fields)
        {
            string color = (string)typeof(AppearanceSettings).GetProperty(key)!.GetValue(appearance)!;
            field.Input.Text = color;
            field.Swatch.Background = new SolidColorBrush(Color.Parse(color));
        }
        ValidationMessage.Text = "";
        SaveButton.IsEnabled = true;
        _updating = wasUpdating;
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
        appearance.IsDark = ModeChoice.SelectedIndex == 1;
        appearance.Preset = _working.Preset;
        ValidationMessage.Text = "";
        SaveButton.IsEnabled = true;
        return true;
    }

    private void Preview()
    {
        if (!_updating && TryRead(out var appearance)) ThemeManager.Apply(appearance);
    }

    private void Preset_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        Populate(ThemeManager.Preset(SelectedPreset));
        Preview();
    }

    private void Mode_Changed(object? sender, SelectionChangedEventArgs e) => Preview();
    private void ResetPreset_Click(object? sender, RoutedEventArgs e)
    {
        Populate(ThemeManager.Preset(SelectedPreset));
        Preview();
    }

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

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (Save()) Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
