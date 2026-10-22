using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;
using SoundPreviewer.Worker;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static async Task TestVitalBankEmbeddedPreviewAsync()
    {
        string directory = Fixture("vital-bank-embedded"), bank = Path.Combine(directory, "TEST ONLY.vitalbank");
        const string internalPath = "TEST ONLY/Presets/Sound.vital";
        var source = new SourceFolder { Id = "TEST ONLY bank", Path = directory, Synth = SynthKind.Vital };
        var scanner = new ScanService();
        var document = EmbeddedVitalFixture();
        byte[] bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        Write(bytes);
        var initial = (await scanner.ScanAsync([source], [])).Presets.Single();
        Equal(PresetStatus.Pending, initial.Status, "Complete embedded resources make a bank entry previewable without global import.");
        Equal(Fingerprints.PresetId(SynthKind.Vital, bank, internalPath), initial.Id, "Enabling bank rendering preserves the historical entry ID.");
        string legacyHash = Fingerprints.HashText($"{Fingerprints.HashFile(bank)}|{internalPath}|{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}");
        Equal(legacyHash, initial.ContentHash, "The full bank and entry composite hash is unchanged.");
        True(VitalBankPresetSource.ReadVerifiedEntry(initial).SequenceEqual(bytes), "The native adapter receives exact embedded preset bytes, including PCM and wave data.");
        initial.Status = PresetStatus.Unsupported;
        initial.Favorite = true;
        initial.ManualTags = ["TEST ONLY saved label"];
        initial.SynthAssignedManually = true;
        var restored = (await scanner.ScanAsync([source], [initial])).Presets.Single();
        Equal(PresetStatus.Pending, restored.Status, "Rescanning recovers a historical unsupported self-contained bank entry.");
        True(restored.Favorite && restored.ManualTags!.Single() == "TEST ONLY saved label", "Rescanning retains personal metadata.");
        Equal(initial.Id, restored.Id, "Restoring preview support preserves the record identity.");
        Equal(initial.ContentHash, restored.ContentHash, "Resource validation does not change cache input hashes.");
        ScanService.AssignSynth(restored, SynthKind.Vital);
        Equal(PresetStatus.Pending, restored.Status, "Confirming the same synth does not reject an already validated embedded bank entry.");

        var invalid = CloneBankRecord(restored);
        invalid.InternalPath = "TEST ONLY/Presets/absent.vital";
        Throws<InvalidDataException>(() => VitalBankPresetSource.ReadVerifiedEntry(invalid));
        invalid.InternalPath = "../outside.vital";
        Throws<InvalidDataException>(() => VitalBankPresetSource.ReadVerifiedEntry(invalid));
        invalid.InternalPath = internalPath;
        invalid.ContentHash = new string('0', 64);
        Throws<InvalidDataException>(() => VitalBankPresetSource.ReadVerifiedEntry(invalid));
        Write(bytes, resource: "TEST ONLY changed unrelated bank resource");
        Throws<InvalidDataException>(() => VitalBankPresetSource.ReadVerifiedEntry(restored));
        Write(bytes);

        var wave = document["settings"]!["wavetables"]![0]!["groups"]![0]!["components"]![0]!;
        wave["type"] = "TEST ONLY unknown external source";
        Write(Encoding.UTF8.GetBytes(document.ToJsonString()));
        var unknown = (await scanner.ScanAsync([source], [])).Presets.Single();
        Equal(PresetStatus.Unsupported, unknown.Status, "Unknown resource types cannot silently use the initial wavetable.");
        True(unknown.Message.Contains("无法预览") && unknown.Message.Contains("导出") && unknown.Message.Contains("独立 .vital"),
            "Unavailable bank entries explain a concrete export-and-rescan action.");
        wave["type"] = "Audio File Source";
        wave["audio_file"] = "TEST ONLY/external-wave.wav";
        Write(Encoding.UTF8.GetBytes(document.ToJsonString()));
        Equal(PresetStatus.Unsupported, (await scanner.ScanAsync([source], [])).Presets.Single().Status,
            "External resource names are not treated as embedded PCM.");
        wave["audio_file"] = "AAA=";
        wave["normalize_gain"] = false;
        wave["window_size"] = 2048;
        Write(Encoding.UTF8.GetBytes(document.ToJsonString()));
        Equal(PresetStatus.Unsupported, (await scanner.ScanAsync([source], [])).Presets.Single().Status,
            "An incomplete audio buffer cannot pass as a complete audio wavetable.");
        wave["type"] = "Line Source";
        wave["keyframes"] = new JsonArray(new JsonObject { ["position"] = 0,
            ["line"] = new JsonObject { ["num_points"] = 2, ["points"] = new JsonArray(), ["powers"] = new JsonArray() } });
        Write(Encoding.UTF8.GetBytes(document.ToJsonString()));
        Equal(PresetStatus.Unsupported, (await scanner.ScanAsync([source], [])).Presets.Single().Status,
            "Empty or mismatched procedural line buffers cannot silently become the default waveform.");
        document = EmbeddedVitalFixture();
        document["settings"]!["sample"]!["samples"] = "AA==";
        Write(Encoding.UTF8.GetBytes(document.ToJsonString()));
        Equal(PresetStatus.Unsupported, (await scanner.ScanAsync([source], [])).Presets.Single().Status,
            "Truncated embedded samples are rejected instead of reading out of bounds in the plugin.");

        Write(new byte[VitalBankPresetSource.MaximumPresetBytes + 1]);
        Throws<InvalidDataException>(() => VitalBankPresetSource.ReadVerifiedEntry(restored));
        Write(bytes);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Throws<OperationCanceledException>(() => VitalBankPresetSource.ReadVerifiedEntry(restored, canceled.Token));
        string output = Path.Combine(directory, "TEST ONLY output.wav");
        var request = new RenderRequest
        {
            Preset = restored, OutputPath = output, Template = new PreviewTemplate(),
            Plugin = new PluginConfiguration { Synth = SynthKind.Vital, Backend = RenderBackend.VitalCli, ExecutablePath = TestExecutable, Version = "1.6.4" }
        };
        await ThrowsAsync<OperationCanceledException>(() => RenderDispatcher.RenderAsync(request, canceled.Token));
        True(!File.Exists(output) && !Directory.EnumerateDirectories(directory, "quick-*").Any() &&
            !Directory.EnumerateDirectories(directory, "vital-*").Any(), "Cancellation leaves no preview output or private extraction directories.");
        True(!File.Exists(Path.Combine(directory, "Sound.vital")), "Reading a bank never exports an archive path to the source directory.");

        void Write(byte[] preset, string resource = "TEST ONLY bank resource")
        {
            using var file = File.Create(bank);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            using (var entry = archive.CreateEntry(internalPath).Open()) entry.Write(preset);
            using var extra = new StreamWriter(archive.CreateEntry("TEST ONLY/Wavetables/metadata.txt").Open());
            extra.Write(resource);
        }
    }

    private static PresetRecord CloneBankRecord(PresetRecord value) =>
        JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(value, LibraryJson.Options), LibraryJson.Options)!;

    private static JsonObject EmbeddedVitalFixture()
    {
        var tables = new JsonArray();
        for (int i = 0; i < 3; i++)
            tables.Add(new JsonObject
            {
                ["version"] = "1.5.3", ["name"] = "TEST ONLY embedded wave",
                ["groups"] = new JsonArray(new JsonObject
                {
                    ["components"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "Wave Source", ["interpolation"] = 1, ["interpolation_style"] = 1,
                        ["keyframes"] = new JsonArray(new JsonObject { ["position"] = 0,
                            ["wave_data"] = Convert.ToBase64String(new byte[2048 * sizeof(float)]) })
                    })
                })
            });
        return new JsonObject
        {
            ["preset_name"] = "TEST ONLY embedded bank fixture", ["synth_version"] = "1.5.3",
            ["settings"] = new JsonObject { ["wavetables"] = tables,
                ["sample"] = new JsonObject { ["length"] = 2, ["sample_rate"] = 44100, ["samples"] = "AAAAAA==" } }
        };
    }
}
