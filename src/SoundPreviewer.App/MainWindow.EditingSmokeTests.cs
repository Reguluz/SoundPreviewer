using System.Text.Json;
using Avalonia.Interactivity;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private async Task<(bool Template, bool Metadata)> CheckEditingPersistenceAsync()
    {
        // Only edit empty/synthetic smoke libraries; never rewrite a real library supplied for inspection.
        if (_settings.Plugins.Any(plugin => plugin.Backend != RenderBackend.Unconfigured) ||
            Presets.Any(row => row.Record.SourceId != "test-only-ui-fixture")) return (false, false);
        var savedSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings, LibraryJson.Options), LibraryJson.Options)!;
        var savedRecords = _store.LoadPresets();
        bool metadataChecked = false;
        try
        {
            QuickPreviewMode.IsChecked = true;
            QuickPreviewSecondsBox.Text = "1.25";
            LowNoteBox.Text = "Db2";
            MiddleNoteBox.Text = "C4";
            HighNoteBox.Text = "A6";
            ParallelRendering.SelectedItem = ParallelRenderChoices.Single(choice => choice.Value == 4);
            Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).AllowParallelRendering = true;
            await RunTask(SaveSettingsCoreAsync);
            var persistedSettings = _store.LoadSettings();
            if (persistedSettings.MaxParallelRenders != 4 || !persistedSettings.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).AllowParallelRendering)
                throw new InvalidOperationException("并行设置没有通过界面保存。");
            foreach (int limit in new[] { 0, 1, 2, 8, 16 })
            {
                ParallelRendering.SelectedItem = ParallelRenderChoices.Single(choice => choice.Value == limit);
                await RunTask(SaveSettingsCoreAsync);
                if (_store.LoadSettings().MaxParallelRenders != limit)
                    throw new InvalidOperationException("自动或手动并行档位没有保存。");
            }
            Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).Version = "TEST ONLY changed plugin version";
            await RunTask(SaveSettingsCoreAsync);
            if (!_store.LoadSettings().Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).AllowParallelRendering ||
                !Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).AllowParallelRendering)
                throw new InvalidOperationException("手工更改音源版本后丢失并行设置。");
            var template = persistedSettings.Template;
            if (template.QuickPreviewSeconds != 1.25 || !template.QuickPreview ||
                template.LowNote != 37 || template.MiddleNote != 60 || template.HighNote != 93)
                throw new InvalidOperationException("试听时长或音名没有通过设置界面保存到数据库。");
            foreach (var record in _store.LoadPresets())
            {
                var original = savedRecords.Single(item => item.Id == record.Id);
                if (record.CacheKey != original.CacheKey || record.TemplateJson != original.TemplateJson || record.PreviewPath != original.PreviewPath)
                    throw new InvalidOperationException("更改试听模板破坏了已保存的旧预览映射。");
            }
            var row = Presets.FirstOrDefault(item => item.Record.Package == "TEST ONLY / no synthesizer loaded" && item.Record.DuplicateOfId.Length == 0);
            if (row is not null)
            {
                ClearFilterSelections();
                Pages.SelectedIndex = 0;
                PresetGrid.SelectedItem = row;
                EditPresetMetadata_Click(PresetMetadataExpander, new RoutedEventArgs());
                PresetTagsEditor.Text = "手工标签， TEST ONLY;手工标签";
                PresetCategoriesEditor.Text = "自定义分类\n第二分类";
                SavePresetMetadata_Click(PresetMetadataExpander, new RoutedEventArgs());
                var stored = _store.LoadPresets().Single(item => item.Id == row.Record.Id);
                if (stored.ManualTags is null || !stored.Tags.SequenceEqual(new[] { "手工标签", "TEST ONLY" }) ||
                    !stored.CategoryTags.SequenceEqual(new[] { "自定义分类", "第二分类" }))
                    throw new InvalidOperationException("手工标签和分类没有通过编辑界面保存。");
                await SettleLayoutAsync();
                CaptureSmokeImage("ui-metadata-edit.png");
                PresetTagsEditor.Text = "";
                PresetCategoriesEditor.Text = "";
                SavePresetMetadata_Click(PresetMetadataExpander, new RoutedEventArgs());
                stored = _store.LoadPresets().Single(item => item.Id == row.Record.Id);
                if (stored.ManualTags is not { Count: 0 } || stored.ManualCategories is not { Count: 0 } ||
                    stored.Tags.Count != 0 || stored.CategoryTags.Count != 0)
                    throw new InvalidOperationException("清空手工标签和分类没有保存为空的手工覆盖。");
                ResetPresetMetadata_Click(PresetMetadataExpander, new RoutedEventArgs());
                stored = _store.LoadPresets().Single(item => item.Id == row.Record.Id);
                if (stored.ManualTags is not null || stored.ManualCategories is not null ||
                    !stored.Tags.SequenceEqual(savedRecords.Single(item => item.Id == stored.Id).Tags))
                    throw new InvalidOperationException("恢复自动标签与分类没有清除手工覆盖。");
                await CaptureSyntheticLibraryViewportsAsync(row);
                metadataChecked = true;
            }
            _storageRelocated = true;
            NotifyAll();
            if (IsIdle || CanEditSelection || CanPlay || !RelocatedLibraryVisibility || !TaskBandVisible)
                throw new InvalidOperationException("迁移提交后的旧窗口未锁定或未提供重新打开入口。");
            bool staleTaskRan = false;
            await RunTask(_ => { staleTaskRan = true; return Task.CompletedTask; });
            if (staleTaskRan) throw new InvalidOperationException("迁移后的旧窗口仍能运行修改旧库的任务。");
            if (_selected is not null)
            {
                bool favorite = _selected.Record.Favorite;
                Favorite_Click(PresetMetadataExpander, new RoutedEventArgs());
                if (_selected.Record.Favorite != favorite) throw new InvalidOperationException("迁移后的旧窗口仍可修改收藏。");
            }
            return (true, metadataChecked);
        }
        finally
        {
            _storageRelocated = false;
            _store.SaveState(savedRecords, savedSettings);
            _settings = savedSettings;
            PopulateSettings();
            ClearFilterSelections();
            SetPresets(savedRecords);
            PresetMetadataExpander.IsExpanded = false;
        }
    }
}
