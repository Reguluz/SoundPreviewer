using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private async Task RunSmokeTestAsync()
    {
        // Exercises the same controller, bindings and native playback on both desktop targets.
        if (ForceGeneration.IsChecked == true || ShowDuplicates.IsChecked == true)
            throw new InvalidOperationException("全量生成与重复项必须默认关闭。");
        if (SourceSynthChoice.SelectedIndex != 0 || SourceChoices[0].Value is not null ||
            PluginDirectoryGrid.ItemsSource?.Cast<object>().Count() != PluginDirectories.Count ||
            DiscoveredPluginGrid.ItemsSource?.Cast<object>().Count() != DiscoveredPlugins.Count)
            throw new InvalidOperationException("设置列表绑定或目录自动识别默认值错误。");
        CheckPluginScanDefaults();
        if (PresetsView.Cast<PresetRow>().Any(row => row.Record.DuplicateOfId.Length > 0))
            throw new InvalidOperationException("重复音色未默认隐藏。");
        ShowDuplicates.IsChecked = true;
        if (PresetsView.Count != Presets.Count) throw new InvalidOperationException("显示重复项未恢复完整索引。");
        ShowDuplicates.IsChecked = false;
        foreach (var synth in Enum.GetValues<SynthKind>())
        {
            var filter = SynthFilters.Single(choice => choice.Value == synth);
            if (filter.Label != $"{Labels.Synth(synth)} ({Labels.PresetExtensions(synth)})")
                throw new InvalidOperationException("音源类型与文件后缀标签不一致。");
        }
        var first = Presets.FirstOrDefault(row => row.Record.DuplicateOfId.Length == 0);
        if (first is not null)
        {
            ResetFilterControls();
            SynthFilter.SelectedItem = SynthFilters.Single(choice => choice.Value == first.Record.Synth);
            SourceFilter.SelectedItem = SourceFilters.Single(choice => choice.Value == (first.Record.SourceId.Length == 0 ? MissingFilter : first.Record.SourceId));
            PackageFilter.SelectedItem = PackageFilters.Single(choice => choice.Value == (first.Package.Length == 0 ? MissingFilter : first.Package));
            CategoryFilter.SelectedItem = CategoryFilters.First(choice => choice.Value == (first.Record.CategoryTags.FirstOrDefault() ?? MissingFilter));
            FavoriteFilter.SelectedItem = FavoriteFilters.Single(choice => choice.Value == (first.Record.Favorite ? 1 : 0));
            StatusFilter.SelectedItem = StatusFilters.Single(choice => choice.Value == first.Record.Status);
            SearchBox.Text = first.Name;
            PresetsView.Refresh();
            if (!PresetsView.Cast<PresetRow>().Contains(first)) throw new InvalidOperationException("组合筛选遗漏匹配音色。");
            FavoriteFilter.SelectedItem = FavoriteFilters.Single(choice => choice.Value == (first.Record.Favorite ? 0 : 1));
            if (PresetsView.Cast<PresetRow>().Contains(first)) throw new InvalidOperationException("收藏筛选没有排除音色。");
            ResetFilterControls();
        }
        var menu = new ContextMenu();
        ColumnMenu_Opened(menu, new RoutedEventArgs());
        if (menu.Items.Count != PresetGrid.Columns.Count) throw new InvalidOperationException("列选择菜单与表格列不一致。");
        var column = PresetGrid.Columns.First(value => value.IsVisible);
        var item = menu.Items.OfType<MenuItem>().First(value => Equals(value.Header, column.Header));
        item.IsChecked = false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (column.IsVisible) throw new InvalidOperationException("列选择没有隐藏主表列。");
        item.IsChecked = true;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        foreach (var order in Enum.GetValues<PreviewNoteOrder>())
        {
            var template = new PreviewTemplate { NoteOrder = order };
            var record = new PresetRecord
            {
                TemplateJson = JsonSerializer.Serialize(template, LibraryJson.Options),
                Notes = template.Pitches.Select((pitch, index) => new NotePreview { MidiNote = pitch, StartSeconds = index * .5, DurationSeconds = .5 }).ToList()
            };
            foreach (var (region, pitch) in new[] { ("Middle", 60), ("High", 84), ("Low", 36) })
                if (FindPreviewNote(record, region)?.MidiNote != pitch) throw new InvalidOperationException("新旧音区顺序的试听按钮映射错误。");
        }
        var playbackChecked = false;
        PresetGrid.SelectedItem = Presets.FirstOrDefault(row => row.Record.DuplicateOfId.Length == 0 && row.HasPreview) ?? first;
        await LoadSelectedPreview();
        if (_mediaOpened)
        {
            _player.Volume = 0;
            _player.Play(0);
            await Task.Delay(250);
            if (_player.PositionSeconds <= 0) throw new InvalidOperationException("原生音频播放位置未前进。");
            _player.Pause();
            var paused = _player.PositionSeconds;
            await Task.Delay(100);
            if (Math.Abs(_player.PositionSeconds - paused) > .05) throw new InvalidOperationException("原生音频暂停检查失败。");
            _player.Seek(Math.Min(1, _player.DurationSeconds / 2));
            _player.Resume();
            await Task.Delay(100);
            _player.Stop();
            _player.Volume = _settings.PlaybackVolume;
            playbackChecked = true;
        }
        var stopChecked = false;
        await RunTask(async token =>
        {
            await SettleLayoutAsync();
            if (!CanStopTask || !StopTaskButton.IsEnabled || !TaskStatusBand.IsVisible || !GenerationProgressBar.IsIndeterminate)
                throw new InvalidOperationException("任务准备阶段未显示进度和停止按钮。");
            var elapsed = TaskElapsed;
            await Task.Delay(1100, token);
            if (TaskElapsed == elapsed) throw new InvalidOperationException("任务用时未更新。");
            CaptureSmokeImage("ui-generation-preparing.png");
            StopTask_Click(StopTaskButton, new RoutedEventArgs());
            if (CanStopTask) throw new InvalidOperationException("停止请求未及时更新。");
            stopChecked = true;
            token.ThrowIfCancellationRequested();
        });
        if (!stopChecked || !IsIdle || CanStopTask || !StatusMessage.StartsWith("已停止", StringComparison.Ordinal))
            throw new InvalidOperationException("停止后任务状态未恢复。");
        bool generatedViewChecked = false, generationFeedbackChecked = false;
        var preview = Presets.FirstOrDefault(row => row.Record.DuplicateOfId.Length == 0 && row.HasPreview);
        if (preview is not null)
        {
            SearchBox.Text = "TEST ONLY: no search match";
            ShowGeneratedPreviews();
            if (StatusFilter.SelectedItem is not StatusFilterChoice { GeneratedOnly: true } ||
                SearchBox.Text?.Length > 0 || !PresetsView.Cast<PresetRow>().Contains(preview) || !CanPlay)
                throw new InvalidOperationException("已生成视图没有清除旧筛选或选择预览。");
            generatedViewChecked = true;
            ResetFilterControls();
        }
        var unconfigured = _settings.Plugins.FirstOrDefault(plugin => plugin.Backend == RenderBackend.Unconfigured);
        if (unconfigured is not null)
        {
            var pending = Enumerable.Range(0, 10000).Select(index => new PresetRecord { Id = $"TEST-ONLY-progress-{index}", Name = $"TEST ONLY progress {index}", Synth = unconfigured.Synth, Status = PresetStatus.Pending }).ToList();
            await Generate(pending);
            if (!IsIdle || !TaskStatusBand.IsVisible || GenerationProgressBar.IsIndeterminate || ProgressValue != 100 ||
                TaskTitle != "本次没有生成预览" || !StatusMessage.Contains("未配置", StringComparison.Ordinal) || !TaskSettingsVisibility)
                throw new InvalidOperationException("无渲染器的大批量任务没有保留明确结果。");
            generationFeedbackChecked = true;
        }
        PluginConfiguration? editedBackendModel = null;
        var originalBackend = RenderBackend.Unconfigured;
        foreach (var (width, height, name) in new[] { (1380d, 900d, "desktop"), (1040d, 700d, "compact") })
        {
            Width = width; Height = height;
            Pages.SelectedIndex = 0;
            SynthFilter.SelectedItem = SynthFilters.Single(choice => choice.Value == SynthKind.Serum2);
            await SettleLayoutAsync();
            CheckSmokeLayout();
            CaptureSmokeImage($"ui-format-{name}.png");
            var feedback = _statusMessage;
            _statusMessage = string.Concat(Enumerable.Repeat("TEST ONLY: long renderer error detail.\n", 250));
            NotifyTaskProgress();
            await SettleLayoutAsync();
            CheckSmokeLayout();
            if (TaskProgressDetails.Bounds.Height > 48.1 || TaskStatusBand.Bounds.Height > 125)
                throw new InvalidOperationException("长渲染错误挤占音色库内容区域。");
            CaptureSmokeImage($"ui-long-feedback-{name}.png");
            _statusMessage = feedback;
            NotifyTaskProgress();
            ResetFilterControls();
            for (var page = 0; page < 3; page++)
            {
                Pages.SelectedIndex = page;
                await SettleLayoutAsync();
                CaptureSmokeImage($"ui-{name}-{page}.png");
            }
            foreach (var (tab, suffix) in new[] { (DirectorySettingsTab, "directories"), (DiscoveredSettingsTab, "discovered"), (RenderSettingsTab, "renderers"), (StorageSettingsTab, "storage"), (ClassificationSettingsTab, "categories") })
            {
                Pages.SelectedItem = SettingsTab;
                SettingsPages.SelectedItem = tab;
                await SettleLayoutAsync();
                if (tab == RenderSettingsTab)
                {
                    var backends = PluginGrid.GetVisualDescendants().OfType<ComboBox>()
                        .Where(combo => combo.IsEffectivelyVisible)
                        .DistinctBy(combo => (combo.DataContext as PluginConfiguration)?.Synth).ToList();
                    await File.WriteAllTextAsync(Path.Combine(App.DataDirectory, $"ui-backends-{name}.json"), JsonSerializer.Serialize(backends.Select(combo => new
                    {
                        Synth = (combo.DataContext as PluginConfiguration)?.Synth.ToString(),
                        ModelBackend = (combo.DataContext as PluginConfiguration)?.Backend.ToString(),
                        SelectedItem = combo.SelectedItem?.ToString(), combo.SelectedIndex, combo.ItemCount
                    })));
                    if (backends.Count != Plugins.Count || backends.Any(combo => combo.DataContext is not PluginConfiguration plugin ||
                        combo.SelectedItem is not RenderBackend selected || selected != plugin.Backend || combo.ItemCount != Enum.GetValues<RenderBackend>().Length))
                        throw new InvalidOperationException("渲染器方式列表未显示全部音源的当前设置。");
                    var editable = backends.First(combo => combo.DataContext is PluginConfiguration { Synth: SynthKind.Serum1 });
                    var model = (PluginConfiguration)editable.DataContext!;
                    if (name == "desktop")
                    {
                        originalBackend = model.Backend;
                        editedBackendModel = model;
                        editable.SelectedIndex = (int)RenderBackend.ExternalWorker;
                        await SettleLayoutAsync();
                    }
                    if (model.Backend != RenderBackend.ExternalWorker || editable.SelectedItem is not RenderBackend.ExternalWorker)
                        throw new InvalidOperationException("渲染器方式编辑未写回设置或在页面及窗口切换后丢失。");
                }
                if (tab == DirectorySettingsTab) CheckScanButtonVisible(DirectoryScanPluginsButton);
                if (tab == DiscoveredSettingsTab) CheckScanButtonVisible(DiscoveredScanPluginsButton);
                CaptureSmokeImage($"ui-settings-{suffix}-{name}.png");
            }
            SettingsPages.SelectedItem = StorageSettingsTab;
            var quick = QuickPreviewMode.IsChecked;
            QuickPreviewMode.IsChecked = true;
            await SettleLayoutAsync();
            if (HoldBox.IsEffectivelyEnabled || TailBox.IsEffectivelyEnabled) throw new InvalidOperationException("快速预览未禁用固定时长输入。");
            QuickPreviewMode.IsChecked = false;
            await SettleLayoutAsync();
            if (!HoldBox.IsEffectivelyEnabled || !TailBox.IsEffectivelyEnabled) throw new InvalidOperationException("固定时长输入未恢复。");
            QuickPreviewMode.IsChecked = quick;
        }
        if (editedBackendModel is not null)
        {
            editedBackendModel.Backend = originalBackend;
            RefreshGrid(PluginGrid);
        }
        await CheckClosingCallbacksAsync();
        await File.WriteAllTextAsync(Path.Combine(App.DataDirectory, "ui-smoke.json"), JsonSerializer.Serialize(new
        {
            Loaded = true, Platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Presets = Presets.Count, PlaybackChecked = playbackChecked, FormatsChecked = true, FormatViewports = 2,
            MixedDirectoryChecked = true, UnidentifiedChecked = Presets.Any(row => row.Record.Status == PresetStatus.Unidentified),
            PluginDirectoriesChecked = true, PluginScanDefaultsChecked = true, PluginScanControlsViewports = 2,
            ClosingCallbacksChecked = true,
            DiscoveredPlugins = DiscoveredPlugins.Count, StopChecked = stopChecked,
            GenerationFeedbackChecked = generationFeedbackChecked, GenerationFeedbackItems = 10000, GeneratedViewChecked = generatedViewChecked,
            DuplicateFilterChecked = true, DuplicateRecords = Presets.Count(row => row.Record.DuplicateOfId.Length > 0),
            ForceDefaultOff = ForceGeneration.IsChecked != true, Width = Bounds.Width, Height = Bounds.Height
        }));
        App.Shutdown();
    }

    private async Task CheckClosingCallbacksAsync()
    {
        var preview = Presets.FirstOrDefault(row => row.HasPreview);
        PresetGrid.SelectedItem = preview;
        var pendingLoad = LoadSelectedPreview();
        // Use the real teardown path while an audio read may still be awaiting its UI continuation.
        CompleteWindowClose();
        int generationAfterClose = _selectionGeneration;
        PresetGrid.SelectedItem = null;
        PresetGrid.SelectedItem = preview;
        await pendingLoad;
        await LoadSelectedPreview();
        StopPlayback();
        UpdatePlayback();
        Play_Click(ScanPluginsButton, new RoutedEventArgs());
        StartPlayback(0, null);
        var volume = VolumeSlider.Value;
        VolumeSlider.Value = volume == 1 ? .5 : 1;
        VolumeSlider.Value = volume;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (!_isClosing || _mediaOpened || _isPlaying || _playWhenOpen || _loadedPreviewPath.Length > 0 ||
            _selectionGeneration != generationAfterClose)
            throw new InvalidOperationException("关闭窗口后的选择、音频加载或播放回调重新启用了播放器。");
    }

    private async Task SettleLayoutAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        UpdateLayout();
        await Task.Delay(70);
    }

    private Rect SmokeBounds(Control control)
    {
        var content = (Control)Content!;
        var point = control.TranslatePoint(default, content) ?? throw new InvalidOperationException("控件不在窗口可视树内。");
        return new Rect(point, control.Bounds.Size);
    }

    private void CheckSmokeLayout()
    {
        var content = (Control)Content!;
        var area = new Rect(content.Bounds.Size);
        if (BuildVersionLabel.Text != AppVersion || !BuildVersionLabel.IsEffectivelyVisible ||
            SmokeBounds(BuildVersionLabel).Intersects(SmokeBounds(StatusFooterText)))
            throw new InvalidOperationException("版本标识缺失或与状态文字重叠。");
        if (!area.Contains(SmokeBounds(ForceGeneration)) || SmokeBounds(ForceGeneration).Intersects(SmokeBounds(AppBrandName)))
            throw new InvalidOperationException("全量覆盖选项不可见或与标题重叠。");
        CheckScanButtonVisible(ScanPluginsButton);
        CheckScanButtonVisible(ScanPresetsButton);
        if (SmokeBounds(ScanPluginsButton).Intersects(SmokeBounds(AppBrandName)) ||
            SmokeBounds(ScanPluginsButton).Intersects(SmokeBounds(ScanPresetsButton)) ||
            SmokeBounds(ScanPresetsButton).Intersects(SmokeBounds(ForceGeneration)))
            throw new InvalidOperationException("扫描音源、扫描音色与主工具栏重叠。");
        var hint = SmokeBounds(PresetFormatHint);
        if (!PresetFormatHint.IsEffectivelyVisible || hint.Width <= 0 || hint.Height <= 0 || !area.Contains(hint) || hint.Bottom > SmokeBounds(PresetGrid).Top + 1)
            throw new InvalidOperationException("文件后缀说明不可见或超出表格区域。");
        foreach (var suffix in new[] { ".vital", ".vitalbank", ".fxp", ".SerumPreset", ".fxb", ".spf", ".spf2", ".sbf", ".swzip" })
            if (PresetFormatHint.Text?.Contains(suffix, StringComparison.Ordinal) != true) throw new InvalidOperationException("文件后缀说明缺失。");
        if (TaskStatusBand.IsEffectivelyVisible && (SmokeBounds(TaskProgressTitle).Intersects(SmokeBounds(TaskProgressMetadata)) ||
            SmokeBounds(TaskProgressDetails).Bottom > SmokeBounds(GenerationProgressBar).Top || SmokeBounds(TaskStatusBand).Bottom > SmokeBounds(Pages).Top))
            throw new InvalidOperationException("任务进度区域内容重叠。");
    }

    private static void CheckPluginScanDefaults()
    {
        // Pure path checks: UI smoke tests must never inspect or associate the user's installed synths.
        var defaults = ResolvePluginScanDirectories([], []);
        if (defaults.Count == 0 || ApplicationPaths.DefaultPluginDirectories.Any(directory => !defaults.Any(value => ApplicationPaths.SamePath(value, directory))))
            throw new InvalidOperationException("首次使用或空目录列表没有恢复平台默认安装位置。");
        var syntheticRoot = Path.Combine(Path.GetTempPath(), "TEST-ONLY-plugin-roots");
        var customRoots = new[] { Path.Combine(syntheticRoot, "unavailable"), Path.Combine(syntheticRoot, "custom") };
        if (!ResolvePluginScanDirectories(customRoots, []).SequenceEqual(customRoots))
            throw new InvalidOperationException("自定义或不可访问的音源安装目录被替换或丢弃。");
        var vital = new PluginConfiguration { Synth = SynthKind.Vital, Backend = RenderBackend.VitalCli,
            ExecutablePath = Path.Combine(syntheticRoot, "Vital.app", "Contents", "MacOS", "Vital") };
        var inferred = ResolvePluginScanDirectories([], [vital]);
        if (!inferred.Any(directory => ApplicationPaths.SamePath(directory, syntheticRoot)) ||
            inferred.Any(directory => directory.EndsWith(Path.Combine("Contents", "MacOS"), StringComparison.Ordinal)))
            throw new InvalidOperationException("已配置的 Vital 应用程序未使用应用程序包所在安装目录。");
        if (vital.Backend != RenderBackend.VitalCli)
            throw new InvalidOperationException("初始化音源目录更改了现有渲染方式。");

        var unavailableRoot = Path.Combine(syntheticRoot, "unavailable");
        var newPlugin = new DiscoveredPlugin { Id = "TEST-ONLY-new", FilePath = Path.Combine(syntheticRoot, "new", "Serum2.vst3") };
        var offlinePlugin = new DiscoveredPlugin { Id = "TEST-ONLY-offline", FilePath = Path.Combine(unavailableRoot, "Vital.app") };
        var otherPlugin = new DiscoveredPlugin { Id = "TEST-ONLY-other", FilePath = Path.Combine(unavailableRoot + "-other", "Vital.app") };
        var scan = new PluginScanResult
        {
            Plugins = [newPlugin], UnavailableRoots = ["", "   ", "\0invalid", unavailableRoot],
            Warnings = ["TEST ONLY: invalid installation root", "TEST ONLY: unavailable installation root"]
        };
        var merged = MergePluginScanResults(scan, [offlinePlugin, otherPlugin]);
        if (merged.Count != 2 || !merged.Contains(newPlugin) || !merged.Contains(offlinePlugin) || merged.Contains(otherPlugin) ||
            scan.Warnings.Count != 2 || scan.UnavailableRoots.Count != 4)
            throw new InvalidOperationException("无效安装目录中断了有效扫描结果，或错误保留了其他目录的音源。");
        if (IsWithinUnavailablePluginRoot(newPlugin.FilePath, "") || IsWithinUnavailablePluginRoot(newPlugin.FilePath, "\0invalid"))
            throw new InvalidOperationException("无效安装目录不能作为恢复旧音源的依据。");
    }

    private void CheckScanButtonVisible(Button button)
    {
        var area = new Rect(((Control)Content!).Bounds.Size);
        var bounds = SmokeBounds(button);
        if (!button.IsEffectivelyVisible || bounds.Width <= 0 || bounds.Height <= 0 || !area.Contains(bounds))
            throw new InvalidOperationException($"扫描按钮 {button.Name} 未完整显示。");
    }

    private void CaptureSmokeImage(string name)
    {
        var content = (Control)Content!;
        using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)content.Bounds.Width), Math.Max(1, (int)content.Bounds.Height)), new Vector(96, 96));
        bitmap.Render(content);
        bitmap.Save(Path.Combine(App.DataDirectory, name));
    }
}
