using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

public sealed class MetadataOption(int id, string label, bool selected) : INotifyPropertyChanged
{
    private bool isChecked = selected;
    public int Id { get; } = id;
    public string Label { get; } = label;
    public bool IsChecked
    {
        get => isChecked;
        set { if (isChecked == value) return; isChecked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow
{
    private MetadataCatalog _categories = null!;
    private MetadataCatalog _tags = null!;
    private List<MetadataOption> _allCategoryOptions = [];
    private List<MetadataOption> _allTagOptions = [];
    public ObservableCollection<MetadataOption> CategoryOptions { get; } = [];
    public ObservableCollection<MetadataOption> TagOptions { get; } = [];
    private string MetadataLanguage => _settings.MetadataLanguage.Length == 0 ? _settings.Language : _settings.MetadataLanguage;

    private void InitializeMetadataCatalogs()
    {
        string root = Path.GetDirectoryName(ApplicationPaths.ConfigurationDirectory)!;
        _categories = new(Path.Combine(root, "categories.json"), categories: true, _settings.CategoryCatalogSnapshot);
        _tags = new(Path.Combine(root, "tags.json"), categories: false, _settings.TagCatalogSnapshot);
    }

    private void SynchronizeMetadata(IReadOnlyList<PresetRecord> records)
    {
        var migrated = records.Where(record => record.CategoryIds is null || record.TagIds is null || record.CategoryCatalogId != _categories.Id || record.TagCatalogId != _tags.Id).ToList();
        if (migrated.Count == 0) { PersistMetadataCatalogs(); return; }
        string backup = Path.Combine(App.DataDirectory, "Backups", "before-metadata-ids.db");
        if (!File.Exists(backup) && _store.LoadPresets().Count > 0) _store.CreateBackup(backup);
        foreach (var record in migrated)
        {
            _categories.Synchronize(record, categories: true, _settings.CategoryCatalogSnapshot);
            _tags.Synchronize(record, categories: false, _settings.TagCatalogSnapshot);
        }
        _store.UpsertMany(migrated);
        PersistMetadataCatalogs();
    }

    private void PersistMetadataCatalogs()
    {
        string categories = _categories.ExportJson(), tags = _tags.ExportJson();
        if (_settings.CategoryCatalogSnapshot == categories && _settings.TagCatalogSnapshot == tags) return;
        _settings.CategoryCatalogSnapshot = categories;
        _settings.TagCatalogSnapshot = tags;
        _store.SaveSettings(_settings);
    }

    private void RefreshMetadataOptions()
    {
        _allCategoryOptions = _categories.Items.OrderBy(item => item.Text(MetadataLanguage).Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => new MetadataOption(item.Id, item.Text(MetadataLanguage).Label, _selected?.Record.CategoryIds?.Contains(item.Id) == true)).ToList();
        _allTagOptions = _tags.Items.OrderBy(item => item.Text(MetadataLanguage).Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => new MetadataOption(item.Id, item.Text(MetadataLanguage).Label, _selected?.Record.TagIds?.Contains(item.Id) == true)).ToList();
        ApplyMetadataSearch();
    }

    private void MetadataSearch_Changed(object? sender, TextChangedEventArgs e) => ApplyMetadataSearch();
    private void ApplyMetadataSearch()
    {
        void Fill(ObservableCollection<MetadataOption> target, List<MetadataOption> source, string? search, MetadataCatalog catalog)
        {
            target.Clear();
            foreach (var option in source.Where(option => string.IsNullOrWhiteSpace(search) || catalog.Matches([option.Id], search.Trim()))) target.Add(option);
        }
        Fill(CategoryOptions, _allCategoryOptions, PresetCategoriesEditor?.Text, _categories);
        Fill(TagOptions, _allTagOptions, PresetTagsEditor?.Text, _tags);
    }

    private async void AddCategory_Click(object? sender, RoutedEventArgs e) => await EditMetadataAsync(true, null);
    private async void AddTag_Click(object? sender, RoutedEventArgs e) => await EditMetadataAsync(false, null);
    private async void EditCategory_Click(object? sender, RoutedEventArgs e) => await EditMetadataAsync(true, ((Control?)sender)?.DataContext as MetadataOption);
    private async void EditTag_Click(object? sender, RoutedEventArgs e) => await EditMetadataAsync(false, ((Control?)sender)?.DataContext as MetadataOption);

    private async Task EditMetadataAsync(bool category, MetadataOption? option)
    {
        if (!CanEditSelection) return;
        var catalog = category ? _categories : _tags;
        var current = option is null ? null : catalog.Find(option.Id)?.Text(MetadataLanguage);
        var name = new TextBox { Text = current?.Name ?? "", Watermark = T("名称（必填）") };
        var aliases = new TextBox { Text = string.Join(", ", current?.Aliases ?? []), Watermark = T("别名（可选，用逗号分隔）") };
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var save = new Button { Content = T("保存"), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var dialog = new Window { Title = T(category ? "编辑分类" : "编辑标签"), Width = 440, Height = 330, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Content = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 10, Children =
        {
            new TextBlock { Text = T("名称") }, name, new TextBlock { Text = T("别名") }, aliases, error, save
        }};
        save.Click += (_, _) =>
        {
            try
            {
                int id = catalog.Edit(option?.Id, name.Text ?? "", SplitMetadata(aliases.Text), MetadataLanguage == "zh-CN" ? "" : MetadataLanguage);
                PersistMetadataCatalogs();
                var checkedCategories = _allCategoryOptions.Where(value => value.IsChecked).Select(value => value.Id).ToHashSet();
                var checkedTags = _allTagOptions.Where(value => value.IsChecked).Select(value => value.Id).ToHashSet();
                if (option is null) (category ? checkedCategories : checkedTags).Add(id);
                RefreshMetadataOptions();
                foreach (var value in _allCategoryOptions) value.IsChecked = checkedCategories.Contains(value.Id);
                foreach (var value in _allTagOptions) value.IsChecked = checkedTags.Contains(value.Id);
                foreach (var row in Presets) row.Refresh();
                RefreshFilterChoices();
                NotifyAll();
                dialog.Close();
            }
            catch (Exception failure) { error.Text = T(failure.Message); }
        };
        await dialog.ShowDialog(this);
    }
}
