using Jacobi.Vst.Core;
using Jacobi.Vst.Host.Interop;
using SoundPreviewer.Core.Audio;

namespace SoundPreviewer.Vst2Host;

internal static class NexusPresetState
{
    // Nexus 2's opaque state changes even on consecutive GetChunk calls. Do not
    // mask unknown bytes or claim an exact state proof from this readback.
    internal static bool HasSignature(ReadOnlySpan<byte> state) =>
        state.Length >= 32 && state[..16].SequenceEqual("NEXUS2 Preset\0\0\0"u8);

    internal static void Load(VstPluginContext context, FxpPreset preset)
    {
        var commands = context.PluginCommandStub.Commands;
        if (preset.PluginId != 0x4e455855 || context.PluginInfo.PluginID != preset.PluginId ||
            preset.Chunk is null || !NexusPresetFormat.IsSupported(preset.PluginVersion, preset.Chunk.Length, preset.Chunk) ||
            (context.PluginInfo.Flags & VstPluginFlags.ProgramChunks) == 0)
            throw new PresetStateCompatibilityException("Nexus 2 的预制身份或状态接口不匹配。");
        int consumed;
        commands.BeginSetProgram();
        try
        {
            commands.SetProgram(0);
            consumed = commands.SetChunk(preset.Chunk, true);
        }
        finally { commands.EndSetProgram(); }
        if (consumed != preset.Chunk.Length || !HasSignature(commands.GetChunk(true) ?? []) ||
            preset.ProgramName.TrimEnd('\0', ' ').Length > 0 &&
            !string.Equals(commands.GetProgramName().TrimEnd('\0', ' '), preset.ProgramName.TrimEnd('\0', ' '), StringComparison.Ordinal))
            throw new PresetStateCompatibilityException("Nexus 2 未能确认载入目标音色，或报告的音色名称不匹配；未保存预览。");
        for (int i = 0; i < context.PluginInfo.ParameterCount; i++)
            if (commands.GetParameter(i) is var value && (!float.IsFinite(value) || value is < 0 or > 1))
                throw new PresetStateCompatibilityException("Nexus 2 回报了无效音色参数，拒绝保存预览。");
    }
}
