using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private async Task<bool> CheckCompressedPreviewAsync()
    {
        // Only temporary test libraries are eligible; never rewrite a supplied real library for verification.
        if (_settings.Plugins.Any(plugin => plugin.Backend != RenderBackend.Unconfigured) ||
            Presets.Any(row => row.Record.SourceId != "test-only-ui-fixture")) return false;
        var savedSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings, LibraryJson.Options), LibraryJson.Options)!;
        var savedRecords = _store.LoadPresets();
        string savedRecordsJson = JsonSerializer.Serialize(savedRecords.OrderBy(record => record.Id), LibraryJson.Options);
        object? savedPage = Pages.SelectedItem, savedSettingsPage = SettingsPages.SelectedItem;
        double savedVolume = _player.Volume;
        string? savedSelectedId = Selected?.Record.Id;
        string id = "TEST-ONLY-compressed-ui-" + Guid.NewGuid().ToString("N");
        string key = Fingerprints.HashText(id), wav = Path.Combine(App.DataDirectory, key + ".wav"), m4a = Path.ChangeExtension(wav, ".m4a");
        string source = Path.Combine(App.DataDirectory, id + ".vital");
        var originalHashes = savedRecords.Select(record => record.PreviewPath).Where(File.Exists).Distinct()
            .ToDictionary(path => path, path => Fingerprints.HashFile(path));
        try
        {
            const int rate = 48000, perNote = rate / 2;
            var template = new PreviewTemplate { SampleRate = rate, QuickPreview = false, HoldSeconds = .4, TailSeconds = .1 };
            float[] samples = new float[perNote * 3 * 2];
            for (int frame = 0; frame < perNote * 3; frame++)
            {
                int note = frame / perNote;
                double time = (frame % perNote) / (double)rate;
                double frequency = 440 * Math.Pow(2, (template.Pitches[note] - 69) / 12.0);
                samples[frame * 2] = (float)(.3 * Math.Sin(2 * Math.PI * frequency * time));
                samples[frame * 2 + 1] = (float)(.15 * Math.Sin(2 * Math.PI * frequency * 1.5 * time));
            }
            WaveFile.WritePcm16(wav, new WaveData { SampleRate = rate, Channels = 2, Samples = samples });
            File.WriteAllText(source, "TEST ONLY preview compression fixture; never loaded by a synth");
            var record = new PresetRecord { Id = id, Name = "TEST ONLY AAC stereo preview", SourceId = "test-only-ui-fixture", SourcePath = source,
                Synth = SynthKind.Vital, CacheKey = key, PreviewPath = wav, Status = PresetStatus.Ready, Favorite = true,
                Tags = ["TEST ONLY retained tag"], ManualTags = ["TEST ONLY retained tag"], CategoryTags = ["TEST ONLY retained category"],
                TemplateJson = JsonSerializer.Serialize(template, LibraryJson.Options), Metrics = WaveFile.Validate(wav), AdapterVersion = "TEST ONLY UI compression",
                Notes = template.Pitches.Select((pitch, index) => new NotePreview { MidiNote = pitch, StartSeconds = index * .5, DurationSeconds = .5,
                    ActualHoldSeconds = .4, ActualTailSeconds = .1, ActualVelocity = template.Velocity }).ToList() };
            _settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(savedSettings, LibraryJson.Options), LibraryJson.Options)!;
            _settings.PreviewDirectory = App.DataDirectory;
            _settings.Template.SampleRate = rate;
            _settings.Sources = []; // The temporary source is a distinct file, outside the cache's protected preset roots.
            // Temporarily expose only this one private record to compression; every original mapping is restored below.
            DeleteCompressionFixtureRows(savedRecords.Select(value => value.Id));
            _store.SaveState([record], _settings);
            PopulateSettings();
            SetPresets(_store.LoadPresets());
            ClearFilterSelections();
            Pages.SelectedItem = SettingsTab;
            SettingsPages.SelectedItem = StorageSettingsTab;
            await SettleLayoutAsync();
            if (!CanCompressPreviews || !CompressPreviewsButton.IsEffectivelyEnabled)
                throw new InvalidOperationException("隔离的已有 WAV 没有启用主动压缩入口。");
            await CompressExistingPreviewsAsync();
            var compressed = _store.LoadPresets().Single(value => value.Id == id);
            if (!IsIdle || compressed.PreviewPath != m4a || !File.Exists(m4a) || File.Exists(wav) ||
                compressed.Status != record.Status || compressed.CacheKey != key || !compressed.Favorite ||
                !compressed.Tags.SequenceEqual(record.Tags) || !compressed.CategoryTags.SequenceEqual(record.CategoryTags) ||
                JsonSerializer.Serialize(compressed.Notes, LibraryJson.Options) != JsonSerializer.Serialize(record.Notes, LibraryJson.Options))
                throw new InvalidOperationException("主动压缩没有安全提交 AAC，或改变了旧记录的音区、状态或手工信息。");
            var decoded = PreviewAudioFile.Read(m4a);
            if (decoded.SampleRate != rate || decoded.Channels != 2 || decoded.Frames != perNote * 3)
                throw new InvalidOperationException("AAC 压缩改变了48 kHz立体声或三音区总长度。");
            foreach (var (path, hash) in originalHashes)
                if (!File.Exists(path) || Fingerprints.HashFile(path) != hash)
                    throw new InvalidOperationException("压缩隔离测试更改了原有 WAV 预览。");
            Pages.SelectedItem = LibraryTab;
            ClearFilterSelections();
            PresetGrid.SelectedItem = Presets.Single(row => row.Record.Id == id);
            await LoadSelectedPreview();
            if (!_mediaOpened || !CanPlay) throw new InvalidOperationException("AAC 预览未能通过正常界面加载。");
            _player.Volume = 0;
            foreach (string region in new[] { "Low", "Middle", "High" })
            {
                var note = FindPreviewNote(compressed, region)!;
                PlayNote_Click(new Button { Tag = region }, new RoutedEventArgs());
                if (Math.Abs(_openPosition - note.StartSeconds) > 1d / rate || _segmentEnd != note.StartSeconds + note.DurationSeconds || !_isPlaying)
                    throw new InvalidOperationException("AAC 的低中高音区试听没有保持原始边界。");
                await Task.Delay(60);
                StopPlayback();
            }
            if (!await CheckPreviewShortcutAsync()) throw new InvalidOperationException("AAC 预览未通过原生播放与空格重播检查。");
            await SettleLayoutAsync();
            CaptureSmokeImage("ui-compressed-preview.png");
            return true;
        }
        finally
        {
            UnloadForStorageMaintenance();
            DeleteCompressionFixtureRows([id]);
            _store.SaveState(savedRecords, savedSettings);
            if (JsonSerializer.Serialize(_store.LoadPresets().OrderBy(record => record.Id), LibraryJson.Options) != savedRecordsJson)
                throw new InvalidOperationException("压缩测试未完整恢复持久化音色记录，或遗留了临时音色行。");
            _settings = savedSettings;
            PopulateSettings();
            ClearFilterSelections();
            SetPresets(savedRecords);
            PresetGrid.SelectedItem = Presets.FirstOrDefault(row => row.Record.Id == savedSelectedId);
            Pages.SelectedItem = savedPage;
            SettingsPages.SelectedItem = savedSettingsPage;
            _player.Volume = savedVolume;
            File.Delete(wav);
            File.Delete(m4a);
            File.Delete(source);
        }
    }

    private void DeleteCompressionFixtureRows(IEnumerable<string> ids)
    {
        // LibraryStore.SaveState upserts records. Delete only these explicitly snapshotted test IDs, in a transaction.
        var type = typeof(SoundPreviewer.Core.Storage.LibraryStore).Assembly.GetType("SoundPreviewer.Core.Storage.NativeSqlite", throwOnError: true)!;
        using var database = (IDisposable)Activator.CreateInstance(type, _store.DatabasePath)!;
        var execute = type.GetMethod("Execute")!;
        void Execute(string sql, params object?[] parameters) => execute.Invoke(database, [sql, parameters]);
        Execute("BEGIN IMMEDIATE;");
        try
        {
            foreach (string id in ids) Execute("DELETE FROM presets WHERE id=?;", id);
            Execute("COMMIT;");
        }
        catch { Execute("ROLLBACK;"); throw; }
    }
}
