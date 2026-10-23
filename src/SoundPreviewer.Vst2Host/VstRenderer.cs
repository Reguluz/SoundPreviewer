using System.Text;
using System.Security.Cryptography;
using Jacobi.Vst.Core;
using Jacobi.Vst.Host.Interop;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Vst2Host;

internal static class VstRenderer
{
    public const string AdapterVersion = PresetLoadVerification.NativeSylenthAdapterVersion;

    public static void CheckBufferInterop()
    {
        using var manager = new VstAudioBufferManager(2, HostCallbacks.BlockSize);
        var buffers = manager.Buffers.ToArray();
        buffers[0][0] = 0.25f;
        if (buffers.Length != 2 || buffers[0][0] != 0.25f) throw new InvalidOperationException("VST.NET buffer interop failed.");
    }

    public static RenderResult Render(RenderRequest request, FxpPreset? preset)
    {
        int rate = request.Template.SampleRate;
        var plan = RenderFramePlan.Create(request.Template, request.MaxOutputBytes);
        int hold = plan.Hold;
        int tail = plan.Tail;
        int frames = plan.Frames;
        string output = Path.GetFullPath(request.OutputPath);
        if ((!string.IsNullOrWhiteSpace(request.Preset.SourcePath) &&
            string.Equals(output, Path.GetFullPath(request.Preset.SourcePath), StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(output, Path.GetFullPath(request.Plugin.PluginPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("输出路径不能覆盖源预制或插件。");
        if (!string.IsNullOrWhiteSpace(request.Plugin.PluginHash) &&
            !string.Equals(Fingerprints.HashFile(request.Plugin.PluginPath), request.Plugin.PluginHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件 SHA256 与配置不一致，拒绝加载。");
        var segments = new List<WaveData>();
        var notes = new List<NotePreview>();
        var names = new List<string>();
        byte[]? canonical = preset?.Chunk is { } chunk && SylenthPresetState.LegacyProgramName(chunk) is not null
            ? CanonicalizeLegacyPreset(request, preset) : null;
        bool allPitchesVerified = preset is not null && !request.IsInitializationBaseline;
        foreach (int pitch in request.Template.Pitches)
        {
            Console.Error.WriteLine($"加载 x86 Sylenth1，演奏 MIDI {pitch}");
            // Each note gets a fresh instance so release/FX state cannot leak from another range.
            var callbacks = new HostCallbacks(rate, request.Template.Bpm, Path.GetDirectoryName(request.Plugin.PluginPath)!);
            using var context = VstPluginContext.Create(Path.GetFullPath(request.Plugin.PluginPath), callbacks);
            callbacks.PluginContext = context;
            var commands = context.PluginCommandStub.Commands;
            commands.Open();
            if (context.PluginInfo.PluginID != (preset?.PluginId ?? 0x73796c31))
                throw new InvalidDataException("实际插件 ID 与 Sylenth1 / FXP 身份不匹配，拒绝生成。");
            if ((context.PluginInfo.Flags & VstPluginFlags.CanReplacing) == 0 || context.PluginInfo.AudioOutputCount is < 1 or > 2 || context.PluginInfo.AudioInputCount is < 0 or > 2)
                throw new NotSupportedException("插件不支持预期的单声道/立体声 float32 处理。");
            commands.SetSampleRate(rate);
            commands.SetBlockSize(HostCallbacks.BlockSize);
            if (preset is not null) allPitchesVerified &= LoadPreset(context, preset, canonical);
            else if (!request.IsInitializationBaseline) throw new InvalidDataException("普通渲染必须明确加载 FXP。");
            names.Add(commands.GetProgramName());
            using var inputs = new VstAudioBufferManager(context.PluginInfo.AudioInputCount, HostCallbacks.BlockSize);
            using var outputs = new VstAudioBufferManager(context.PluginInfo.AudioOutputCount, HostCallbacks.BlockSize);
            var inputBuffers = inputs.Buffers.ToArray();
            var outputBuffers = outputs.Buffers.ToArray();
            var samples = new float[checked(frames * 2)];
            commands.MainsChanged(true);
            try
            {
                commands.StartProcess();
                for (int position = 0; position < frames; position += HostCallbacks.BlockSize)
                {
                    callbacks.SamplePosition = position;
                    foreach (var buffer in inputBuffers) buffer.AsSpan().Clear();
                    foreach (var buffer in outputBuffers) buffer.AsSpan().Clear();
                    var events = new List<VstEvent>(2);
                    if (position == 0) events.Add(Midi(0, 0x90, pitch, request.Template.Velocity));
                    if (plan.NoteOffOffset(position) is int noteOff) events.Add(Midi(noteOff, 0x80, pitch, 0));
                    if (events.Count > 0) commands.ProcessEvents(events.ToArray());
                    commands.ProcessReplacing(inputBuffers, outputBuffers);
                    int count = Math.Min(HostCallbacks.BlockSize, frames - position);
                    for (int frame = 0; frame < count; frame++)
                    {
                        samples[(position + frame) * 2] = outputBuffers[0][frame];
                        samples[(position + frame) * 2 + 1] = outputBuffers[outputBuffers.Length == 1 ? 0 : 1][frame];
                    }
                }
            }
            finally
            {
                commands.StopProcess();
                commands.MainsChanged(false);
            }
            var audio = new WaveData { SampleRate = rate, Channels = 2, Samples = samples };
            var metrics = WaveFile.Analyze(audio);
            if (metrics.HasNonFinite) throw new InvalidDataException("插件输出包含非有限数值，未写入预览。");
            notes.Add(new NotePreview
            {
                MidiNote = pitch, StartSeconds = segments.Count * frames / (double)rate, DurationSeconds = frames / (double)rate,
                ActualHoldSeconds = hold / (double)rate, ActualTailSeconds = tail / (double)rate, ActualVelocity = request.Template.Velocity,
                ActualNormalizedVelocity = request.Template.Velocity / 127.0, Metrics = metrics
            });
            segments.Add(audio);
        }
        var combined = WaveFile.Concatenate(segments);
        var combinedMetrics = WaveFile.Analyze(combined);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        WriteFloat32(output, combined);
        return new RenderResult
        {
            Success = true, OutputPath = output, Metrics = combinedMetrics, Notes = notes, AdapterVersion = AdapterVersion, NeedsReview = true,
            PresetLoadProof = allPitchesVerified ? new PresetLoadProof
            {
                Scheme = canonical is null ? PresetLoadVerification.Scheme : PresetLoadVerification.CanonicalScheme,
                SourceSha256 = request.Preset.ContentHash,
                CanonicalStateSha256 = canonical is null ? "" : Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant(),
                Pitches = request.Template.Pitches.ToList()
            } : null,
            Message = (request.IsInitializationBaseline
                ? "默认启动基准：每音使用全新 Sylenth1 实例，未加载预制或调用 SetChunk。插件报告音色："
                : canonical is null ? "已通过独立 x86 VST.NET 宿主加载 Sylenth1 FXP 并生成真实输出；需试听确认音色及资源。插件报告音色："
                : "旧格式 FXP 已通过独立实例转换、名称核对及逐音区完整状态回读；需与 DAW 试听确认。插件报告音色：") + string.Join(" / ", names.Distinct())
        };
    }

    private static VstMidiEvent Midi(int offset, int status, int pitch, int velocity) =>
        new(offset, 0, 0, [(byte)status, (byte)pitch, (byte)velocity, 0], 0, 0);

    private static byte[] CanonicalizeLegacyPreset(RenderRequest request, FxpPreset preset)
    {
        var callbacks = new HostCallbacks(request.Template.SampleRate, request.Template.Bpm, Path.GetDirectoryName(request.Plugin.PluginPath)!);
        using var context = VstPluginContext.Create(Path.GetFullPath(request.Plugin.PluginPath), callbacks);
        callbacks.PluginContext = context;
        var commands = context.PluginCommandStub.Commands;
        commands.Open();
        if (context.PluginInfo.PluginID != preset.PluginId || (context.PluginInfo.Flags & VstPluginFlags.ProgramChunks) == 0)
            throw new PresetStateCompatibilityException("旧格式转换的实际插件身份或状态接口不匹配。");
        commands.SetSampleRate(request.Template.SampleRate);
        commands.SetBlockSize(HostCallbacks.BlockSize);
        byte[] before;
        int consumed;
        commands.BeginSetProgram();
        try
        {
            commands.SetProgram(0);
            before = commands.GetChunk(true) ?? [];
            consumed = commands.SetChunk(preset.Chunk!, true);
        }
        finally { commands.EndSetProgram(); }
        byte[] canonical = commands.GetChunk(true) ?? [];
        if (consumed < 0 || before.AsSpan().SequenceEqual(canonical) || !SylenthPresetState.IsLegacyReadback(preset.Chunk!, canonical))
            throw new PresetStateCompatibilityException("旧格式 FXP 转换未通过独立实例的状态变化与完整名称核验，拒绝保存默认音色。");
        return canonical;
    }

    private static bool LoadPreset(VstPluginContext context, FxpPreset preset, byte[]? canonical)
    {
        var commands = context.PluginCommandStub.Commands;
        commands.BeginSetProgram();
        try
        {
            commands.SetProgram(preset.ProgramIndex(context.PluginInfo.ProgramCount));
            if (preset.Chunk is not null)
            {
                if ((context.PluginInfo.Flags & VstPluginFlags.ProgramChunks) == 0) throw new NotSupportedException("插件未声明 chunk 预制支持。");
                byte[] before = commands.GetChunk(true) ?? [];
                int consumed = commands.SetChunk(preset.Chunk, true);
                byte[] after = commands.GetChunk(true) ?? [];
                ValidateChunkLoaded(consumed, before, after, canonical ?? preset.Chunk);
            }
            else
            {
                if (preset.Parameters!.Length != context.PluginInfo.ParameterCount) throw new InvalidDataException("FXP 参数数量与实际插件不匹配。");
                for (int i = 0; i < preset.Parameters.Length; i++) commands.SetParameter(i, preset.Parameters[i]);
                commands.SetProgramName(preset.ProgramName);
            }
        }
        finally { commands.EndSetProgram(); }
        // Verify after EndSetProgram, against the full state actually used for this note.
        if (preset.Chunk is not null)
        {
            byte[] actual = commands.GetChunk(true) ?? [];
            if (!SylenthPresetState.Matches(canonical ?? preset.Chunk, actual))
                throw new PresetStateCompatibilityException("目标预制加载核验失败：" + SylenthPresetState.DescribeMismatch(canonical ?? preset.Chunk, actual));
            return true;
        }
        if (!preset.Parameters!.Select((value, index) => commands.GetParameter(index) is var actual &&
            float.IsFinite(actual) && Math.Abs(actual - value) <= 0.000001f).All(value => value))
            throw new PresetStateCompatibilityException("目标预制加载核验失败：插件回读参数与源 FXP 不一致，请使用匹配版本的 Sylenth1 重新导出独立 FXP。");
        return true;
    }

    internal static void ValidateChunkLoaded(int consumed, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, ReadOnlySpan<byte> target)
    {
        if (consumed < 0) throw new PresetStateCompatibilityException("插件拒绝载入 FXP。" + SylenthPresetState.DescribeMismatch(target, after));
        // Factory presets may already occupy the selected bank slot. An unchanged
        // state is valid only when its complete readback matches the target.
        if (!before.IsEmpty && !after.IsEmpty && before.SequenceEqual(after) && !SylenthPresetState.Matches(target, after))
            throw new PresetStateCompatibilityException("SetChunk 后音色状态完全未变，拒绝把默认状态当成目标音色。" +
                SylenthPresetState.DescribeMismatch(target, after));
        if (consumed == 0 && !SylenthPresetState.Matches(target, after) && (before.IsEmpty || after.IsEmpty))
            throw new InvalidDataException("插件未确认载入 FXP，也没有可核验的音色状态变化。");
    }

    private static void WriteFloat32(string path, WaveData audio)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        int bytes = checked(audio.Samples.Length * sizeof(float));
        writer.Write("RIFF"u8); writer.Write(bytes + 48); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((ushort)3); writer.Write((ushort)audio.Channels); writer.Write(audio.SampleRate);
        writer.Write(audio.SampleRate * audio.Channels * sizeof(float)); writer.Write((ushort)(audio.Channels * sizeof(float))); writer.Write((ushort)32);
        writer.Write("fact"u8); writer.Write(4); writer.Write(audio.Frames);
        writer.Write("data"u8); writer.Write(bytes);
        foreach (float sample in audio.Samples) writer.Write(sample);
        writer.Flush();
        stream.Flush(true);
    }
}
