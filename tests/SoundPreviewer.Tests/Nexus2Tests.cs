using System.Buffers.Binary;
using System.Text.Json;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static async Task TestNexus2Async()
    {
        Equal(4, (int)SynthKind.Unknown, "Existing unknown records retain their identity.");
        Equal(5, (int)SynthKind.Spire, "Existing Spire records retain their identity.");
        Equal(6, (int)SynthKind.Nexus2, "Nexus 2 appends a new persisted value.");
        var settings = JsonSerializer.Deserialize<AppSettings>("""
            {"Plugins":[{"Synth":1,"Version":"preserved","PluginPath":"offline-serum"}],
             "Sources":[{"Synth":4,"AutoDetect":true,"Path":"offline-root"}]}
            """, LibraryJson.Options)!;
        Equal(6, settings.Plugins.Count, "Old settings acquire exactly one configuration per supported synth.");
        Equal("preserved", settings.Plugins.Single(p => p.Synth == SynthKind.Serum1).Version, "Migration preserves existing settings.");
        Equal(RenderBackend.Unconfigured, settings.Plugins.Single(p => p.Synth == SynthKind.Nexus2).Backend, "Adding a synth never fabricates an installed renderer.");

        string directory = Fixture("nexus2");
        string root = Path.Combine(directory, "Serum 2");
        Directory.CreateDirectory(root);
        void Preset(string name, byte major)
        {
            string path = Path.Combine(root, name + ".fxp");
            WriteFxp(path, 1, pluginId: "NEXU");
            byte[] bytes = [.. File.ReadAllBytes(path), .. new byte[32]];
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)bytes.Length - 8);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(56), 32);
            "NEXUS2 Preset\0\0\0"u8.CopyTo(bytes.AsSpan(60));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)major << 24);
            File.WriteAllBytes(path, bytes);
        }
        Preset("second-generation", 2);
        Preset("unconfirmed-generation", 3);
        File.WriteAllText(Path.Combine(root, "expansion.nxp"), "TEST ONLY: not an individual preset");
        for(int index=0;index<3;index++)
        {
            byte[] legacy=new byte[60+NexusPresetFormat.LegacyChunkBytes+(index==1?1:index==2?2:0)];
            "CcnK"u8.CopyTo(legacy); "FPCh"u8.CopyTo(legacy.AsSpan(8)); "NEXU"u8.CopyTo(legacy.AsSpan(16));
            BinaryPrimitives.WriteUInt32BigEndian(legacy.AsSpan(4), (uint)(52+NexusPresetFormat.LegacyChunkBytes));
            BinaryPrimitives.WriteUInt32BigEndian(legacy.AsSpan(20), index==0?0x01030800u:1u);
            BinaryPrimitives.WriteUInt32BigEndian(legacy.AsSpan(56), NexusPresetFormat.LegacyChunkBytes);
            legacy[^1]=0xcc;
            File.WriteAllBytes(Path.Combine(root,"legacy-"+index+".fxp"),legacy);
        }
        var source = new SourceFolder { Path = root, AutoDetect = true, Synth = SynthKind.Unknown };
        var scan = await new ScanService().ScanAsync([source], []);
        Equal(5, scan.Presets.Count, "Expansion containers are not interpreted as individual presets.");
        var nexus = scan.Presets.Single(p => p.Name == "second-generation");
        Equal(SynthKind.Nexus2, nexus.Synth, "Verified NEXU v2 header overrides an unrelated directory hint.");
        Equal(PresetStatus.Pending, nexus.Status, "An identified standalone FXP can be offered to its configured renderer.");
        Equal(SynthKind.Unknown, scan.Presets.Single(p => p.Name == "unconfirmed-generation").Synth, "A shared NEXU ID never implies that an unconfirmed generation is Nexus 2.");
        Equal(SynthKind.Nexus2,scan.Presets.Single(p=>p.Name=="legacy-0").Synth,"A known 1356-byte legacy Nexus state is compatible with Nexus 2.");
        Equal(SynthKind.Nexus2,scan.Presets.Single(p=>p.Name=="legacy-1").Synth,"The observed single extra byte is ignored without modifying the file.");
        Equal(SynthKind.Unknown,scan.Presets.Single(p=>p.Name=="legacy-2").Synth,"The compatibility exception cannot accept arbitrary trailing data.");
        nexus.Favorite = true; nexus.ManualTags = ["keep me"];
        var rescan = await new ScanService().ScanAsync([source], scan.Presets);
        var retained = rescan.Presets.Single(p => p.Id == nexus.Id);
        True(retained.Favorite && retained.ManualTags!.SequenceEqual(["keep me"]), "Rescanning retains user metadata.");

        string pluginPath = Path.Combine(directory, "Nexus.dll");
        WritePeFixture(pluginPath, x64: false, "VSTPluginMain");
        var discovered = await new PluginScanService().ScanAsync([directory]);
        Equal(SynthKind.Unknown, discovered.Plugins.Single().Synth, "A Nexus filename without reFX v2 metadata remains unconfirmed.");
        string host = Path.Combine(directory, "Vst2Host-x86", "SoundPreviewer.Vst2Host.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(host)!); File.WriteAllText(host, "TEST ONLY: selection fixture");
        var candidate = new DiscoveredPlugin
        {
            Synth = SynthKind.Nexus2, FilePath = pluginPath, BinaryPath = pluginPath, Version = "2.2.0.0",
            Platform = PluginPlatform.Windows, Architecture = PluginArchitecture.X86, Format = PluginBinaryFormat.Vst2
        };
        var configuration = settings.Plugins.Single(p => p.Synth == SynthKind.Nexus2);
        Equal(1, PluginSelectionService.ApplyAutomatic([candidate], [configuration], host, PluginPlatform.Windows, PluginArchitecture.X64), "Windows Nexus 2 x86 can select the bundled host.");
        Equal(RenderBackend.ExternalWorker, configuration.Backend, "Nexus 2 x86 uses an isolated native host.");
        True(RenderReadinessService.Evaluate(configuration, [candidate], PluginPlatform.Windows, PluginArchitecture.X64).CanAttemptRender, "A confirmed Nexus 2 x86 configuration passes readiness.");
        True(InitBaselineService.Supports(configuration), "Nexus 2 retains startup audio protection.");
        True(!PresetLoadVerification.IsSupported(configuration), "Opaque Nexus state must not inherit Sylenth's exact-state proof fallback.");
        var foreign = new DiscoveredPlugin { Synth = SynthKind.Sylenth1, FilePath = pluginPath, BinaryPath = pluginPath, Platform = PluginPlatform.Windows, Architecture = PluginArchitecture.X86, Format = PluginBinaryFormat.Vst2 };
        True(!RenderReadinessService.Evaluate(configuration, [foreign], PluginPlatform.Windows, PluginArchitecture.X64).CanAttemptRender, "A host path cannot override a mismatched plugin identity.");
        configuration.Backend = RenderBackend.DawDreamer; configuration.ExecutablePath = "python";
        True(!RenderReadinessService.Evaluate(configuration, [candidate], PluginPlatform.Windows, PluginArchitecture.X64).CanAttemptRender, "An unimplemented DawDreamer Nexus adapter is never advertised as ready.");
    }
}
