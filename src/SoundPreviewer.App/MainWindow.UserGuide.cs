using System.Diagnostics;
using Avalonia.Interactivity;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private static ProcessStartInfo PrepareUserGuide()
    {
        string home = Path.Combine(AppContext.BaseDirectory, "操作说明", "index.html");
        if (!File.Exists(home))
            throw new FileNotFoundException(T("操作说明文件缺失，请重新完整解压或安装程序。"), home);
        return new ProcessStartInfo(home) { UseShellExecute = true };
    }

    private void OpenUserGuide_Click(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(PrepareUserGuide()); }
        catch (Exception error) { ShowError(error); }
    }
}
