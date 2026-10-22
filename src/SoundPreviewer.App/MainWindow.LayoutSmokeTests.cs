using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private sealed record LibraryLayoutSnapshot(Rect Pages, Rect Table, Rect List, Rect Details, Rect Filters, Rect Options, Rect Playback, Rect Progress);

    private LibraryLayoutSnapshot CaptureLibraryLayout() => new(SmokeBounds(Pages), SmokeBounds(PresetGrid),
        SmokeBounds(LibraryListPane), SmokeBounds(LibraryDetailsPane), SmokeBounds(FilterStrip), SmokeBounds(FilterOptionsBand), SmokeBounds(PlaybackBar), SmokeBounds(TaskStatusBand));

    private void CheckLibraryWorkspaceGeometry()
    {
        var area = new Rect(((Control)Content!).Bounds.Size);
        var workspace = SmokeBounds(LibraryWorkspace);
        var list = SmokeBounds(LibraryListPane);
        var details = SmokeBounds(LibraryDetailsPane);
        var table = SmokeBounds(PresetGrid);
        double listShare = list.Width / workspace.Width;
        // Allow rounding and the gutter while keeping the list near four fifths of the library.
        if (listShare is < .74 or > .82 || details.Left < list.Right - 1 || details.Right > workspace.Right + 1 ||
            table.Width < list.Width - 28 || table.Height < Math.Max(180, ClientSize.Height - 582))
            throw SmokeLayoutFailure("音色列表未获得足够的宽度或高度，或详情栏挤占了列表。");

        var filters = SmokeBounds(FilterStrip);
        var row = new Control[] { SearchBox, FilterTabs };
        double center = SmokeBounds(SearchBox).Center.Y;
        if (Math.Abs(filters.Height - 38) > .5 || row.Any(control => !control.IsEffectivelyVisible ||
                Math.Abs(SmokeBounds(control).Center.Y - center) > 6 || !filters.Inflate(1).Contains(SmokeBounds(control))) ||
            SearchBox.Bounds.Width < 120)
            throw SmokeLayoutFailure("搜索与七组筛选标签没有保持为一行可见控件。");
        var tabs = FilterTabs.GetVisualDescendants().OfType<ToggleButton>().Where(button => button.DataContext is FilterGroup).ToList();
        if (tabs.Count != 7 || tabs.Any(tab => !tab.IsEffectivelyVisible || !filters.Inflate(1).Contains(SmokeBounds(tab))))
            throw SmokeLayoutFailure("七组筛选标签没有完整显示。");
        var options = SmokeBounds(FilterOptionsBand);
        if (!FilterOptionsBand.IsEffectivelyVisible || Math.Abs(options.Height - 96) > .5 ||
            options.Top < filters.Bottom - 1 || list.Top < options.Bottom - 1 || details.Top < options.Bottom - 1 ||
            !options.Inflate(1).Contains(SmokeBounds(FilterOptionsScroll)))
            throw SmokeLayoutFailure("筛选选项未固定占用96像素，或与列表、详情重叠。");

        var playback = SmokeBounds(PlaybackBar);
        var progress = SmokeBounds(TaskStatusBand);
        if (!PlaybackBar.IsEffectivelyVisible || !TaskStatusBand.IsEffectivelyVisible ||
            playback.Height is < 64 or > 90 || progress.Height is < 100 or > 130 ||
            playback.Top < SmokeBounds(Pages).Bottom - 1 || progress.Top < playback.Bottom - 1 ||
            !area.Inflate(1).Contains(playback) || !area.Inflate(1).Contains(progress) ||
            !progress.Inflate(1).Contains(SmokeBounds(GenerationProgressBar)))
            throw SmokeLayoutFailure("播放栏及其下方的进度日志未固定在窗口底部。");
        foreach (var control in new Control[] { TaskProgressTitle, TaskProgressDetails, TaskResourceDetails })
            if (control.IsEffectivelyVisible && !progress.Inflate(1).Contains(SmokeBounds(control)))
                throw SmokeLayoutFailure("任务状态或内存说明超出了固定进度区域。");
        if (!RecentLogList.IsEffectivelyVisible || !progress.Inflate(1).Contains(SmokeBounds(RecentLogList)) ||
            RecentLogList.Bounds.Height > 90 || RecentLogList.ItemCount > 12)
            throw SmokeLayoutFailure("最近日志未限制在固定、可滚动的底部区域。");
    }

    private void CheckLibraryLayoutUnchanged(LibraryLayoutSnapshot expected)
    {
        var actual = CaptureLibraryLayout();
        if (!Same(expected.Pages, actual.Pages) || !Same(expected.Table, actual.Table) ||
            !Same(expected.List, actual.List) || !Same(expected.Details, actual.Details) ||
            !Same(expected.Filters, actual.Filters) || !Same(expected.Options, actual.Options) ||
            !Same(expected.Playback, actual.Playback) || !Same(expected.Progress, actual.Progress))
            throw SmokeLayoutFailure("空闲、生成、停止或切换筛选改变了列表与底部播放进度区域的位置。");

        static bool Same(Rect left, Rect right) => Math.Abs(left.X - right.X) <= .5 && Math.Abs(left.Y - right.Y) <= .5 &&
            Math.Abs(left.Width - right.Width) <= .5 && Math.Abs(left.Height - right.Height) <= .5;
    }

    private async Task CheckStableLibraryLayoutAsync(string name)
    {
        var previous = (_hasTaskFeedback, _taskTitle, _statusMessage, _taskProgressText, _taskMemoryText,
            _taskSelectionText, _progressValue, _progressIndeterminate);
        string activeGroup = FilterGroups.Single(group => group.IsActive).Key;
        var overflow = new PresetRow(new PresetRecord { Id = Guid.NewGuid().ToString("N"), Name = "TEST ONLY filter overflow",
            Tags = Enumerable.Range(0, 48).Select(index => $"TEST ONLY long filter option {index:00}").ToList() });
        try
        {
            Presets.Add(overflow);
            RefreshFilterCollections();
            _smokeLayoutPhase = name + "-idle";
            _hasTaskFeedback = false;
            _statusMessage = "TEST ONLY: idle";
            _taskProgressText = _taskMemoryText = _taskSelectionText = "";
            NotifyAll();
            await SettleLayoutAsync();
            CheckSmokeLayout();
            var fixedLayout = CaptureLibraryLayout();
            CaptureSmokeImage($"ui-idle-{name}.png");

            foreach (var group in FilterGroups)
            {
                SelectFilterGroup(group.Key);
                await SettleLayoutAsync();
                CheckSmokeLayout();
                CheckLibraryLayoutUnchanged(fixedLayout);
                var tab = FilterTabs.GetVisualDescendants().OfType<ToggleButton>().Single(button => ReferenceEquals(button.DataContext, group));
                // ToggleButton changes its target before Click; the OneWay model must restore an already-active tab.
                tab.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                tab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await SettleLayoutAsync();
                if (!group.IsActive || tab.IsChecked != true || !ReferenceEquals(ActiveFilterOptions, group.Options))
                    throw SmokeLayoutFailure("再次点击已激活筛选标签后标签或选项失去选中状态。");
            }
            SelectFilterGroup("tag");
            await SettleLayoutAsync();
            if (FilterOptionsScroll.Extent.Height <= FilterOptionsScroll.Viewport.Height)
                throw SmokeLayoutFailure("大量筛选选项没有在固定区域中滚动。");
            var viewport = SmokeBounds(FilterOptionsScroll).Inflate(1);
            var visibleRows = FilterOptionsItems.GetVisualDescendants().OfType<ToggleButton>()
                .Where(button => viewport.Contains(SmokeBounds(button))).Select(button => Math.Round(SmokeBounds(button).Top)).Distinct().Count();
            if (visibleRows < 3)
                throw SmokeLayoutFailure("固定筛选区域无法完整显示三行紧凑选项。");
            var allOption = ActiveFilterOptions.Single(option => option.Key.Length == 0);
            var allButton = FilterOptionsItems.GetVisualDescendants().OfType<ToggleButton>().Single(button => ReferenceEquals(button.DataContext, allOption));
            if (allOption.IsSelected)
            {
                allButton.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                allButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await SettleLayoutAsync();
                if (!allOption.IsSelected || allButton.IsChecked != true)
                    throw SmokeLayoutFailure("再次点击全部选项后它失去选中状态。");
            }
            string[] savedTagKeys = FilterGroups.Single(group => group.Key == "tag").SelectedKeys.ToArray();
            foreach (string key in overflow.Record.Tags.Take(2))
            {
                var option = ActiveFilterOptions.Single(option => option.Key == key);
                var button = FilterOptionsItems.GetVisualDescendants().OfType<ToggleButton>().Single(button => ReferenceEquals(button.DataContext, option));
                button.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await SettleLayoutAsync();
                if (!option.IsSelected || button.IsChecked != true)
                    throw SmokeLayoutFailure("点击多个筛选选项后选中样式与模型不同步。");
            }
            CheckSmokeLayout();
            CheckLibraryLayoutUnchanged(fixedLayout);
            CaptureSmokeImage($"ui-filters-multiselect-{name}.png");
            SetFilterSelection("tag", savedTagKeys);
            FilterOptionsScroll.Offset = new Vector(0, FilterOptionsScroll.Extent.Height);
            await SettleLayoutAsync();
            if (FilterOptionsScroll.Offset.Y <= 0)
                throw SmokeLayoutFailure("筛选内容溢出时无法向下滚动。");
            CheckLibraryLayoutUnchanged(fixedLayout);
            CaptureSmokeImage($"ui-filters-{name}.png");
            FilterOptionsScroll.Offset = default;
            SelectFilterGroup(activeGroup);
            await SettleLayoutAsync();
            CheckLibraryLayoutUnchanged(fixedLayout);

            await RunTask(async token =>
            {
                _smokeLayoutPhase = name + "-generation-preparing";
                await SettleLayoutAsync();
                CheckLibraryLayoutUnchanged(fixedLayout);
                _taskTitle = "TEST ONLY: generation";
                _taskProgressText = "已处理 9999 / 10000 (100%) · 成功 8999（新生成 8000 / 复用 999） · 失败 1000 · 运行 4/4";
                _taskMemoryText = "实占 3584 / 4096 MiB · 内存预留 128 MiB · 当前并行上限 4 条（设定 16，内存允许 4）";
                _taskSelectionText = "本次所选 14007 条 · 去重 12551 个 · 处理队列 6432 个 · 重复副本 1456 · 待识别 5130 · 暂不支持生成 989";
                _statusMessage = string.Concat(Enumerable.Repeat("TEST ONLY: long renderer error detail.\n", 250));
                Log(_statusMessage);
                NotifyTaskProgress();
                _smokeLayoutPhase = name + "-long-feedback";
                await SettleLayoutAsync();
                CheckSmokeLayout();
                CheckLibraryLayoutUnchanged(fixedLayout);
                if (TaskProgressDetails.Bounds.Height > 18.1 || TaskResourceDetails.Bounds.Height > 18.1)
                    throw SmokeLayoutFailure("长进度说明没有限制在紧凑的单行区域。");
                CaptureSmokeImage($"ui-long-feedback-{name}.png");
                StopTask_Click(StopTaskButton, new RoutedEventArgs());
                token.ThrowIfCancellationRequested();
            });
            _smokeLayoutPhase = name + "-generation-stopped";
            await SettleLayoutAsync();
            CheckLibraryLayoutUnchanged(fixedLayout);
            foreach (string detail in new[] { "TEST ONLY short", "TEST ONLY first line\nTEST ONLY second line", "" })
            {
                _statusMessage = detail;
                _taskMemoryText = detail;
                NotifyTaskProgress();
                await SettleLayoutAsync();
                CheckLibraryLayoutUnchanged(fixedLayout);
            }
        }
        finally
        {
            (_hasTaskFeedback, _taskTitle, _statusMessage, _taskProgressText, _taskMemoryText,
                _taskSelectionText, _progressValue, _progressIndeterminate) = previous;
            PresetGrid.SelectedItem = null;
            PresetsView.Filter = null;
            PresetsView.MoveCurrentToPosition(-1);
            try { Presets.Remove(overflow); RefreshFilterCollections(); }
            finally { PresetsView.Filter = FilterPreset; }
            SelectFilterGroup(activeGroup);
            PresetsView.Refresh();
            NotifyAll();
            await SettleLayoutAsync();
        }
    }

    private async Task CaptureSyntheticLibraryViewportsAsync(PresetRow row)
    {
        double originalWidth = Width, originalHeight = Height;
        object? originalPage = Pages.SelectedItem;
        bool expanded = PresetMetadataExpander.IsExpanded;
        try
        {
            Pages.SelectedItem = LibraryTab;
            ClearFilterSelections();
            PresetGrid.SelectedItem = row;
            PresetMetadataExpander.IsExpanded = false;
            PresetDetailsScroll.Offset = default;
            foreach (var (width, height, name) in new[] { (1380d, 900d, "desktop"), (1040d, 700d, "compact") })
            {
                Width = width;
                Height = height;
                _smokeRequestedViewport = new Size(width, height);
                _smokeLayoutPhase = name + "-synthetic-library";
                await SettleLayoutAsync();
                CheckSmokeLayout();
                WriteSmokeLayoutState($"ui-library-data-{name}.json");
                CaptureSmokeImage($"ui-library-data-{name}.png");
            }
        }
        finally
        {
            Width = originalWidth;
            Height = originalHeight;
            PresetMetadataExpander.IsExpanded = expanded;
            Pages.SelectedItem = originalPage;
            await SettleLayoutAsync();
        }
    }
}
