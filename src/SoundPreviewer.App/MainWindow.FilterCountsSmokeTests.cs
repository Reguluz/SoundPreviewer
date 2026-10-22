using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private double _filterCountsBenchmarkMilliseconds;
    private long _filterCountsBenchmarkAllocatedBytes;

    private Task CheckFilterCountsAsync()
    {
        var savedSelections = FilterGroups.ToDictionary(group => group.Key, group => group.SelectedKeys.ToArray());
        string originalSearch = SearchBox.Text ?? "";
        bool? originalDuplicates = ShowDuplicates.IsChecked;
        string token = "TEST-ONLY-filter-counts-" + Guid.NewGuid().ToString("N");
        string sourceA = token + "-source-a", sourceB = token + "-source-b";
        string packageA = token + "-package-a", packageB = token + "-package-b";
        string bass = token + "-bass", lead = token + "-lead", pad = token + "-pad";
        string warm = token + "-warm", bright = token + "-bright";
        var rows = new List<PresetRow>
        {
            new(new PresetRecord { Id = token + "-a", Name = token + " canonical", SourceId = sourceA,
                SourcePath = Path.Combine(App.DataDirectory, token, "a.vital"), Synth = SynthKind.Vital,
                Package = packageA, Favorite = true, CategoryTags = [bass, bass.ToUpperInvariant(), lead],
                Tags = [warm, warm.ToUpperInvariant(), bass] }),
            new(new PresetRecord { Id = token + "-b", Name = token + " failed", SourceId = sourceA,
                SourcePath = Path.Combine(App.DataDirectory, token, "b.fxp"), Synth = SynthKind.Serum1,
                Package = packageB, Status = PresetStatus.Failed, CategoryTags = [bass], Tags = [bright] }),
            new(new PresetRecord { Id = token + "-c", Name = token + " rendering", SourceId = sourceB,
                SourcePath = Path.Combine(App.DataDirectory, token, "c.SerumPreset"), Synth = SynthKind.Serum2,
                Package = packageA, Favorite = true, Status = PresetStatus.Rendering }),
            new(new PresetRecord { Id = token + "-d", Name = token + " copy", SourceId = sourceB,
                SourcePath = Path.Combine(App.DataDirectory, token, "d.vital"), Synth = SynthKind.Vital,
                Package = packageB, DuplicateOfId = token + "-a", CategoryTags = [lead], Tags = [warm] }),
            new(new PresetRecord { Id = token + "-e", Name = token + " unavailable", SourceId = sourceB,
                SourcePath = Path.Combine(App.DataDirectory, token, "e.spf"), Synth = SynthKind.Spire,
                Status = PresetStatus.Unsupported, CategoryTags = [pad], Tags = [warm, bright] })
        };
        try
        {
            ClearFilterSelections();
            ShowDuplicates.IsChecked = false;
            SearchBox.Text = token;
            foreach (var row in rows) Presets.Add(row);
            RefreshFilterCollections();
            AssertAllCounts();
            AssertCount("synth", "", 4);
            AssertCount("synth", SynthKind.Vital.ToString(), 1);
            AssertCount("synth", SynthKind.Sylenth1.ToString(), 0);
            AssertCount("source", sourceA, 2);
            AssertCount("source", sourceB, 2);
            AssertCount("category", bass, 2);
            AssertCount("category", MissingFilter, 1);
            AssertCount("tag", bass, 2);
            AssertCount("tag", warm, 2);
            AssertCount("tag", MissingFilter, 1);
            if (Group("tag").Options.Where(option => option.Key.Length > 0).Sum(option => option.Count) <= 4)
                throw new InvalidOperationException("标签计数错误地分摊了可同时属于多项的同一个音色。");

            SetFilterSelection("category", bass);
            AssertAllCounts();
            AssertCount("category", "", 4);
            AssertCount("category", pad, 1);
            AssertCount("source", "", 2);
            SetFilterSelection("tag", warm);
            AssertAllCounts();
            AssertCount("category", "", 2);
            AssertCount("category", pad, 1);
            AssertCount("tag", bright, 1);
            AssertCount("tag", "", 2);
            AssertCount("favorite", "1", 1);
            AssertCount("favorite", "0", 0);
            SetFilterSelection("synth", SynthKind.Serum1.ToString());
            AssertAllCounts();
            AssertCount("synth", SynthKind.Vital.ToString(), 1);
            AssertCount("synth", SynthKind.Serum1.ToString(), 0);
            AssertCount("tag", bright, 1);

            ClearFilterSelections();
            SetFilterSelection("synth", SynthKind.Vital.ToString(), SynthKind.Serum1.ToString());
            AssertAllCounts();
            AssertCount("synth", "", 4);
            AssertCount("synth", SynthKind.Serum2.ToString(), 1);
            AssertCount("status", "pending", 1);
            AssertCount("status", "failed", 1);
            ShowDuplicates.IsChecked = true;
            RefreshPresetView();
            AssertAllCounts();
            AssertCount("synth", "", 5);
            AssertCount("synth", SynthKind.Vital.ToString(), 2);
            AssertCount("status", "pending", 2);

            ClearFilterSelections();
            ShowDuplicates.IsChecked = false;
            SearchBox.Text = rows[0].Name;
            RefreshPresetView();
            AssertAllCounts();
            AssertCount("source", sourceA, 1);
            AssertCount("source", sourceB, 0);
            SearchBox.Text = warm;
            RefreshPresetView();
            AssertAllCounts();
            AssertCount("synth", "", 2);
            SearchBox.Text = token + " no-match";
            RefreshPresetView();
            AssertAllCounts();
            if (FilterGroups.SelectMany(group => group.Options).Any(option => option.Count != 0 || option.CountLabel != "0"))
                throw new InvalidOperationException("空搜索结果仍显示了旧筛选计数，或隐藏了零计数。");

            SearchBox.Text = token;
            rows[0].Record.Status = PresetStatus.Rendering;
            rows[0].Record.Favorite = false;
            rows[0].Refresh();
            RefreshPresetView();
            AssertAllCounts();
            AssertCount("status", "pending", 0);
            AssertCount("status", "rendering", 2);
            AssertCount("favorite", "1", 1);
            AssertCount("favorite", "0", 3);
            string changedTag = token + " changed-tag";
            rows[0].Record.Tags = [changedTag];
            rows[0].Record.CategoryTags = [pad];
            rows[0].Refresh();
            RefreshFilterCollections();
            AssertAllCounts();
            AssertCount("tag", changedTag, 1);
            AssertCount("tag", warm, 1);
            AssertCount("category", pad, 2);

            ClearFilterSelections();
            // Count realistic libraries without adding 14,000 items one by one
            // to the live observable grid. Timing is diagnostic, never a limit.
            var largeLibrary = Enumerable.Range(0, 14000).Select(index => new PresetRow(new PresetRecord
            {
                Id = token + index, Name = token + " large library", SourceId = index % 2 == 0 ? sourceA : sourceB,
                Synth = index % 2 == 0 ? SynthKind.Vital : SynthKind.Serum1,
                Package = index % 2 == 0 ? packageA : packageB, Favorite = index % 3 == 0,
                Tags = [warm, bright, warm.ToUpperInvariant(), token + "-texture-" + index % 128, token + "-macro-" + index % 256],
                CategoryTags = [index % 2 == 0 ? bass : lead, pad]
            })).ToArray();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            RefreshFilterCounts(largeLibrary);
            watch.Stop();
            _filterCountsBenchmarkMilliseconds = watch.Elapsed.TotalMilliseconds;
            _filterCountsBenchmarkAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            AssertCount("synth", "", 14000);
            AssertCount("synth", SynthKind.Vital.ToString(), 7000);
            AssertCount("tag", warm, 14000);
            AssertCount("category", bass, 7000);
            AssertCount("category", pad, 14000);
        }
        finally
        {
            PresetGrid.SelectedItem = null;
            ClearFilterSelections();
            SearchBox.Clear();
            ShowDuplicates.IsChecked = false;
            PresetsView.Filter = null;
            PresetsView.MoveCurrentToPosition(-1);
            try
            {
                foreach (var row in rows) Presets.Remove(row);
                RefreshFilterCollections();
            }
            finally { PresetsView.Filter = FilterPreset; }
            foreach (var (key, values) in savedSelections) SetFilterSelection(key, values);
            SearchBox.Text = originalSearch;
            ShowDuplicates.IsChecked = originalDuplicates;
            RefreshPresetView();
        }
        return Task.CompletedTask;

        FilterGroup Group(string key) => FilterGroups.Single(group => group.Key == key);
        void AssertCount(string group, string key, int expected)
        {
            var option = Group(group).Options.Single(option => Group(group).KeyComparer.Equals(option.Key, key));
            if (option.Count != expected || option.CountLabel != expected.ToString())
                throw new InvalidOperationException($"{group}/{key} 筛选计数应为 {expected}，实际为 {option.CountLabel}。");
        }
        void AssertAllCounts()
        {
            string search = SearchBox.Text?.Trim() ?? "";
            foreach (var group in FilterGroups)
            {
                var candidates = rows.Where(row => (ShowDuplicates.IsChecked == true || row.Record.DuplicateOfId.Length == 0) &&
                    new[] { row.Name, row.Package, row.Record.SourcePath, row.Record.InternalPath, row.SynthLabel, row.Record.ContentMd5 }
                        .Concat(row.Record.Tags).Concat(row.Record.CategoryTags)
                        .Any(value => value.Contains(search, StringComparison.CurrentCultureIgnoreCase)) &&
                    FilterGroups.Where(other => other != group && other.SelectedKeys.Count > 0)
                        .All(other => Keys(row, other.Key).Any(other.SelectedKeys.Contains))).ToArray();
                foreach (var option in group.Options)
                {
                    int expected = option.Key.Length == 0 ? candidates.Length : candidates.Count(row =>
                        Keys(row, group.Key).Contains(option.Key, group.KeyComparer));
                    if (option.Count != expected || option.CountLabel != expected.ToString())
                        throw new InvalidOperationException($"{group.Key}/{option.DisplayLabel} 未同时遵循搜索、其他筛选及重复项开关；应为 {expected}，实际为 {option.CountLabel}。");
                }
            }
        }
        static IEnumerable<string> Keys(PresetRow row, string group) => group switch
        {
            "synth" => [row.Record.Synth.ToString()],
            "status" => [Labels.PreviewStatusKey(row.Record, row.HasPreview)],
            "source" => [row.Record.SourceId.Length == 0 ? MissingFilter : row.Record.SourceId],
            "package" => [row.Package.Length == 0 ? MissingFilter : row.Package],
            "category" => row.Record.CategoryTags.Count == 0 ? [MissingFilter] : row.Record.CategoryTags,
            "tag" => row.Record.Tags.Count + row.Record.CategoryTags.Count == 0 ? [MissingFilter] : row.Record.Tags.Concat(row.Record.CategoryTags),
            "favorite" => [row.Record.Favorite ? "1" : "0"],
            _ => []
        };
    }
}
