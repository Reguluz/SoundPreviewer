using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SoundPreviewer.App;

public sealed class PresetCollection : ObservableCollection<PresetRow>
{
    public void ReplaceAll(IEnumerable<PresetRow> rows)
    {
        var snapshot = rows.ToArray();
        CheckReentrancy();
        Items.Clear();
        foreach (var row in snapshot) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
