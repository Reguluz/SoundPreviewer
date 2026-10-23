using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private bool _changingLanguage;
    public IReadOnlyList<LanguageChoice> LanguageChoices { get; } = LanguageService.Current.Languages();
    private IReadOnlyList<LanguageChoice>? _metadataLanguageChoices;
    public IReadOnlyList<LanguageChoice> MetadataLanguageChoices => _metadataLanguageChoices ??= new[] { new LanguageChoice("", "跟随界面") }.Concat(LanguageChoices).ToList();
    public static string TextResourceKey(string text) => "text_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    private void ApplyLanguage()
    {
        LanguageService.Current.Select(_settings.Language);
        string path = Path.Combine(LanguageService.DirectoryPath, "zh-CN.json");
        using var stream = File.Exists(path) ? File.OpenRead(path) : typeof(LanguageService).Assembly.GetManifestResourceStream("SoundPreviewer.Core.language.zh-CN.json")!;
        using var json = System.Text.Json.JsonDocument.Parse(stream);
        foreach (var entry in json.RootElement.GetProperty("strings").EnumerateObject())
            Avalonia.Application.Current!.Resources[TextResourceKey(entry.Name)] = LanguageService.Current.Translate(entry.Name);
    }
    private void Language_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_changingLanguage || LanguageChoiceBox?.SelectedItem is not LanguageChoice language || MetadataLanguageChoiceBox?.SelectedItem is not LanguageChoice metadata) return;
        if (_settings.Language == language.Id && _settings.MetadataLanguage == metadata.Id) return;
        _changingLanguage = true;
        try
        {
            _settings.Language = language.Id;
            _settings.MetadataLanguage = metadata.Id;
            ApplyLanguage();
            Title = T("SoundPreviewer · 合成器音色库") + " (" + AppVersion + ")";
            _store.SaveSettings(_settings);
            foreach (var row in Presets) row.Refresh();
            RefreshFilterChoices();
            foreach (var option in FilterGroups.SelectMany(group => group.Options)) option.RefreshLanguage();
            RefreshPresetMetadataEditors();
            NotifyAll();
        }
        catch (Exception error) { ShowError(error); }
        finally { _changingLanguage = false; }
    }
    private void PopulateLanguageChoices()
    {
        _changingLanguage = true;
        LanguageChoiceBox.SelectedItem = LanguageChoices.FirstOrDefault(item => item.Id == _settings.Language);
        MetadataLanguageChoiceBox.SelectedItem = MetadataLanguageChoices.FirstOrDefault(item => item.Id == _settings.MetadataLanguage);
        _changingLanguage = false;
    }
}
