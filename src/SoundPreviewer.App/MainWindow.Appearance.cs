using System.Text.Json;
using Avalonia.Interactivity;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private async void Appearance_Click(object? sender, RoutedEventArgs e)
    {
        if (!IsIdle || _isClosing) return;
        var dialog = new AppearanceDialog(_settings.Appearance, SaveAppearance);
        try { await dialog.ShowDialog<bool>(this); }
        finally { ThemeManager.Apply(_settings.Appearance); }
    }

    private void SaveAppearance(AppearanceSettings appearance)
    {
        if (!IsIdle || _isClosing) throw new InvalidOperationException("请等待当前任务结束后再保存配色。");
        var candidate = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings, LibraryJson.Options), LibraryJson.Options)!;
        candidate.Appearance = ThemeManager.Copy(appearance);
        _store.SaveSettings(candidate);
        _settings.Appearance = candidate.Appearance;
        ThemeManager.Apply(candidate.Appearance);
    }
}
