using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private HashSet<string> _filterRepresentativeIds = new(StringComparer.Ordinal);
    public ObservableCollection<FilterGroup> FilterGroups { get; } = CreateFilterGroups();
    public ObservableCollection<FilterOption> ActiveFilterOptions => FilterGroups.First(group => group.IsActive).Options;
    public string ActiveFilterLabel => FilterGroups.First(group => group.IsActive).Label;

    private static ObservableCollection<FilterGroup> CreateFilterGroups() =>
    [
        new("synth", "音源", [new("", "全部音源"), .. Enum.GetValues<SynthKind>().Select(synth =>
            new FilterOption(synth.ToString(), Labels.Synth(synth), $"{Labels.Synth(synth)} ({Labels.PresetExtensions(synth)})"))]) { IsActive = true },
        new("status", "状态", [new("", "全部状态"), new("pending", "待生成"), new("rendering", "生成中"),
            new("generated", "已生成"), new("failed", "生成失败"), new("unavailable", "无法预览")]),
        new("source", "来源", [new("", "全部来源")], StringComparer.Ordinal),
        new("package", "音色包", [new("", "全部音色包")]),
        new("category", "分类", [new("", "全部分类")]),
        new("tag", "标签", [new("", "全部标签")]),
        new("favorite", "收藏", [new("", "全部收藏状态"), new("1", "已收藏"), new("0", "未收藏")])
    ];

    public void SelectFilterGroup(string key)
    {
        if (!FilterGroups.Any(group => group.Key == key)) throw new ArgumentException("未知筛选组。", nameof(key));
        foreach (var group in FilterGroups) group.IsActive = group.Key == key;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveFilterOptions)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveFilterLabel)));
    }

    public void SetFilterSelection(string groupKey, params string[] keys)
    {
        var group = FilterGroups.FirstOrDefault(group => group.Key == groupKey)
            ?? throw new ArgumentException("未知筛选组。", nameof(groupKey));
        group.SetSelection(keys);
        RefreshPresetView();
        NotifyFilterResults();
    }

    public void ClearFilterSelections()
    {
        foreach (var group in FilterGroups) group.SetSelection([]);
        RefreshPresetView();
        NotifyFilterResults();
    }

    private void FilterGroup_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not FilterGroup group || !FilterGroups.Contains(group)) return;
        SelectFilterGroup(group.Key);
        // ToggleButton changes its target before Click; a same-valued binding
        // notification cannot restore an already-active group on a second click.
        if (sender is ToggleButton toggle) toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, group.IsActive);
    }

    private void FilterOption_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not FilterOption option) return;
        var group = FilterGroups.First(group => group.IsActive);
        if (!group.Options.Contains(option)) return;
        group.ToggleSelection(option.Key);
        if (sender is ToggleButton toggle) toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, option.IsSelected);
        RefreshPresetView();
        NotifyFilterResults();
    }

    private bool FilterPreset(object item) => item is PresetRow row && MatchesFilterPreset(row) &&
        (ShowDuplicates?.IsChecked == true || _filterRepresentativeIds.Contains(row.Record.Id));

    private bool MatchesFilterPreset(PresetRow row)
    {
        string search = SearchBox?.Text?.Trim() ?? "";
        if (search.Length > 0 && !new[] { row.Name, row.Package, row.Record.SourcePath, row.Record.InternalPath, row.SynthLabel,
                row.Record.ContentMd5 }.Concat(row.Record.Tags).Concat(row.Record.CategoryTags)
            .Any(value => value.Contains(search, StringComparison.CurrentCultureIgnoreCase))) return false;
        foreach (var group in FilterGroups)
        {
            var keys = group.SelectedKeys;
            if (keys.Count == 0) continue;
            bool matches = group.Key switch
            {
                "synth" => keys.Contains(row.Record.Synth.ToString()),
                "status" => keys.Contains(Labels.PreviewStatusKey(row.Record, row.HasPreview)),
                "source" => keys.Contains(row.Record.SourceId.Length == 0 ? MissingFilter : row.Record.SourceId),
                "package" => keys.Contains(row.Package.Length == 0 ? MissingFilter : row.Package),
                "category" => row.Record.CategoryTags.Count == 0 ? keys.Contains(MissingFilter) : row.Record.CategoryTags.Any(keys.Contains),
                "tag" => row.Record.Tags.Count + row.Record.CategoryTags.Count == 0 ? keys.Contains(MissingFilter) :
                    row.Record.Tags.Concat(row.Record.CategoryTags).Any(keys.Contains),
                "favorite" => keys.Contains(row.Record.Favorite ? "1" : "0"),
                _ => true
            };
            if (!matches) return false;
        }
        return true;
    }

    private void RefreshPresetView()
    {
        // Choose a representative within the matching source union, not globally.
        var representatives = new Dictionary<string, PresetRecord>(StringComparer.Ordinal);
        foreach (var row in Presets)
        {
            if (!MatchesFilterPreset(row)) continue;
            var record = row.Record;
            string canonicalId = _duplicateView.ResolveCanonical(record).Id;
            if (!representatives.TryGetValue(canonicalId, out var current) || Prefer(record, current, canonicalId))
                representatives[canonicalId] = record;
        }
        _filterRepresentativeIds = representatives.Values.Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
        PresetsView.Refresh();

        static bool Prefer(PresetRecord candidate, PresetRecord current, string canonicalId)
        {
            if ((candidate.Id == canonicalId) != (current.Id == canonicalId)) return candidate.Id == canonicalId;
            if ((candidate.Status == PresetStatus.Missing) != (current.Status == PresetStatus.Missing)) return candidate.Status != PresetStatus.Missing;
            int path = StringComparer.OrdinalIgnoreCase.Compare(candidate.SourcePath, current.SourcePath);
            return path < 0 || path == 0 && StringComparer.Ordinal.Compare(candidate.Id, current.Id) < 0;
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingFilterChoices) return;
        RefreshPresetView();
        NotifyFilterResults();
    }

    private void NotifyFilterResults()
    {
        foreach (string property in new[] { nameof(EmptyVisibility), nameof(LibrarySummary), nameof(VisiblePresetCount), nameof(CanRenderFiltered) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public void RefreshFilterCollections() => RefreshFilterChoices();

    private void RefreshFilterChoices()
    {
        _updatingFilterChoices = true;
        try
        {
            var records = Presets.Select(row => row.Record).ToList();
            _duplicateView = new PresetDuplicateView(records);
            FilterGroup Group(string key) => FilterGroups.First(group => group.Key == key);
            static FilterOption SourceOption(string key, string path, PresetSourceCounts counts) =>
                new(key, SourceCountLabel(path, counts), displayLabel: path, countLabel: $"{counts.Total} · 去重 {counts.Unique}");
            Group("source").ReplaceOptions(new[] { SourceOption("", "全部来源", _duplicateView.TotalCounts) }.Concat(
                records.GroupBy(record => record.SourceId, StringComparer.Ordinal).Select(group =>
                {
                    string path = group.Key.Length == 0 ? "未关联来源" : Sources.FirstOrDefault(source => source.Id == group.Key)?.Path ??
                        Path.GetDirectoryName(group.First().SourcePath) ?? group.Key;
                    return SourceOption(group.Key.Length == 0 ? MissingFilter : group.Key, path, _duplicateView.GetSourceCounts(group.Key));
                }).OrderBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)));
            Group("package").ReplaceOptions(new[] { new FilterOption("", "全部音色包") }.Concat(records.Select(record => record.Package)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                .Select(value => new FilterOption(value.Length == 0 ? MissingFilter : value, value.Length == 0 ? "无音色包" : value))));
            Group("category").ReplaceOptions(new[] { new FilterOption("", "全部分类") }
                .Concat(records.Any(record => record.CategoryTags.Count == 0) ? new[] { new FilterOption(MissingFilter, "未分类") } : [])
                .Concat(records.SelectMany(record => record.CategoryTags).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).Select(value => new FilterOption(value, value))));
            Group("tag").ReplaceOptions(new[] { new FilterOption("", "全部标签") }
                .Concat(records.Any(record => record.Tags.Count + record.CategoryTags.Count == 0) ? new[] { new FilterOption(MissingFilter, "无标签") } : [])
                .Concat(records.SelectMany(record => record.Tags.Concat(record.CategoryTags)).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).Select(value => new FilterOption(value, value))));
        }
        finally { _updatingFilterChoices = false; }
        RefreshPresetView();
        NotifyFilterResults();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveFilterOptions)));
    }
}
