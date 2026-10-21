using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia;
using Avalonia.VisualTree;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    public string AdditionalFiltersLabel
    {
        get
        {
            int count = new[] { PackageFilter?.SelectedIndex, CategoryFilter?.SelectedIndex,
                TagFilter?.SelectedIndex, FavoriteFilter?.SelectedIndex }.Count(index => index > 0);
            return count == 0 ? "更多筛选" : $"更多筛选 · {count}";
        }
    }

    private void CloseMoreFilters_Click(object? sender, RoutedEventArgs e) => MoreFiltersToggle.IsChecked = false;

    private void DismissMoreFilters_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (MoreFiltersToggle.IsChecked != true || e.Source is not Visual source || source.GetVisualRoot() != this ||
            source.GetVisualAncestors().Prepend(source).Any(control => control == AdvancedFiltersPanel || control == MoreFiltersToggle))
            return;
        MoreFiltersToggle.IsChecked = false;
    }
}
