using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private bool _isStorageMaintenance;
    private bool _storageRelocated;
    private string _relocatedDirectory = "";

    private void EditPresetMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditSelection) return;
        PresetMetadataExpander.IsExpanded = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosing) return;
            PresetCategoriesEditor.Focus();
            if (PresetMetadataExpander.TranslatePoint(default, PresetDetailsContent) is { } position)
                PresetDetailsScroll.Offset = new Vector(0, position.Y);
        }, DispatcherPriority.Background);
    }

    private void RefreshPresetMetadataEditors()
    {
        PresetCategoriesEditor.Text = "";
        PresetTagsEditor.Text = "";
        RefreshMetadataOptions();
    }

    private void SavePresetMetadata_Click(object sender, RoutedEventArgs e) => SavePresetMetadata(reset: false);
    private void ResetPresetMetadata_Click(object sender, RoutedEventArgs e) => SavePresetMetadata(reset: true);

    private void SavePresetMetadata(bool reset)
    {
        if (!CanEditSelection || _selected is null) return;
        try
        {
            var row = _selected;
            var copy = JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(row.Record, LibraryJson.Options), LibraryJson.Options)!;
            if (reset)
            {
                PresetMetadataService.ResetTags(copy);
                PresetMetadataService.ResetCategories(copy);
            }
            else
            {
                var categories = _allCategoryOptions.Where(option => option.IsChecked).Select(option => option.Id).ToList();
                var tags = _allTagOptions.Where(option => option.IsChecked).Select(option => option.Id).ToList();
                PresetMetadataService.SetCategories(copy, categories.Select(id => _categories.Label(id, "")));
                PresetMetadataService.SetTags(copy, tags.Select(id => _tags.Label(id, "")));
                copy.CategoryIds = categories;
                copy.TagIds = tags;
            }
            _categories.Synchronize(copy, true);
            _tags.Synchronize(copy, false);
            _store.Upsert(copy);
            row.Record.Tags = copy.Tags;
            row.Record.CategoryTags = copy.CategoryTags;
            row.Record.CategoryIds = copy.CategoryIds;
            row.Record.TagIds = copy.TagIds;
            row.Record.CategoryCatalogId = copy.CategoryCatalogId;
            row.Record.TagCatalogId = copy.TagCatalogId;
            PersistMetadataCatalogs();
            row.Record.ManualTags = copy.ManualTags;
            row.Record.ManualCategories = copy.ManualCategories;
            row.Record.AutomaticTags = copy.AutomaticTags;
            row.Record.AutomaticCategories = copy.AutomaticCategories;
            row.Refresh();
            RefreshFilterChoices();
            PresetsView.Refresh();
            RefreshPresetMetadataEditors();
            NotifyAll();
            Log(reset ? "已恢复自动标签与分类。" : "手工标签与分类已保存到当前音色库。");
        }
        catch (Exception error) { ShowError(error); }
    }

    private static IEnumerable<string> SplitMetadata(string? text) => Regex.Split(text ?? "", "[,，;；\\r\\n]+");

    private void OpenRelocatedLibrary_Click(object sender, RoutedEventArgs e) => OpenRelocatedLibrary();

    private void OpenRelocatedLibrary()
    {
        if (!_storageRelocated || _isClosing || _isBusy) return;
        try
        {
            ApplicationPaths.RememberDataDirectory(_relocatedDirectory);
            App.StartWithLibrary(_relocatedDirectory);
            Close();
        }
        catch (Exception error)
        {
            ShowError(new IOException($"新音色库已经保存到：{_relocatedDirectory}\n暂未能打开，请修复以下问题后点击“打开新音色库”重试。当前窗口已暂停编辑，避免修改旧库。\n{error.Message}", error));
        }
    }

    private async void ClearPreviews_Click(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || _isClosing) return;
        if (!await Dialogs.ShowAsync(this, "清空本库生成的预览音频和加载防错声音参照，释放磁盘空间。音色记录、收藏、反馈、标签和分类会保留，之后可重新生成预览。", "清空预览", question: true) || _isClosing) return;
        _isStorageMaintenance = true;
        try
        {
            await RunTask(async _ =>
            {
                _taskTitle = "正在清空预览";
                UnloadForStorageMaintenance();
                var result = await Task.Run(() => PreviewStorageService.ClearPreviews(_store, _settings));
                _settings = _store.LoadSettings();
                foreach (var plugin in Plugins) plugin.InitBaselinePath = "";
                ResetFilterControls();
                SetPresets(_store.LoadPresets());
                _taskTitle = "预览已清空";
                _statusMessage = $"清理 {result.DeletedFiles} 个文件，释放 {result.FreedBytes / 1024d / 1024:0.0} MB；音色记录、收藏和手工内容保留。" + FormatStorageWarnings(result.Warnings, App.DataDirectory);
                Log(_statusMessage);
            });
        }
        finally { _isStorageMaintenance = false; NotifyAll(); }
    }

    private void UnloadForStorageMaintenance()
    {
        ++_selectionGeneration;
        StopPlayback();
        _player.Unload();
        _loadedPreviewPath = "";
        _mediaOpened = false;
        Waveform.SetAudio(null);
    }

    private static string FormatStorageWarnings(IReadOnlyList<string> warnings, string? reportDirectory = null)
    {
        if (warnings.Count == 0) return "";
        string message = "\n" + string.Join("\n", warnings.Take(8));
        if (reportDirectory is not null)
        {
            try
            {
                string directory = Path.Combine(reportDirectory, "Maintenance");
                Directory.CreateDirectory(directory);
                string report = Path.Combine(directory, "preview-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + ".json");
                File.WriteAllText(report, JsonSerializer.Serialize(warnings, LibraryJson.Options));
                message += "\n完整处理报告：" + report;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { message += "\n处理报告未能保存：" + error.Message; }
        }
        if (warnings.Count > 8) message += $"\n另有 {warnings.Count - 8} 项保留信息。";
        return message;
    }
}
