using System.IO;
using System.Text.Json;
using Avalonia.Interactivity;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    public string RendererSetupLocation => "环境保存在：" + Path.Combine(App.DataDirectory, "renderers");

    private async void PrepareDawDreamer_Click(object? sender, RoutedEventArgs e) =>
        await RunTask(PrepareDawDreamerCoreAsync);

    private async Task PrepareDawDreamerCoreAsync(CancellationToken cancellationToken)
    {
        string diagnostics = Path.Combine(App.DataDirectory, "diagnostics");
        Directory.CreateDirectory(diagnostics);
        string resultPath = Path.Combine(diagnostics, "renderer-setup-" + Guid.NewGuid().ToString("N") + ".json");
        var command = CreateRendererSetupCommand(AppContext.BaseDirectory, App.DataDirectory, resultPath,
            PluginSelectionService.CurrentPlatform, PluginSelectionService.CurrentArchitecture);
        if (!File.Exists(command.Script)) throw new FileNotFoundException("缺少生成环境准备脚本，请重新完整解压安装包。", command.Script);
        _taskTitle = "正在准备生成环境";
        _statusMessage = "首次需要联网下载私有 Python 和渲染依赖，无需另行安装。完成后会自动填入设置。";
        _taskProgressText = "正在下载、检查环境；可停止后重试";
        NotifyAll();
        string workingDirectory = App.DataDirectory;
        if (OperatingSystem.IsWindows() && workingDirectory.Length >= 240)
        {
            workingDirectory = AppContext.BaseDirectory;
            if (workingDirectory.Length >= 240) workingDirectory = Path.GetPathRoot(workingDirectory)!;
        }
        var outcome = await ProcessRunner.RunAsync(command.Executable, command.Arguments, workingDirectory,
            TimeSpan.FromMinutes(20), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (string line in outcome.StandardOutput.Split('\n').Where(line => line.StartsWith("SP_SETUP:", StringComparison.Ordinal)))
            Log(line["SP_SETUP:".Length..].Trim());
        if (outcome.ExitCode != 0 || !File.Exists(resultPath))
        {
            string detail = outcome.StandardError.Trim();
            if (detail.Length > 3000) detail = detail[^3000..];
            throw new IOException("生成环境准备未完成。已下载的缓存保留，可检查网络后重试。" +
                (detail.Length == 0 ? "" : Environment.NewLine + detail));
        }
        if (new FileInfo(resultPath).Length > 1024 * 1024) throw new InvalidDataException("环境准备结果过大。");
        var result = JsonSerializer.Deserialize<RendererSetupResult>(await File.ReadAllTextAsync(resultPath, cancellationToken), LibraryJson.Options)
            ?? throw new InvalidDataException("环境准备结果无效。");
        if (!result.Success || !result.ManagedRuntime || !ApplicationPaths.SamePath(result.DataDirectory, App.DataDirectory) ||
            !DawDreamerRuntimeService.TryGetLocalRuntime(App.DataDirectory, out var runtime) ||
            !ApplicationPaths.SamePath(result.EnvironmentPythonPath, runtime!.PythonPath) ||
            !runtime.SupportsSerum2)
            throw new InvalidDataException("私有生成环境未通过完整检查，未更改已保存的生成方式。");
        var candidate = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings, LibraryJson.Options), LibraryJson.Options)!;
        candidate.Plugins = ClonePlugins();
        int configured = PluginSelectionService.ApplyPreparedDawDreamer(candidate.Plugins, App.DataDirectory, _settings.DiscoveredPlugins);
        await Task.Run(() => RefreshBinaryHashes(candidate, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _store.SaveSettings(candidate);
        _settings = candidate;
        foreach (var plugin in Plugins)
        {
            var saved = candidate.Plugins.Single(item => item.Synth == plugin.Synth);
            plugin.Backend = saved.Backend;
            plugin.ExecutablePath = saved.ExecutablePath;
            plugin.ExecutableHash = saved.ExecutableHash;
            plugin.PluginHash = saved.PluginHash;
            plugin.InitBaselinePath = saved.InitBaselinePath;
        }
        RefreshGrid(PluginGrid);
        RefreshCompatibility();
        _taskTitle = "生成环境已就绪";
        _taskProgressText = "环境检查通过";
        _statusMessage = configured > 0 ? $"已准备私有生成环境，并自动填入 {configured} 个音源。可开始生成预览。"
            : "私有生成环境已准备好。尚未关联音源时，请先扫描并选择对应版本。";
        Log(_statusMessage);
        Log(RendererSetupLocation);
        NotifyAll();
    }

    private sealed record RendererSetupCommand(string Executable, string Script, List<string> Arguments);

    private static RendererSetupCommand CreateRendererSetupCommand(string application, string data, string result,
        PluginPlatform platform, PluginArchitecture architecture)
    {
        application = Path.GetFullPath(application);
        data = Path.GetFullPath(data);
        result = Path.GetFullPath(result);
        bool windows = platform == PluginPlatform.Windows;
        if (architecture is not (PluginArchitecture.X64 or PluginArchitecture.Arm64) || windows && architecture != PluginArchitecture.X64)
            throw new NotSupportedException("私有生成环境目前支持 Windows x64 与 macOS x64 / Apple Silicon。");
        string script = Path.Combine(application, windows ? "Prepare-DawDreamer.ps1" : "Prepare-DawDreamer.sh");
        if (windows)
        {
            string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            return new(shell, script, ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                "-ApplicationPath", application, "-DataDirectory", data, "-ResultPath", result, "-IncludeSerum2"]);
        }
        return new("/bin/bash", script, [script, "--app-path", application, "--data-directory", data, "--result", result,
            "--architecture", architecture == PluginArchitecture.Arm64 ? "arm64" : "x64", "--include-serum2"]);
    }

    private sealed record RendererSetupResult
    {
        public bool Success { get; init; }
        public bool ManagedRuntime { get; init; }
        public string EnvironmentPythonPath { get; init; } = "";
        public string DataDirectory { get; init; } = "";
    }
}
