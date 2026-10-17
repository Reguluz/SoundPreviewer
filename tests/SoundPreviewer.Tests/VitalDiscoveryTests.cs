using System.Xml.Linq;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static async Task TestShippingMacPluginDiscoveryAsync()
    {
        string directory = Fixture("shipping-mac-plugins");
        // Identities and versions observed in installed shipping macOS bundles. The
        // executables remain synthetic fixtures; this never loads a real plugin.
        string applications = Path.Combine(directory, "Applications");
        string vst = Path.Combine(directory, "Library", "Audio", "Plug-Ins", "VST");
        string vst3 = Path.Combine(directory, "Library", "Audio", "Plug-Ins", "VST3");
        string vitalApp = WriteMacBundle(applications, "Vital.app", "Vital", "audio.vital.synth", "1.6.4", universal: true);
        string vitalVst = WriteMacBundle(vst, "Vital.vst", "Vital", "audio.vital.synth", "1.6.4", universal: true);
        string vitalVst3 = WriteMacBundle(vst3, "Vital.vst3", "Vital", "audio.vital.synth", "1.6.4", universal: true);
        string serum2 = WriteMacBundle(vst3, "Serum2.vst3", "Serum2", "com.xfer.serum2.VST3", "2.0.23", universal: true, trie: true);
        var scanner = new PluginScanService();
        var result = await scanner.ScanAsync([applications, vst, vst3, vitalApp, vitalVst3]);
        Equal(4, result.Plugins.Count, "Real shipping Vital and Serum 2 bundle identities are recognized once across shared and direct bundle roots.");
        Equal(0, result.Warnings.Count, "Valid shipping-style bundle metadata requires no fallback or warning.");
        Equal(3, result.Plugins.Count(plugin => plugin.Synth == SynthKind.Vital), "Vital's audio.vital.synth identity works for its app, VST2 and VST3 packages.");
        True(result.Plugins.All(plugin => plugin.Architecture == PluginArchitecture.Universal), "Both shipping native architectures are verified.");
        Equal(SynthKind.Serum2, result.Plugins.Single(plugin => plugin.FilePath == serum2).Synth,
            "Serum2's format suffix in com.xfer.serum2.VST3 preserves its product identity.");
        Equal("2.0.23", result.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum2).Version,
            "Serum2 patch versions remain intact for state compatibility and render cache keys.");

        var vital = new PluginConfiguration { Synth = SynthKind.Vital };
        var serum = new PluginConfiguration { Synth = SynthKind.Serum2 };
        Equal(2, PluginSelectionService.ApplyAutomatic(result.Plugins, [vital, serum], platform: PluginPlatform.MacOS,
            architecture: PluginArchitecture.Arm64), "A fresh Mac configuration associates both installed instruments.");
        Equal(RenderBackend.VitalCli, vital.Backend, "Vital's installed app is preferred to VST packages for native preview generation.");
        Equal(PluginScanService.ResolveBundleExecutable(vitalApp), vital.ExecutablePath, "The app association launches its actual native executable.");
        Equal(serum2, serum.PluginPath, "Serum2 retains its native VST3 bundle path.");

        // A lookalike may not inherit Vital's identity from a partial vendor name.
        string spoof = WriteMacBundle(Path.Combine(directory, "lookalikes"), "Vital.vst3", "Vital", "audio.vital-unrelated.synth", "1.6.4");
        Equal(SynthKind.Unknown, (await scanner.ScanAsync([spoof])).Plugins.Single().Synth,
            "A lookalike identifier cannot be accepted as a Vital manufacturer identity.");

        // CFBundleExecutable is the only bundle executable source; a renamed
        // display binary must still resolve without assuming it matches the app.
        string executable = PluginScanService.ResolveBundleExecutable(vitalVst)!;
        string renamed = Path.Combine(Path.GetDirectoryName(executable)!, "NativeVital");
        File.Move(executable, renamed);
        string plist = Path.Combine(vitalVst, "Contents", "Info.plist");
        var metadata = XDocument.Load(plist);
        metadata.Descendants("key").Single(key => key.Value == "CFBundleExecutable").NextNode!.ReplaceWith(new XElement("string", "NativeVital"));
        metadata.Save(plist);
        Equal(renamed, PluginScanService.ResolveBundleExecutable(vitalVst), "Bundle executable resolution follows verified plist metadata.");
        Equal(SynthKind.Vital, (await scanner.ScanAsync([vitalVst])).Plugins.Single().Synth,
            "The native executable's spelling does not replace the bundle's verified product identity.");

        if (OperatingSystem.IsMacOS())
        {
            string userApplications = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications");
            True(ApplicationPaths.DefaultPluginDirectories.Contains(userApplications, ApplicationPaths.PathComparer),
                "The default Mac scan includes user-installed standalone applications.");
        }
    }
}
