using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Audio;

public static class PresetLoadVerification
{
    public const string Scheme = "vst2-program-state-roundtrip-v2";
    public const string CanonicalScheme = "vst2-legacy-canonical-state-roundtrip-v1";
    public const string NativeSylenthAdapterVersion = "vstnet-2.1.0-sylenth-x86-v4";
    private const string PreviousNativeAdapter = "vstnet-2.1.0-sylenth-x86-v3";
    private const string LegacyScheme = "vst2-program-state-roundtrip-v1";
    private const string LegacyNativeAdapter = "vstnet-2.1.0-sylenth-x86-v2";

    public static bool IsSupported(PluginConfiguration plugin) =>
        plugin.Synth == SynthKind.Sylenth1 && plugin.Backend == RenderBackend.ExternalWorker &&
        Path.GetFileName(plugin.ExecutablePath).Equals("SoundPreviewer.Vst2Host.exe", StringComparison.OrdinalIgnoreCase);

    public static bool IsKnownAdapter(string adapterVersion) =>
        adapterVersion == NativeSylenthAdapterVersion ||
        adapterVersion == Fingerprints.QuickPreviewPolicy + "/" + NativeSylenthAdapterVersion ||
        adapterVersion == PreviousNativeAdapter || adapterVersion == Fingerprints.QuickPreviewPolicy + "/" + PreviousNativeAdapter ||
        adapterVersion == LegacyNativeAdapter || adapterVersion == Fingerprints.QuickPreviewPolicy + "/" + LegacyNativeAdapter;

    public static bool IsValid(RenderResult result, PresetRecord preset, PreviewTemplate template)
    {
        var proof = result.PresetLoadProof;
        return result.Success && preset.Synth == SynthKind.Sylenth1 && preset.InternalPath.Length == 0 && preset.BankSlot is null &&
            Path.GetExtension(preset.SourcePath).Equals(".fxp", StringComparison.OrdinalIgnoreCase) &&
            preset.ContentHash.Length == 64 && preset.ContentHash.All(Uri.IsHexDigit) &&
            proof is not null && (proof.Scheme == (result.AdapterVersion.EndsWith("/" + LegacyNativeAdapter, StringComparison.Ordinal) ||
                result.AdapterVersion == LegacyNativeAdapter ? LegacyScheme : Scheme) ||
                proof.Scheme == CanonicalScheme &&
                (result.AdapterVersion == NativeSylenthAdapterVersion || result.AdapterVersion == Fingerprints.QuickPreviewPolicy + "/" + NativeSylenthAdapterVersion) &&
                proof.CanonicalStateSha256 is { Length: 64 } canonicalHash && canonicalHash.All(Uri.IsHexDigit)) &&
            string.Equals(proof.SourceSha256, preset.ContentHash, StringComparison.OrdinalIgnoreCase) &&
            proof.Pitches is not null && proof.Pitches.SequenceEqual(template.Pitches) &&
            IsKnownAdapter(result.AdapterVersion);
    }
}
