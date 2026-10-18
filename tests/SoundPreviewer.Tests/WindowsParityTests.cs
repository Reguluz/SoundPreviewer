using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestWindowsPlatformParityAsync()
    {
        string directory = Fixture("windows-platform-parity");
        string bundle = Path.Combine(directory, "TEST-ONLY-Serum2.vst3");
        string intel = Path.Combine(bundle, "Contents", "x86_64-win", "Serum2.vst3");
        string legacy = Path.Combine(bundle, "Contents", "x86-win", "Serum2.vst3");
        Directory.CreateDirectory(Path.GetDirectoryName(intel)!);
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        WritePeFixture(intel, x64: true, "GetPluginFactory");
        WritePeFixture(legacy, x64: false, "GetPluginFactory");
        DiscoveredPlugin Binary(string path, PluginArchitecture architecture) => new()
        {
            Synth = SynthKind.Serum2, Platform = PluginPlatform.Windows, Architecture = architecture,
            Format = PluginBinaryFormat.Vst3, FilePath = bundle, BinaryPath = path, Version = "2.0.23"
        };
        var x86 = Binary(legacy, PluginArchitecture.X86);
        var x64 = Binary(intel, PluginArchitecture.X64);
        var plugin = new PluginConfiguration
        {
            Synth = SynthKind.Serum2, Backend = RenderBackend.DawDreamer, PluginPath = bundle,
            ExecutablePath = TestExecutable, Version = "2.0.23"
        };
        foreach (var inventory in new[] { new[] { x86, x64 }, new[] { x64, x86 } })
        {
            True(RenderReadinessService.Evaluate(plugin, inventory, PluginPlatform.Windows, PluginArchitecture.X64).CanAttemptRender,
                "A bundle's compatible x64 binary must be selected regardless of discovery order.");
            Equal(0, PluginSelectionService.ApplyAutomatic(inventory, [plugin], platform: PluginPlatform.Windows,
                architecture: PluginArchitecture.X64), "A mixed-architecture bundle keeps its selected native configuration.");
        }
        True(!RenderReadinessService.Evaluate(plugin, [x86], PluginPlatform.Windows, PluginArchitecture.X64).CanAttemptRender,
            "A 32-bit-only Serum 2 bundle cannot be passed to the x64 host.");
        File.Delete(intel);
        True(!RenderReadinessService.Evaluate(plugin, [x86, x64], PluginPlatform.Windows, PluginArchitecture.X64).CanAttemptRender,
            "An unavailable native binary cannot be replaced with a 32-bit bundle entry.");
        return Task.CompletedTask;
    }
}
