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
    private AppSettings _settings;
    private CancellationTokenSource? _taskCancellation;
    private bool _isBusy;
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

    public ObservableCollection<PresetRow> Presets { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<SourceFolder> Sources { get; } = [];
    public ObservableCollection<PluginConfiguration> Plugins { get; } = [];
    public ICollectionView PresetsView { get; }
    public List<Choice<SynthKind>> SynthChoices { get; } = Enum.GetValues<SynthKind>().Select(s => new Choice<SynthKind>(Labels.Synth(s), s)).ToList();
    public List<Choice<SynthKind?>> SynthFilters { get; } = [new("全部音源", null), .. Enum.GetValues<SynthKind>().Select(s => new Choice<SynthKind?>($"{Labels.Synth(s)} ({Labels.PresetExtensions(s)})", s))];
    public List<Choice<PresetStatus?>> StatusFilters { get; } = [new("全部状态", null), .. Enum.GetValues<PresetStatus>().Select(s => new Choice<PresetStatus?>(Labels.Status(s), s))];
    public List<Choice<RenderBackend>> BackendChoices { get; } = [new("未配置", RenderBackend.Unconfigured), new("Vital 原生", RenderBackend.VitalCli), new("外部工作进程", RenderBackend.ExternalWorker), new("DawDreamer", RenderBackend.DawDreamer)];
    public List<CompatibilityRow> Compatibility { get; } =
    [
        new("Vital", "待实机验证", "原生 CLI 适配器已实现；本机未安装。加载结果需试听确认。"),
        new("Serum 1", "待实机验证", "支持 FXP 扫描；可选 DawDreamer 宿主尚未验证插件渲染。"),
        new("Serum 2", "宿主待接入", "支持 SerumPreset 与 FXP 扫描；未开展插件验证。"),
        new("Sylenth1", "待实机验证", "支持 FXP 扫描与 FXB 识别；宿主须匹配插件位数，银行槽位未验证。")
    ];
    public PresetRow? Selected => _selected;
    public bool IsBusy => _isBusy;
    public bool IsIdle => !_isBusy;
    public bool HasSelection => _selected is not null;
    public bool CanEditSelection => HasSelection && IsIdle;
    public bool CanPlay => _selected is not null && File.Exists(_selected.Record.PreviewPath);
    public bool CanConfirm => IsIdle && CanPlay && _selected?.Record.Status == PresetStatus.NeedsReview;
    public bool CanRenderSelection => IsIdle && _selected is not null && _selected.Record.Status is not (PresetStatus.Unsupported or PresetStatus.Missing);
    public Visibility EmptyVisibility => Presets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string NowPlaying => _selected?.Name ?? "未选择预览";
    public string LibrarySummary => $"{Presets.Count} 条索引 · {Presets.Count(p => p.Record.Status == PresetStatus.Ready)} 已就绪 · {Presets.Count(p => p.Record.Status == PresetStatus.NeedsReview)} 待确认 · {Presets.Count(p => p.Record.Favorite)} 收藏";
    public string StatusMessage => _statusMessage;
    public double ProgressValue => _progressValue;
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        _store = new LibraryStore(Path.Combine(App.DataDirectory, "library.db"));
        _store.Initialize();
        _settings = App.ImportedSettings ?? _store.LoadSettings();
        if (string.IsNullOrWhiteSpace(_settings.PreviewDirectory)) _settings.PreviewDirectory = Path.Combine(App.DataDirectory, "Previews");
        if (App.ImportedSettings is not null) _store.SaveSettings(_settings);
        PresetsView = CollectionViewSource.GetDefaultView(Presets);
        PresetsView.Filter = FilterPreset;
        InitializeComponent();
        DataContext = this;
        PopulateSettings();
        _player.Volume = _settings.PlaybackVolume;
        _playbackTimer.Tick += (_, _) => UpdatePlayback();
        _playbackTimer.Start();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var restored = await Task.Run(() => _store.RecoverInterrupted());
            var records = await Task.Run(_store.LoadPresets);
            SetPresets(records);
            Log($"打开本地音色库，恢复 {restored} 个未完成任务。");
            if (App.ScanOnStart) await Scan();
            if (App.SmokeTest)
            {
                var expectedFormats = new Dictionary<SynthKind, string>
                {
                    [SynthKind.Vital] = "Vital (.vital / .vitalbank)",
                    [SynthKind.Serum1] = "Serum 1 (.fxp)",
                    [SynthKind.Serum2] = "Serum 2 (.SerumPreset / .fxp)",
                    [SynthKind.Sylenth1] = "Sylenth1 (.fxp / .fxb)"
                };
                if (SynthFilters.Count != expectedFormats.Count + 1 || SynthFilters.Count(c => c.Value is null) != 1)
                    throw new InvalidOperationException("音源筛选选项数量检查失败。");
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
                    var hint = Bounds(PresetFormatHint);
                    var table = Bounds(PresetGrid);
                    if (!PresetFormatHint.IsVisible || hint.Width <= 0 || hint.Height <= 0
                        || !new Rect(new Point(), visual.RenderSize).Contains(hint)
                        || hint.Bottom > table.Top + 1 || hint.Left < table.Left - 1 || hint.Right > table.Right + 1)
                        throw new InvalidOperationException("文件后缀说明不可见或超出表格区域。");
                    foreach (var control in new FrameworkElement[] { SearchBox, SynthFilter, StatusFilter, FavoritesOnly })
                        if (Bounds(control).Bottom > hint.Top + 1)
                            throw new InvalidOperationException("文件后缀说明与筛选工具栏重叠。");
                    foreach (var suffix in new[] { ".vital", ".vitalbank", ".fxp", ".SerumPreset", ".fxb" })
                        if (!PresetFormatHint.Text.Contains(suffix, StringComparison.Ordinal))
                            throw new InvalidOperationException($"文件后缀说明缺少 {suffix}。");
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
                if (Presets.Count > 0)
                {
                    PresetGrid.SelectedItem = Presets.FirstOrDefault(p => File.Exists(p.Record.PreviewPath)) ?? Presets[0];
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
                    playbackChecked = true;
                }
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var smokeSelection = PresetGrid.SelectedItem;
                foreach (var (width, height, name) in new[] { (1380d, 900d, "desktop"), (1040d, 700d, "compact") })
                {
                    Width = width; Height = height;
                    Pages.SelectedIndex = 0;
                    SynthFilter.SelectedItem = serumFilter;
                    UpdateLayout();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    CheckFormatLayout();
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
                    if (PresetsView.Cast<PresetRow>().Count() != Presets.Count)
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
                }
                await File.WriteAllTextAsync(Path.Combine(App.DataDirectory, "ui-smoke.json"), JsonSerializer.Serialize(new { Loaded = true, Presets = Presets.Count, PlaybackChecked = playbackChecked, FormatsChecked = true, FormatViewports = 2, Width = ActualWidth, Height = ActualHeight }));
                Application.Current.Shutdown();
            }
        }
        catch (Exception ex) { ShowError(ex); if (App.SmokeTest) Application.Current.Shutdown(1); }
    }

    private bool FilterPreset(object item)
    {
        if (item is not PresetRow row) return false;
        var search = SearchBox?.Text?.Trim() ?? "";
        if (search.Length > 0 && !new[] { row.Name, row.Package, row.Record.SourcePath, row.Record.InternalPath }.Any(s => s.Contains(search, StringComparison.CurrentCultureIgnoreCase))) return false;
        if (SynthFilter?.SelectedItem is Choice<SynthKind?> { Value: { } synth } && row.Record.Synth != synth) return false;
        if (StatusFilter?.SelectedItem is Choice<PresetStatus?> { Value: { } status } && row.Record.Status != status) return false;
        return FavoritesOnly?.IsChecked != true || row.Record.Favorite;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => PresetsView?.Refresh();

    private void SetPresets(IEnumerable<PresetRecord> records)
    {
        var selectedId = _selected?.Record.Id;
        Presets.Clear();
        foreach (var record in records) Presets.Add(new PresetRow(record));
        PresetsView.Refresh();
        PresetGrid.SelectedItem = Presets.FirstOrDefault(p => p.Record.Id == selectedId);
        NotifyAll();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await Scan();
    private async Task Scan() => await RunTask(async token =>
    {
        if (Sources.Count == 0) { Pages.SelectedItem = SettingsTab; throw new InvalidOperationException("请先添加预制目录。"); }
        _statusMessage = "正在扫描预制目录…";
        NotifyAll();
        var scanWatch = Stopwatch.StartNew();
        var result = await new ScanService().ScanAsync(Sources.ToList(), Presets.Select(p => p.Record).ToList(), token);
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
        Log($"扫描完成：新增 {result.Added}，内容变更 {result.Changed}，读取警告 {result.Warnings.Count}，耗时 {scanWatch.Elapsed.TotalSeconds:0.0} 秒。");
    });

    private async void Render_Click(object sender, RoutedEventArgs e) => await Generate(Presets.Select(p => p.Record).ToList());
    private async void RenderSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        _selected.Record.Attempts = 0;
        _selected.Record.CacheKey = "";
        _selected.Record.Status = PresetStatus.Pending;
        _store.Upsert(_selected.Record);
        await Generate([_selected.Record]);
    }
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        var failed = Presets.Where(p => p.Record.Status == PresetStatus.Failed).ToList();
        foreach (var row in failed) { row.Record.Attempts = 0; row.Record.Status = PresetStatus.Pending; row.Refresh(); }
        _store.UpsertMany(failed.Select(p => p.Record));
        await Generate(failed.Select(p => p.Record).ToList());
    }

    private async Task Generate(IReadOnlyList<PresetRecord> records) => await RunTask(async token =>
    {
        if (records.Count == 0) throw new InvalidOperationException("尚无可生成的音色，请先扫描目录。");
        if (!_settings.Plugins.Any(p => p.Backend != RenderBackend.Unconfigured)) { Pages.SelectedItem = SettingsTab; throw new InvalidOperationException("请先配置并保存音源渲染器。"); }
        if (_selected is not null && records.Any(p => p.Id == _selected.Record.Id))
        {
            StopPlayback();
            _player.Unload();
            _mediaOpened = false;
            _loadedPreviewPath = "";
        }
        var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings, LibraryJson.Options), LibraryJson.Options)!;
        await Task.Run(() => RefreshBinaryHashes(settings), token);
        _settings.Plugins = settings.Plugins;
        _store.SaveSettings(_settings);
        var rows = Presets.ToDictionary(p => p.Record.Id);
        var progress = new Progress<QueueProgress>(p =>
        {
            _progressValue = p.Total == 0 ? 100 : p.Completed * 100d / p.Total;
            _statusMessage = $"{p.Completed}/{p.Total} · {p.PresetName} · {p.Message}";
            if (p.Preset is not null && rows.TryGetValue(p.Preset.Id, out var row)) row.Refresh();
            PresetsView.Refresh();
            NotifyAll();
            Log(_statusMessage);
        });
        var workerPath = Path.Combine(AppContext.BaseDirectory, "SoundPreviewer.Worker.exe");
        await Task.Run(() => new RenderQueue(_store).RunAsync(settings, records, workerPath, progress, token), token);
        Log("本轮预览任务已结束。");
    });

    private async Task RunTask(Func<CancellationToken, Task> action)
    {
        if (_isBusy) return;
        _isBusy = true;
        _progressValue = 0;
        _taskCancellation = new CancellationTokenSource();
        NotifyAll();
        try { await action(_taskCancellation.Token); _statusMessage = "就绪"; }
        catch (OperationCanceledException) { _statusMessage = "已暂停，可继续。"; Log(_statusMessage); }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            _isBusy = false;
            _taskCancellation.Dispose();
            _taskCancellation = null;
            foreach (var row in Presets) row.Refresh();
            NotifyAll();
            if (_selected is not null && _selected.Record.PreviewPath != _loadedPreviewPath) await LoadSelectedPreview();
            if (_closeWhenDone) Close();
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => _taskCancellation?.Cancel();

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
        var index = int.Parse((string)button.Tag, CultureInfo.InvariantCulture);
        if (_selected.Record.Notes.Count <= index) { Log("此预览缺少分音区映射。"); return; }
        var note = _selected.Record.Notes[index];
        StartPlayback(note.StartSeconds, note.StartSeconds + note.DurationSeconds);
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
        var duration = TimeSpan.FromSeconds(SeekSlider.Maximum > 1 ? SeekSlider.Maximum : 0);
        TimeLabel.Text = $"{TimeSpan.FromSeconds(_player.PositionSeconds):mm\\:ss} / {duration:mm\\:ss}";
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

    private void PopulateSettings()
    {
        Sources.Clear(); foreach (var source in _settings.Sources) Sources.Add(source);
        Plugins.Clear(); foreach (var plugin in _settings.Plugins)
            Plugins.Add(JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(plugin, LibraryJson.Options), LibraryJson.Options)!);
        var t = _settings.Template;
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

    private void AddSourceFromLibrary_Click(object sender, RoutedEventArgs e) { Pages.SelectedItem = SettingsTab; AddSource_Click(sender, e); }
    private void AddSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择预制目录", Multiselect = false };
        if (dialog.ShowDialog(this) != true || SourceSynthChoice.SelectedItem is not Choice<SynthKind> synth) return;
        if (Sources.Any(s => s.Synth == synth.Value && string.Equals(s.Path, dialog.FolderName, StringComparison.OrdinalIgnoreCase))) return;
        Sources.Add(new SourceFolder { Path = dialog.FolderName, Synth = synth.Value });
        _settings.Sources = Sources.ToList();
        _store.SaveSettings(_settings);
        Log("已添加预制目录：" + dialog.FolderName);
    }
    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourceGrid.SelectedItem is not SourceFolder source) return;
        Sources.Remove(source);
        _settings.Sources = Sources.ToList();
        _store.SaveSettings(_settings);
        Log("已移除扫描目录，保留已索引音色：" + source.Path);
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
    private void BrowsePreview_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择预览保存目录" };
        if (dialog.ShowDialog(this) == true) PreviewDirectoryBox.Text = dialog.FolderName;
    }
    private void VitalDefaults_Click(object sender, RoutedEventArgs e)
    {
        var hold = Math.Clamp(Math.Round(ParseDouble(HoldBox.Text, "按键时长")), 1, 30);
        HoldBox.Text = hold.ToString(CultureInfo.InvariantCulture);
        TailBox.Text = (hold * 0.3).ToString("0.0", CultureInfo.InvariantCulture);
        VelocityBox.Text = "89";
    }
    private void BrowsePluginBinary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PluginConfiguration plugin }) return;
        var dialog = new OpenFileDialog { Title = "选择音源插件", Filter = "音源插件 (*.dll;*.vst3)|*.dll;*.vst3|全部文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        plugin.PluginPath = dialog.FileName;
        PluginGrid.Items.Refresh();
    }
    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!PluginGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !PluginGrid.CommitEdit(DataGridEditingUnit.Row, true))
                throw new ArgumentException("请修正渲染器设置中的输入。");
            var template = new PreviewTemplate
            {
                LowNote = ParseInt(LowNoteBox.Text, "低音"), MiddleNote = ParseInt(MiddleNoteBox.Text, "中音"), HighNote = ParseInt(HighNoteBox.Text, "高音"),
                Velocity = ParseInt(VelocityBox.Text, "力度"), HoldSeconds = ParseDouble(HoldBox.Text, "按键时长"), TailSeconds = ParseDouble(TailBox.Text, "尾音"),
                Bpm = ParseInt(BpmBox.Text, "BPM"), SampleRate = SampleRateBox.SelectedIndex == 1 ? 48000 : 44100
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
            RefreshBinaryHashes(candidate);
            candidate.PreviewDirectory = Path.GetFullPath(PreviewDirectoryBox.Text.Trim());
            candidate.StorageBudgetGb = budget;
            candidate.MaxAttempts = attempts;
            _store.SaveSettings(candidate);
            _settings = candidate;
            foreach (var row in Presets)
            {
                var plugin = _settings.Plugins.First(p => p.Synth == row.Record.Synth);
                var previousKey = row.Record.AttemptedCacheKey.Length > 0 ? row.Record.AttemptedCacheKey : row.Record.CacheKey;
                if (row.Record.Status is not (PresetStatus.Missing or PresetStatus.Unsupported) && previousKey.Length > 0 && previousKey != Fingerprints.CacheKey(row.Record, plugin, template))
                {
                    row.Record.Status = PresetStatus.Pending;
                    row.Record.Attempts = 0;
                    row.Record.AttemptedCacheKey = Fingerprints.CacheKey(row.Record, plugin, template);
                    row.Record.Message = "试听配置已变更，等待重新生成。";
                    row.Refresh();
                }
            }
            _store.UpsertMany(Presets.Select(p => p.Record));
            NotifyAll();
            Log("设置已保存。");
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private static int ParseInt(string value, string label) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : throw new ArgumentException(label + "须为整数。");
    private static void RefreshBinaryHashes(AppSettings settings)
    {
        foreach (var plugin in settings.Plugins)
        {
            plugin.ExecutableHash = File.Exists(plugin.ExecutablePath) ? Fingerprints.HashFile(plugin.ExecutablePath) : "";
            plugin.PluginHash = File.Exists(plugin.PluginPath) ? Fingerprints.HashFile(plugin.PluginPath) : "";
        }
    }
    private static double ParseDouble(string value, string label) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : throw new ArgumentException(label + "须为数字。");

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "导出音色与预览映射", FileName = "SoundPreviewer-mapping.json", Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var json = JsonSerializer.Serialize(new { SchemaVersion = 1, ExportedUtc = DateTime.UtcNow, Presets = Presets.Select(p => p.Record).ToList() }, LibraryJson.Options);
            await File.WriteAllTextAsync(dialog.FileName, json);
            Log("来源映射已导出：" + dialog.FileName);
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
        _player.Dispose();
        _store.SaveSettings(_settings);
    }
}
