using System.IO;
using System.Windows;
using System.Text.Json;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    public static string DataDirectory { get; private set; } = "";
    public static bool SmokeTest { get; private set; }
    public static bool ScanOnStart { get; private set; }
    public static AppSettings? ImportedSettings { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dataIndex = Array.IndexOf(e.Args, "--data-directory");
        DataDirectory = dataIndex >= 0 && dataIndex + 1 < e.Args.Length
            ? Path.GetFullPath(e.Args[dataIndex + 1])
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundPreviewer");
        SmokeTest = e.Args.Contains("--smoke-test");
        ScanOnStart = e.Args.Contains("--scan-on-start");
        var name = "Local\\SoundPreviewer-" + Core.Models.Fingerprints.HashText(DataDirectory.ToUpperInvariant())[..20];
        _instanceMutex = new Mutex(true, name, out var created);
        if (!created)
        {
            MessageBox.Show("此音色库已经打开。", "SoundPreviewer", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = !SmokeTest;
            if (SmokeTest) File.WriteAllText(Path.Combine(DataDirectory, "ui-error.txt"), args.Exception.ToString());
            else MessageBox.Show(args.Exception.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        try
        {
            var importIndex = Array.IndexOf(e.Args, "--import-settings");
            if (importIndex >= 0 && importIndex + 1 < e.Args.Length)
                ImportedSettings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(e.Args[importIndex + 1]), LibraryJson.Options)
                    ?? throw new InvalidDataException("设置文件无效。");
            Directory.CreateDirectory(DataDirectory);
            new MainWindow().Show();
        }
        catch (Exception ex)
        {
            if (SmokeTest) File.WriteAllText(Path.Combine(DataDirectory, "ui-startup-error.txt"), ex.ToString());
            else MessageBox.Show(ex.Message, "无法打开本地音色库", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        if (SmokeTest) File.WriteAllText(Path.Combine(DataDirectory, "ui-exit.json"), JsonSerializer.Serialize(new { ExitCode = e.ApplicationExitCode }));
        base.OnExit(e);
    }
}
