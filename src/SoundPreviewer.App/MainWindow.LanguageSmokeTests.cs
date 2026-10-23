using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private async Task CheckLanguagesAsync()
    {
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
