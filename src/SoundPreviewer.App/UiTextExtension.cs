using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

public sealed class UiTextExtension : MarkupExtension
{
    public string Path { get; set; } = "";
    public string? ElementName { get; set; }
    public object? TargetNullValue { get; set; }
    public object? FallbackValue { get; set; }
    public IValueConverter? Converter { get; set; }
    public UiTextExtension() { }
    public UiTextExtension(string path) => Path = path;
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new ReflectionBindingExtension(Path) { Mode = BindingMode.OneWay, Converter = Converter };
        if (ElementName is not null) binding.ElementName = ElementName;
        if (TargetNullValue is not null) binding.TargetNullValue = TargetNullValue;
        if (FallbackValue is not null) binding.FallbackValue = FallbackValue;
        return new MultiBinding { Bindings = { (IBinding)binding.ProvideValue(serviceProvider), new Binding(nameof(LanguageService.Revision)) { Source = LanguageService.Current } }, Converter = UiTextConverter.Instance };
    }
}
public sealed class UiTextConverter : IMultiValueConverter
{
    public static UiTextConverter Instance { get; } = new();
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count > 0 && values[0] is string text ? LanguageService.Current.Translate(text) : values.FirstOrDefault();
}
