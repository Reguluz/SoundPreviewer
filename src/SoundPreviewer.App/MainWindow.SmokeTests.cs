using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private Size? _smokeRequestedViewport;
    private string _smokeLayoutPhase = "startup";

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
        CheckSourceFilterCounts();
        CheckDirectoryConfiguration();
        if (!_categoryService.Load().Success || !File.Exists(_categoryService.RulesPath) || string.IsNullOrWhiteSpace(RulesEditor.Text))
            throw new InvalidOperationException("分类规则未能从已有配置或程序内置默认值加载。");
        var progressSample = new QueueProgress(7, 20, "", "", null)
        {
            Generated = 2, Reused = 1, Failed = 1, Unconfigured = 2, RetryLimited = 1,
            ActiveCount = 1, MaxConcurrency = 8, AllowedConcurrency = 3, EffectiveConcurrency = 1,
            MemoryBudgetBytes = RenderResourcePolicy.MemoryLimitBytes, WorkingSetBytes = 536870912
        };
        if (!FormatRenderProgress(progressSample).Contains("成功 3（新生成 2 / 复用 1） · 失败 1", StringComparison.Ordinal) ||
            !FormatRenderProgress(progressSample).Contains("运行 1/1", StringComparison.Ordinal) ||
            !FormatRenderResources(progressSample).Contains("当前并行上限 1 条（设定 8，内存允许 3）", StringComparison.Ordinal) ||
            !FormatRenderResources(progressSample).Contains("实占 512 / 4096 MiB", StringComparison.Ordinal))
            throw new InvalidOperationException("实时成功失败计数或有效并行分母显示不一致。");
        if (App.TestInstalledStorage && (!ApplicationPaths.SamePath(App.DataDirectory, ApplicationPaths.DefaultPreviewDirectory) ||
            !ApplicationPaths.SamePath(_settings.PreviewDirectory, App.DataDirectory) || !PreviewDirectoryBox.IsReadOnly))
            throw new InvalidOperationException("安装版与 Portable 的音色库及生成目录没有固定在程序旁 Preview。");
        await CheckPresetDragDataAsync();
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
                TaskTitle != "本次没有生成预览" || !StatusMessage.Contains("未配置", StringComparison.Ordinal) || !TaskSettingsVisibility ||
                !TaskProgressText.Contains("成功 0（新生成 0 / 复用 0） · 失败 0 · 未配置 10000", StringComparison.Ordinal) ||
                !TaskSelectionText.Contains("本次所选 10000 条 · 去重 10000 个 · 处理队列 10000 个", StringComparison.Ordinal) ||
                TaskSelectionDetails.Text != TaskSelectionText)
                throw new InvalidOperationException("无渲染器的大批量任务没有保留明确结果。");
            generationFeedbackChecked = true;
        }
        var editingChecked = await CheckEditingPersistenceAsync();
        var rendererSettingsChecked = await CheckRendererSettingsIsolationAsync();
        PluginConfiguration? editedBackendModel = null;
        var originalBackend = RenderBackend.Unconfigured;
        var actualViewports = new List<object>();
        foreach (var (width, height, name) in new[] { (1380d, 900d, "desktop"), (1040d, 700d, "compact") })
        {
            _smokeRequestedViewport = new Size(width, height);
            _smokeLayoutPhase = name + "-format";
            Width = width; Height = height;
            Pages.SelectedIndex = 0;
            SynthFilter.SelectedItem = SynthFilters.Single(choice => choice.Value == SynthKind.Serum2);
            await SettleLayoutAsync();
            CheckSmokeLayout();
            actualViewports.Add(new { Name = name, RequestedWidth = width, RequestedHeight = height, ActualWidth = ClientSize.Width, ActualHeight = ClientSize.Height });
            WriteSmokeLayoutState($"ui-layout-{name}.json");
            CaptureSmokeImage($"ui-format-{name}.png");
            var feedback = _statusMessage;
            var progressText = _taskProgressText;
            var memoryText = _taskMemoryText;
            var selectionText = _taskSelectionText;
            var fixedTaskBounds = TaskStatusBand.Bounds;
            var fixedPageBounds = Pages.Bounds;
            _smokeLayoutPhase = name + "-long-feedback";
            _taskProgressText = "已处理 9999 / 10000 (100%) · 成功 8999（新生成 8000 / 复用 999） · 失败 1000 · 运行 4/4";
            _taskMemoryText = "实占 3584 / 4096 MiB · 内存预留 128 MiB · 当前并行上限 4 条（设定 16，内存允许 4）";
            _taskSelectionText = "本次所选 14007 条 · 去重 12551 个 · 处理队列 6432 个 · 重复副本 1456 · 待识别 5130 · 暂不支持生成 989";
            _statusMessage = string.Concat(Enumerable.Repeat("TEST ONLY: long renderer error detail.\n", 250));
            NotifyTaskProgress();
            await SettleLayoutAsync();
            CheckSmokeLayout();
            if (TaskProgressDetails.Bounds.Height > 32.1 || TaskResourceDetails.Bounds.Height > 32.1 ||
                Math.Abs(TaskStatusBand.Bounds.Height - 188) > .1 || TaskStatusBand.Bounds != fixedTaskBounds || Pages.Bounds != fixedPageBounds)
                throw SmokeLayoutFailure("长渲染错误挤占音色库内容区域。");
            CaptureSmokeImage($"ui-long-feedback-{name}.png");
            foreach (string detail in new[] { "TEST ONLY short", "TEST ONLY first line\nTEST ONLY second line", "" })
            {
                _statusMessage = detail;
                _taskMemoryText = detail;
                NotifyTaskProgress();
                await SettleLayoutAsync();
                if (TaskStatusBand.Bounds != fixedTaskBounds || Pages.Bounds != fixedPageBounds)
                    throw SmokeLayoutFailure("进度说明从一行变成两行或内存说明显隐时改变了任务区域或音色库位置。");
            }
            _statusMessage = feedback;
            _taskProgressText = progressText;
            _taskMemoryText = memoryText;
            _taskSelectionText = selectionText;
            NotifyTaskProgress();
            ResetFilterControls();
            for (var page = 0; page < 3; page++)
            {
                _smokeLayoutPhase = $"{name}-page-{page}";
                Pages.SelectedIndex = page;
                await SettleLayoutAsync();
                if (page == 0)
                {
                    PresetGrid.SelectedItem = Presets.FirstOrDefault(row => row.Record.DuplicateOfId.Length == 0 && PresetDragUnavailableReason(row.Record).Length == 0);
                    PresetDetailsScroll.Offset = default;
                    await SettleLayoutAsync();
                    if (Selected is not null && (!CanLocatePreset || !CanDragPreset || !LocatePresetButton.IsEffectivelyEnabled || !PresetDragHandle.IsEffectivelyEnabled))
                        throw SmokeLayoutFailure("独立音色的定位或拖出入口未启用。");
                    PresetDragHandle.BringIntoView();
                    await SettleLayoutAsync();
                    if (!SmokeBounds(PresetDetailsScroll).Contains(SmokeBounds(PresetDragHandle)) ||
                        !SmokeBounds(PresetDetailsScroll).Contains(SmokeBounds(LocatePresetButton)))
                        throw SmokeLayoutFailure("音色定位或拖出入口不能滚动到详情可视区域。");
                }
                CaptureSmokeImage($"ui-{name}-{page}.png");
            }
            foreach (var (tab, suffix) in new[] { (DirectorySettingsTab, "directories"), (DiscoveredSettingsTab, "discovered"), (RenderSettingsTab, "renderers"), (StorageSettingsTab, "storage"), (ClassificationSettingsTab, "categories") })
            {
                _smokeLayoutPhase = $"{name}-settings-{suffix}";
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
            Presets = Presets.Count, PlaybackChecked = playbackChecked, FormatsChecked = true, FormatViewports = actualViewports.Count, ActualViewports = actualViewports,
            MixedDirectoryChecked = true, UnidentifiedChecked = Presets.Any(row => row.Record.Status == PresetStatus.Unidentified),
            PluginDirectoriesChecked = true, PluginScanDefaultsChecked = true, PluginScanControlsViewports = 2,
            ClosingCallbacksChecked = true,
            TemplatePersistenceChecked = editingChecked.Template, MetadataPersistenceChecked = editingChecked.Metadata,
            RendererSettingsChecked = rendererSettingsChecked,
            PresetFileTransferChecked = true, SourceRevealCommandChecked = true, SourceCountsChecked = true,
            CrossSourceDuplicatesChecked = true, DirectoryConfigurationChecked = true,
            CategoryRulesAvailableChecked = true, CategoryRulesPath = _categoryService.RulesPath,
            StartupWarnings = App.StorageStartupMessages,
            RenderProgressCountersChecked = true, EffectiveConcurrencyDisplayChecked = true,
            QueueSelectionCountsChecked = true, ProgressLayoutStableChecked = true,
            DirectoryConfigurationPath = DirectoryConfigurationLocation, DirectoryPluginDirectories = _settings.PluginDirectories,
            InstalledStorageChecked = App.TestInstalledStorage, LibraryDirectory = App.DataDirectory, PreviewDirectory = _settings.PreviewDirectory,
            ExistingPreviewsLocal = Presets.Where(row => row.Record.PreviewPath.Length > 0).All(row => File.Exists(row.Record.PreviewPath) && ApplicationPaths.IsWithin(row.Record.PreviewPath, App.DataDirectory)),
            StoredPreviewRecords = Presets.Count(row => row.Record.PreviewPath.Length > 0),
            DiscoveredPlugins = DiscoveredPlugins.Count, StopChecked = stopChecked,
            GenerationFeedbackChecked = generationFeedbackChecked, GenerationFeedbackItems = 10000, GeneratedViewChecked = generatedViewChecked,
            DuplicateFilterChecked = true, DuplicateRecords = Presets.Count(row => row.Record.DuplicateOfId.Length > 0),
            ForceDefaultOff = ForceGeneration.IsChecked != true, Width = Bounds.Width, Height = Bounds.Height
        }));
        App.Shutdown();
    }

    private void CheckSourceFilterCounts()
    {
        string originalSource = (SourceFilter.SelectedItem as Choice<string>)?.Value ?? "";
        string sourceId = "TEST-ONLY-source-counts-" + Guid.NewGuid().ToString("N");
        string secondSourceId = sourceId + "-other";
        string path = Path.Combine(App.DataDirectory, "TEST ONLY count source", "preset.vital");
        var canonical = new PresetRow(new PresetRecord { Id = Guid.NewGuid().ToString("N"), SourceId = sourceId, SourcePath = path });
        var duplicate = new PresetRow(new PresetRecord { Id = Guid.NewGuid().ToString("N"), SourceId = sourceId, SourcePath = path, DuplicateOfId = canonical.Record.Id });
        var added = new PresetRow(new PresetRecord { Id = Guid.NewGuid().ToString("N"), SourceId = sourceId, SourcePath = path });
        var crossPath = new PresetRow(new PresetRecord { Id = Guid.NewGuid().ToString("N"), SourceId = secondSourceId,
            SourcePath = Path.Combine(App.DataDirectory, "TEST ONLY other source", "copy.vital"), DuplicateOfId = canonical.Record.Id });
        try
        {
            Presets.Add(canonical);
            Presets.Add(duplicate);
            RefreshFilterChoices();
            var choice = SourceFilters.Single(item => item.Value == sourceId);
            if (choice.Label != Path.GetDirectoryName(path) + "（2 个音色，去重 1 个，重复副本 1 个）")
                throw new InvalidOperationException("来源下拉未显示对应路径的音色总量和重复副本数量。");
            SourceFilter.SelectedItem = choice;
            if (PresetsView.Count != 1) throw new InvalidOperationException("来源计数改变了默认重复项筛选。");
            ShowDuplicates.IsChecked = true;
            if (PresetsView.Count != 2 || ((Choice<string>)SourceFilter.SelectedItem!).Label != choice.Label)
                throw new InvalidOperationException("显示重复项应恢复副本，来源扫描总量不应变化。");
            ShowDuplicates.IsChecked = false;
            SearchBox.Text = "TEST ONLY no source count match";
            PresetsView.Refresh();
            if (PresetsView.Count != 0 || ((Choice<string>)SourceFilter.SelectedItem!).Label != choice.Label)
                throw new InvalidOperationException("搜索筛选不应改变来源路径的扫描总量。");
            SearchBox.Clear();
            Presets.Add(added);
            RefreshFilterChoices();
            if (SourceFilter.SelectedItem is not Choice<string> selected || selected.Value != sourceId ||
                selected.Label != Path.GetDirectoryName(path) + "（3 个音色，去重 2 个，重复副本 1 个）")
                throw new InvalidOperationException("来源计数刷新后未保留所选来源 ID 或新增音色数量。");
            Presets.Add(crossPath);
            RefreshFilterChoices();
            if (!SourceFilters.Single(item => item.Value == sourceId).Label.Contains("跨来源相同 2 个", StringComparison.Ordinal) ||
                !SourceFilters.Single(item => item.Value == secondSourceId).Label.Contains("1 个音色，去重 1 个，跨来源相同 1 个", StringComparison.Ordinal))
                throw new InvalidOperationException("跨路径重复统计遗漏了主项或其他来源副本。");
            SourceFilter.SelectedItem = SourceFilters.Single(item => item.Value == secondSourceId);
            if (PresetsView.Count != 1 || PresetsView.Cast<PresetRow>().Single() != crossPath ||
                _duplicateView.ResolveCanonicalSelection(PresetsView.Cast<PresetRow>().Select(item => item.Record)).Single() != canonical.Record)
                throw new InvalidOperationException("仅含跨路径副本的来源未保留本地代表，或生成未归并到主项。");
            PresetGrid.SelectedItem = crossPath;
            if (!CanRenderSelection) throw new InvalidOperationException("来源副本无法通过主项重新生成。");
            if (!SourceFilters[0].Label.StartsWith($"全部来源（{Presets.Count} 个音色，去重 ", StringComparison.Ordinal))
                throw new InvalidOperationException("全部来源的音色总量不正确。");
        }
        finally
        {
            PresetGrid.SelectedItem = null;
            SourceFilter.SelectedItem = SourceFilters[0];
            SearchBox.Clear();
            ShowDuplicates.IsChecked = false;
            // Reset currency before removing selected fixtures from a filtered Avalonia view.
            PresetsView.Filter = null;
            PresetsView.MoveCurrentToPosition(-1);
            try
            {
                Presets.Remove(canonical);
                Presets.Remove(duplicate);
                Presets.Remove(added);
                Presets.Remove(crossPath);
                RefreshFilterChoices();
            }
            finally { PresetsView.Filter = FilterPreset; }
            SourceFilter.SelectedItem = SourceFilters.FirstOrDefault(item => item.Value == originalSource) ?? SourceFilters[0];
            PresetsView.Refresh();
        }
    }

    private void CheckDirectoryConfiguration()
    {
        string expectedRoot = App.TestInstalledStorage ? ApplicationPaths.PortableApplicationDirectory : App.DataDirectory;
        if (!ApplicationPaths.SamePath(DirectoryConfigurationLocation, Path.Combine(expectedRoot, "Config", "directories.json")) ||
            !File.Exists(DirectoryConfigurationLocation))
            throw new InvalidOperationException("目录 JSON 没有保存在要求的程序根目录。");
        using var document = JsonDocument.Parse(File.ReadAllText(DirectoryConfigurationLocation),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        if (!root.GetProperty("Sources").EnumerateArray().Select(source => source.GetProperty("Id").GetString()).SequenceEqual(_settings.Sources.Select(source => source.Id)) ||
            !root.GetProperty("PluginDirectories").EnumerateArray().Select(directory => directory.GetString()).SequenceEqual(_settings.PluginDirectories) ||
            !DirectoryConfigurationHint.Contains(DirectoryConfigurationLocation, StringComparison.Ordinal))
            throw new InvalidOperationException("目录 JSON 与启动时目录设置或界面位置提示不一致。");
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
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        var previousClient = ClientSize;
        int stableSamples = 0;
        while (timeout.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(50);
            var client = ClientSize;
            stableSamples = client == previousClient && client.Width > 0 && client.Height > 0 ? stableSamples + 1 : 0;
            previousClient = client;
            if (stableSamples < 3) continue;

            // Native desktops can reject a requested size without another resize notification.
            // Keep subsequent invalidated measurements at the actual client size, including long text.
            Width = client.Width;
            Height = client.Height;
            RefreshClientLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var content = (Control)Content!;
            if (ClientSize == client && content.IsMeasureValid && content.IsArrangeValid &&
                Math.Abs(content.Bounds.Width - client.Width) <= .5 && Math.Abs(content.Bounds.Height - client.Height) <= .5)
                return;
            stableSamples = 0;
        }
        throw SmokeLayoutFailure("窗口客户区与布局在 3 秒内未稳定。");
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
            throw SmokeLayoutFailure("版本标识缺失或与状态文字重叠。");
        if (!area.Contains(SmokeBounds(ForceGeneration)) || SmokeBounds(ForceGeneration).Intersects(SmokeBounds(AppBrandName)))
            throw SmokeLayoutFailure("全量覆盖选项不可见或与标题重叠。");
        CheckScanButtonVisible(ScanPluginsButton);
        CheckScanButtonVisible(ScanPresetsButton);
        if (SmokeBounds(ScanPluginsButton).Intersects(SmokeBounds(AppBrandName)) ||
            SmokeBounds(ScanPluginsButton).Intersects(SmokeBounds(ScanPresetsButton)) ||
            SmokeBounds(ScanPresetsButton).Intersects(SmokeBounds(ForceGeneration)))
            throw SmokeLayoutFailure("扫描音源、扫描音色与主工具栏重叠。");
        var hint = SmokeBounds(PresetFormatHint);
        if (!PresetFormatHint.IsEffectivelyVisible || hint.Width <= 0 || hint.Height <= 0 || !area.Contains(hint) || hint.Bottom > SmokeBounds(PresetGrid).Top + 1)
            throw SmokeLayoutFailure("文件后缀说明不可见或超出表格区域。");
        foreach (var suffix in new[] { ".vital", ".vitalbank", ".fxp", ".SerumPreset", ".fxb", ".spf", ".spf2", ".sbf", ".swzip" })
            if (PresetFormatHint.Text?.Contains(suffix, StringComparison.Ordinal) != true) throw new InvalidOperationException("文件后缀说明缺失。");
        if (TaskStatusBand.IsEffectivelyVisible && (SmokeBounds(TaskProgressTitle).Intersects(SmokeBounds(TaskProgressMetadata)) ||
            SmokeBounds(TaskProgressDetails).Bottom > SmokeBounds(GenerationProgressBar).Top || SmokeBounds(TaskStatusBand).Bottom > SmokeBounds(Pages).Top))
            throw SmokeLayoutFailure("任务进度区域内容重叠。");
        if (TaskResourceDetails.IsEffectivelyVisible && (SmokeBounds(TaskResourceDetails).Bottom > SmokeBounds(GenerationProgressBar).Top ||
            SmokeBounds(TaskResourceDetails).Top < SmokeBounds(TaskProgressDetails).Bottom || !area.Contains(SmokeBounds(TaskResourceDetails))))
            throw SmokeLayoutFailure("任务内存信息超出界面或与其他内容重叠。");
        if (EmptyPresetState.IsEffectivelyVisible && SmokeBounds(EmptyPresetState).Bottom > SmokeBounds(PresetGrid).Bottom + 1)
            throw SmokeLayoutFailure("空列表说明溢出音色表格。");
        var drag = SmokeBounds(PresetDragHandle);
        var details = SmokeBounds(PresetDetailsScroll);
        if (!PresetDragHandle.IsEffectivelyVisible || drag.Width <= 0 || drag.Height < 32 ||
            drag.Left < details.Left - 1 || drag.Right > details.Right + 1)
            throw SmokeLayoutFailure("音色拖动入口不可见或横向溢出详情区。");
    }

    private InvalidOperationException SmokeLayoutFailure(string message)
    {
        try
        {
            WriteSmokeLayoutState("ui-layout-error.json");
            CaptureSmokeImage("ui-layout-error.png");
        }
        catch (Exception error) { message += " 布局诊断保存失败：" + error.Message; }
        return new InvalidOperationException(message + $" 阶段：{_smokeLayoutPhase}；客户区：{ClientSize}；内容：{((Control)Content!).Bounds}。");
    }

    private void WriteSmokeLayoutState(string fileName)
    {
        var content = (Control)Content!;
        var screen = Screens.ScreenFromWindow(this);
        Control[] controls = [AppBrandName, ForceGeneration, ScanPluginsButton, ScanPresetsButton, BuildVersionLabel,
            StatusFooterText, PresetFormatHint, PresetGrid, TaskStatusBand, TaskProgressTitle, TaskProgressMetadata,
            TaskProgressDetails, TaskResourceDetails, GenerationProgressBar, Pages, PresetDragHandle, PresetDetailsScroll, LocatePresetButton];
        var state = new
        {
            Phase = _smokeLayoutPhase,
            RequestedWidth = _smokeRequestedViewport?.Width, RequestedHeight = _smokeRequestedViewport?.Height,
            WindowWidth = Width, WindowHeight = Height, ClientWidth = ClientSize.Width, ClientHeight = ClientSize.Height,
            ContentBounds = SmokeRect(content.Bounds), RenderScaling,
            ScreenWorkingArea = screen?.WorkingArea.ToString(),
            Controls = controls.Select(control => new
            {
                control.Name, control.IsEffectivelyVisible, control.IsMeasureValid, control.IsArrangeValid,
                Bounds = control.TranslatePoint(default, content) is { } point ? SmokeRect(new Rect(point, control.Bounds.Size)) : null,
                DesiredWidth = control.DesiredSize.Width, DesiredHeight = control.DesiredSize.Height
            }).ToList()
        };
        File.WriteAllText(Path.Combine(App.DataDirectory, fileName), JsonSerializer.Serialize(state, LibraryJson.Options));
    }

    private static object SmokeRect(Rect rect) => new { rect.X, rect.Y, rect.Width, rect.Height };

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
            throw SmokeLayoutFailure($"扫描按钮 {button.Name} 未完整显示。");
    }

    private void CaptureSmokeImage(string name)
    {
        var content = (Control)Content!;
        using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)content.Bounds.Width), Math.Max(1, (int)content.Bounds.Height)), new Vector(96, 96));
        bitmap.Render(content);
        bitmap.Save(Path.Combine(App.DataDirectory, name));
    }
}
