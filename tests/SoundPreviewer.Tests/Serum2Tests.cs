using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestSerum2ReadinessAsync()
    {
        string directory = Fixture("serum2-readiness");
        string windows = Path.Combine(directory, "TEST-ONLY-Serum2.vst3");
        WritePeFixture(windows, x64: true, "GetPluginFactory");
        var native = new DiscoveredPlugin
        {
            Synth = SynthKind.Serum2, Platform = PluginPlatform.Windows, Architecture = PluginArchitecture.X64,
            Format = PluginBinaryFormat.Vst3, FilePath = windows, BinaryPath = windows, Version = "2.0.23"
        };
        var plugin = new PluginConfiguration
        {
            Synth = SynthKind.Serum2, Backend = RenderBackend.DawDreamer, PluginPath = windows,
            ExecutablePath = TestExecutable, Version = "2.0.23"
        };
        var ready = RenderReadinessService.Evaluate(plugin, [native], PluginPlatform.Windows, PluginArchitecture.X64);
        True(ready.CanAttemptRender && !ready.IsVerified && ready.Detail.Contains("requirements-serum2.txt", StringComparison.Ordinal),
            "Serum 2 native VST3 state loading is attemptable on Windows and still requires installed optional dependencies.");
        True(InitBaselineService.Supports(plugin), "Serum 2 gets fresh-instance startup protection before rendering.");
        plugin.Synth = SynthKind.Serum1;
        True(!RenderReadinessService.Evaluate(plugin, [native], PluginPlatform.Windows, PluginArchitecture.X64).CanAttemptRender,
            "Adding Serum 2 must not route Serum 1 FXP through an incompatible VST3 instrument.");
        string bundle = WriteMacBundle(directory, "Serum2.vst3", "Serum 2", "com.xfer.serum2.VST3", "2.0.23", universal: true);
        var mac = new DiscoveredPlugin
        {
            Synth = SynthKind.Serum2, Platform = PluginPlatform.MacOS, Architecture = PluginArchitecture.Universal,
            Format = PluginBinaryFormat.Vst3, FilePath = bundle, BinaryPath = Path.Combine(bundle, "Contents", "MacOS", "Serum2"), Version = "2.0.23"
        };
        plugin.Synth = SynthKind.Serum2;
        plugin.PluginPath = bundle;
        True(RenderReadinessService.Evaluate(plugin, [mac], PluginPlatform.MacOS, PluginArchitecture.Arm64).CanAttemptRender,
            "Universal Serum 2 bundle supports the native Apple Silicon renderer.");
        True(RenderReadinessService.Evaluate(plugin, [mac], PluginPlatform.MacOS, PluginArchitecture.X64).CanAttemptRender,
            "The same shared renderer supports Intel macOS.");
        mac.Architecture = PluginArchitecture.X64;
        True(!RenderReadinessService.Evaluate(plugin, [mac], PluginPlatform.MacOS, PluginArchitecture.Arm64).CanAttemptRender,
            "A genuine host/plugin architecture mismatch still blocks rendering.");
        plugin.Synth = SynthKind.Spire;
        True(!InitBaselineService.Supports(plugin), "Unverified Spire is not advertised as having a built-in initialization adapter.");
        return Task.CompletedTask;
    }
}
