using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Services;

public static class PluginSelectionService
{
    public static int ApplyAutomatic(IReadOnlyList<DiscoveredPlugin> discovered, IList<PluginConfiguration> configurations)
    {
        var changed = 0;
        foreach (var configuration in configurations.Where(p => p.Synth != SynthKind.Unknown))
        {
            var candidates = discovered.Where(p => p.Synth == configuration.Synth && p.Architecture == PluginArchitecture.X64
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
            Assign(configuration, selected);
            if (before != (configuration.PluginPath, configuration.ExecutablePath, configuration.Version, configuration.Backend)) changed++;
        }
        return changed;
    }

    public static bool Matches(PluginConfiguration configuration, DiscoveredPlugin plugin) => configuration.Synth == plugin.Synth
        && (plugin.Format != PluginBinaryFormat.Standalone || configuration.Backend == RenderBackend.VitalCli)
        && string.Equals(plugin.Format == PluginBinaryFormat.Standalone ? configuration.ExecutablePath : configuration.PluginPath,
            plugin.FilePath, StringComparison.OrdinalIgnoreCase);

    public static void Assign(PluginConfiguration configuration, DiscoveredPlugin plugin)
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
        }
        configuration.Version = plugin.Version;
    }

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
