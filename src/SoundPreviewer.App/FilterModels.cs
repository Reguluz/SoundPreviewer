using System.Collections.ObjectModel;
using System.ComponentModel;

namespace SoundPreviewer.App;

public sealed class FilterOption(string key, string label, string? toolTip = null, string? displayLabel = null, bool userText = false) : INotifyPropertyChanged
{
    private bool isSelected;
    private int count;
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string DisplayLabel { get; } = displayLabel ?? label;
    public int Count => count;
    public string CountLabel => count.ToString();
    public string ToolTip { get; } = toolTip ?? label;
    public string LocalizedDisplayLabel => userText ? DisplayLabel : T(DisplayLabel);
    public string LocalizedToolTip => userText ? ToolTip : T(ToolTip);
    public bool IsSelected
    {
        get => isSelected;
        internal set
        {
            if (isSelected == value) return;
            isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void RefreshLanguage()
    {
        PropertyChanged?.Invoke(this, new(nameof(LocalizedDisplayLabel)));
        PropertyChanged?.Invoke(this, new(nameof(LocalizedToolTip)));
    }

    internal void SetCount(int value)
    {
        if (count == value) return;
        count = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountLabel)));
    }
}

public sealed class FilterGroup : INotifyPropertyChanged
{
    private readonly HashSet<string> selectedKeys;
    private bool isActive;

    public FilterGroup(string key, string label, IEnumerable<FilterOption> options, StringComparer? comparer = null)
    {
        Key = key;
        Label = label;
        selectedKeys = new(comparer ?? StringComparer.OrdinalIgnoreCase);
        ReplaceOptions(options);
    }

    public string Key { get; }
    public string Label { get; }
    public string DisplayLabel => selectedKeys.Count == 0 ? Label : $"{Label} · {selectedKeys.Count}";
    public bool IsActive
    {
        get => isActive;
        internal set
        {
            if (isActive == value) return;
            isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }
    public ObservableCollection<FilterOption> Options { get; } = [];
    public IReadOnlySet<string> SelectedKeys => selectedKeys;
    internal IEqualityComparer<string> KeyComparer => selectedKeys.Comparer;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void ReplaceOptions(IEnumerable<FilterOption> options)
    {
        var next = options.DistinctBy(option => option.Key, selectedKeys.Comparer).ToList();
        if (next.All(option => option.Key.Length != 0)) next.Insert(0, new("", "全部"));
        selectedKeys.IntersectWith(next.Where(option => option.Key.Length > 0).Select(option => option.Key));
        Options.Clear();
        foreach (var option in next) Options.Add(option);
        RefreshSelection();
    }

    internal void SetSelection(IEnumerable<string> keys)
    {
        var requested = keys.ToHashSet(selectedKeys.Comparer);
        selectedKeys.Clear();
        if (!requested.Contains(""))
            selectedKeys.UnionWith(Options.Where(option => option.Key.Length > 0 && requested.Contains(option.Key)).Select(option => option.Key));
        RefreshSelection();
    }

    internal void ToggleSelection(string key)
    {
        if (key.Length == 0) selectedKeys.Clear();
        else if (!selectedKeys.Remove(key)) selectedKeys.Add(key);
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        foreach (var option in Options)
            option.IsSelected = option.Key.Length == 0 ? selectedKeys.Count == 0 : selectedKeys.Contains(option.Key);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedKeys)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
    }
}
