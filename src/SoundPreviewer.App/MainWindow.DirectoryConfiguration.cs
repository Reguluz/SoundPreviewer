using System.Diagnostics;
using System.Text.Json;
using Avalonia.Interactivity;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private readonly DirectoryConfigurationService _directoryConfiguration;
    public string DirectoryConfigurationLocation => _directoryConfiguration.FilePath;
    public string DirectoryConfigurationHint => "音色来源与音源安装目录保存在：" + DirectoryConfigurationLocation +
        "\n可退出程序后手动修改，重启时读取；升级时请保留此文件和 Preview。";

    private void SaveDirectoryConfiguration(AppSettings settings)
    {
        _directoryConfiguration.Save(settings);
        // Keep the existing database settings readable by previous versions.
        _store.SaveSettings(settings);
    }

    private bool TryChangeDirectories(Action change)
    {
        var previous = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings, LibraryJson.Options), LibraryJson.Options)!;
        try
        {
            change();
            _settings.Sources = Sources.ToList();
            _settings.PluginDirectories = PluginDirectories.ToList();
            SaveDirectoryConfiguration(_settings);
            return true;
        }
        catch (Exception error)
        {
            _settings = previous;
            PopulateSettings();
            ShowError(error);
            return false;
        }
    }

    private void OpenDirectoryConfiguration_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(DirectoryConfigurationLocation) { UseShellExecute = true });
        }
        catch (Exception error) { ShowError(error); }
    }
}
