using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private sealed record LibraryLayoutSnapshot(Rect Pages, Rect Table, Rect List, Rect Details, Rect Filters, Rect Playback, Rect Progress);

    private LibraryLayoutSnapshot CaptureLibraryLayout() => new(SmokeBounds(Pages), SmokeBounds(PresetGrid),
        SmokeBounds(LibraryListPane), SmokeBounds(LibraryDetailsPane), SmokeBounds(FilterStrip), SmokeBounds(PlaybackBar), SmokeBounds(TaskStatusBand));

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
            table.Width < list.Width - 28 || table.Height < Math.Max(180, ClientSize.Height - 480))
            throw SmokeLayoutFailure("音色列表未获得足够的宽度或高度，或详情栏挤占了列表。");

        var filters = SmokeBounds(FilterStrip);
        var row = new Control[] { SearchBox, SynthFilter, StatusFilter, SourceFilter };
        double center = SmokeBounds(SearchBox).Center.Y;
        if (filters.Height > 48 || row.Any(control => !control.IsEffectivelyVisible ||
                Math.Abs(SmokeBounds(control).Center.Y - center) > 6 || !filters.Inflate(1).Contains(SmokeBounds(control))) ||
            SearchBox.Bounds.Width < 120)
            throw SmokeLayoutFailure("搜索和主要筛选没有保持为一行可见控件。");

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
            !Same(expected.Filters, actual.Filters) || !Same(expected.Playback, actual.Playback) || !Same(expected.Progress, actual.Progress))
            throw SmokeLayoutFailure("空闲、生成、停止或筛选浮层改变了列表与底部播放进度区域的位置。");

        static bool Same(Rect left, Rect right) => Math.Abs(left.X - right.X) <= .5 && Math.Abs(left.Y - right.Y) <= .5 &&
            Math.Abs(left.Width - right.Width) <= .5 && Math.Abs(left.Height - right.Height) <= .5;
    }

    private async Task CheckStableLibraryLayoutAsync(string name)
    {
        var previous = (_hasTaskFeedback, _taskTitle, _statusMessage, _taskProgressText, _taskMemoryText,
            _taskSelectionText, _progressValue, _progressIndeterminate, MoreFiltersToggle.IsChecked);
        try
        {
            _smokeLayoutPhase = name + "-idle";
            _hasTaskFeedback = false;
            _statusMessage = "TEST ONLY: idle";
            _taskProgressText = _taskMemoryText = _taskSelectionText = "";
            MoreFiltersToggle.IsChecked = false;
            NotifyAll();
            await SettleLayoutAsync();
            CheckSmokeLayout();
            var fixedLayout = CaptureLibraryLayout();
            CaptureSmokeImage($"ui-idle-{name}.png");

            MoreFiltersToggle.IsChecked = true;
            await SettleLayoutAsync();
            if (!AdvancedFiltersPanel.IsEffectivelyVisible)
                throw SmokeLayoutFailure("更多筛选没有打开覆盖面板。");
            CheckLibraryLayoutUnchanged(fixedLayout);
            CaptureSmokeImage($"ui-filters-{name}.png");
            MoreFiltersToggle.IsChecked = false;
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
                _taskSelectionText, _progressValue, _progressIndeterminate, MoreFiltersToggle.IsChecked) = previous;
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
            ResetFilterControls();
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
