using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    public ObservableCollection<FilterGroup> VisibleFilterGroups { get; } = [];
    public ObservableCollection<FilterLayoutOption> FilterLayoutOptions { get; } = [];

    private void PopulateFilterLayout()
    {
        FilterLayoutOptions.Clear();
        var order = LibraryFilterLayout.Normalize(_settings.LibraryFilterOrder);
        foreach (string key in order.Concat(LibraryFilterLayout.KnownKeys.Except(order)))
            FilterLayoutOptions.Add(new(key, FilterGroups.Single(group => group.Key == key).Label, order.Contains(key)));
        FilterOrderList.SelectedIndex = 0;
        ApplyFilterLayout();
    }

    private void ApplyFilterLayout()
    {
        var order = LibraryFilterLayout.Normalize(_settings.LibraryFilterOrder);
        if (VisibleFilterGroups.Select(group => group.Key).SequenceEqual(order)) return;
        var previous = VisibleFilterGroups.ToList();
        VisibleFilterGroups.Clear();
        foreach (string key in order)
            VisibleFilterGroups.Add(FilterGroups.Single(group => group.Key == key));
        foreach (var group in previous.Except(VisibleFilterGroups)) group.SetSelection([]);
        if (!VisibleFilterGroups.Any(group => group.IsActive)) SelectFilterGroup(VisibleFilterGroups[0].Key);
        RefreshPresetView();
        NotifyFilterResults();
    }

    private void MoveFilterGroup_Click(object? sender, RoutedEventArgs e)
    {
        int index = FilterOrderList.SelectedIndex;
        int target = index + ((sender as Button)?.Tag?.ToString() == "up" ? -1 : 1);
        if (index < 0 || target < 0 || target >= FilterLayoutOptions.Count) return;
        FilterLayoutOptions.Move(index, target);
        FilterOrderList.SelectedIndex = target;
    }

    private void ResetFilterLayout_Click(object? sender, RoutedEventArgs e)
    {
        FilterLayoutOptions.Clear();
        foreach (string key in LibraryFilterLayout.KnownKeys)
            FilterLayoutOptions.Add(new(key, FilterGroups.Single(group => group.Key == key).Label,
                LibraryFilterLayout.DefaultOrder.Contains(key)));
        FilterOrderList.SelectedIndex = 0;
    }
}
