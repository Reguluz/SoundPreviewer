using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestRendererSettingsAsync()
    {
        string directory = Fixture("renderer-partial-settings");
        string absent = Path.Combine(directory, "offline", "python.exe");
        var serum = new PluginConfiguration { Synth = SynthKind.Serum1, Backend = RenderBackend.DawDreamer, ExecutablePath = absent };
        var other = new PluginConfiguration { Synth = SynthKind.Serum2, Backend = RenderBackend.DawDreamer };
        RenderReadinessService.ValidateSettings([serum, other]);
        True(!EvaluateWindowsReadiness(serum, []).CanAttemptRender && !EvaluateWindowsReadiness(other, []).CanAttemptRender,
            "Saving partial renderers never means that a missing program or plugin can execute.");
        var store = new LibraryStore(Path.Combine(directory, "library.db"));
        store.SaveSettings(new AppSettings { Plugins = [serum, other], MaxParallelRenders = 4 });
        var saved = store.LoadSettings();
        Equal(absent, saved.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).ExecutablePath, "Saving unrelated settings retains offline renderer paths.");
        Equal(RenderBackend.DawDreamer, saved.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum2).Backend, "An incomplete row retains its chosen backend.");
        Equal(4, saved.MaxParallelRenders, "Incomplete rows do not prevent other settings from persisting.");
        serum.TimeoutSeconds = 4;
        Throws<ArgumentException>(() => RenderReadinessService.ValidateSettings([serum]));
        serum.TimeoutSeconds = 120;
        serum.Backend = RenderBackend.VitalCli;
        Throws<ArgumentException>(() => RenderReadinessService.ValidateSettings([serum]));
        serum.Backend = (RenderBackend)99;
        Throws<ArgumentException>(() => RenderReadinessService.ValidateSettings([serum]));

        string python = Path.Combine(directory, "renderers", "dawdreamer-cpython-x64", "Scripts", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "TEST ONLY placeholder. Never execute this fixture.");
        serum.Backend = RenderBackend.DawDreamer;
        var unconfigured = new PluginConfiguration { Synth = SynthKind.Sylenth1 };
        var spire = new PluginConfiguration { Synth = SynthKind.Spire, Backend = RenderBackend.DawDreamer };
        Equal(1, PluginSelectionService.RefreshLocalDawDreamer([serum, other, unconfigured, spire], directory, PluginPlatform.Windows),
            "Only an eligible DawDreamer row without a selected executable receives the library's known local Python.");
        Equal(absent, serum.ExecutablePath, "Local host discovery preserves an explicit missing or offline executable.");
        Equal(python, other.ExecutablePath, "A blank eligible row uses only the prepared local default environment.");
        Equal(RenderBackend.Unconfigured, unconfigured.Backend, "Finding an environment never changes unconfigured backend choices.");
        Equal("", spire.ExecutablePath, "Finding Python does not imply support for an unadapted synth.");
        Equal(0, PluginSelectionService.RefreshLocalDawDreamer([new PluginConfiguration { Synth = SynthKind.Serum1, Backend = RenderBackend.DawDreamer }],
            Path.Combine(directory, "another-library"), PluginPlatform.Windows), "Local discovery never uses a development or another library environment.");
        string macPython = Path.Combine(directory, "renderers", "dawdreamer-cpython", "bin", "python");
        Directory.CreateDirectory(Path.GetDirectoryName(macPython)!);
        File.WriteAllText(macPython, "TEST ONLY placeholder. Never execute this fixture.");
        var macSerum = new PluginConfiguration { Synth = SynthKind.Serum1, Backend = RenderBackend.DawDreamer };
        Equal(1, PluginSelectionService.RefreshLocalDawDreamer([macSerum], directory, PluginPlatform.MacOS), "Mac uses its own setup script's known environment path.");
        Equal(macPython, macSerum.ExecutablePath, "Mac never selects the Windows Python executable in the same fixture.");

        string host = Path.Combine(directory, "Vst2Host-x86", "SoundPreviewer.Vst2Host.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllText(host, "TEST ONLY placeholder. Never execute this fixture.");
        string synth = Path.Combine(directory, "TEST-ONLY-Sylenth1.dll");
        WritePeFixture(synth, x64: false, "VSTPluginMain");
        var discovered = new DiscoveredPlugin { Synth = SynthKind.Sylenth1, FilePath = synth, BinaryPath = synth,
            Platform = PluginPlatform.Windows, Architecture = PluginArchitecture.X86, Format = PluginBinaryFormat.Vst2, Version = "TEST ONLY 2" };
        var sylenth = new PluginConfiguration { Synth = SynthKind.Sylenth1, Backend = RenderBackend.DawDreamer, ExecutablePath = host, PluginPath = synth, Version = discovered.Version };
        True(!EvaluateWindowsReadiness(sylenth, [discovered]).CanAttemptRender, "The bundled x86 worker cannot be launched as Python.");
        Equal(1, PluginSelectionService.RefreshBundledHost([sylenth], host, PluginPlatform.Windows, [discovered]),
            "An exactly identified bundled worker with a scanned x86 Sylenth selection restores its external backend.");
        Equal(RenderBackend.ExternalWorker, sylenth.Backend, "Repair selects the backend actually implemented by the bundled worker.");
        True(EvaluateWindowsReadiness(sylenth, [discovered]).CanAttemptRender, "The matching x86 selection can use its own external worker.");
        sylenth.Synth = SynthKind.Serum1;
        True(!EvaluateWindowsReadiness(sylenth, [discovered]).CanAttemptRender, "The Sylenth-only worker cannot be repurposed for Serum 1.");
        serum.ExecutablePath = synth;
        True(!EvaluateWindowsReadiness(serum, []).CanAttemptRender, "A plugin DLL cannot serve as the DawDreamer Python executable.");
        string serum32 = Path.Combine(directory, "TEST-ONLY-Serum-x86.dll");
        WritePeFixture(serum32, x64: false, "VSTPluginMain");
        var scannedSerum32 = new DiscoveredPlugin { Synth = SynthKind.Serum1, FilePath = serum32, BinaryPath = serum32,
            Platform = PluginPlatform.Windows, Architecture = PluginArchitecture.X86, Format = PluginBinaryFormat.Vst2, Version = "TEST ONLY 1" };
        serum.ExecutablePath = python;
        AssignWindowsPlugin(serum, scannedSerum32);
        var x86Readiness = EvaluateWindowsReadiness(serum, [scannedSerum32]);
        True(!x86Readiness.CanAttemptRender && x86Readiness.Status == "位数不兼容", "Associating x86 Serum accurately requires another compatible host instead of implying DawDreamer support.");
        return Task.CompletedTask;
    }
}
