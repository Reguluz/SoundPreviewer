using System.Diagnostics;
using System.Text.Json;
using Avalonia.Threading;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private double _assignmentYieldMilliseconds;

    private async Task CheckSynthAssignmentAsync()
    {
        var saved = Presets.ToArray();
        string? selectedId = _selected?.Record.Id;
        string search = SearchBox.Text ?? "";
        var filters = FilterGroups.ToDictionary(group => group.Key, group => group.SelectedKeys.ToArray());
        string token = "TEST-ONLY-assignment-" + Guid.NewGuid().ToString("N");
        string source = Path.Combine(App.DataDirectory, token + ".fxp");
        await File.WriteAllTextAsync(source, "TEST ONLY: manual classification fixture, no plugin rendering");
        var rows = Enumerable.Range(0, 14000).Select(index => new PresetRow(new PresetRecord
        {
            Id = token + index, Name = "TEST ONLY assignment " + index, Synth = SynthKind.Unknown,
            SourcePath = index == 0 ? source : "", ContentMd5 = index == 0 ? new string('b', 32) : "",
            Favorite = index == 0, ManualTags = index == 0 ? ["TEST ONLY keep tag"] : null
        }, _categories, _tags, () => MetadataLanguage)).ToArray();
        try
        {
            SearchBox.Text = "";
            ClearFilterSelections();
            Presets.ReplaceAll(rows);
            RefreshFilterChoices();
            PresetGrid.SelectedItem = rows[0];
            var watch = Stopwatch.StartNew();
            Task assignment = AssignSelectedSynthAsync(SynthKind.Nexus2);
            _assignmentYieldMilliseconds = watch.Elapsed.TotalMilliseconds;
            bool busy = IsBusy;
            bool dispatcherResponded = false;
            await Dispatcher.UIThread.InvokeAsync(() => dispatcherResponded = IsBusy, DispatcherPriority.Send);
            await assignment;
            if (!busy || !dispatcherResponded || _assignmentYieldMilliseconds > 1000)
                throw new InvalidOperationException("确认音源未在后台执行，或阻塞了界面调度。");
            var assigned = Presets.Single(row => row.Record.Id == rows[0].Record.Id);
            if (assigned.Record.Synth != SynthKind.Nexus2 || !assigned.Record.Favorite || assigned.Record.ManualTags?.Single() != "TEST ONLY keep tag" ||
                _selected?.Record.Id != assigned.Record.Id || !ReferenceEquals(Presets[1], rows[1]) || !IsIdle)
                throw new InvalidOperationException("确认音源丢失了选择、个人信息，或重建了无关音色行。");
            var stored = _store.LoadPresets();
            if (stored.Count(record => record.Id.StartsWith(token, StringComparison.Ordinal)) != 1 ||
                stored.Single(record => record.Id == assigned.Record.Id).Synth != SynthKind.Nexus2)
                throw new InvalidOperationException("单条音源确认写入了无关音色。");
        }
        finally
        {
            Presets.ReplaceAll(saved);
            RefreshFilterChoices();
            foreach (var group in FilterGroups) group.SetSelection(filters[group.Key]);
            SearchBox.Text = search;
            RefreshPresetView();
            PresetGrid.SelectedItem = Presets.FirstOrDefault(row => row.Record.Id == selectedId);
        }
    }
}
