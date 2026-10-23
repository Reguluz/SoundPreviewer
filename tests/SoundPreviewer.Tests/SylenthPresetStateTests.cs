using System.Buffers.Binary;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static async Task TestSylenthPresetStateAsync()
    {
        byte[] target = new byte[1028];
        "1lys"u8.CopyTo(target);
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(12), 243);
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(16), 37);
        for (int index = 20; index < target.Length; index++) target[index] = (byte)(index * 31);
        foreach (int version in new[] { 2202, 2211 })
        {
            BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(4), version);
            byte[] actual = [.. target];
            BinaryPrimitives.WriteInt32LittleEndian(actual.AsSpan(4), 2200);
            True(SylenthPresetState.Matches(target, actual), "Only the observed single-program header rewrite is compatible.");
            for (int index = 0; index < target.Length; index++)
            {
                if (index is >= 4 and < 8) continue;
                actual[index] ^= 1;
                True(!SylenthPresetState.Matches(target, actual), "Every other byte, including name, slot and parameter data, must remain exact: " + index);
                actual[index] ^= 1;
            }
            True(!SylenthPresetState.Matches(target, actual[..^1]), "A truncated state cannot produce proof.");
            BinaryPrimitives.WriteInt32LittleEndian(actual.AsSpan(4), 2201);
            True(!SylenthPresetState.Matches(target, actual), "Unobserved output formats are not a metadata exception.");
            BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(4), 9999);
            BinaryPrimitives.WriteInt32LittleEndian(actual.AsSpan(4), 2200);
            True(!SylenthPresetState.Matches(target, actual), "Future source formats are not implicitly accepted.");
        }
        var proofPreset = new PresetRecord { Synth = SynthKind.Sylenth1, SourcePath = "TEST-ONLY-proof.fxp", ContentHash = new string('a', 64) };
        var proofTemplate = new PreviewTemplate();
        var legacy = new RenderResult { Success = true, AdapterVersion = "vstnet-2.1.0-sylenth-x86-v2",
            PresetLoadProof = new PresetLoadProof { Scheme = "vst2-program-state-roundtrip-v1", SourceSha256 = proofPreset.ContentHash, Pitches = proofTemplate.Pitches.ToList() } };
        True(PresetLoadVerification.IsValid(legacy, proofPreset, proofTemplate), "Previously verified adapter/proof pairs remain understood.");
        legacy.AdapterVersion = PresetLoadVerification.NativeSylenthAdapterVersion;
        True(!PresetLoadVerification.IsValid(legacy, proofPreset, proofTemplate), "New adapters cannot claim the old stricter proof scheme.");
        legacy.PresetLoadProof.Scheme = PresetLoadVerification.Scheme;
        True(PresetLoadVerification.IsValid(legacy, proofPreset, proofTemplate), "The new adapter uses its separately versioned state proof.");
        legacy.PresetLoadProof.Scheme = PresetLoadVerification.CanonicalScheme;
        True(!PresetLoadVerification.IsValid(legacy, proofPreset, proofTemplate), "Canonical verification requires the normalized state identity.");
        legacy.PresetLoadProof.CanonicalStateSha256 = new string('b', 64);
        True(PresetLoadVerification.IsValid(legacy, proofPreset, proofTemplate), "Known legacy conversions retain source, canonical state and all three pitch identities.");
        legacy.AdapterVersion = "vstnet-2.1.0-sylenth-x86-v3";
        True(!PresetLoadVerification.IsValid(legacy, proofPreset, proofTemplate), "Old adapters cannot claim the new canonical verification scheme.");
        legacy.PresetLoadProof.Scheme = PresetLoadVerification.Scheme;
        True(PresetLoadVerification.IsValid(legacy, proofPreset, proofTemplate), "Previous v3 exact readbacks remain valid.");
        True(SylenthPresetState.LegacyProgramName(new byte[4096]) is null &&
            SylenthPresetState.LegacyProgramName(target) is null, "Unknown opaque chunks and modern states cannot enter legacy conversion.");
        var directory = Fixture("preset-permanent-failure");
        var store = new LibraryStore(Path.Combine(directory, "library.db"));
        store.Initialize();
        var request = Request("permanent-compatibility-failure", Path.Combine(directory, "never.wav"));
        request.Plugin.ExecutablePath = TestExecutable;
        var settings = new AppSettings { PreviewDirectory = directory, Template = request.Template, Plugins = [request.Plugin], MaxAttempts = 5 };
        var result = await new RenderQueue(store).RunAsync(settings, [request.Preset], TestExecutable, new InlineProgress(), CancellationToken.None);
        Equal(1, result.Failed, "A permanent state-format failure stays a failed result.");
        Equal(1, request.Preset.Attempts, "Retrying cannot fix a deterministic state-format mismatch within this batch.");
        True(!File.Exists(request.OutputPath), "An incompatible preset never publishes audio.");
    }
}
