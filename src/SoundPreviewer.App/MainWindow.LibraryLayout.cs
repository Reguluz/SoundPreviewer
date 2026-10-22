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
        (ShowDuplicates?.IsChecked == true || row.Record.DuplicateOfId.Length == 0);

    private bool MatchesFilterPreset(PresetRow row)
    {
        string search = SearchBox?.Text?.Trim() ?? "";
        if (!MatchesFilterSearch(row, search)) return false;
        var values = new FilterRowValues(row);
        foreach (var group in FilterGroups)
            if (!MatchesFilterGroup(values, group)) return false;
        return true;
    }

    private static bool MatchesFilterSearch(PresetRow row, string search) => search.Length == 0 ||
        row.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        row.Package.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        row.Record.SourcePath.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        row.Record.InternalPath.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        row.SynthLabel.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        row.Record.ContentMd5.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        row.Record.Tags.Any(value => value.Contains(search, StringComparison.CurrentCultureIgnoreCase)) ||
        row.Record.CategoryTags.Any(value => value.Contains(search, StringComparison.CurrentCultureIgnoreCase));

    private readonly struct FilterRowValues(PresetRow row)
    {
        public PresetRow Row { get; } = row;
        public string Synth { get; } = row.Record.Synth.ToString();
        public string Status { get; } = Labels.PreviewStatusKey(row.Record, row.HasPreview);
        public string Source { get; } = row.Record.SourceId.Length == 0 ? MissingFilter : row.Record.SourceId;
        public string Package { get; } = row.Package.Length == 0 ? MissingFilter : row.Package;
        public string Favorite { get; } = row.Record.Favorite ? "1" : "0";
    }

    private static bool MatchesFilterGroup(FilterRowValues values, FilterGroup group)
    {
        var row = values.Row;
        var keys = group.SelectedKeys;
        return keys.Count == 0 || (group.Key switch
        {
            "synth" => keys.Contains(values.Synth),
            "status" => keys.Contains(values.Status),
            "source" => keys.Contains(values.Source),
            "package" => keys.Contains(values.Package),
            "category" => row.Record.CategoryTags.Count == 0 ? keys.Contains(MissingFilter) : row.Record.CategoryTags.Any(keys.Contains),
            "tag" => row.Record.Tags.Count + row.Record.CategoryTags.Count == 0 ? keys.Contains(MissingFilter) :
                row.Record.Tags.Any(keys.Contains) || row.Record.CategoryTags.Any(keys.Contains),
            "favorite" => keys.Contains(values.Favorite),
            _ => true
        });
    }

    private void RefreshPresetView()
    {
        RefreshFilterCounts();
        PresetsView.Refresh();
    }

    private void RefreshFilterCounts(IEnumerable<PresetRow>? records = null)
    {
        string search = SearchBox?.Text?.Trim() ?? "";
        bool showDuplicates = ShowDuplicates?.IsChecked == true;
        var groups = FilterGroups.ToArray();
        var counts = groups.Select(group => new Dictionary<string, int>(group.KeyComparer)).ToArray();
        var seen = groups.Select(group => new HashSet<string>(group.KeyComparer)).ToArray();
        var totals = new int[groups.Length];
        foreach (var row in records ?? Presets)
        {
            if ((!showDuplicates && row.Record.DuplicateOfId.Length > 0) || !MatchesFilterSearch(row, search)) continue;
            var values = new FilterRowValues(row);
            int unmatchedGroup = -1;
            bool multipleUnmatched = false;
            for (int index = 0; index < groups.Length; index++)
            {
                if (MatchesFilterGroup(values, groups[index])) continue;
                if (unmatchedGroup >= 0) { multipleUnmatched = true; break; }
                unmatchedGroup = index;
            }
            // A facet ignores its own selection. A row that misses two groups
            // cannot qualify for any facet; one miss qualifies only for that group.
            if (multipleUnmatched) continue;
            for (int index = 0; index < groups.Length; index++)
            {
                if (unmatchedGroup >= 0 && unmatchedGroup != index) continue;
                totals[index]++;
                AddFilterCounts(values, groups[index].Key, counts[index], seen[index]);
            }
        }
        // Cache the finished values on options; tab changes and UI bindings do
        // not rescan records. No record is compared with every available option.
        for (int index = 0; index < groups.Length; index++)
            foreach (var option in groups[index].Options)
                option.SetCount(option.Key.Length == 0 ? totals[index] : counts[index].GetValueOrDefault(option.Key));
    }

    private static void AddFilterCounts(FilterRowValues values, string groupKey, Dictionary<string, int> counts, HashSet<string> seen)
    {
        var row = values.Row;
        void Add(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
        switch (groupKey)
        {
            case "synth": Add(values.Synth); break;
            case "status": Add(values.Status); break;
            case "source": Add(values.Source); break;
            case "package": Add(values.Package); break;
            case "favorite": Add(values.Favorite); break;
            case "category":
                seen.Clear();
                foreach (string key in row.Record.CategoryTags)
                    if (seen.Add(key)) Add(key);
                if (seen.Count == 0) Add(MissingFilter);
                break;
            case "tag":
                seen.Clear();
                foreach (string key in row.Record.Tags)
                    if (seen.Add(key)) Add(key);
                foreach (string key in row.Record.CategoryTags)
                    if (seen.Add(key)) Add(key);
                if (seen.Count == 0) Add(MissingFilter);
                break;
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
                new(key, SourceCountLabel(path, counts), displayLabel: path);
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
