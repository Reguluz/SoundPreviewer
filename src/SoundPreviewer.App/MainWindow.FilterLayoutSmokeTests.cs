using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private async Task<bool> CheckFilterLayoutPersistenceAsync()
    {
        if (_settings.Plugins.Any(plugin => plugin.Backend != RenderBackend.Unconfigured) ||
            Presets.Any(row => row.Record.SourceId != "test-only-ui-fixture")) return false;
        var saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings, LibraryJson.Options), LibraryJson.Options)!;
        try
        {
            FilterOrderList.SelectedIndex = FilterLayoutOptions.ToList().FindIndex(option => option.Key == "tag");
            while (FilterOrderList.SelectedIndex > 0)
                MoveFilterGroup_Click(new Button { Tag = "up" }, new RoutedEventArgs());
            string[] expected = ["tag", "synth", "source"];
            foreach (var option in FilterLayoutOptions) option.IsVisible = expected.Contains(option.Key);
            SelectFilterGroup("status");
            SetFilterSelection("status", "pending");
            await RunTask(SaveSettingsCoreAsync);
            if (!_store.LoadSettings().LibraryFilterOrder.SequenceEqual(expected) ||
                !VisibleFilterGroups.Select(group => group.Key).SequenceEqual(expected) ||
                ActiveFilterLabel != "标签" || FilterGroups.Single(group => group.Key == "status").SelectedKeys.Count != 0)
                throw new InvalidOperationException("筛选顺序或显示设置没有保存，或隐藏的条件仍在筛选音色。");
            _settings = _store.LoadSettings();
            PopulateSettings();
            if (!VisibleFilterGroups.Select(group => group.Key).SequenceEqual(expected) ||
                !FilterLayoutOptions.Where(option => option.IsVisible).Select(option => option.Key).SequenceEqual(expected))
                throw new InvalidOperationException("重新读取设置后筛选顺序或勾选状态丢失。");
            ResetFilterLayout_Click(null, new RoutedEventArgs());
            await RunTask(SaveSettingsCoreAsync);
            if (!VisibleFilterGroups.Select(group => group.Key).SequenceEqual(LibraryFilterLayout.DefaultOrder))
                throw new InvalidOperationException("恢复默认没有显示状态、分类、标签、音源四组筛选。");
            return true;
        }
        finally
        {
            _settings = saved;
            _store.SaveSettings(saved);
            PopulateSettings();
            ClearFilterSelections();
        }
    }
}
