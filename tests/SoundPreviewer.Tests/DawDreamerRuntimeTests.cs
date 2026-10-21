using System.Text.Json;
using System.Text.Json.Nodes;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestManagedDawDreamerSelectionAsync()
    {
        string data = Fixture("TEST-ONLY-private-python-selection");
        var runtime = WriteManagedDawDreamerFixture(data);
        True(DawDreamerRuntimeService.TryGetLocalRuntime(data, out var found, PluginPlatform.Windows, PluginArchitecture.X64),
            "A bounded prepared runtime is found inside this library only.");
        Equal(runtime.PythonPath, found!.PythonPath, "Discovery uses the relative executable recorded by the local contract.");
        True(!DawDreamerRuntimeService.TryGetLocalRuntime(data, out _, PluginPlatform.Windows, PluginArchitecture.Arm64),
            "Windows ARM does not silently claim an x64 private environment.");
        True(!DawDreamerRuntimeService.TryGetLocalRuntime(Path.Combine(data, "other-library"), out _, PluginPlatform.Windows, PluginArchitecture.X64),
            "Private discovery never borrows another library environment.");
        var blank = new PluginConfiguration { Synth = SynthKind.Serum1, Backend = RenderBackend.DawDreamer };
        var external = new PluginConfiguration { Synth = SynthKind.Serum2, Backend = RenderBackend.ExternalWorker };
        var sushi = new PluginConfiguration { Synth = SynthKind.Serum1, Backend = RenderBackend.Sushi };
        var custom = new PluginConfiguration { Synth = SynthKind.Sylenth1, Backend = RenderBackend.DawDreamer,
            ExecutablePath = Path.Combine(data, "my-offline-python", "python.exe") };
        Equal(1, PluginSelectionService.RefreshLocalDawDreamer([blank, external, sushi, custom], data, PluginPlatform.Windows, PluginArchitecture.X64),
            "Automatic refresh fills only eligible blank DawDreamer choices.");
        Equal(runtime.PythonPath, blank.ExecutablePath, "A prepared private runtime takes priority over a legacy default for blank choices.");
        Equal(RenderBackend.ExternalWorker, external.Backend, "Finding Python never replaces external workers.");
        Equal(RenderBackend.Sushi, sushi.Backend, "Finding Python never replaces a selected experiment.");
        True(custom.ExecutablePath.Contains("my-offline-python"), "An explicit offline custom interpreter remains selected.");

        string legacy = Path.Combine(data, "renderers", "dawdreamer-cpython-x64", "Scripts", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "TEST ONLY old venv placeholder; never executed.");
        var old = new PluginConfiguration { Synth = SynthKind.Serum1, Backend = RenderBackend.DawDreamer, ExecutablePath = legacy };
        Equal(0, PluginSelectionService.RefreshLocalDawDreamer([old], data, PluginPlatform.Windows, PluginArchitecture.X64),
            "An existing legacy venv is not silently replaced by the new private runtime.");
        File.Delete(legacy);
        Equal(1, PluginSelectionService.RefreshLocalDawDreamer([old], data, PluginPlatform.Windows, PluginArchitecture.X64),
            "A missing exact legacy default can migrate to a newly prepared standalone runtime.");

        string nextData = Fixture("TEST-ONLY-moved-private-python");
        var next = WriteManagedDawDreamerFixture(nextData);
        blank.ExecutableHash = "TEST ONLY old hash";
        blank.InitBaselinePath = "TEST ONLY old reference";
        Equal(0, PluginSelectionService.RefreshLocalDawDreamer([blank], nextData, PluginPlatform.Windows, PluginArchitecture.X64),
            "An existing selected runtime from another location remains an explicit choice.");
        File.Delete(runtime.PythonPath);
        Equal(1, PluginSelectionService.RefreshLocalDawDreamer([blank], nextData, PluginPlatform.Windows, PluginArchitecture.X64),
            "Moving the installation repairs only its missing known runtime path.");
        Equal(next.PythonPath, blank.ExecutablePath, "Relocation selects the new library's prepared private runtime.");
        Equal("", blank.ExecutableHash, "Relocation refreshes the executable identity.");
        Equal("", blank.InitBaselinePath, "Relocation never retains a reference tied to the previous absolute runtime.");

        string binary = Path.Combine(data, "TEST-ONLY-Serum.dll");
        WritePeFixture(binary, true, "VSTPluginMain");
        var scanned = new DiscoveredPlugin { Synth = SynthKind.Serum1, FilePath = binary, BinaryPath = binary,
            Format = PluginBinaryFormat.Vst2, Platform = PluginPlatform.Windows, Architecture = PluginArchitecture.X64, Version = "TEST ONLY 1" };
        var automatic = new PluginConfiguration { Synth = SynthKind.Serum1, PluginPath = binary, Version = scanned.Version };
        var unscanned = new PluginConfiguration { Synth = SynthKind.Serum2 };
        var x86 = new PluginConfiguration { Synth = SynthKind.Sylenth1, PluginPath = Path.Combine(data, "TEST-ONLY-Sylenth32.dll") };
        WritePeFixture(x86.PluginPath, false, "VSTPluginMain");
        var scannedX86 = new DiscoveredPlugin { Synth = SynthKind.Sylenth1, FilePath = x86.PluginPath, BinaryPath = x86.PluginPath,
            Format = PluginBinaryFormat.Vst2, Platform = PluginPlatform.Windows, Architecture = PluginArchitecture.X86 };
        Equal(1, PluginSelectionService.ApplyPreparedDawDreamer([automatic, unscanned, x86, external, sushi], nextData,
            [scanned, scannedX86], PluginPlatform.Windows, PluginArchitecture.X64), "Explicit setup enables only scanned compatible automatic rows.");
        Equal(RenderBackend.DawDreamer, automatic.Backend, "Explicit preparation can complete a previously unconfigured Serum selection.");
        Equal(RenderBackend.Unconfigured, x86.Backend, "A prepared x64 host does not take ownership of x86 instruments.");
        Equal(RenderBackend.Unconfigured, unscanned.Backend, "An environment alone is not evidence of an installed instrument.");
        True(EvaluateWindowsReadiness(automatic, [scanned]).CanAttemptRender, "A complete local configuration can proceed to actual rendering validation.");

        foreach (var architecture in new[] { PluginArchitecture.X64, PluginArchitecture.Arm64 })
        {
            var mac = WriteManagedDawDreamerFixture(nextData, PluginPlatform.MacOS, architecture);
            True(DawDreamerRuntimeService.TryGetLocalRuntime(nextData, out var selected, PluginPlatform.MacOS, architecture),
                "Mac discovery selects only its matching private architecture.");
            Equal(mac.PythonPath, selected!.PythonPath, "The Mac interpreter is the recorded bin/python3.12.");
        }
        return Task.CompletedTask;
    }

    private static async Task TestManagedDawDreamerIdentityAsync()
    {
        string data = Fixture("TEST-ONLY-private-python-identity");
        var runtime = WriteManagedDawDreamerFixture(data);
        var plugin = new PluginConfiguration { Synth = SynthKind.Serum1, Backend = RenderBackend.DawDreamer,
            ExecutablePath = runtime.PythonPath, PluginPath = Path.Combine(data, "TEST-ONLY-plugin.dll"), Version = "TEST ONLY 1" };
        File.WriteAllText(plugin.PluginPath, "TEST ONLY plugin metadata; never loaded.");
        string rawPython = Fingerprints.HashFile(plugin.ExecutablePath);
        string identity = Fingerprints.RendererExecutableHash(plugin);
        var settings = new AppSettings { PreviewDirectory = Path.Combine(data, "previews"), Plugins = [plugin],
            Template = new PreviewTemplate { QuickPreview = false, HoldSeconds = 0.5, TailSeconds = 0 } };
        var generated = await InitBaselineService.GenerateAsync(settings, plugin, TestExecutable);
        True(generated.CanCompare, "Synthetic test-only fresh captures can establish a reference for a private runtime identity.");
        await File.AppendAllTextAsync(Path.Combine(runtime.RootDirectory, "TEST-ONLY-native.pyd"), "TEST ONLY replacement native code");
        Equal(rawPython, Fingerprints.HashFile(plugin.ExecutablePath), "The Python launcher itself remains unchanged in this fixture.");
        NotEqual(identity, Fingerprints.RendererExecutableHash(plugin), "Replacing a private native dependency changes only the managed renderer identity.");
        True(!(await InitBaselineService.InspectAsync(settings, plugin)).CanCompare, "A changed private dependency expires its old startup reference.");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            Throws<OperationCanceledException>(() => Fingerprints.RendererExecutableHash(plugin, cancellation.Token));
        }

        var old = new PluginConfiguration { Backend = RenderBackend.DawDreamer, ExecutablePath = TestExecutable };
        Equal(Fingerprints.HashFile(TestExecutable), Fingerprints.RendererExecutableHash(old), "All non-managed DawDreamer executable hashes remain byte-compatible.");
        var record = Preset("TEST-ONLY-existing-private-preview");
        record.Synth = SynthKind.Serum1;
        CompletePreview(record, Path.Combine(data, "previews", "TEST-ONLY-existing.wav"));
        string audioHash = Fingerprints.HashFile(record.PreviewPath);
        var store = new LibraryStore(Path.Combine(data, "library.db"));
        store.SaveState([record], settings);
        var reused = await new RenderQueue(store).RunAsync(settings, [record], Path.Combine(data, "missing-worker.exe"), new InlineProgress(), CancellationToken.None);
        Equal(1, reused.Reused, "Existing complete audio is reused before a changed runtime or expired baseline needs a worker.");
        Equal(audioHash, Fingerprints.HashFile(record.PreviewPath), "Private environment changes leave existing cached audio bytes unchanged.");

        string validManifest = await File.ReadAllTextAsync(runtime.ManifestPath);
        var marker = JsonNode.Parse(validManifest)!;
        marker["Files"]!["../../outside.dll"] = new string('a', 64);
        await File.WriteAllTextAsync(runtime.ManifestPath, marker.ToJsonString());
        True(!DawDreamerRuntimeService.TryGetRuntime(runtime.PythonPath, out _), "Manifest paths cannot escape their private runtime.");
        await File.WriteAllTextAsync(runtime.ManifestPath, validManifest);
        marker = JsonNode.Parse(validManifest)!;
        marker["PythonExecutable"] = "../python.exe";
        await File.WriteAllTextAsync(runtime.ManifestPath, marker.ToJsonString());
        True(!DawDreamerRuntimeService.TryGetRuntime(runtime.PythonPath, out _), "The interpreter cannot be replaced with an arbitrary relative path.");
        await File.WriteAllTextAsync(runtime.ManifestPath, "{TEST ONLY damaged manifest");
        True(!DawDreamerRuntimeService.TryGetRuntime(runtime.PythonPath, out _), "A damaged completion record never prevents startup discovery from returning normally.");
        await File.WriteAllTextAsync(runtime.ManifestPath, new string(' ', 1024 * 1024 + 1));
        True(!DawDreamerRuntimeService.TryGetRuntime(runtime.PythonPath, out _), "Runtime records have a strict one-MiB read bound.");
        await File.WriteAllTextAsync(runtime.ManifestPath, validManifest);
        if (!OperatingSystem.IsWindows())
        {
            string link = Path.Combine(runtime.RootDirectory, "TEST-ONLY-native-link.pyd");
            File.CreateSymbolicLink(link, Path.Combine(runtime.RootDirectory, "TEST-ONLY-native.pyd"));
            marker = JsonNode.Parse(validManifest)!;
            marker["Files"]!["TEST-ONLY-native-link.pyd"] = new string('a', 64);
            await File.WriteAllTextAsync(runtime.ManifestPath, marker.ToJsonString());
            True(!DawDreamerRuntimeService.TryGetRuntime(runtime.PythonPath, out _), "A listed dependency cannot be a symbolic link.");
            File.Delete(link);
            await File.WriteAllTextAsync(runtime.ManifestPath, validManifest);
        }
        File.Delete(runtime.ManifestPath);
        True(!DawDreamerRuntimeService.TryGetRuntime(runtime.PythonPath, out _), "An incomplete environment without its final completion marker is never selected.");
        True(!EvaluateWindowsReadiness(plugin, []).CanAttemptRender, "A managed interpreter missing its completion record cannot enter rendering.");
    }

    private static DawDreamerRuntime WriteManagedDawDreamerFixture(string data, PluginPlatform platform = PluginPlatform.Windows,
        PluginArchitecture architecture = PluginArchitecture.X64)
    {
        string root = DawDreamerRuntimeService.GetRuntimeDirectory(data, platform, architecture);
        string relative = platform == PluginPlatform.Windows ? "python.exe" : "bin/python3.12";
        string python = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "TEST ONLY private interpreter metadata; never executed.");
        string native = Path.Combine(root, "TEST-ONLY-native.pyd");
        File.WriteAllText(native, "TEST ONLY native dependency metadata; never loaded.");
        string rid = platform == PluginPlatform.Windows ? "win-x64" : architecture == PluginArchitecture.Arm64 ? "osx-arm64" : "osx-x64";
        string manifest = Path.Combine(root, DawDreamerRuntimeService.ManifestName);
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, RuntimeKind = DawDreamerRuntimeService.RuntimeKind, Platform = rid, PythonExecutable = relative,
            PythonVersion = DawDreamerRuntimeService.PythonVersion, UvVersion = DawDreamerRuntimeService.UvVersion,
            Dependencies = new Dictionary<string, string> { ["dawdreamer"] = "0.8.3", ["numpy"] = "1.26.4",
                ["serum2-preset-loader"] = "0.1.3", ["cbor2"] = "5.6.5", ["zstandard"] = "0.23.0" },
            Files = new Dictionary<string, string> { [relative] = Fingerprints.HashFile(python), ["TEST-ONLY-native.pyd"] = Fingerprints.HashFile(native) }
        }, LibraryJson.Options));
        True(DawDreamerRuntimeService.TryGetRuntime(python, out var runtime), "TEST ONLY managed metadata fixture is complete.");
        return runtime!;
    }
}
