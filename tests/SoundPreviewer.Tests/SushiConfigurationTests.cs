using System.Text.Json;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestSushiReadinessAsync()
    {
        Equal(0, (int)RenderBackend.Unconfigured, "Unconfigured backend value stays compatible.");
        Equal(1, (int)RenderBackend.VitalCli, "Vital backend value stays compatible.");
        Equal(2, (int)RenderBackend.ExternalWorker, "External worker value stays compatible.");
        Equal(3, (int)RenderBackend.DawDreamer, "DawDreamer value stays compatible.");
        Equal(4, (int)RenderBackend.Sushi, "Sushi uses a new numeric value.");
        var legacy = JsonSerializer.Deserialize<PluginConfiguration>("{\"Synth\":1,\"Backend\":3}", LibraryJson.Options)!;
        Equal(RenderBackend.DawDreamer, legacy.Backend, "Old numeric settings keep their renderer.");

        string directory = Fixture("sushi-readiness");
        string host = Path.Combine(directory, PluginSelectionService.SushiExecutableName);
        string dependency = Path.Combine(directory, "sndfile.dll");
        string plugin = Path.Combine(directory, "Serum_x64.dll");
        WritePeFixture(host, true);
        WritePeFixture(dependency, true);
        WritePeFixture(plugin, true, "VSTPluginMain");
        PluginConfiguration Configuration(SynthKind synth = SynthKind.Serum1) => new()
        {
            Synth = synth, Backend = RenderBackend.Sushi, ExecutablePath = host,
            PluginPath = plugin, Version = "TEST ONLY 1", TimeoutSeconds = 120
        };
        DiscoveredPlugin Discovery() => new()
        {
            Synth = SynthKind.Serum1, FilePath = plugin, BinaryPath = plugin,
            Platform = PluginPlatform.Windows, Architecture = PluginArchitecture.X64, Format = PluginBinaryFormat.Vst2
        };
        RenderReadiness Check(PluginConfiguration configuration, IReadOnlyList<DiscoveredPlugin>? plugins = null,
            PluginPlatform platform = PluginPlatform.Windows, PluginArchitecture architecture = PluginArchitecture.X64) =>
            RenderReadinessService.Evaluate(configuration, plugins ?? new[] { Discovery() }, platform, architecture);

        var configuration = Configuration();
        configuration.ExecutableHash = Fingerprints.RendererExecutableHash(configuration);
        string nativeKey = Fingerprints.CacheKey(new PresetRecord { Synth = SynthKind.Serum1, ContentHash = "TEST ONLY CONTENT" }, configuration, new PreviewTemplate());
        File.AppendAllText(dependency, "TEST ONLY sidecar revision");
        configuration.ExecutableHash = Fingerprints.RendererExecutableHash(configuration);
        NotEqual(nativeKey, Fingerprints.CacheKey(new PresetRecord { Synth = SynthKind.Serum1, ContentHash = "TEST ONLY CONTENT" }, configuration, new PreviewTemplate()),
            "Only changing the native audio sidecar invalidates new Sushi cache identity.");
        configuration.Backend = RenderBackend.DawDreamer;
        Equal(Fingerprints.HashFile(host), Fingerprints.RendererExecutableHash(configuration), "Existing backends preserve their exact executable fingerprint.");
        configuration.Backend = RenderBackend.Sushi;
        True(PluginSelectionService.IsSushiHostAvailable(host), "A complete PE64 host is available.");
        True(Check(configuration).CanAttemptRender, "Serum 1 x64 VST2 can use the native host.");
        True(!Check(configuration).IsVerified, "Configuration does not claim audio or preset verification.");
        True(!Check(configuration, platform: PluginPlatform.MacOS).CanAttemptRender, "macOS retains its existing renderers.");
        True(!Check(configuration, architecture: PluginArchitecture.X86).CanAttemptRender, "The native host requires an x64 application.");
        True(!Check(configuration, architecture: PluginArchitecture.Arm64).CanAttemptRender, "ARM is not silently treated as x64.");
        True(!Check(Configuration(SynthKind.Serum2)).CanAttemptRender, "Serum 2 is not claimed as adapted.");
        True(!Check(Configuration(SynthKind.Sylenth1)).CanAttemptRender, "Sushi does not replace the verified x86 adapter.");
        True(!Check(configuration, Array.Empty<DiscoveredPlugin>()).CanAttemptRender, "A binary needs scanned identity metadata.");
        var wrongFormat = Discovery(); wrongFormat.Format = PluginBinaryFormat.Vst3;
        True(!Check(configuration, new[] { wrongFormat }).CanAttemptRender, "VST3 is not accepted as VST2.");
        var wrongArchitecture = Discovery(); wrongArchitecture.Architecture = PluginArchitecture.X86;
        True(!Check(configuration, new[] { wrongArchitecture }).CanAttemptRender, "Scanned x86 metadata is rejected.");
        var missingBinary = Discovery(); missingBinary.BinaryPath = Path.Combine(directory, "missing.dll");
        True(!Check(configuration, new[] { missingBinary }).CanAttemptRender, "A missing scanned binary cannot render.");

        configuration.ExecutablePath = plugin;
        True(!Check(configuration).CanAttemptRender, "A plugin is not a Sushi executable.");
        configuration.ExecutablePath = Path.Combine(directory, "missing", PluginSelectionService.SushiExecutableName);
        True(!Check(configuration).CanAttemptRender, "A missing native host cannot render.");
        configuration.ExecutablePath = host;
        File.Delete(dependency);
        True(!Check(configuration).CanAttemptRender, "The audio sidecar is required.");
        WritePeFixture(dependency, false);
        True(!Check(configuration).CanAttemptRender, "An x86 sidecar cannot satisfy the x64 native host.");
        WritePeFixture(dependency, true);
        WritePeFixture(host, false);
        True(!Check(configuration).CanAttemptRender, "The executable must actually be PE64.");
        WritePeFixture(host, true);
        WritePeFixture(plugin, false, "VSTPluginMain");
        True(!Check(configuration).CanAttemptRender, "Scanned metadata cannot hide an actual x86 plugin.");
        File.WriteAllText(plugin, "TEST ONLY malformed executable");
        True(!Check(configuration).CanAttemptRender, "A malformed PE is rejected without executing it.");
        WritePeFixture(plugin, true, "VSTPluginMain");
        configuration.Version = "";
        True(!Check(configuration).CanAttemptRender, "A version remains required for cache identity.");
        configuration.Version = "TEST ONLY 1";
        configuration.PluginPath = Path.Combine(directory, "missing-plugin.dll");
        True(!Check(configuration).CanAttemptRender, "A missing configured plugin cannot render.");
        RenderReadinessService.ValidateSettings(new[] { new PluginConfiguration { Synth = SynthKind.Serum1, Backend = RenderBackend.Sushi } });

        configuration = Configuration();
        bool supportsNative = PluginSelectionService.CurrentPlatform == PluginPlatform.Windows &&
            PluginSelectionService.CurrentArchitecture == PluginArchitecture.X64;
        Equal(supportsNative, InitBaselineService.Supports(configuration), "Native Serum supports audio baselines only on Windows x64.");
        True(!InitBaselineService.Supports(Configuration(SynthKind.Serum2)), "Serum 2 does not gain a native baseline claim.");
        True(!PresetLoadVerification.IsSupported(configuration), "Sushi does not inherit Sylenth's exact-state proof.");
        return Task.CompletedTask;
    }

    private static Task TestSushiPortableSelectionAsync()
    {
        string oldDirectory = Path.Combine(Fixture("sushi-portable-old"), "Renderers", "Sushi");
        string newDirectory = Path.Combine(Fixture("sushi-portable-new"), "Renderers", "Sushi");
        Directory.CreateDirectory(newDirectory);
        string oldHost = Path.Combine(oldDirectory, PluginSelectionService.SushiExecutableName);
        string newHost = Path.Combine(newDirectory, PluginSelectionService.SushiExecutableName);
        WritePeFixture(newHost, true);
        WritePeFixture(Path.Combine(newDirectory, "sndfile.dll"), true);
        PluginConfiguration Configuration(RenderBackend backend, string path, SynthKind synth = SynthKind.Serum1) => new()
        {
            Synth = synth, Backend = backend, ExecutablePath = path, PluginPath = "preserved-plugin.dll",
            Version = "preserved-version", ExecutableHash = "old-host-hash", PluginHash = "preserved-plugin-hash",
            InitBaselinePath = "old-baseline"
        };
        var native = Configuration(RenderBackend.Sushi, oldHost);
        var dd = Configuration(RenderBackend.DawDreamer, oldHost);
        var external = Configuration(RenderBackend.ExternalWorker, oldHost);
        var unconfigured = Configuration(RenderBackend.Unconfigured, oldHost);
        var custom = Configuration(RenderBackend.Sushi, Path.Combine(Fixture("sushi-user-host"), "sushi.exe"));
        var otherSynth = Configuration(RenderBackend.Sushi, oldHost, SynthKind.Serum2);
        var configurations = new[] { native, dd, external, unconfigured, custom, otherSynth };
        Equal(0, PluginSelectionService.RefreshBundledSushiHost(configurations, newHost, PluginPlatform.MacOS, PluginArchitecture.X64),
            "Portable repair does not alter macOS settings.");
        Equal(0, PluginSelectionService.RefreshBundledSushiHost(configurations, newHost, PluginPlatform.Windows, PluginArchitecture.Arm64),
            "Portable repair does not alter ARM settings.");
        File.Delete(Path.Combine(newDirectory, "sndfile.dll"));
        Equal(0, PluginSelectionService.RefreshBundledSushiHost(configurations, newHost, PluginPlatform.Windows, PluginArchitecture.X64),
            "Incomplete bundled assets do not replace saved settings.");
        WritePeFixture(Path.Combine(newDirectory, "sndfile.dll"), true);
        Equal(1, PluginSelectionService.RefreshBundledSushiHost(configurations, newHost, PluginPlatform.Windows, PluginArchitecture.X64),
            "Only a previously selected bundled Sushi host is relocated.");
        Equal(Path.GetFullPath(newHost), native.ExecutablePath, "Native selection follows the portable application.");
        Equal("", native.ExecutableHash, "A moved host is rehashed.");
        Equal("", native.InitBaselinePath, "A moved host is checked against its own baseline identity.");
        Equal("preserved-plugin.dll", native.PluginPath, "Plugin choice is preserved.");
        Equal("preserved-version", native.Version, "Plugin version is preserved.");
        Equal("preserved-plugin-hash", native.PluginHash, "Plugin fingerprint is preserved.");
        foreach (var preserved in configurations.Skip(1))
        {
            Equal("old-host-hash", preserved.ExecutableHash, "Other renderer choices are untouched.");
            Equal("old-baseline", preserved.InitBaselinePath, "Other renderer baselines are untouched.");
        }
        Equal(oldHost, dd.ExecutablePath, "DawDreamer is never automatically switched to Sushi.");
        Equal(oldHost, external.ExecutablePath, "External hosts are never automatically switched to Sushi.");
        native.ExecutableHash = "current-hash"; native.InitBaselinePath = "current-baseline";
        Equal(0, PluginSelectionService.RefreshBundledSushiHost(configurations, newHost, PluginPlatform.Windows, PluginArchitecture.X64),
            "An unchanged portable path needs no rewrite.");
        Equal("current-hash", native.ExecutableHash, "Unchanged native settings keep their fingerprint.");
        Equal("current-baseline", native.InitBaselinePath, "Unchanged native settings keep their baseline.");
        return Task.CompletedTask;
    }
}
