using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly LibraryStore _store;
    private readonly WavePlayer _player = new();
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _taskTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Stopwatch _taskWatch = new();
    private Action? _flushTaskProgress;
    private AppSettings _settings;
    private CancellationTokenSource? _taskCancellation;
    private bool _isBusy;
    private bool _stopRequested;
    private bool _closeWhenDone;
    private bool _isPlaying;
    private bool _mediaOpened;
    private bool _playWhenOpen;
    private double _openPosition;
    private double? _segmentEnd;
    private int _selectionGeneration;
    private string _loadedPreviewPath = "";
    private PresetRow? _selected;
    private string _statusMessage = "就绪";
    private double _progressValue;
    private bool _hasTaskFeedback;
    private bool _progressIndeterminate;
    private bool _needsRendererSetup;
    private string _taskTitle = "";
    private string _taskProgressText = "";
    private bool _updatingFilterChoices;
    private const string MissingFilter = "\0";
    private readonly PresetCategoryService _categoryService = new();
    private string _categoryRulesStatus = "";
    private string _loadedCategoryRules = "";
    private int _baselineRefreshGeneration;

    public ObservableCollection<PresetRow> Presets { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<SourceFolder> Sources { get; } = [];
    public ObservableCollection<PluginConfiguration> Plugins { get; } = [];
    public ObservableCollection<string> PluginDirectories { get; } = [];
    public ObservableCollection<DiscoveredPluginRow> DiscoveredPlugins { get; } = [];
    public ICollectionView PresetsView { get; }
    public ObservableCollection<Choice<string>> SourceFilters { get; } = [new("全部来源", "")];
    public ObservableCollection<Choice<string>> PackageFilters { get; } = [new("全部音色包", "")];
    public ObservableCollection<Choice<string>> TagFilters { get; } = [new("全部标签", "")];
    public ObservableCollection<Choice<string>> CategoryFilters { get; } = [new("全部分类", "")];
    public List<Choice<int?>> FavoriteFilters { get; } = [new("全部收藏状态", null), new("已收藏", 1), new("未收藏", 0)];
    public ObservableCollection<InitializationRow> InitBaselines { get; } = [];
    public string CategoryRulesStatus => _categoryRulesStatus;
    public List<Choice<SynthKind>> SynthChoices { get; } = Enum.GetValues<SynthKind>().Where(s => s != SynthKind.Unknown).Select(s => new Choice<SynthKind>(Labels.Synth(s), s)).ToList();
    public List<Choice<SynthKind?>> SourceChoices { get; } = [new("自动识别（混合音色包）", null), .. Enum.GetValues<SynthKind>().Where(s => s != SynthKind.Unknown).Select(s => new Choice<SynthKind?>(Labels.Synth(s), s))];
    public List<Choice<SynthKind?>> SynthFilters { get; } = [new("全部音源", null), .. Enum.GetValues<SynthKind>().Select(s => new Choice<SynthKind?>($"{Labels.Synth(s)} ({Labels.PresetExtensions(s)})", s))];
    public List<StatusFilterChoice> StatusFilters { get; } = [new("全部状态"), new("已生成（含待确认）", GeneratedOnly: true), new("最近生成失败", LatestFailedOnly: true), .. Enum.GetValues<PresetStatus>().Select(s => new StatusFilterChoice(Labels.Status(s), s))];
    public List<Choice<RenderBackend>> BackendChoices { get; } = [new("未配置", RenderBackend.Unconfigured), new("Vital 原生", RenderBackend.VitalCli), new("外部工作进程", RenderBackend.ExternalWorker), new("DawDreamer", RenderBackend.DawDreamer)];
    public ObservableCollection<CompatibilityRow> Compatibility { get; } = [];
    public PresetRow? Selected => _selected;
    public bool IsBusy => _isBusy;
    public bool IsIdle => !_isBusy;
    public bool CanStopTask => _isBusy && !_stopRequested;
    public bool HasSelection => _selected is not null;
    public bool CanEditSelection => HasSelection && IsIdle;
    public bool CanAssignSynth => CanEditSelection && _selected?.Record.Status != PresetStatus.Missing;
    public bool CanPlay => _selected is not null && File.Exists(_selected.Record.PreviewPath);
    public bool CanConfirm => IsIdle && CanPlay && _selected?.Record.Status == PresetStatus.NeedsReview;
    public bool CanRenderSelection => IsIdle && _selected is not null && _selected.Record.DuplicateOfId.Length == 0 && _selected.Record.Synth != SynthKind.Unknown && _selected.Record.Status is not (PresetStatus.Unsupported or PresetStatus.Missing or PresetStatus.Unidentified);
    public bool CanViewCanonical => _selected is not null && Presets.Any(p => p.Record.Id == _selected.Record.DuplicateOfId);
    public Visibility CanonicalVisibility => CanViewCanonical ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => PresetsView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyLibraryVisibility => Presets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyFilterVisibility => Presets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyMessage => Presets.Count == 0 ? "暂无音色" : "当前筛选没有匹配音色";
    public int GeneratedPreviewCount => Presets.Count(p => p.Record.DuplicateOfId.Length == 0 && p.HasPreview);
    public int ReviewPreviewCount => Presets.Count(p => p.Record.DuplicateOfId.Length == 0 && p.HasPreview && p.Record.Status == PresetStatus.NeedsReview);
    public Visibility GeneratedResultsVisibility => GeneratedPreviewCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string LibraryLocation => Path.GetFullPath(App.DataDirectory);
    private static string X86HostPath => Path.Combine(AppContext.BaseDirectory, "Vst2Host-x86", "SoundPreviewer.Vst2Host.exe");
    public string NowPlaying => _selected?.Name ?? "未选择预览";
    public int VisiblePresetCount => PresetsView is CollectionView view ? view.Count : PresetsView.Cast<PresetRow>().Count();
    public bool CanRenderFiltered => IsIdle && !PresetsView.IsEmpty;
    public bool CanRetryTasks => IsIdle && Presets.Any(row => row.Record.DuplicateOfId.Length == 0 &&
        (row.Record.Status == PresetStatus.Failed || row.Record.LastRenderOutcome == "失败"));
    public string LibrarySummary => $"筛选 {VisiblePresetCount} / {Presets.Count} · 已生成 {GeneratedPreviewCount}（待确认 {ReviewPreviewCount}） · {Presets.Count(p => p.Record.DuplicateOfId.Length > 0)} 个重复副本";
    public string StatusMessage => _statusMessage;
    public double ProgressValue => _progressValue;
    public Visibility TaskStatusVisibility => _hasTaskFeedback ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TaskSettingsVisibility => _needsRendererSetup && IsIdle ? Visibility.Visible : Visibility.Collapsed;
    public string TaskTitle => _taskTitle;
    public string TaskProgressText => _taskProgressText;
    public string TaskElapsed => $"用时 {(_taskWatch.Elapsed.TotalHours >= 1 ? _taskWatch.Elapsed.ToString(@"h\:mm\:ss") : _taskWatch.Elapsed.ToString(@"m\:ss"))}";
    public bool IsProgressIndeterminate => IsBusy && _progressIndeterminate;
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        _store = new LibraryStore(Path.Combine(App.DataDirectory, "library.db"));
        _store.Initialize();
        _settings = App.ImportedSettings ?? _store.LoadSettings();
        if (string.IsNullOrWhiteSpace(_settings.PreviewDirectory)) _settings.PreviewDirectory = Path.Combine(App.DataDirectory, "Previews");
        bool upgradedNoteOrder = _settings.Template.NoteOrder != PreviewNoteOrder.MiddleHighLow;
        _settings.Template.NoteOrder = PreviewNoteOrder.MiddleHighLow;
        int refreshedHost = PluginSelectionService.RefreshBundledHost(_settings.Plugins, X86HostPath);
        if (App.ImportedSettings is not null || refreshedHost > 0 || upgradedNoteOrder) _store.SaveSettings(_settings);
        PresetsView = CollectionViewSource.GetDefaultView(Presets);
        PresetsView.Filter = FilterPreset;
        InitializeComponent();
        DataContext = this;
        PopulateSettings();
        if (refreshedHost > 0) Log("已更新内置 32 位 Sylenth1 宿主路径，保留原有插件与音色库。");
        LoadCategoryRulesEditor();
        _player.Volume = _settings.PlaybackVolume;
        _playbackTimer.Tick += (_, _) => UpdatePlayback();
        _playbackTimer.Start();
        _taskTimer.Tick += (_, _) =>
        {
            _flushTaskProgress?.Invoke();
            NotifyTaskProgress();
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var restored = await Task.Run(() => _store.RecoverInterrupted());
            var records = await Task.Run(_store.LoadPresets);
            if (_categoryService.Load().Success)
            {
                var classified = await Task.Run(() => _categoryService.Apply(records));
                if (classified.Changed > 0) await Task.Run(() => _store.UpsertMany(records));
            }
            SetPresets(records);
            await RefreshInitBaselinesAsync();
            if (GeneratedPreviewCount > 0)
            {
                _hasTaskFeedback = true;
                _taskTitle = $"已保存 {GeneratedPreviewCount} 个预览";
                _statusMessage = $"{ReviewPreviewCount} 项待试听确认。";
                _progressValue = 100;
                NotifyAll();
            }
            Log($"打开本地音色库，恢复 {restored} 个未完成任务。");
            if (App.ScanOnStart) await Scan();
            if (App.SmokeTest)
            {
                var expectedFormats = new Dictionary<SynthKind, string>
                {
                    [SynthKind.Vital] = "Vital (.vital / .vitalbank)",
                    [SynthKind.Serum1] = "Serum 1 (.fxp)",
                    [SynthKind.Serum2] = "Serum 2 (.SerumPreset / .fxp)",
                    [SynthKind.Sylenth1] = "Sylenth1 (.fxp / .fxb)",
                    [SynthKind.Unknown] = "待识别 (.fxp / .fxb)"
                };
                if (SynthFilters.Count != expectedFormats.Count + 1 || SynthFilters.Count(c => c.Value is null) != 1)
                    throw new InvalidOperationException("音源筛选选项数量检查失败。");
                if (SourceChoices[0].Value is not null || SourceSynthChoice.SelectedIndex != 0
                    || PluginDirectories.Count != PluginDirectoryGrid.Items.Count
                    || DiscoveredPlugins.Count != DiscoveredPluginGrid.Items.Count)
                    throw new InvalidOperationException("自动识别目录或共享插件目录绑定检查失败。");
                if (ForceGeneration.IsChecked == true || ShowDuplicates.IsChecked == true)
                    throw new InvalidOperationException("全量生成与显示重复项必须默认关闭。");
                if (PresetsView.Cast<PresetRow>().Any(p => p.Record.DuplicateOfId.Length > 0))
                    throw new InvalidOperationException("重复音色未默认隐藏。");
                ShowDuplicates.IsChecked = true;
                if (PresetsView.Cast<PresetRow>().Count() != Presets.Count)
                    throw new InvalidOperationException("显示重复项没有恢复完整路径索引。");
                ShowDuplicates.IsChecked = false;
                foreach (var (synth, label) in expectedFormats)
                    if (SynthFilters.Count(c => c.Value == synth && c.Label == label) != 1)
                        throw new InvalidOperationException($"音源筛选标签与类型不匹配：{synth}。");
                var allFilter = SynthFilters.Single(c => c.Value is null);
                var serumFilter = SynthFilters.Single(c => c.Value == SynthKind.Serum2);

                Rect Bounds(FrameworkElement element) => element.TransformToAncestor((FrameworkElement)Content)
                    .TransformBounds(new Rect(new Point(), element.RenderSize));
                IEnumerable<DependencyObject> Descendants(DependencyObject element)
                {
                    for (var childIndex = 0; childIndex < VisualTreeHelper.GetChildrenCount(element); childIndex++)
                    {
                        var child = VisualTreeHelper.GetChild(element, childIndex);
                        yield return child;
                        foreach (var descendant in Descendants(child)) yield return descendant;
                    }
                }
                void CheckFormatLayout()
                {
                    var visual = (FrameworkElement)Content;
                    if (!new Rect(new Point(), visual.RenderSize).Contains(Bounds(ForceGeneration)) ||
                        Bounds(ForceGeneration).IntersectsWith(Bounds(AppBrandName)) ||
                        !ForceGeneration.Content.ToString()!.Contains("覆盖", StringComparison.Ordinal))
                        throw new InvalidOperationException("全量覆盖选项不可见或与标题重叠。");
                    var hint = Bounds(PresetFormatHint);
                    var table = Bounds(PresetGrid);
                    if (!PresetFormatHint.IsVisible || hint.Width <= 0 || hint.Height <= 0
                        || !new Rect(new Point(), visual.RenderSize).Contains(hint)
                        || hint.Bottom > table.Top + 1 || hint.Left < table.Left - 1)
                        throw new InvalidOperationException("文件后缀说明不可见或超出表格区域。");
                    foreach (var control in new FrameworkElement[] { SearchBox, SynthFilter, StatusFilter })
                        if (Bounds(control).Bottom > hint.Top + 1)
                            throw new InvalidOperationException("文件后缀说明与筛选工具栏重叠。");
                    foreach (var suffix in new[] { ".vital", ".vitalbank", ".fxp", ".SerumPreset", ".fxb" })
                        if (!PresetFormatHint.Text.Contains(suffix, StringComparison.Ordinal))
                            throw new InvalidOperationException($"文件后缀说明缺少 {suffix}。");
                    if (TaskStatusBand.IsVisible && (Bounds(TaskProgressTitle).IntersectsWith(Bounds(TaskProgressMetadata))
                        || Bounds(TaskProgressDetails).Bottom > Bounds(GenerationProgressBar).Top
                        || Bounds(TaskStatusBand).Bottom > Bounds(Pages).Top
                        || !new Rect(new Point(), visual.RenderSize).Contains(Bounds(TaskStatusBand))))
                        throw new InvalidOperationException("任务进度区域内容重叠或超出窗口。");
                }
                void Capture(string name)
                {
                    var visual = (FrameworkElement)Content;
                    var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    using var output = File.Create(Path.Combine(App.DataDirectory, name));
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    encoder.Save(output);
                }
                void CheckLibraryFilters()
                {
                    var row = Presets.FirstOrDefault(p => p.Record.DuplicateOfId.Length == 0);
                    if (row is null) return;
                    ResetFilterControls();
                    SynthFilter.SelectedItem = SynthFilters.Single(choice => choice.Value == row.Record.Synth);
                    SourceFilter.SelectedItem = SourceFilters.Single(choice => choice.Value == (row.Record.SourceId.Length == 0 ? MissingFilter : row.Record.SourceId));
                    PackageFilter.SelectedItem = PackageFilters.Single(choice => choice.Value == (row.Package.Length == 0 ? MissingFilter : row.Package));
                    CategoryFilter.SelectedItem = CategoryFilters.First(choice => choice.Value == (row.Record.CategoryTags.FirstOrDefault() ?? MissingFilter));
                    var tag = row.Record.Tags.Concat(row.Record.CategoryTags).FirstOrDefault();
                    if (tag is not null) TagFilter.SelectedItem = TagFilters.First(choice => choice.Value == tag);
                    FavoriteFilter.SelectedItem = FavoriteFilters.Single(choice => choice.Value == (row.Record.Favorite ? 1 : 0));
                    StatusFilter.SelectedItem = StatusFilters.Single(choice => choice.Value == row.Record.Status);
                    SearchBox.Text = row.Name;
                    PresetsView.Refresh();
                    if (!PresetsView.Cast<PresetRow>().Contains(row)) throw new InvalidOperationException("来源、分类、标签、状态和收藏组合筛选错误。");
                    FavoriteFilter.SelectedItem = FavoriteFilters.Single(choice => choice.Value == (row.Record.Favorite ? 0 : 1));
                    if (PresetsView.Cast<PresetRow>().Contains(row)) throw new InvalidOperationException("收藏筛选没有排除不匹配音色。");
                    ResetFilterControls();
                    var previousOutcome = row.Record.LastRenderOutcome;
                    row.Record.LastRenderOutcome = "失败";
                    StatusFilter.SelectedItem = StatusFilters.Single(choice => choice.LatestFailedOnly);
                    if (!PresetsView.Cast<PresetRow>().Contains(row)) throw new InvalidOperationException("最近失败筛选遗漏保留旧预览的失败项目。");
                    row.Record.LastRenderOutcome = previousOutcome;
                    ResetFilterControls();
                    var menu = new ContextMenu();
                    ColumnMenu_Opened(menu, new RoutedEventArgs());
                    var column = PresetGrid.Columns.First(value => value.Visibility == Visibility.Visible);
                    var item = menu.Items.OfType<MenuItem>().First(value => value.Header?.ToString() == column.Header?.ToString());
                    item.IsChecked = false;
                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    if (column.Visibility != Visibility.Collapsed) throw new InvalidOperationException("列选择未隐藏主表列。");
                    item.IsChecked = true;
                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    PresetsView.Refresh();
                }
                CheckLibraryFilters();
                foreach (var order in Enum.GetValues<PreviewNoteOrder>())
                {
                    var template = new PreviewTemplate { NoteOrder = order };
                    var record = new PresetRecord
                    {
                        TemplateJson = JsonSerializer.Serialize(template, LibraryJson.Options),
                        Notes = template.Pitches.Select((pitch, index) => new NotePreview { MidiNote = pitch, StartSeconds = index * 0.5, DurationSeconds = 0.5 }).ToList()
                    };
                    foreach (var (region, pitch) in new[] { ("Middle", 60), ("High", 84), ("Low", 36) })
                        if (FindPreviewNote(record, region)?.MidiNote != pitch)
                            throw new InvalidOperationException("新旧音区顺序的试听按钮映射错误。");
                }
                if (Presets.Count > 0)
                {
                    PresetGrid.SelectedItem = Presets.FirstOrDefault(p => p.Record.DuplicateOfId.Length == 0 && File.Exists(p.Record.PreviewPath))
                        ?? Presets.First(p => p.Record.DuplicateOfId.Length == 0);
                    await LoadSelectedPreview();
                    var firstName = _selected!.Name;
                    SearchBox.Text = firstName;
                    if (PresetsView.IsEmpty) throw new InvalidOperationException("音色搜索检查失败。");
                    SearchBox.Clear();
                }
                var playbackChecked = false;
                if (_mediaOpened)
                {
                    _player.Volume = 0;
                    _player.Play(0);
                    await Task.Delay(250);
                    if (_player.PositionSeconds <= 0) throw new InvalidOperationException("原生音频播放位置未前进。");
                    _player.Pause();
                    var paused = _player.PositionSeconds;
                    await Task.Delay(100);
                    if (Math.Abs(_player.PositionSeconds - paused) > 0.05) throw new InvalidOperationException("原生音频暂停检查失败。");
                    _player.Seek(Math.Min(1, _player.DurationSeconds / 2));
                    _player.Resume();
                    await Task.Delay(100);
                    _player.Stop();
                    _player.Volume = _settings.PlaybackVolume;
                    UpdatePlayback();
                    if (_player.DurationSeconds < 10 && !TimeLabel.Text.EndsWith(
                        TimeSpan.FromSeconds(_player.DurationSeconds).ToString(@"mm\:ss\.f"), StringComparison.Ordinal))
                        throw new InvalidOperationException("短预览时长未按十分之一秒显示。");
                    playbackChecked = true;
                }
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var stopChecked = false;
                await RunTask(async token =>
                {
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!CanStopTask || !StopTaskButton.IsEnabled || !TaskStatusBand.IsVisible
                        || !GenerationProgressBar.IsIndeterminate)
                        throw new InvalidOperationException("任务准备阶段未立即显示进度或停止按钮。");
                    var initialElapsed = TaskElapsed;
                    await Task.Delay(1100, token);
                    if (TaskElapsed == initialElapsed) throw new InvalidOperationException("等待任务时用时未更新。");
                    Capture("ui-generation-preparing.png");
                    StopTask_Click(StopTaskButton, new RoutedEventArgs());
                    if (CanStopTask) throw new InvalidOperationException("停止请求未及时更新状态。");
                    stopChecked = true;
                    await Task.Delay(1000, token);
                });
                if (!stopChecked || !IsIdle || CanStopTask || !StatusMessage.StartsWith("已停止", StringComparison.Ordinal))
                    throw new InvalidOperationException("停止后任务状态没有恢复。");
                var generatedViewChecked = false;
                var previewFixture = Presets.FirstOrDefault(p => p.Record.DuplicateOfId.Length == 0 && p.HasPreview && p.Record.Tags.Contains("TEST ONLY"));
                if (previewFixture is not null)
                {
                    previewFixture.Record.Status = PresetStatus.NeedsReview;
                    previewFixture.Refresh();
                    _store.Upsert(previewFixture.Record);
                    SearchBox.Text = "TEST ONLY: no search match";
                    SynthFilter.SelectedItem = serumFilter;
                    FavoritesOnly.IsChecked = true;
                    StatusFilter.SelectedItem = StatusFilters.Single(filter => filter.Value == PresetStatus.Pending);
                    await Generate([previewFixture.Record]);
                    if (StatusFilter.SelectedItem is not StatusFilterChoice { GeneratedOnly: true }
                        || SearchBox.Text.Length > 0 || SynthFilter.SelectedItem is not Choice<SynthKind?> { Value: null } || FavoritesOnly.IsChecked == true
                        || !PresetsView.Cast<PresetRow>().Contains(previewFixture) || !CanPlay
                        || GeneratedPreviewCount == 0 || ReviewPreviewCount == 0 || previewFixture.StatusLabel != "已生成待确认")
                        throw new InvalidOperationException("已生成的待确认预览未能从旧筛选中恢复并显示。");
                    var reopened = _store.LoadPresets().Single(p => p.Id == previewFixture.Record.Id);
                    if (reopened.Status != PresetStatus.NeedsReview || !File.Exists(reopened.PreviewPath))
                        throw new InvalidOperationException("待确认预览未持久保留。");
                    var generatedCount = GeneratedPreviewCount;
                    Confirm_Click(this, new RoutedEventArgs());
                    if (GeneratedPreviewCount != generatedCount || !PresetsView.Cast<PresetRow>().Contains(previewFixture))
                        throw new InvalidOperationException("确认音色后不应从已生成视图消失。");
                    previewFixture.Record.Status = PresetStatus.NeedsReview;
                    previewFixture.Refresh();
                    _store.Upsert(previewFixture.Record);
                    var savedPreviewPath = previewFixture.Record.PreviewPath;
                    previewFixture.Record.PreviewPath = Path.Combine(App.DataDirectory, "TEST-ONLY-missing-preview.wav");
                    previewFixture.Refresh();
                    PresetsView.Refresh();
                    if (previewFixture.HasPreview || PresetsView.Cast<PresetRow>().Contains(previewFixture))
                        throw new InvalidOperationException("丢失的预览文件不应出现在已生成视图。");
                    previewFixture.Record.PreviewPath = savedPreviewPath;
                    previewFixture.Refresh();
                    ClearFilters_Click(this, new RoutedEventArgs());
                    generatedViewChecked = true;
                }
                var noRenderer = _settings.Plugins.FirstOrDefault(p => p.Backend == RenderBackend.Unconfigured);
                var generationFeedbackChecked = false;
                if (noRenderer is not null)
                {
                    var pending = Enumerable.Range(0, 10000).Select(index => new PresetRecord
                    {
                        Id = $"TEST-ONLY-progress-{index}", Name = $"TEST ONLY progress {index}",
                        Synth = noRenderer.Synth, Status = PresetStatus.Pending
                    }).ToList();
                    await Generate(pending);
                    await Task.Delay(250);
                    if (!IsIdle || !TaskStatusBand.IsVisible || GenerationProgressBar.IsIndeterminate
                        || ProgressValue != 100 || TaskTitle != "本次没有生成预览"
                        || !StatusMessage.Contains("未配置", StringComparison.Ordinal)
                        || TaskSettingsVisibility != Visibility.Visible)
                        throw new InvalidOperationException("无渲染器的大批量任务没有保留明确结果与设置入口。");
                    generationFeedbackChecked = true;
                }
                var smokeSelection = PresetGrid.SelectedItem;
                var unidentified = Presets.FirstOrDefault(p => p.Record.Status == PresetStatus.Unidentified);
                if (unidentified is not null)
                {
                    PresetGrid.SelectedItem = unidentified;
                    await LoadSelectedPreview();
                    if (!CanAssignSynth || CanRenderSelection)
                        throw new InvalidOperationException("待识别音色必须允许确认音源但不能直接渲染。");
                    var tag = unidentified.Record.Tags.FirstOrDefault();
                    if (tag is not null)
                    {
                        SearchBox.Text = tag;
                        if (!PresetsView.Cast<PresetRow>().Contains(unidentified)) throw new InvalidOperationException("音源标签搜索检查失败。");
                        SearchBox.Clear();
                    }
                    UpdateLayout();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Capture("ui-unidentified.png");
                    PresetGrid.SelectedItem = smokeSelection;
                }
                foreach (var (width, height, name) in new[] { (1380d, 900d, "desktop"), (1040d, 700d, "compact") })
                {
                    Width = width; Height = height;
                    Pages.SelectedIndex = 0;
                    SynthFilter.SelectedItem = serumFilter;
                    UpdateLayout();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    CheckFormatLayout();
                    var feedback = _statusMessage;
                    _statusMessage = string.Concat(Enumerable.Repeat("TEST ONLY: long renderer error detail, no synthesizer was loaded.\n", 250));
                    NotifyTaskProgress();
                    UpdateLayout();
                    CheckFormatLayout();
                    if (TaskProgressDetails.ActualHeight > 48 || TaskStatusBand.ActualHeight > 125)
                        throw new InvalidOperationException("长渲染错误挤占了音色库内容区域。");
                    Capture($"ui-long-feedback-{name}.png");
                    _statusMessage = feedback;
                    NotifyTaskProgress();
                    UpdateLayout();
                    if (SynthFilter.SelectedItem is not Choice<SynthKind?> { Value: SynthKind.Serum2 }
                        || PresetsView.Cast<PresetRow>().Any(row => row.Record.Synth != SynthKind.Serum2))
                        throw new InvalidOperationException("Serum 2 音源筛选类型检查失败。");
                    var selectedLabel = Descendants(SynthFilter).OfType<TextBlock>()
                        .FirstOrDefault(text => text.IsVisible && text.Text == serumFilter.Label)
                        ?? throw new InvalidOperationException("Serum 2 筛选标签未显示。");
                    var labelWidth = new FormattedText(selectedLabel.Text, CultureInfo.CurrentCulture,
                        selectedLabel.FlowDirection, new Typeface(selectedLabel.FontFamily, selectedLabel.FontStyle,
                            selectedLabel.FontWeight, selectedLabel.FontStretch), selectedLabel.FontSize,
                        selectedLabel.Foreground, VisualTreeHelper.GetDpi(selectedLabel).PixelsPerDip).WidthIncludingTrailingWhitespace;
                    if (selectedLabel.ActualWidth + 1 < labelWidth || !Bounds(SynthFilter).Contains(Bounds(selectedLabel)))
                        throw new InvalidOperationException("Serum 2 最长筛选标签被裁切。");
                    Capture($"ui-format-{name}.png");
                    SynthFilter.SelectedItem = allFilter;
                    if (PresetsView.Cast<PresetRow>().Count() != Presets.Count(p => p.Record.DuplicateOfId.Length == 0))
                        throw new InvalidOperationException("全部音源筛选未恢复。");
                    PresetGrid.SelectedItem = smokeSelection;
                    await LoadSelectedPreview();
                    for (var page = 0; page < 3; page++)
                    {
                        Pages.SelectedIndex = page;
                        UpdateLayout();
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        Capture($"ui-{name}-{page}.png");
                    }
                    ShowGeneratedPreviews();
                    UpdateLayout();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    CheckFormatLayout();
                    Capture($"ui-generated-{name}.png");
                    ClearFilters_Click(this, new RoutedEventArgs());
                    DiscoveredPluginGrid.BringIntoView();
                    Pages.SelectedItem = SettingsTab;
                    SettingsPages.SelectedItem = DiscoveredSettingsTab;
                    UpdateLayout();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Capture($"ui-plugins-{name}.png");
                    Pages.SelectedItem = SettingsTab;
                    SettingsPages.SelectedItem = StorageSettingsTab;
                    var savedQuickMode = QuickPreviewMode.IsChecked;
                    QuickPreviewMode.IsChecked = true;
                    UpdateLayout();
                    if (HoldBox.IsEnabled || TailBox.IsEnabled) throw new InvalidOperationException("快速预览应禁用固定按键及尾音输入。");
                    QuickPreviewMode.IsChecked = false;
                    UpdateLayout();
                    if (!HoldBox.IsEnabled || !TailBox.IsEnabled) throw new InvalidOperationException("关闭快速预览后固定时长输入未恢复。");
                    QuickPreviewMode.IsChecked = savedQuickMode;
                    QuickPreviewMode.BringIntoView();
                    UpdateLayout();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Capture($"ui-quick-settings-{name}.png");
                    foreach (var (tab, suffix, table) in new[]
                    {
                        (DirectorySettingsTab, "directories", (DataGrid?)SourceGrid),
                        (DiscoveredSettingsTab, "discovered", DiscoveredPluginGrid),
                        (RenderSettingsTab, "renderers", PluginGrid),
                        (ClassificationSettingsTab, "categories", null)
                    })
                    {
                        SettingsPages.SelectedItem = tab;
                        UpdateLayout();
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        if (table is not null)
                        {
                            var visible = Rect.Intersect(Bounds(table), Bounds(SettingsPages));
                            if (visible.Height < table.ColumnHeaderHeight + table.RowHeight * 5)
                                throw new InvalidOperationException($"设置列表可见空间不足五行：{suffix}，高度 {visible.Height:0}。");
                            var secondary = tab == DirectorySettingsTab ? PluginDirectoryGrid : tab == RenderSettingsTab ? InitializationGrid : null;
                            if (secondary is not null && Rect.Intersect(Bounds(secondary), Bounds(SettingsPages)).Height < secondary.ColumnHeaderHeight + secondary.RowHeight * 4)
                                throw new InvalidOperationException($"设置右侧列表可见空间不足：{suffix}。");
                        }
                        Capture($"ui-settings-{suffix}-{name}.png");
                    }
                }
                await File.WriteAllTextAsync(Path.Combine(App.DataDirectory, "ui-smoke.json"), JsonSerializer.Serialize(new { Loaded = true, Presets = Presets.Count, PlaybackChecked = playbackChecked, FormatsChecked = true, FormatViewports = 2, MixedDirectoryChecked = true, UnidentifiedChecked = unidentified is not null, PluginDirectoriesChecked = true, DiscoveredPlugins = DiscoveredPlugins.Count, StopChecked = stopChecked, GenerationFeedbackChecked = generationFeedbackChecked, GenerationFeedbackItems = 10000, GeneratedViewChecked = generatedViewChecked, DuplicateFilterChecked = true, DuplicateRecords = Presets.Count(p => p.Record.DuplicateOfId.Length > 0), ForceDefaultOff = ForceGeneration.IsChecked != true, Width = ActualWidth, Height = ActualHeight }));
                Application.Current.Shutdown();
            }
        }
        catch (Exception ex) { ShowError(ex); if (App.SmokeTest) Application.Current.Shutdown(1); }
    }

    private bool FilterPreset(object item)
    {
        if (item is not PresetRow row) return false;
        if (ShowDuplicates?.IsChecked != true && row.Record.DuplicateOfId.Length > 0) return false;
        var search = SearchBox?.Text?.Trim() ?? "";
        if (search.Length > 0 && !new[] { row.Name, row.Package, row.Record.SourcePath, row.Record.InternalPath, row.SynthLabel, row.Record.ContentMd5 }.Concat(row.Record.Tags).Concat(row.Record.CategoryTags).Any(s => s.Contains(search, StringComparison.CurrentCultureIgnoreCase))) return false;
        if (SynthFilter?.SelectedItem is Choice<SynthKind?> { Value: { } synth } && row.Record.Synth != synth) return false;
        if (StatusFilter?.SelectedItem is StatusFilterChoice statusFilter &&
            (statusFilter.GeneratedOnly ? !row.HasPreview : statusFilter.LatestFailedOnly ? row.Record.LastRenderOutcome != "失败" : statusFilter.Value is { } status && row.Record.Status != status)) return false;
        if (SourceFilter?.SelectedItem is Choice<string> { Value.Length: > 0 } source &&
            (source.Value == MissingFilter ? row.Record.SourceId.Length != 0 : row.Record.SourceId != source.Value)) return false;
        if (PackageFilter?.SelectedItem is Choice<string> { Value.Length: > 0 } package &&
            (package.Value == MissingFilter ? row.Package.Length != 0 : !string.Equals(row.Package, package.Value, StringComparison.OrdinalIgnoreCase))) return false;
        if (CategoryFilter?.SelectedItem is Choice<string> { Value.Length: > 0 } category &&
            (category.Value == MissingFilter ? row.Record.CategoryTags.Count != 0 : !row.Record.CategoryTags.Contains(category.Value, StringComparer.OrdinalIgnoreCase))) return false;
        if (TagFilter?.SelectedItem is Choice<string> { Value.Length: > 0 } tag &&
            !row.Record.Tags.Concat(row.Record.CategoryTags).Contains(tag.Value, StringComparer.OrdinalIgnoreCase)) return false;
        if (FavoriteFilter?.SelectedItem is Choice<int?> { Value: { } favorite } && row.Record.Favorite != (favorite == 1)) return false;
        return FavoritesOnly?.IsChecked != true || row.Record.Favorite;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingFilterChoices) return;
        PresetsView?.Refresh();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EmptyVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LibrarySummary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisiblePresetCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRenderFiltered)));
    }

    private void RefreshFilterChoices()
    {
        _updatingFilterChoices = true;
        try
        {
            var records = Presets.Select(p => p.Record).ToList();
            void Fill(ObservableCollection<Choice<string>> target, ComboBox control, string allLabel, IEnumerable<Choice<string>> values)
            {
                var selected = (control.SelectedItem as Choice<string>)?.Value ?? "";
                target.Clear();
                target.Add(new(allLabel, ""));
                foreach (var value in values) target.Add(value);
                control.SelectedItem = target.FirstOrDefault(item => item.Value == selected) ?? target[0];
            }
            Fill(SourceFilters, SourceFilter, "全部来源", records.Select(p => p.SourceId).Distinct(StringComparer.Ordinal)
                .Select(id => new Choice<string>(id.Length == 0 ? "未关联来源" : Sources.FirstOrDefault(source => source.Id == id)?.Path ??
                    Path.GetDirectoryName(records.First(p => p.SourceId == id).SourcePath) ?? id,
                    id.Length == 0 ? MissingFilter : id)).OrderBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase));
            Fill(PackageFilters, PackageFilter, "全部音色包", records.Select(p => p.Package).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).Select(value => new Choice<string>(value.Length == 0 ? "无音色包" : value, value.Length == 0 ? MissingFilter : value)));
            Fill(CategoryFilters, CategoryFilter, "全部分类", new[] { new Choice<string>("未分类", MissingFilter) }.Concat(records.SelectMany(p => p.CategoryTags)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).Select(value => new Choice<string>(value, value))));
            Fill(TagFilters, TagFilter, "全部标签", records.SelectMany(p => p.Tags.Concat(p.CategoryTags)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).Select(value => new Choice<string>(value, value)));
        }
        finally { _updatingFilterChoices = false; }
    }

    private void SetPresets(IEnumerable<PresetRecord> records)
    {
        var selectedId = _selected?.Record.Id;
        Presets.Clear();
        foreach (var record in records) Presets.Add(new PresetRow(record));
        RefreshFilterChoices();
        PresetsView.Refresh();
        PresetGrid.SelectedItem = Presets.FirstOrDefault(p => p.Record.Id == selectedId);
        RefreshCompatibility();
        NotifyAll();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await Scan();
    private async Task Scan() => await RunTask(async token =>
    {
        if (Sources.Count == 0) { Pages.SelectedItem = SettingsTab; SettingsPages.SelectedItem = DirectorySettingsTab; throw new InvalidOperationException("请先添加预制目录。"); }
        _statusMessage = "正在扫描预制目录…";
        NotifyAll();
        var scanWatch = Stopwatch.StartNew();
        var result = await new ScanService(_categoryService).ScanAsync(Sources.ToList(), Presets.Select(p => p.Record).ToList(), token);
        var latestFeedback = Presets.ToDictionary(p => p.Record.Id);
        foreach (var record in result.Presets)
            if (latestFeedback.TryGetValue(record.Id, out var latest))
            {
                record.Favorite = latest.Record.Favorite;
                record.Feedback = latest.Record.Feedback;
                record.FeedbackUtc = latest.Record.FeedbackUtc;
            }
        await Task.Run(() => _store.UpsertMany(result.Presets), token);
        SetPresets(result.Presets);
        foreach (var warning in result.Warnings) Log(warning);
        _taskTitle = "目录扫描完成";
        _statusMessage = $"新增 {result.Added}，内容变更 {result.Changed}，MD5 重复副本 {result.Presets.Count(p => p.DuplicateOfId.Length > 0)}，读取警告 {result.Warnings.Count}，耗时 {scanWatch.Elapsed.TotalSeconds:0.0} 秒。";
        Log(_statusMessage);
    });

    private async void Render_Click(object sender, RoutedEventArgs e)
    {
        var force = ForceGeneration.IsChecked == true;
        ForceGeneration.IsChecked = false;
        await Generate(Presets.Select(p => p.Record).ToList(), force);
    }
    private async void RenderSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRenderSelection || _selected is null) return;
        await Generate([_selected.Record], forceRegenerate: true);
    }
    private async void RenderFiltered_Click(object sender, RoutedEventArgs e) =>
        await Generate(PresetsView.Cast<PresetRow>().Select(row => row.Record).ToList());
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        var failed = Presets.Where(row => row.Record.DuplicateOfId.Length == 0 &&
            (row.Record.Status == PresetStatus.Failed || row.Record.LastRenderOutcome == "失败")).Select(row => row.Record).ToList();
        if (failed.Count > 0) await Generate(failed, forceRegenerate: true);
    }

    private async Task Generate(IReadOnlyList<PresetRecord> records, bool forceRegenerate = false) => await RunTask(async token =>
    {
        if (records.Count == 0) throw new InvalidOperationException("尚无可生成的音色，请先扫描目录。");
        _taskTitle = "正在准备生成预览";
        _statusMessage = "正在检查音源与渲染程序…";
        NotifyTaskProgress();
        Log(forceRegenerate ? "开始全量重生成，逐项保留旧预览直到新结果完成。" : "继续生成，默认跳过已有完整预览与 MD5 重复副本。");
        if (_selected is not null && records.Any(p => p.Id == _selected.Record.Id))
        {
            StopPlayback();
            _player.Unload();
            _mediaOpened = false;
            _loadedPreviewPath = "";
        }
        var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings, LibraryJson.Options), LibraryJson.Options)!;
        var preparation = new Progress<string>(message =>
        {
            if (!_isBusy || _stopRequested || _taskTitle != "正在准备生成预览") return;
            _statusMessage = message;
            NotifyTaskProgress();
        });
        await Task.Run(() => RefreshBinaryHashes(settings, token, preparation), token);
        _settings.Plugins = settings.Plugins;
        await Task.Run(() => _store.SaveSettings(_settings), token);
        var rows = Presets.ToDictionary(p => p.Record.Id);
        var changedRows = new ConcurrentDictionary<string, byte>();
        QueueProgress? pendingProgress = null;
        var lastViewRefresh = TimeSpan.Zero;
        var lastLog = TimeSpan.Zero;
        var progress = new QueueReporter(p =>
        {
            if (p.Preset is not null) changedRows.TryAdd(p.Preset.Id, 0);
            Interlocked.Exchange(ref pendingProgress, p);
        });
        _flushTaskProgress = () =>
        {
            var p = Interlocked.Exchange(ref pendingProgress, null);
            if (p is null) return;
            _progressValue = p.Total == 0 ? 0 : p.Completed * 100d / p.Total;
            _progressIndeterminate = p.IsIndeterminate;
            _taskProgressText = p.Total > 0 ? $"已处理 {p.Completed} / {p.Total} ({_progressValue:0}%)" : "";
            if (!_stopRequested)
            {
                _taskTitle = p.Stage == "完成" ? "预览任务已结束" : $"正在{p.Stage}";
                _statusMessage = string.IsNullOrWhiteSpace(p.PresetName) ? p.Message : $"{p.PresetName} · {p.Message}";
            }
            foreach (var id in changedRows.Keys)
                if (changedRows.TryRemove(id, out _) && rows.TryGetValue(id, out var row)) row.Refresh();
            if (_taskWatch.Elapsed - lastViewRefresh >= TimeSpan.FromSeconds(1))
            {
                PresetsView.Refresh();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LibrarySummary)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GeneratedResultsVisibility)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EmptyVisibility)));
                lastViewRefresh = _taskWatch.Elapsed;
            }
            if (_taskWatch.Elapsed - lastLog >= TimeSpan.FromSeconds(2))
            {
                Log($"{p.Completed}/{p.Total} · {_statusMessage}");
                lastLog = _taskWatch.Elapsed;
            }
        };
        var workerPath = Path.Combine(AppContext.BaseDirectory, "SoundPreviewer.Worker.exe");
        var result = await Task.Run(() => new RenderQueue(_store).RunAsync(settings, records, workerPath, progress, token, forceRegenerate), token);
        _flushTaskProgress();
        _flushTaskProgress = null;
        _needsRendererSetup = result.Unconfigured > 0 || result.Failed > 0;
        _taskTitle = result.Generated > 0
            ? result.Failed + result.Unconfigured + result.RetryLimited > 0 ? "预览任务结束，部分项目未生成" : "预览生成完成"
            : result.Reused > 0 ? "已有预览已保留" : "本次没有生成预览";
        _statusMessage = result.Summary;
        _taskProgressText = $"已处理 {result.Eligible} / {result.Eligible}";
        RefreshCompatibility();
        await RefreshInitBaselinesAsync();
        if (result.Generated + result.Reused > 0) ShowGeneratedPreviews();
        Log(result.Summary);
    });

    private async Task RunTask(Func<CancellationToken, Task> action)
    {
        if (_isBusy) return;
        _isBusy = true;
        _stopRequested = false;
        _progressValue = 0;
        _hasTaskFeedback = true;
        _progressIndeterminate = true;
        _needsRendererSetup = false;
        _taskTitle = "任务进行中";
        _statusMessage = "正在准备任务…";
        _taskProgressText = "准备中";
        _taskWatch.Restart();
        _taskTimer.Start();
        _taskCancellation = new CancellationTokenSource();
        NotifyAll();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            _taskCancellation.Token.ThrowIfCancellationRequested();
            await action(_taskCancellation.Token);
            if (_taskTitle == "任务进行中") _taskTitle = "任务完成";
            if (_statusMessage == "正在准备任务…") _statusMessage = "任务已完成。";
            _progressValue = 100;
            _taskProgressText = _taskProgressText == "准备中" ? "" : _taskProgressText;
        }
        catch (OperationCanceledException)
        {
            _flushTaskProgress?.Invoke();
            _taskTitle = "任务已停止";
            _statusMessage = "已停止，已完成的预览已保留，可继续生成。";
            Log(_statusMessage);
        }
        catch (Exception ex)
        {
            _flushTaskProgress?.Invoke();
            _flushTaskProgress = null;
            _taskTimer.Stop();
            _progressIndeterminate = false;
            _taskTitle = "任务未完成";
            ShowError(ex);
        }
        finally
        {
            _flushTaskProgress = null;
            _taskTimer.Stop();
            _taskWatch.Stop();
            _progressIndeterminate = false;
            if (_taskProgressText == "准备中") _taskProgressText = "";
            _isBusy = false;
            _stopRequested = false;
            _taskCancellation.Dispose();
            _taskCancellation = null;
            foreach (var row in Presets) row.Refresh();
            PresetsView.Refresh();
            RefreshCompatibility();
            try { await RefreshInitBaselinesAsync(); }
            catch (Exception error) { Log("基准状态未刷新：" + error.Message); }
            NotifyAll();
            if (_selected is not null && _selected.Record.PreviewPath != _loadedPreviewPath) await LoadSelectedPreview();
            if (_closeWhenDone) Close();
        }
    }

    private void StopTask_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStopTask) return;
        _stopRequested = true;
        _taskTitle = "正在停止任务";
        _statusMessage = "正在停止当前任务…";
        _taskCancellation?.Cancel();
        NotifyAll();
    }

    private void ViewTasks_Click(object sender, RoutedEventArgs e) => Pages.SelectedItem = TasksTab;
    private void ViewGenerated_Click(object sender, RoutedEventArgs e) => ShowGeneratedPreviews();
    private void ShowGeneratedPreviews()
    {
        ResetFilterControls();
        ShowDuplicates.IsChecked = false;
        StatusFilter.SelectedItem = StatusFilters.Single(filter => filter.GeneratedOnly);
        Pages.SelectedIndex = 0;
        PresetsView.Refresh();
        PresetGrid.SelectedItem = PresetsView.Cast<PresetRow>().FirstOrDefault();
        NotifyAll();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        ResetFilterControls();
        PresetsView.Refresh();
        NotifyAll();
    }

    private void ResetFilterControls()
    {
        SearchBox.Clear();
        SynthFilter.SelectedIndex = 0;
        StatusFilter.SelectedIndex = 0;
        SourceFilter.SelectedIndex = 0;
        PackageFilter.SelectedIndex = 0;
        TagFilter.SelectedIndex = 0;
        CategoryFilter.SelectedIndex = 0;
        FavoriteFilter.SelectedIndex = 0;
        FavoritesOnly.IsChecked = false;
    }

    private void RefreshCompatibility()
    {
        Compatibility.Clear();
        foreach (var readiness in RenderReadinessService.Evaluate(_settings))
        {
            var generated = Presets.Count(p => p.Record.Synth == readiness.Synth && p.Record.DuplicateOfId.Length == 0 && p.HasPreview);
            var review = Presets.Count(p => p.Record.Synth == readiness.Synth && p.Record.DuplicateOfId.Length == 0 && p.HasPreview && p.Record.Status == PresetStatus.NeedsReview);
            var output = generated == 0 ? "尚无已生成预览。" : $"已生成 {generated} 项，其中 {review} 项待试听确认。";
            Compatibility.Add(new(Labels.Synth(readiness.Synth), readiness.Status, readiness.Detail + " " + output));
        }
    }
    private void ViewTaskSettings_Click(object sender, RoutedEventArgs e)
    {
        Pages.SelectedItem = SettingsTab;
        SettingsPages.SelectedItem = RenderSettingsTab;
        RendererSettings.IsExpanded = true;
        RendererSettings.BringIntoView();
    }

    private sealed class QueueReporter(Action<QueueProgress> report) : IProgress<QueueProgress>
    {
        public void Report(QueueProgress value) => report(value);
    }

    private void NotifyTaskProgress()
    {
        foreach (var name in new[] { nameof(TaskTitle), nameof(StatusMessage), nameof(TaskProgressText), nameof(TaskElapsed), nameof(ProgressValue), nameof(IsProgressIndeterminate) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private async void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = PresetGrid.SelectedItem as PresetRow;
        await LoadSelectedPreview();
    }

    private async Task LoadSelectedPreview()
    {
        var generation = ++_selectionGeneration;
        _loadedPreviewPath = "";
        StopPlayback();
        _player.Unload();
        _mediaOpened = false;
        Waveform.SetAudio(null);
        NotifyAll();
        if (!CanPlay || _selected is null) return;
        var path = _selected.Record.PreviewPath;
        try
        {
            var audio = await Task.Run(() => WaveFile.Read(path));
            if (generation != _selectionGeneration) return;
            Waveform.SetAudio(audio);
            SeekSlider.Maximum = audio.DurationSeconds;
            _player.Load(audio);
            _mediaOpened = true;
            if (_playWhenOpen) { _player.Play(_openPosition); _isPlaying = true; }
            _loadedPreviewPath = path;
        }
        catch (Exception ex) { if (generation == _selectionGeneration) Log("无法读取预览：" + ex.Message); }
    }

    private void Preset_DoubleClick(object sender, MouseButtonEventArgs e) { if (CanPlay) StartPlayback(0, null); }
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying) { _player.Pause(); _isPlaying = false; }
        else StartPlayback(_player.PositionSeconds, null);
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();
    private void PlayNote_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || sender is not Button button) return;
        var note = FindPreviewNote(_selected.Record, (string)button.Tag);
        if (note is null) { Log("此预览缺少对应音区的保存记录。"); return; }
        StartPlayback(note.StartSeconds, note.StartSeconds + note.DurationSeconds);
    }
    private static NotePreview? FindPreviewNote(PresetRecord record, string region)
    {
        if (string.IsNullOrWhiteSpace(record.TemplateJson))
        {
            int legacyIndex = region switch { "Low" => 0, "Middle" => 1, "High" => 2, _ => -1 };
            return legacyIndex >= 0 && legacyIndex < record.Notes.Count ? record.Notes[legacyIndex] : null;
        }
        try
        {
            var template = JsonSerializer.Deserialize<PreviewTemplate>(record.TemplateJson, LibraryJson.Options);
            int? pitch = region switch { "Low" => template?.LowNote, "Middle" => template?.MiddleNote, "High" => template?.HighNote, _ => null };
            int index = template?.NoteOrder == PreviewNoteOrder.MiddleHighLow
                ? region switch { "Middle" => 0, "High" => 1, "Low" => 2, _ => -1 }
                : region switch { "Low" => 0, "Middle" => 1, "High" => 2, _ => -1 };
            return index >= 0 && index < record.Notes.Count && record.Notes[index].MidiNote == pitch ? record.Notes[index] : null;
        }
        catch (JsonException) { return null; }
    }
    private void StartPlayback(double seconds, double? end)
    {
        if (!CanPlay) return;
        _openPosition = seconds;
        _segmentEnd = end;
        _playWhenOpen = true;
        if (_mediaOpened)
        {
            try { _player.Play(seconds); _isPlaying = true; }
            catch (Exception ex) { ShowError(ex); }
        }
    }
    private void StopPlayback()
    {
        _player.Stop();
        _isPlaying = false;
        _playWhenOpen = false;
        _openPosition = 0;
        _segmentEnd = null;
        if (SeekSlider is not null) SeekSlider.Value = 0;
        Waveform?.SetPosition(0);
    }
    private void UpdatePlayback()
    {
        if (_isPlaying && (!_player.IsPlaying || _segmentEnd is { } end && _player.PositionSeconds >= end)) { StopPlayback(); return; }
        if (_mediaOpened && !SeekSlider.IsMouseCaptureWithin) SeekSlider.Value = _player.PositionSeconds;
        Waveform.SetPosition(_player.PositionSeconds);
        var duration = TimeSpan.FromSeconds(_mediaOpened ? _player.DurationSeconds : 0);
        var format = duration.TotalSeconds < 10 ? @"mm\:ss\.f" : @"mm\:ss";
        TimeLabel.Text = TimeSpan.FromSeconds(_player.PositionSeconds).ToString(format) + " / " + duration.ToString(format);
    }
    private void Seek_Released(object sender, MouseButtonEventArgs e)
    {
        _segmentEnd = null;
        _openPosition = SeekSlider.Value;
        if (_mediaOpened) _player.Seek(SeekSlider.Value);
    }
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _player.Volume = e.NewValue;
        if (_settings is not null) _settings.PlaybackVolume = e.NewValue;
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        _selected.Record.Favorite = !_selected.Record.Favorite;
        SaveFeedback();
    }
    private void Like_Click(object sender, RoutedEventArgs e) => SetFeedback(1);
    private void Dislike_Click(object sender, RoutedEventArgs e) => SetFeedback(-1);
    private void SetFeedback(int value)
    {
        if (_selected is null) return;
        _selected.Record.Feedback = _selected.Record.Feedback == value ? 0 : value;
        _selected.Record.FeedbackUtc = DateTime.UtcNow;
        SaveFeedback();
    }
    private void SaveFeedback()
    {
        _store.Upsert(_selected!.Record);
        _selected.Refresh();
        PresetsView.Refresh();
        NotifyAll();
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!CanConfirm || _selected is null) return;
        _selected.Record.Status = PresetStatus.Ready;
        _selected.Record.Message = "已由用户试听确认音色对应关系。";
        SaveFeedback();
        RefreshCompatibility();
    }
    private async void AssignSynth_Click(object sender, RoutedEventArgs e)
    {
        if (!CanAssignSynth || _selected is null) return;
        try
        {
            if (AssignmentSynthChoice.SelectedItem is not Choice<SynthKind> choice)
                throw new ArgumentException("请先选择此预制所属的音源。");
            var row = _selected;
            ScanService.AssignSynth(row.Record, choice.Value);
            PresetIndex.RebuildDuplicates(Presets.Select(p => p.Record).ToList(), Sources.ToList());
            _store.UpsertMany(Presets.Select(p => p.Record));
            foreach (var preset in Presets) preset.Refresh();
            PresetsView.Refresh();
            RefreshCompatibility();
            NotifyAll();
            await LoadSelectedPreview();
            Log($"已确认音源标签：{row.Name} → {Labels.Synth(choice.Value)}。");
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void ViewCanonical_Click(object sender, RoutedEventArgs e)
    {
        var record = Presets.FirstOrDefault(p => p.Record.Id == _selected?.Record.DuplicateOfId);
        if (record is null) return;
        ResetFilterControls();
        ShowDuplicates.IsChecked = false;
        PresetGrid.SelectedItem = record;
        PresetGrid.ScrollIntoView(record);
    }
    private void Locate_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            if (!File.Exists(_selected.Record.SourcePath)) throw new FileNotFoundException("原预制文件已不存在。");
            var start = new ProcessStartInfo("explorer.exe");
            start.ArgumentList.Add("/select,");
            start.ArgumentList.Add(_selected.Record.SourcePath);
            Process.Start(start);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowColumns_Click(object sender, RoutedEventArgs e)
    {
        if (ColumnsButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = ColumnsButton;
        menu.IsOpen = true;
    }

    private void ColumnMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        menu.Items.Clear();
        foreach (var column in PresetGrid.Columns.OrderBy(column => column.DisplayIndex))
        {
            var item = new MenuItem { Header = column.Header?.ToString(), IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible, StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                if (!item.IsChecked && PresetGrid.Columns.Count(value => value.Visibility == Visibility.Visible) <= 1)
                { item.IsChecked = true; return; }
                column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            };
            menu.Items.Add(item);
        }
    }

    private void LoadCategoryRulesEditor()
    {
        RulesPathBox.Text = PresetCategoryService.DefaultRulesPath;
        try
        {
            _categoryService.EnsureRulesFile();
            if (new FileInfo(PresetCategoryService.DefaultRulesPath).Length > 256 * 1024)
                throw new InvalidDataException("分类规则超过 256 KiB，请缩小文件后重新加载。");
            RulesEditor.Text = File.ReadAllText(PresetCategoryService.DefaultRulesPath);
            _loadedCategoryRules = RulesEditor.Text;
            var result = _categoryService.Load();
            _categoryRulesStatus = result.Success ? $"已加载 {result.RuleCount} 个分类。" : result.Error + " 已保留原有分类。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { _categoryRulesStatus = "分类规则未加载：" + error.Message; }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CategoryRulesStatus)));
    }

    private void ReloadCategoryRules_Click(object sender, RoutedEventArgs e)
    {
        if (RulesEditor.Text != _loadedCategoryRules && MessageBox.Show(this, "重新载入将放弃尚未保存的分类规则，是否继续？", "分类规则", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        LoadCategoryRulesEditor();
    }

    private void OpenCategoryRules_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _categoryService.EnsureRulesFile();
            Process.Start(new ProcessStartInfo(PresetCategoryService.DefaultRulesPath) { UseShellExecute = true });
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void ApplyCategoryRules_Click(object sender, RoutedEventArgs e)
    {
        if (RulesEditor.Text != _loadedCategoryRules)
        {
            ShowError(new InvalidOperationException("分类规则尚未保存，请先点击“保存并应用”。"));
            return;
        }
        await ApplyCategoriesAsync();
    }

    private async void SaveCategoryRules_Click(object sender, RoutedEventArgs e)
    {
        var text = RulesEditor.Text;
        var validation = PresetCategoryService.Validate(text);
        if (!validation.Success) { ShowError(new InvalidDataException(validation.Error)); return; }
        await RunTask(async token =>
        {
            var path = PresetCategoryService.DefaultRulesPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, text, new System.Text.UTF8Encoding(false), token);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
                _loadedCategoryRules = text;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            await ApplyCategoriesCoreAsync(token);
        });
    }

    private Task ApplyCategoriesAsync() => RunTask(ApplyCategoriesCoreAsync);

    private async Task ApplyCategoriesCoreAsync(CancellationToken token)
    {
        var records = Presets.Select(row => row.Record).ToList();
        var applied = await Task.Run(() =>
        {
            var copies = JsonSerializer.Deserialize<List<PresetRecord>>(JsonSerializer.Serialize(records, LibraryJson.Options), LibraryJson.Options)!;
            var result = _categoryService.ReloadAndApply(copies, token);
            if (result.Success)
            {
                token.ThrowIfCancellationRequested();
                _store.UpsertMany(copies);
            }
            return (Result: result, Records: copies);
        }, token);
        var result = applied.Result;
        if (!result.Success) throw new InvalidDataException(result.Error);
        var categories = applied.Records.ToDictionary(record => record.Id, record => record.CategoryTags);
        foreach (var record in records) record.CategoryTags = categories[record.Id];
        foreach (var row in Presets) row.Refresh();
        RefreshFilterChoices();
        PresetsView.Refresh();
        _categoryRulesStatus = $"已检查 {result.Processed} 个音色，更新 {result.Changed} 个分类。";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CategoryRulesStatus)));
        Log(_categoryRulesStatus);
        NotifyAll();
    }

    private async Task RefreshInitBaselinesAsync()
    {
        int generation = ++_baselineRefreshGeneration;
        foreach (var plugin in Plugins)
            plugin.InitBaselinePath = _settings.Plugins.FirstOrDefault(saved => saved.Synth == plugin.Synth)?.InitBaselinePath ?? "";
        var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings, LibraryJson.Options), LibraryJson.Options)!;
        var rows = new List<InitializationRow>();
        foreach (var plugin in settings.Plugins)
        {
            var state = await InitBaselineService.InspectAsync(settings, plugin);
            rows.Add(new(plugin.Synth, Labels.Synth(plugin.Synth), state.Status, state.Detail, state.WavPath,
                InitBaselineService.Supports(plugin) && File.Exists(plugin.ExecutablePath),
                state.CanCompare && File.Exists(state.WavPath)));
        }
        if (generation != _baselineRefreshGeneration) return;
        InitBaselines.Clear();
        foreach (var row in rows) InitBaselines.Add(row);
    }

    private async void GenerateInitBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InitializationRow row }) return;
        await RunTask(async token =>
        {
            var plugin = _settings.Plugins.First(p => p.Synth == row.Kind);
            _taskTitle = "正在生成默认启动基准";
            _statusMessage = Labels.Synth(plugin.Synth) + "：采集三份未加载预制的默认启动声音并校准。";
            NotifyTaskProgress();
            await Task.Run(() => RefreshBinaryHashes(_settings, token), token);
            var state = await Task.Run(() => InitBaselineService.GenerateAsync(_settings, plugin, Path.Combine(AppContext.BaseDirectory, "SoundPreviewer.Worker.exe"), token), token);
            await Task.Run(() => _store.SaveSettings(_settings));
            Log(state.Detail);
            _statusMessage = state.Detail;
            await RefreshInitBaselinesAsync();
            if (!state.CanCompare) throw new InvalidOperationException(state.Detail);
            _taskTitle = "默认启动基准已生成";
        });
    }

    private async void PlayInitBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InitializationRow row }) return;
        await RunTask(async token =>
        {
            StopPlayback();
            var plugin = _settings.Plugins.First(p => p.Synth == row.Kind);
            var state = await InitBaselineService.InspectAsync(_settings, plugin, token);
            if (!state.CanCompare) throw new InvalidOperationException(state.Detail);
            using var player = new WavePlayer();
            var audio = await Task.Run(() => WaveFile.Read(state.WavPath, token), token);
            player.Volume = _settings.PlaybackVolume;
            player.Load(audio);
            player.Play(0);
            var watch = Stopwatch.StartNew();
            while (player.IsPlaying)
            {
                token.ThrowIfCancellationRequested();
                if (watch.Elapsed.TotalSeconds > audio.DurationSeconds + 10) throw new TimeoutException("基准试听未在预期时间内结束。");
                await Task.Delay(50, token);
            }
        });
    }

    private void PopulateSettings()
    {
        Sources.Clear(); foreach (var source in _settings.Sources) Sources.Add(source);
        Plugins.Clear(); foreach (var plugin in _settings.Plugins)
            Plugins.Add(JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(plugin, LibraryJson.Options), LibraryJson.Options)!);
        PluginDirectories.Clear();
        var directories = _settings.PluginDirectories.Count > 0 ? _settings.PluginDirectories
            : Plugins.Select(p => p.PluginPath.Length > 0 ? p.PluginPath : p.Backend == RenderBackend.VitalCli ? p.ExecutablePath : "")
                .Where(File.Exists).Select(p => Path.GetDirectoryName(p)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var directory in directories) PluginDirectories.Add(directory);
        RefreshDiscoveredPlugins();
        var t = _settings.Template;
        QuickPreviewMode.IsChecked = t.QuickPreview;
        LowNoteBox.Text = t.LowNote.ToString(CultureInfo.InvariantCulture);
        MiddleNoteBox.Text = t.MiddleNote.ToString(CultureInfo.InvariantCulture);
        HighNoteBox.Text = t.HighNote.ToString(CultureInfo.InvariantCulture);
        VelocityBox.Text = t.Velocity.ToString(CultureInfo.InvariantCulture);
        HoldBox.Text = t.HoldSeconds.ToString(CultureInfo.InvariantCulture);
        TailBox.Text = t.TailSeconds.ToString(CultureInfo.InvariantCulture);
        BpmBox.Text = t.Bpm.ToString(CultureInfo.InvariantCulture);
        SampleRateBox.SelectedIndex = t.SampleRate == 48000 ? 1 : 0;
        PreviewDirectoryBox.Text = _settings.PreviewDirectory;
        BudgetBox.Text = _settings.StorageBudgetGb.ToString(CultureInfo.InvariantCulture);
        AttemptsBox.Text = _settings.MaxAttempts.ToString(CultureInfo.InvariantCulture);
        VolumeSlider.Value = _settings.PlaybackVolume;
    }

    private void AddSourceFromLibrary_Click(object sender, RoutedEventArgs e) { Pages.SelectedItem = SettingsTab; SettingsPages.SelectedItem = DirectorySettingsTab; AddSource_Click(sender, e); }
    private void AddSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择预制目录", Multiselect = false };
        if (dialog.ShowDialog(this) != true || SourceSynthChoice.SelectedItem is not Choice<SynthKind?> synth) return;
        var automatic = synth.Value is null;
        if (Sources.Any(s => s.AutoDetect == automatic && (automatic || s.Synth == synth.Value) && string.Equals(s.Path, dialog.FolderName, StringComparison.OrdinalIgnoreCase))) return;
        Sources.Add(new SourceFolder { Path = dialog.FolderName, Synth = synth.Value ?? SynthKind.Vital, AutoDetect = automatic });
        _settings.Sources = Sources.ToList();
        _store.SaveSettings(_settings);
        Log($"已添加预制目录（{(automatic ? "自动识别" : Labels.Synth(synth.Value.GetValueOrDefault()))}）：{dialog.FolderName}");
    }
    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourceGrid.SelectedItem is not SourceFolder source) return;
        Sources.Remove(source);
        _settings.Sources = Sources.ToList();
        _store.SaveSettings(_settings);
        Log("已移除扫描目录，保留已索引音色：" + source.Path);
    }
    private void AutoDetectSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourceGrid.SelectedItem is not SourceFolder source) return;
        source.AutoDetect = true;
        _settings.Sources = Sources.ToList();
        _store.SaveSettings(_settings);
        SourceGrid.Items.Refresh();
        Log("目录已切换为自动识别，下次扫描更新音源标签：" + source.Path);
    }
    private void BrowsePlugin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PluginConfiguration plugin }) return;
        var dialog = new OpenFileDialog { Title = "选择渲染程序", Filter = "可执行程序 (*.exe)|*.exe|全部文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        plugin.ExecutablePath = dialog.FileName;
        if (plugin.Backend == RenderBackend.Unconfigured) plugin.Backend = plugin.Synth == SynthKind.Vital ? RenderBackend.VitalCli : RenderBackend.ExternalWorker;
        if (plugin.Backend == RenderBackend.VitalCli)
            plugin.Version = FileVersionInfo.GetVersionInfo(dialog.FileName).ProductVersion ?? plugin.Version;
        PluginGrid.Items.Refresh();
    }
    private void AddPluginDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 VSTi 安装目录", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var directory in dialog.FolderNames)
            if (!PluginDirectories.Contains(directory, StringComparer.OrdinalIgnoreCase)) PluginDirectories.Add(directory);
        _settings.PluginDirectories = PluginDirectories.ToList();
        _store.SaveSettings(_settings);
        Log("VSTi 安装目录已保存。");
    }
    private void RemovePluginDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (PluginDirectoryGrid.SelectedItem is not string directory) return;
        PluginDirectories.Remove(directory);
        _settings.PluginDirectories = PluginDirectories.ToList();
        _store.SaveSettings(_settings);
        Log("已移除 VSTi 扫描目录，保留已关联的音源：" + directory);
    }
    private async void ScanPlugins_Click(object sender, RoutedEventArgs e) => await RunTask(async token =>
    {
        if (PluginDirectories.Count == 0) throw new InvalidOperationException("请先添加 VSTi 安装目录。");
        _statusMessage = "正在扫描已安装音源…";
        NotifyAll();
        var result = await new PluginScanService().ScanAsync(PluginDirectories.ToList(), token);
        var previous = _settings.DiscoveredPlugins.Where(p => result.UnavailableRoots.Any(root =>
            p.FilePath.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        var merged = result.Plugins.Concat(previous).DistinctBy(p => p.Id).ToList();
        var associated = PluginSelectionService.ApplyAutomatic(result.Plugins, Plugins, X86HostPath);
        _settings.PluginDirectories = PluginDirectories.ToList();
        _settings.DiscoveredPlugins = merged;
        _settings.Plugins = ClonePlugins();
        _store.SaveSettings(_settings);
        await InvalidateChangedPreviewsAsync();
        RefreshDiscoveredPlugins();
        PluginGrid.Items.Refresh();
        await RefreshInitBaselinesAsync();
        foreach (var warning in result.Warnings) Log(warning);
        _taskTitle = "音源扫描完成";
        _statusMessage = $"发现 {result.Plugins.Count} 个插件 / 独立程序，自动关联 {associated} 个音源。渲染兼容性仍需验证。";
        Log(_statusMessage);
    });
    private async void UseDiscoveredPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (DiscoveredPluginGrid.SelectedItem is not DiscoveredPluginRow row) return;
        await RunTask(async token =>
        {
            var configuration = Plugins.FirstOrDefault(p => p.Synth == row.Record.Synth)
                ?? throw new ArgumentException("此插件尚不属于支持的音源类型。");
            PluginSelectionService.Assign(configuration, row.Record, X86HostPath);
            _settings.Plugins = ClonePlugins();
            _store.SaveSettings(_settings);
            await InvalidateChangedPreviewsAsync();
            RefreshDiscoveredPlugins();
            PluginGrid.Items.Refresh();
            await RefreshInitBaselinesAsync();
            Log($"已关联 {Labels.Synth(row.Record.Synth)}：{row.Record.FilePath}（{row.ArchitectureLabel} / {row.FormatLabel}）。");
        });
    }
    private void RefreshDiscoveredPlugins()
    {
        var selectedId = (DiscoveredPluginGrid?.SelectedItem as DiscoveredPluginRow)?.Record.Id;
        DiscoveredPlugins.Clear();
        foreach (var plugin in _settings.DiscoveredPlugins.OrderBy(p => p.Synth == SynthKind.Unknown).ThenBy(p => p.Synth)
            .ThenBy(p => p.FilePath, StringComparer.OrdinalIgnoreCase)) DiscoveredPlugins.Add(new DiscoveredPluginRow(plugin, Plugins));
        if (DiscoveredPluginGrid is not null) DiscoveredPluginGrid.SelectedItem = DiscoveredPlugins.FirstOrDefault(p => p.Record.Id == selectedId);
        RefreshCompatibility();
    }
    private List<PluginConfiguration> ClonePlugins() => JsonSerializer.Deserialize<List<PluginConfiguration>>(
        JsonSerializer.Serialize(Plugins, LibraryJson.Options), LibraryJson.Options)!;
    private void BrowsePreview_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择预览保存目录" };
        if (dialog.ShowDialog(this) == true) PreviewDirectoryBox.Text = dialog.FolderName;
    }
    private void VitalDefaults_Click(object sender, RoutedEventArgs e)
    {
        QuickPreviewMode.IsChecked = false;
        var hold = Math.Clamp(Math.Round(ParseDouble(HoldBox.Text, "按键时长")), 1, 30);
        HoldBox.Text = hold.ToString(CultureInfo.InvariantCulture);
        TailBox.Text = (hold * 0.3).ToString("0.0", CultureInfo.InvariantCulture);
        VelocityBox.Text = "89";
    }
    private async void SaveSettings_Click(object sender, RoutedEventArgs e) => await RunTask(async token =>
        {
            if (!PluginGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !PluginGrid.CommitEdit(DataGridEditingUnit.Row, true))
                throw new ArgumentException("请修正渲染器设置中的输入。");
            var template = new PreviewTemplate
            {
                LowNote = ParseInt(LowNoteBox.Text, "低音"), MiddleNote = ParseInt(MiddleNoteBox.Text, "中音"), HighNote = ParseInt(HighNoteBox.Text, "高音"),
                Velocity = ParseInt(VelocityBox.Text, "力度"), HoldSeconds = ParseDouble(HoldBox.Text, "按键时长"), TailSeconds = ParseDouble(TailBox.Text, "尾音"),
                Bpm = ParseInt(BpmBox.Text, "BPM"), SampleRate = SampleRateBox.SelectedIndex == 1 ? 48000 : 44100,
                QuickPreview = QuickPreviewMode.IsChecked == true
            };
            template.Validate();
            var budget = ParseDouble(BudgetBox.Text, "存储预算");
            var attempts = ParseInt(AttemptsBox.Text, "最大尝试次数");
            if (budget <= 0 || !double.IsFinite(budget) || attempts is < 1 or > 5) throw new ArgumentException("存储预算须大于 0，最大尝试次数须为 1 到 5。");
            foreach (var p in Plugins.Where(p => p.Backend != RenderBackend.Unconfigured))
            {
                if (!File.Exists(p.ExecutablePath)) throw new FileNotFoundException($"{Labels.Synth(p.Synth)} 的渲染程序不存在。", p.ExecutablePath);
                if (p.TimeoutSeconds is < 5 or > 3600) throw new ArgumentException("超时须为 5 到 3600 秒。");
                if (string.IsNullOrWhiteSpace(p.Version)) throw new ArgumentException("请填写渲染器对应的音源实际版本。");
                if (p.Backend == RenderBackend.VitalCli && p.Synth != SynthKind.Vital) throw new ArgumentException("Vital 原生方式只能用于 Vital。");
            }
            var candidate = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings, LibraryJson.Options), LibraryJson.Options)!;
            candidate.Template = template;
            candidate.Plugins = JsonSerializer.Deserialize<List<PluginConfiguration>>(JsonSerializer.Serialize(Plugins, LibraryJson.Options), LibraryJson.Options)!;
            candidate.PluginDirectories = PluginDirectories.ToList();
            await Task.Run(() => RefreshBinaryHashes(candidate, token), token);
            candidate.PreviewDirectory = Path.GetFullPath(PreviewDirectoryBox.Text.Trim());
            candidate.StorageBudgetGb = budget;
            candidate.MaxAttempts = attempts;
            _store.SaveSettings(candidate);
            _settings = candidate;
            await InvalidateChangedPreviewsAsync();
            RefreshCompatibility();
            await RefreshInitBaselinesAsync();
            Log("设置已保存。");
        });
    private async Task InvalidateChangedPreviewsAsync()
    {
        var records = Presets.Select(row => row.Record).ToList();
        var updates = await Task.Run(() =>
        {
            var changed = new List<PresetRecord>();
            foreach (var record in records)
            {
                if (record.Synth == SynthKind.Unknown || record.Status == PresetStatus.Unidentified) continue;
                var plugin = _settings.Plugins.FirstOrDefault(p => p.Synth == record.Synth);
                if (plugin is null) continue;
                var previousKey = record.AttemptedCacheKey.Length > 0 ? record.AttemptedCacheKey : record.CacheKey;
                var key = Fingerprints.CacheKey(record, plugin, _settings.Template);
                if (record.Status is PresetStatus.Missing or PresetStatus.Unsupported || previousKey.Length == 0 || previousKey == key) continue;
                var copy = JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(record, LibraryJson.Options), LibraryJson.Options)!;
                if (File.Exists(copy.PreviewPath)) copy.Message = "配置已变更，已保留现有预览。";
                else
                {
                    copy.Status = PresetStatus.Pending;
                    copy.Attempts = 0;
                    copy.AttemptedCacheKey = key;
                    copy.Message = "配置已变更，等待生成。";
                }
                changed.Add(copy);
            }
            if (changed.Count > 0) _store.UpsertMany(changed);
            return changed.ToDictionary(record => record.Id);
        });
        foreach (var row in Presets)
        {
            if (!updates.TryGetValue(row.Record.Id, out var copy)) continue;
            row.Record.Status = copy.Status;
            row.Record.Attempts = copy.Attempts;
            row.Record.AttemptedCacheKey = copy.AttemptedCacheKey;
            row.Record.Message = copy.Message;
            row.Refresh();
        }
        NotifyAll();
    }
    private static int ParseInt(string value, string label) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : throw new ArgumentException(label + "须为整数。");
    private static void RefreshBinaryHashes(AppSettings settings, CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        foreach (var plugin in settings.Plugins.Where(p => p.Backend != RenderBackend.Unconfigured))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"正在校验 {Labels.Synth(plugin.Synth)} 渲染程序…");
            plugin.ExecutableHash = File.Exists(plugin.ExecutablePath) ? Fingerprints.HashFile(plugin.ExecutablePath, cancellationToken) : "";
            progress?.Report($"正在校验 {Labels.Synth(plugin.Synth)} 插件文件…");
            plugin.PluginHash = Fingerprints.PluginBinaryHash(plugin, settings.DiscoveredPlugins, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
    private static double ParseDouble(string value, string label) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : throw new ArgumentException(label + "须为数字。");

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "导出音色 MD5 索引与预览映射", FileName = "SoundPreviewer-index.json", Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var json = JsonSerializer.Serialize(new { SchemaVersion = 2, ExportedUtc = DateTime.UtcNow, Presets = Presets.Select(p => p.Record).ToList() }, LibraryJson.Options);
            await File.WriteAllTextAsync(dialog.FileName, json);
            Log("音色 MD5 索引已导出：" + dialog.FileName);
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(App.DataDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void Log(string message)
    {
        Logs.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (Logs.Count > 250) Logs.RemoveAt(0);
        if (!App.SmokeTest)
            try { File.AppendAllText(Path.Combine(App.DataDirectory, "application.log"), $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}"); }
            catch (IOException) { }
    }
    private void ShowError(Exception error)
    {
        _statusMessage = error.Message;
        Log(error.Message);
        NotifyAll();
        if (!App.SmokeTest) MessageBox.Show(this, error.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void NotifyAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isBusy) { e.Cancel = true; _closeWhenDone = true; _taskCancellation?.Cancel(); return; }
        _playbackTimer.Stop();
        _taskTimer.Stop();
        _player.Dispose();
        _store.SaveSettings(_settings);
    }
}

public sealed record InitializationRow(SynthKind Kind, string Synth, string Status, string Detail, string WavPath, bool CanGenerate, bool CanPlay);
