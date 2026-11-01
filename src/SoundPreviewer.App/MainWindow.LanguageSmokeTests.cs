using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private async Task CheckSettingsLanguagesAsync()
    {
        var original = (Language: _settings.Language, Page: Pages.SelectedItem, SettingsPage: SettingsPages.SelectedItem,
            RenderTab: RenderStatusTabs.SelectedIndex, Preset: PresetGrid.SelectedItem, Source: SourceGrid.SelectedItem);
        var baselines = InitBaselines.ToArray();
        var compatibility = Compatibility.ToArray();
        string recordsBefore = JsonSerializer.Serialize(_store.LoadPresets(), LibraryJson.Options);
        var source = new SourceFolder { AutoDetect = true, Path = Path.Combine(App.DataDirectory, "TEST ONLY 音色库", "自动识别") };
        var unsupported = new InitializationRow(SynthKind.Vital, "Vital", "不支持此检查",
            "当前后端不支持此声音检查，但不阻止生成；生成结果须人工试听，未启用默认音色保护。", "", false, false);
        var unconfigured = new InitializationRow(SynthKind.Serum1, "Serum 1", "不支持此检查",
            "渲染器未配置，尚不能生成默认启动基准。", "", false, false);
        var verified = new InitializationRow(SynthKind.Sylenth1, "Sylenth1", "逐音色核验",
            "渲染器未配置，尚不能生成默认启动基准。", "", false, false);
        Sources.Insert(0, source);
        InitBaselines.Clear();
        InitBaselines.Add(unsupported);
        InitBaselines.Add(verified);
        Compatibility.Clear();
        var readiness = new CompatibilityRow("Serum 1", "未选择生成方式", unconfigured.Detail);
        Compatibility.Add(readiness);
        try
        {
            // Reuse the same rows through both directions of a live language switch.
            foreach (string language in new[] { "en-US", "zh-CN", "en-US" })
            {
                LanguageChoiceBox.SelectedItem = LanguageChoices.Single(choice => choice.Id == language);
                bool english = language == "en-US";
                Pages.SelectedItem = SettingsTab;
                SettingsPages.SelectedItem = StorageSettingsTab;
                await SettleLayoutAsync();
                if (PreviewDirectoryBox.Text != (english ? "Preview/ (beside this application)" : "Preview/（当前程序目录下）"))
                    throw new InvalidOperationException("The fixed Preview location did not follow the live language change.");
                CaptureSmokeImage("ui-language-storage-" + language + ".png");

                SettingsPages.SelectedItem = DirectorySettingsTab;
                SourceGrid.ScrollIntoView(source, SourceGrid.Columns[0]);
                await SettleLayoutAsync();
                RequireCellText(SourceGrid, source, english ? "Auto-detect" : "自动识别");
                RequireCellText(SourceGrid, source, source.Path);

                SettingsPages.SelectedItem = RenderSettingsTab;
                RenderStatusTabs.SelectedIndex = 0;
                await SettleLayoutAsync();
                if (RendererSetupLocationText.Text != (english ? "Environment saved at: " : "环境保存在：") + Path.Combine(App.DataDirectory, "renderers"))
                    throw new InvalidOperationException("The rendering environment location was untranslated or its path changed.");
                RequireCellText(InitializationGrid, unsupported, english ? "Check unsupported" : unsupported.Status);
                RequireCellText(InitializationGrid, unsupported, english
                    ? "This backend does not support the audio check but can still generate previews. Listen to results manually; startup sound protection is disabled."
                    : unsupported.Detail);
                RequireCellText(InitializationGrid, verified, english ? "Per-preset verification" : verified.Status);
                // Results arriving after the switch must use the current language too.
                if (!InitBaselines.Contains(unconfigured)) InitBaselines.Add(unconfigured);
                await SettleLayoutAsync();
                RequireCellText(InitializationGrid, unconfigured, english ? "Renderer not configured; cannot create startup baseline yet." : unconfigured.Detail);
                CaptureSmokeImage("ui-language-rendering-" + language + ".png");

                RenderStatusTabs.SelectedIndex = 1;
                await SettleLayoutAsync();
                RequireCellText(CompatibilityGrid, readiness, english ? "No rendering method selected" : readiness.Status);
                Pages.SelectedItem = LibraryTab;
                PresetGrid.SelectedItem = null;
                await SettleLayoutAsync();
                if (SelectedPresetNameText.Text != (english ? "No preset selected" : "未选择音色"))
                    throw new InvalidOperationException("The empty preset selection did not follow the interface language.");
                string version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)!.InformationalVersion.Split('+')[0];
                if (AppVersion != "v" + version || BuildVersionLabel.Text != AppVersion || !Title!.EndsWith("(" + AppVersion + ")", StringComparison.Ordinal))
                    throw new InvalidOperationException("The title, footer, and compiled product version disagree.");
            }
            if (JsonSerializer.Serialize(_store.LoadPresets(), LibraryJson.Options) != recordsBefore)
                throw new InvalidOperationException("Settings language checks changed stored preset records.");
        }
        finally
        {
            Sources.Remove(source);
            InitBaselines.Clear();
            foreach (var row in baselines) InitBaselines.Add(row);
            Compatibility.Clear();
            foreach (var row in compatibility) Compatibility.Add(row);
            LanguageChoiceBox.SelectedItem = LanguageChoices.Single(choice => choice.Id == original.Language);
            Pages.SelectedItem = original.Page;
            SettingsPages.SelectedItem = original.SettingsPage;
            RenderStatusTabs.SelectedIndex = original.RenderTab;
            PresetGrid.SelectedItem = original.Preset;
            SourceGrid.SelectedItem = original.Source;
            await SettleLayoutAsync();
        }

        static void RequireCellText(DataGrid grid, object row, string text)
        {
            if (!grid.GetVisualDescendants().OfType<TextBlock>().Any(block => ReferenceEquals(block.DataContext, row) && block.Text == text))
                throw new InvalidOperationException($"Expected localized cell in {grid.Name}: {text}");
        }
    }

    private async Task CheckLanguagesAsync()
    {
        await CheckSettingsLanguagesAsync();
        var original = (_settings.Language, _settings.MetadataLanguage, Pages.SelectedItem, SettingsPages.SelectedItem, Width, Height, PresetMetadataExpander.IsExpanded);
        string recordsBefore = JsonSerializer.Serialize(_store.LoadPresets(), LibraryJson.Options);
        var filtersBefore = FilterGroups.ToDictionary(group => group.Key, group => group.SelectedKeys.ToArray());
        try
        {
            LanguageChoiceBox.SelectedItem = LanguageChoices.Single(choice => choice.Id == "en-US");
            await SettleLayoutAsync();
            if (!Equals(LibraryTab.Header, "Library") || !Equals(StorageSettingsTab.Header, "Playback and storage") ||
                !Equals(UserGuideButton.GetValue(Avalonia.Automation.AutomationProperties.NameProperty), "User guide") || _store.LoadSettings().Language != "en-US")
                throw new InvalidOperationException("English selection did not update resources and persisted preferences.");
            int arp = _categories.Resolve("Arp");
            if (_categories.Label(arp, MetadataLanguage) != "Arp") throw new InvalidOperationException("Metadata did not follow the interface language.");
            PresetGrid.SelectedItem = Presets.FirstOrDefault(row => row.Record.DuplicateOfId.Length == 0);
            foreach (var (width, height, name) in new[] { (1380d, 900d, "desktop"), (1040d, 700d, "compact") })
            {
                Width = width; Height = height;
                _smokeRequestedViewport = new Size(width, height);
                _smokeLayoutPhase = "english-" + name;
                Pages.SelectedItem = LibraryTab;
                PresetDetailsScroll.Offset = default;
                await SettleLayoutAsync();
                CheckSmokeLayout();
                CaptureSmokeImage("ui-english-" + name + ".png");
                Pages.SelectedItem = SettingsTab;
                SettingsPages.SelectedItem = StorageSettingsTab;
                await SettleLayoutAsync();
                CaptureSmokeImage("ui-english-settings-" + name + ".png");
            }
            MetadataLanguageChoiceBox.SelectedItem = MetadataLanguageChoices.Single(choice => choice.Id == "zh-CN");
            await SettleLayoutAsync();
            if (_categories.Label(arp, MetadataLanguage) != "Arp / 琶音 / 点缀" || !Equals(LibraryTab.Header, "Library"))
                throw new InvalidOperationException("Independent metadata language did not preserve English interface text.");
            Pages.SelectedItem = LibraryTab;
            PresetMetadataExpander.IsExpanded = true;
            PresetCategoriesEditor.Text = "点缀";
            await SettleLayoutAsync();
            if (CategoryOptions.Count != 1 || CategoryOptions.Single().Id != arp)
                throw new InvalidOperationException("Category search did not find an alias.");
            if (CanEditSelection)
            {
            var edit = EditMetadataAsync(true, CategoryOptions.Single());
            await SettleLayoutAsync();
            var editor = OwnedWindows.Single();
            var inputs = editor.GetVisualDescendants().OfType<TextBox>().ToArray();
            if (editor.Title != T("编辑分类") || inputs.Length != 2 || inputs[0].Text != "Arp" ||
                inputs[1].Text?.Contains("点缀", StringComparison.Ordinal) != true || inputs[1].Watermark != T("别名（可选，用逗号分隔）"))
                throw new InvalidOperationException("Category editor did not separate required names, optional aliases, and unselectable watermarks.");
            using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)editor.ClientSize.Width, (int)editor.ClientSize.Height)))
            { bitmap.Render(editor); bitmap.Save(Path.Combine(App.DataDirectory, "ui-category-name-alias-editor.png")); }
            editor.Close();
            await edit;
            }
            PresetCategoriesEditor.Text = "";
            TagOptions.Clear();
            for (int index = 0; index < 200; index++) TagOptions.Add(new(100000 + index, "TEST ONLY mood " + index, false));
            TagOptionsList.BringIntoView();
            await SettleLayoutAsync();
            int realized = TagOptionsList.GetVisualDescendants().OfType<CheckBox>().Count();
            if (TagOptionsList.Bounds.Height > 121 || realized is < 1 or >= 200)
                throw new InvalidOperationException("Large tag collections do not use a bounded, virtualized scrolling list.");
            CaptureSmokeImage("ui-english-metadata.png");
            if (JsonSerializer.Serialize(_store.LoadPresets(), LibraryJson.Options) != recordsBefore ||
                FilterGroups.Any(group => !group.SelectedKeys.SetEquals(filtersBefore[group.Key])))
                throw new InvalidOperationException("Language switching changed preset records, previews, or filter IDs.");
        }
        finally
        {
            LanguageChoiceBox.SelectedItem = LanguageChoices.Single(choice => choice.Id == original.Language);
            MetadataLanguageChoiceBox.SelectedItem = MetadataLanguageChoices.Single(choice => choice.Id == original.MetadataLanguage);
            Pages.SelectedItem = original.Item3;
            SettingsPages.SelectedItem = original.Item4;
            Width = original.Width; Height = original.Height;
            PresetMetadataExpander.IsExpanded = original.IsExpanded;
            RefreshMetadataOptions();
            await SettleLayoutAsync();
        }
    }
}
