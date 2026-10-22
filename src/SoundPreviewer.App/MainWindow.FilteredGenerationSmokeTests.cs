using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private async Task<bool> CheckFilteredGenerationAsync()
    {
        if (_settings.Plugins.Any(plugin => plugin.Backend != RenderBackend.Unconfigured) ||
            Presets.Any(row => row.Record.SourceId != "test-only-ui-fixture")) return false;
        var savedSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings, LibraryJson.Options), LibraryJson.Options)!;
        var savedRecords = _store.LoadPresets();
        string savedRecordsJson = JsonSerializer.Serialize(savedRecords.OrderBy(record => record.Id), LibraryJson.Options);
        var savedSelections = FilterGroups.ToDictionary(group => group.Key, group => group.SelectedKeys.ToArray());
        string savedActiveGroup = FilterGroups.Single(group => group.IsActive).Key;
        string savedSearch = SearchBox.Text ?? "";
        bool? savedDuplicates = ShowDuplicates.IsChecked, savedForce = ForceGeneration.IsChecked;
        object? savedPage = Pages.SelectedItem;
        string? savedSelectedId = Selected?.Record.Id;
        string prefix = "TEST-ONLY-filter-generation-" + Guid.NewGuid().ToString("N");
        string selectedSource = prefix + "-selected", package = prefix + "-package";
        var files = new List<string>();
        var fixtures = new List<PresetRecord>();
        try
        {
            var template = new PreviewTemplate { SampleRate = 44100, QuickPreview = false, HoldSeconds = .4, TailSeconds = .1 };
            var audio = new WaveData { SampleRate = template.SampleRate, Channels = 2,
                Samples = Enumerable.Range(0, template.SampleRate * 3).Select(index =>
                    (float)(.25 * Math.Sin(2 * Math.PI * 440 * (index / 2) / template.SampleRate))).ToArray() };
            PresetRecord Record(string suffix, PresetStatus status, bool outside = false)
            {
                var record = new PresetRecord { Id = prefix + "-" + suffix, Name = "TEST ONLY filtered " + suffix,
                    Synth = SynthKind.Vital, SourceId = outside ? prefix + "-outside" : selectedSource,
                    SourcePath = Path.Combine(App.DataDirectory, prefix, outside ? "outside" : "selected", suffix + ".vital"),
                    Package = outside ? prefix + "-other-package" : package, ContentHash = Fingerprints.HashText(suffix),
                    Status = status, Favorite = true, CategoryTags = ["TEST ONLY bass", "TEST ONLY lead"],
                    Tags = ["TEST ONLY warm", "TEST ONLY bright"], Message = "TEST ONLY preserved " + suffix, Attempts = 1 };
                fixtures.Add(record);
                return record;
            }
            async Task Cache(PresetRecord record, string extension, bool corrupt = false, bool missing = false)
            {
                record.CacheKey = Fingerprints.HashText(record.Id);
                record.PreviewPath = Path.Combine(App.DataDirectory, record.CacheKey + extension);
                files.Add(record.PreviewPath);
                record.TemplateJson = JsonSerializer.Serialize(template, LibraryJson.Options);
                record.AdapterVersion = "TEST ONLY filtered generation";
                record.Notes = template.Pitches.Select((pitch, index) => new NotePreview { MidiNote = pitch, StartSeconds = index * .5,
                    DurationSeconds = .5, ActualHoldSeconds = .4, ActualTailSeconds = .1, ActualVelocity = template.Velocity }).ToList();
                if (missing) return;
                if (corrupt) File.WriteAllText(record.PreviewPath, "TEST ONLY invalid audio, despite an existing .wav path");
                else if (extension == ".m4a") await PreviewAudioFile.EncodeAsync(audio, record.PreviewPath);
                else WaveFile.WritePcm16(record.PreviewPath, audio);
                if (!corrupt) record.Metrics = PreviewAudioFile.Validate(record.PreviewPath);
            }
            var pending = Record("pending", PresetStatus.Pending);
            var unrelated = Record("unrelated", PresetStatus.Pending, outside: true);
            var canonical = Record("canonical", PresetStatus.Failed, outside: true);
            await Cache(canonical, ".wav");
            var copy = Record("local-copy", PresetStatus.Pending);
            copy.DuplicateOfId = canonical.Id;
            copy.ContentHash = canonical.ContentHash;
            var ready = Record("ready-wav", PresetStatus.Ready);
            await Cache(ready, ".wav");
            var review = Record("review-aac", PresetStatus.NeedsReview);
            await Cache(review, ".m4a");
            var stale = Record("failed-with-valid-wav", PresetStatus.Failed);
            await Cache(stale, ".wav");
            var missing = Record("ready-missing", PresetStatus.Ready);
            await Cache(missing, ".wav", missing: true);
            var corrupt = Record("ready-corrupt", PresetStatus.Ready);
            await Cache(corrupt, ".wav", corrupt: true);
            string unrelatedJson = JsonSerializer.Serialize(unrelated, LibraryJson.Options);
            var hashes = files.Where(File.Exists).ToDictionary(path => path, path => Fingerprints.HashFile(path));

            UnloadForStorageMaintenance();
            DeleteCompressionFixtureRows(savedRecords.Select(record => record.Id));
            _settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(savedSettings, LibraryJson.Options), LibraryJson.Options)!;
            _settings.Sources = [];
            _settings.PreviewDirectory = App.DataDirectory;
            _store.SaveState(fixtures, _settings);
            PopulateSettings();
            SetPresets(_store.LoadPresets());
            ClearFilterSelections();
            Pages.SelectedItem = LibraryTab;
            SearchBox.Text = "TEST ONLY filtered";
            ShowDuplicates.IsChecked = false;
            SetFilterSelection("synth", SynthKind.Vital.ToString());
            SetFilterSelection("source", selectedSource);
            SetFilterSelection("package", package);
            SetFilterSelection("category", "TEST ONLY bass", "TEST ONLY lead");
            SetFilterSelection("tag", "TEST ONLY warm", "TEST ONLY bright");
            SetFilterSelection("favorite", "1");
            SetFilterSelection("status", "pending", "generated", "failed");
            SelectFilterGroup("package");
            string filterSnapshot = Filters();
            if (PresetsView.Count != 7 || !PresetsView.Cast<PresetRow>().Any(row => row.Record.Id == copy.Id) ||
                PresetsView.Cast<PresetRow>().Any(row => row.Record.Id == canonical.Id || row.Record.Id == unrelated.Id))
                throw new InvalidOperationException("生成测试没有保留当前来源的跨目录副本代表。");

            await ClickAndWait(RenderCurrentFilterButton, force: false);
            CheckResult(reused: 4, unconfigured: 3);
            var afterDefault = _store.LoadPresets().ToDictionary(record => record.Id);
            if (afterDefault[canonical.Id].Status != PresetStatus.NeedsReview || afterDefault[stale.Id].Status != PresetStatus.NeedsReview ||
                afterDefault[copy.Id].PreviewPath != canonical.PreviewPath || afterDefault[copy.Id].Status != copy.Status ||
                Presets.Single(row => row.Record.Id == copy.Id).StatusLabel != "已生成")
                throw new InvalidOperationException("筛选副本未复用主项，或有效旧预览因旧失败状态被当作未生成。");
            await SettleLayoutAsync();
            CaptureSmokeImage("ui-filtered-generation.png");
            await ClickAndWait(RenderFilteredButton, force: true);
            CheckResult(reused: 0, unconfigured: 7);
            if (!Logs.Any(entry => entry.EndsWith("仅生成当前筛选中未生成的音色，已有有效预览跳过。", StringComparison.Ordinal)) ||
                !Logs.Any(entry => entry.EndsWith("仅覆盖重渲染当前筛选结果，逐项保留旧预览直到新结果完成。", StringComparison.Ordinal)))
                throw new InvalidOperationException("生成任务没有说明当前筛选范围及覆盖语义。");

            // An offline configured host must keep its identity and give a concrete reason,
            // while valid previews remain reusable without launching that host.
            var vital = _settings.Plugins.Single(plugin => plugin.Synth == SynthKind.Vital);
            vital.Backend = RenderBackend.ExternalWorker;
            vital.ExecutablePath = Path.Combine(App.DataDirectory, prefix, "TEST ONLY missing Vital renderer");
            vital.PluginPath = Path.Combine(App.DataDirectory, prefix, "TEST ONLY offline Vital plugin");
            vital.Version = "TEST ONLY previous version";
            vital.ExecutableHash = "TEST ONLY retained host identity";
            vital.PluginHash = "TEST ONLY retained plugin identity";
            var unselected = _settings.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1);
            unselected.Backend = RenderBackend.ExternalWorker;
            unselected.ExecutablePath = Path.Combine(App.DataDirectory, prefix, "TEST ONLY missing unselected Serum renderer");
            string configuredJson = JsonSerializer.Serialize(_settings.Plugins, LibraryJson.Options);
            var execution = CreateExecutionSettings(_settings, out var readiness);
            var vitalReadiness = readiness.Single(state => state.Synth == SynthKind.Vital);
            if (vitalReadiness.CanAttemptRender || vitalReadiness.Status != "程序缺失" ||
                JsonSerializer.Serialize(execution.Plugins, LibraryJson.Options) != configuredJson)
                throw new InvalidOperationException("不可用配置在执行快照中丢失了生成方式、路径或指纹。");
            PopulateSettings();
            var previousLogs = Logs.ToHashSet(StringComparer.Ordinal);
            await ClickAndWait(RenderCurrentFilterButton, force: false);
            CheckResult(reused: 4, unconfigured: 0, unavailable: 3);
            var diagnosed = _store.LoadPresets().ToDictionary(record => record.Id);
            foreach (var original in new[] { pending, missing, corrupt })
            {
                var actual = diagnosed[original.Id];
                if (actual.Attempts != original.Attempts || actual.Status != original.Status || actual.CacheKey != original.CacheKey ||
                    actual.PreviewPath != original.PreviewPath || actual.LastRenderOutcome != "已配置，暂不可用" ||
                    !actual.LastRenderMessage.Contains(vitalReadiness.Status, StringComparison.Ordinal) ||
                    !actual.LastRenderMessage.Contains(vital.ExecutablePath, StringComparison.Ordinal))
                    throw new InvalidOperationException("不可用宿主的具体原因未持久化，或跳过任务改变了尝试次数、状态及旧缓存。");
            }
            if (!diagnosed[pending.Id].Message.Contains(vitalReadiness.Status, StringComparison.Ordinal) ||
                JsonSerializer.Serialize(_settings.Plugins, LibraryJson.Options) != configuredJson ||
                JsonSerializer.Serialize(_store.LoadSettings().Plugins, LibraryJson.Options) != configuredJson ||
                TaskProgressDetails.Text != StatusMessage || !TaskSettingsVisibility ||
                StatusMessage.IndexOf("Vital", StringComparison.Ordinal) is < 0 or > 20 ||
                StatusMessage.IndexOf(vitalReadiness.Status, StringComparison.Ordinal) is < 0 or > 45 ||
                !StatusMessage.Contains(vital.ExecutablePath, StringComparison.Ordinal) ||
                Logs.Where(entry => !previousLogs.Contains(entry)).Any(entry => entry.Contains("Serum", StringComparison.Ordinal)))
                throw new InvalidOperationException("已配置暂不可用的原因没有在当前结果前部显示、配置被改写，或无关音源产生了告警。");
            CaptureSmokeImage("ui-renderer-unavailable.png");
            foreach (var (path, hash) in hashes)
                if (!File.Exists(path) || Fingerprints.HashFile(path) != hash)
                    throw new InvalidOperationException("生成方式未选择或暂不可用时，任务改动了已有预览或损坏文件。");
            SearchBox.Text = prefix + "-no-match";
            await SettleLayoutAsync();
            if (!PresetsView.IsEmpty || RenderCurrentFilterButton.IsEnabled || RenderFilteredButton.IsEnabled)
                throw new InvalidOperationException("当前筛选为空时仍能通过生成按钮隐式处理全库。");
            return true;

            string Filters() => JsonSerializer.Serialize(new { SearchBox.Text, ShowDuplicates.IsChecked,
                Active = FilterGroups.Single(group => group.IsActive).Key,
                Selections = FilterGroups.ToDictionary(group => group.Key, group => group.SelectedKeys.Order().ToArray()) });
            async Task ClickAndWait(Button button, bool force)
            {
                ForceGeneration.IsChecked = force;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (IsIdle || ForceGeneration.IsChecked == true)
                    throw new InvalidOperationException("生成入口未启动当前筛选任务，或覆盖勾选没有在读取后复位。");
                var watch = Stopwatch.StartNew();
                while (!IsIdle && watch.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);
                if (!IsIdle) throw new InvalidOperationException("隔离筛选生成任务未及时结束。");
                await SettleLayoutAsync();
            }
            void CheckResult(int reused, int unconfigured, int unavailable = 0)
            {
                string counters = $"成功 {reused}（新生成 0 / 复用 {reused}） · 失败 0";
                if (unconfigured > 0) counters += $" · 未选择生成方式 {unconfigured}";
                if (unavailable > 0) counters += $" · 暂不可用 {unavailable}";
                if (!StatusMessage.Contains("当前筛选：", StringComparison.Ordinal) ||
                    !TaskProgressText.Contains(counters, StringComparison.Ordinal) ||
                    !TaskSelectionText.StartsWith("当前筛选 · 本次所选 7 条 · 去重 7 个 · 处理队列 7 个", StringComparison.Ordinal) ||
                    Filters() != filterSnapshot || PresetsView.Count != 7 ||
                    JsonSerializer.Serialize(_store.LoadPresets().Single(record => record.Id == unrelated.Id), LibraryJson.Options) != unrelatedJson)
                    throw new InvalidOperationException("生成范围、旧 WAV/AAC 复用、损坏预览重试或完成后的筛选保留不正确：" + StatusMessage + " / " + TaskProgressText + " / " + TaskSelectionText);
            }
        }
        finally
        {
            UnloadForStorageMaintenance();
            DeleteCompressionFixtureRows(fixtures.Select(record => record.Id));
            _store.SaveState(savedRecords, savedSettings);
            if (JsonSerializer.Serialize(_store.LoadPresets().OrderBy(record => record.Id), LibraryJson.Options) != savedRecordsJson)
                throw new InvalidOperationException("筛选生成测试未完整恢复原持久记录。");
            _settings = savedSettings;
            PopulateSettings();
            ClearFilterSelections();
            SetPresets(savedRecords);
            foreach (var (key, values) in savedSelections) SetFilterSelection(key, values);
            SelectFilterGroup(savedActiveGroup);
            SearchBox.Text = savedSearch;
            ShowDuplicates.IsChecked = savedDuplicates;
            ForceGeneration.IsChecked = savedForce;
            PresetGrid.SelectedItem = Presets.FirstOrDefault(row => row.Record.Id == savedSelectedId);
            Pages.SelectedItem = savedPage;
            foreach (string file in files) File.Delete(file);
        }
    }
}
