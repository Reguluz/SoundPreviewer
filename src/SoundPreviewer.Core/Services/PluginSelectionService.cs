using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Services;

public static class PluginSelectionService
{
    public static int RefreshBundledHost(IList<PluginConfiguration> configurations, string? newHostPath)
    {
        ArgumentNullException.ThrowIfNull(configurations);
        if (!File.Exists(newHostPath)) return 0;
        string nextPath = Path.GetFullPath(newHostPath!);
        int changed = 0;
        foreach (var configuration in configurations)
        {
            if (configuration.Synth != SynthKind.Sylenth1 || configuration.Backend != RenderBackend.ExternalWorker ||
                string.IsNullOrWhiteSpace(configuration.ExecutablePath)) continue;
            string previousPath;
            try { previousPath = Path.GetFullPath(configuration.ExecutablePath); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            { continue; }
            if (string.Equals(previousPath, nextPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(previousPath), "SoundPreviewer.Vst2Host.exe", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(Path.GetDirectoryName(previousPath)), "Vst2Host-x86", StringComparison.OrdinalIgnoreCase)) continue;
            configuration.ExecutablePath = nextPath;
            configuration.ExecutableHash = "";
            configuration.InitBaselinePath = "";
            changed++;
        }
        return changed;
    }

    public static int ApplyAutomatic(IReadOnlyList<DiscoveredPlugin> discovered, IList<PluginConfiguration> configurations, string? x86HostPath = null)
    {
        var changed = 0;
        foreach (var configuration in configurations.Where(p => p.Synth != SynthKind.Unknown))
        {
            var candidates = discovered.Where(p => p.Synth == configuration.Synth &&
                    (p.Architecture == PluginArchitecture.X64 || CanUseX86Host(configuration, p, x86HostPath))
                    && (p.Format != PluginBinaryFormat.Standalone || configuration.Backend is RenderBackend.Unconfigured or RenderBackend.VitalCli))
                .OrderBy(p => Preference(p, configuration.Synth)).ThenByDescending(p => ParseVersion(p.Version))
                .ThenBy(p => p.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
            var selected = candidates.FirstOrDefault(p => Matches(configuration, p));
            // Keep a user's existing selection even when its installation root is offline or omitted.
            if (selected is null && (File.Exists(configuration.PluginPath) || Directory.Exists(configuration.PluginPath)
                || configuration.Backend == RenderBackend.VitalCli && File.Exists(configuration.ExecutablePath))) continue;
            selected ??= candidates.FirstOrDefault();
            if (selected is null) continue;
            var before = (configuration.PluginPath, configuration.ExecutablePath, configuration.Version, configuration.Backend);
            Assign(configuration, selected, x86HostPath);
            if (before != (configuration.PluginPath, configuration.ExecutablePath, configuration.Version, configuration.Backend)) changed++;
        }
        return changed;
    }

    public static bool Matches(PluginConfiguration configuration, DiscoveredPlugin plugin) => configuration.Synth == plugin.Synth
        && (plugin.Format != PluginBinaryFormat.Standalone || configuration.Backend == RenderBackend.VitalCli)
        && string.Equals(plugin.Format == PluginBinaryFormat.Standalone ? configuration.ExecutablePath : configuration.PluginPath,
            plugin.FilePath, StringComparison.OrdinalIgnoreCase);

    public static void Assign(PluginConfiguration configuration, DiscoveredPlugin plugin, string? x86HostPath = null)
    {
        if (plugin.Synth == SynthKind.Unknown || configuration.Synth != plugin.Synth)
            throw new ArgumentException("扫描结果不能关联到此音源。");
        if (!File.Exists(plugin.BinaryPath)) throw new FileNotFoundException("扫描到的音源文件已不存在，请重新扫描。", plugin.BinaryPath);
        if (plugin.Format == PluginBinaryFormat.Standalone)
        {
            if (plugin.Synth != SynthKind.Vital) throw new ArgumentException("只有 Vital 独立程序支持原生预览方式。");
            configuration.ExecutablePath = plugin.FilePath;
            configuration.Backend = RenderBackend.VitalCli;
            configuration.PluginPath = "";
            configuration.PluginHash = "";
        }
        else
        {
            configuration.PluginPath = plugin.FilePath;
            if (configuration.Backend == RenderBackend.VitalCli)
            {
                configuration.Backend = RenderBackend.Unconfigured;
                configuration.ExecutablePath = "";
                configuration.ExecutableHash = "";
            }
            if (CanUseX86Host(configuration, plugin, x86HostPath) &&
                configuration.Backend is RenderBackend.Unconfigured or RenderBackend.DawDreamer)
            {
                configuration.Backend = RenderBackend.ExternalWorker;
                configuration.ExecutablePath = Path.GetFullPath(x86HostPath!);
                configuration.ExecutableHash = "";
            }
        }
        configuration.Version = plugin.Version;
    }

    private static bool CanUseX86Host(PluginConfiguration configuration, DiscoveredPlugin plugin, string? hostPath) =>
        plugin.Synth == SynthKind.Sylenth1 && plugin.Architecture == PluginArchitecture.X86 &&
        plugin.Format == PluginBinaryFormat.Vst2 && !string.IsNullOrWhiteSpace(hostPath) && File.Exists(hostPath) &&
        (configuration.Backend is RenderBackend.Unconfigured or RenderBackend.DawDreamer ||
         configuration.Backend == RenderBackend.ExternalWorker &&
         string.Equals(configuration.ExecutablePath, hostPath, StringComparison.OrdinalIgnoreCase));

    private static int Preference(DiscoveredPlugin plugin, SynthKind synth) => (synth, plugin.Format) switch
    {
        (SynthKind.Vital, PluginBinaryFormat.Standalone) => 0,
        (SynthKind.Serum2, PluginBinaryFormat.Vst3) => 0,
        (_, PluginBinaryFormat.Vst2) => 1,
        _ => 2
    };

    private static Version ParseVersion(string value)
    {
        var normalized = value.Split('+', '-')[0].Trim().Replace(',', '.').Replace(" ", "");
        return Version.TryParse(normalized, out var version) ? version : new Version(0, 0);
    }
}
