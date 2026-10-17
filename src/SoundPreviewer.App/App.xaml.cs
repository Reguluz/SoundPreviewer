using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    public static string DataDirectory { get; private set; } = "";
    public static bool SmokeTest { get; private set; }
    public static bool ScanOnStart { get; private set; }
    public static AppSettings? ImportedSettings { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }
        var args = desktop.Args ?? [];
        SmokeTest = args.Contains("--smoke-test");
        ScanOnStart = args.Contains("--scan-on-start");
        var dataIndex = Array.IndexOf(args, "--data-directory");
        ApplicationPaths.ConfigureDataDirectory(dataIndex >= 0 && dataIndex + 1 < args.Length ? args[dataIndex + 1] : null);
        DataDirectory = ApplicationPaths.DataDirectory;
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var identity = OperatingSystem.IsWindows() ? DataDirectory.ToUpperInvariant() : DataDirectory;
            var name = (OperatingSystem.IsWindows() ? "Local\\" : "") + "SoundPreviewer-" + Fingerprints.HashText(identity)[..20];
            _instanceMutex = new Mutex(true, name, out var created);
            if (!created)
            {
                ShowStartupMessage(desktop, "此音色库已经打开。", "SoundPreviewer");
                base.OnFrameworkInitializationCompleted();
                return;
            }
            Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
            {
                eventArgs.Handled = true;
                if (SmokeTest)
                {
                    File.WriteAllText(Path.Combine(DataDirectory, "ui-error.txt"), eventArgs.Exception.ToString());
                    desktop.Shutdown(1);
                }
                else if (desktop.MainWindow is { } owner) _ = Dialogs.ShowAsync(owner, eventArgs.Exception.Message, "操作未完成");
            };
            var importIndex = Array.IndexOf(args, "--import-settings");
            if (importIndex >= 0 && importIndex + 1 < args.Length)
                ImportedSettings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(args[importIndex + 1]), LibraryJson.Options)
                    ?? throw new InvalidDataException("设置文件无效。");
            desktop.MainWindow = new MainWindow();
        }
        catch (Exception ex)
        {
            if (SmokeTest)
            {
                File.WriteAllText(Path.Combine(DataDirectory, "ui-startup-error.txt"), ex.ToString());
                desktop.Shutdown(1);
            }
            else ShowStartupMessage(desktop, ex.Message, "无法打开本地音色库");
        }
        desktop.Exit += (_, eventArgs) =>
        {
            _instanceMutex?.Dispose();
            if (SmokeTest) File.WriteAllText(Path.Combine(DataDirectory, "ui-exit.json"), JsonSerializer.Serialize(new { ExitCode = eventArgs.ApplicationExitCode }));
        };
        base.OnFrameworkInitializationCompleted();
    }

    public static void Shutdown(int exitCode = 0) =>
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(exitCode);

    private static void ShowStartupMessage(IClassicDesktopStyleApplicationLifetime desktop, string text, string title)
    {
        var window = new Window { Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var close = new Button { Content = "确定", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        close.Click += (_, _) => window.Close();
        window.Content = new StackPanel { Margin = new Thickness(24), Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, close } };
        desktop.MainWindow = window;
    }
}
