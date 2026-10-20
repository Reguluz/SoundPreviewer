using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Worker;

public static partial class RenderDispatcher
{
    public const string SushiAdapterVersion = "sushi-serum1-fpch-win-x64-v1";
    private const long SushiMaximumPresetBytes = 64L * 1024 * 1024;
    private const int SushiBlockFrames = 64;

    private static async Task<RenderResult> RenderSushiAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        request.Template.Validate();
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new NotSupportedException("Sushi 原生适配器目前仅开放 Windows x64。");
        if (request.Preset.Synth != SynthKind.Serum1 || request.Plugin.Synth != SynthKind.Serum1)
            throw new NotSupportedException("Sushi 原生适配器目前仅支持 Serum 1 VST2 FPCh 预制。");
        if (request.Preset.BankSlot is not null || !string.IsNullOrEmpty(request.Preset.InternalPath))
            throw new NotSupportedException("Sushi 暂不支持银行音色或压缩包内预制，请先导出独立 FXP。");

        string executable = Path.GetFullPath(request.Plugin.ExecutablePath);
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到已配置的 Sushi 原生渲染程序。", executable);
        string plugin = ValidateSushiPlugin(request.Plugin.PluginPath);
        byte[]? preset = null;
        string? expectedProgram = null;
        if (!request.IsInitializationBaseline)
        {
            (preset, expectedProgram) = await ReadSushiPresetAsync(request.Preset, cancellationToken);
        }

        string output = Path.GetFullPath(request.OutputPath);
        string parent = Path.GetDirectoryName(output)!;
        string scratch = Path.Combine(parent, "s-" + Guid.NewGuid().ToString("N")[..8]);
        if (Directory.Exists(scratch)) throw new IOException("临时采集目录发生冲突，请重试。");
        Directory.CreateDirectory(scratch);
        try
        {
            string? presetCopy = null;
            if (preset is not null)
            {
                presetCopy = Path.Combine(scratch, "target.fxp");
                await File.WriteAllBytesAsync(presetCopy, preset, cancellationToken);
            }
            var segments = new List<WaveData>();
            for (int index = 0; index < request.Template.Pitches.Length; index++)
            {
                // A finite silent gap or MIDI all-sound-off cannot clear every
                // plugin effect's delayed echoes. Fresh instances isolate all pitches.
                segments.Add(await CaptureSushiAsync(request, executable, plugin, presetCopy, expectedProgram,
                    request.Template.Pitches[index], index, scratch, cancellationToken));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var combined = WaveFile.Concatenate(segments);
            if (checked(combined.Samples.LongLength * sizeof(float) + 56) > request.MaxOutputBytes)
                throw new InvalidDataException("Sushi 输出超出存储剩余额度，未提交音频。");
            var notes = new List<NotePreview>();
            int offset = 0;
            double actualHold = Math.Round(request.Template.HoldSeconds * combined.SampleRate) / combined.SampleRate;
            foreach (var (segment, index) in segments.Select((segment, index) => (segment, index)))
            {
                notes.Add(new NotePreview
                {
                    MidiNote = request.Template.Pitches[index], StartSeconds = offset / (double)combined.SampleRate,
                    DurationSeconds = segment.DurationSeconds, ActualHoldSeconds = actualHold,
                    ActualTailSeconds = segment.DurationSeconds - actualHold,
                    ActualVelocity = request.Template.Velocity,
                    ActualNormalizedVelocity = request.Template.Velocity / 127.0,
                    Metrics = WaveFile.Analyze(segment, cancellationToken)
                });
                offset += segment.Frames;
            }
            var result = new RenderResult
            {
                Success = true, OutputPath = output, Notes = notes,
                Metrics = WaveFile.Analyze(combined, cancellationToken),
                AdapterVersion = SushiAdapterVersion, NeedsReview = true,
                Message = request.IsInitializationBaseline
                    ? "已通过 Sushi 原生宿主分别新建三个 Serum 1 实例生成默认启动试听，未加载任何预制。"
                    : "已通过 Sushi 原生宿主分别新建三个 Serum 1 实例加载 FXP 并生成真实三音区试听，避免音区间延迟及尾音串入。插件状态会自行规范化，尚未形成逐音区状态一致性证明，须试听确认音色及外部资源。"
            };
            if (result.Metrics.HasNonFinite) throw new InvalidDataException("Sushi 输出包含非有限音频数值。");
            WorkerProcessClient.ValidateNotes(result, request.Template);
            string staged = Path.Combine(scratch, "complete.wav");
            WaveFile.WriteFloat32(staged, combined, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staged, output, overwrite: true);
            return result;
        }
        finally
        {
            // Only this GUID child of the requested output directory may be removed.
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(scratch)), parent, StringComparison.OrdinalIgnoreCase))
            {
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try { Directory.Delete(scratch, recursive: true); break; }
                    catch (DirectoryNotFoundException) { break; }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        if (attempt < 9) await Task.Delay(50, CancellationToken.None);
                    }
                }
            }
        }
    }

    private static string ValidateSushiPlugin(string path)
    {
        string plugin = Path.GetFullPath(path);
        if (!File.Exists(plugin)) throw new FileNotFoundException("找不到 Serum 1 VST2 乐器插件。", plugin);
        if (!Path.GetExtension(plugin).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Sushi 的 Serum 1 适配器需要 Windows VST2 乐器 DLL。");
        using var stream = File.OpenRead(plugin);
        if (stream.Length is < 64 or > 512L * 1024 * 1024)
            throw new InvalidDataException("插件大小无效或超出元数据读取限制。");
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader?.Magic != PEMagic.PE32Plus ||
            (pe.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) == 0)
            throw new InvalidDataException("Sushi 需要 x64 Windows VST2 插件，不能加载 x86 或其他架构。");
        var metadata = FileVersionInfo.GetVersionInfo(plugin);
        string file = CompactSushiName(Path.GetFileNameWithoutExtension(plugin));
        bool instrumentName = file is "serum" or "serumx64" or "serum64" or "serumvst2";
        bool product = (metadata.ProductName ?? "").Contains("Serum", StringComparison.OrdinalIgnoreCase) ||
            (metadata.FileDescription ?? "").Contains("Serum", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(metadata.OriginalFilename ?? ""), "Serum", StringComparison.OrdinalIgnoreCase);
        string identity = CompactSushiName((metadata.ProductName ?? "") + (metadata.FileDescription ?? ""));
        int major = metadata.FileMajorPart > 0 ? metadata.FileMajorPart : metadata.ProductMajorPart;
        if (!instrumentName || !product || major != 1 ||
            !(metadata.CompanyName ?? "").Contains("Xfer", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("serumfx", StringComparison.Ordinal) || identity.Contains("serumeffect", StringComparison.Ordinal))
            throw new InvalidDataException("插件文件名、厂商与版本元数据无法确认 Serum 1 乐器身份；禁止交给 Sushi 生成。");
        return plugin;
    }

    private static string CompactSushiName(string text) => text.Replace(" ", "", StringComparison.Ordinal)
        .Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

    private static async Task<(byte[] Bytes, string? Program)> ReadSushiPresetAsync(PresetRecord preset, CancellationToken cancellationToken)
    {
        if (!Path.GetExtension(preset.SourcePath).Equals(".fxp", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Sushi 的 Serum 1 适配器目前仅支持独立 FPCh FXP。");
        await using var stream = new FileStream(preset.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 60 or > SushiMaximumPresetBytes) throw new InvalidDataException("FXP 大小无效或超过 64 MiB 限制。");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (!bytes.AsSpan(0, 4).SequenceEqual("CcnK"u8) || !bytes.AsSpan(8, 4).SequenceEqual("FPCh"u8) ||
            !bytes.AsSpan(16, 4).SequenceEqual("XfsX"u8))
            throw new InvalidDataException("FXP 必须是带 XfsX 身份的 Serum 1 FPCh 单音色，不能使用其他音源或银行文件。");
        uint declared = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4));
        uint chunk = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(56, 4));
        if (((long)declared + 8 != bytes.Length && declared != bytes.Length) || chunk == 0 || (long)chunk + 60 != bytes.Length)
            throw new InvalidDataException("FXP 长度或状态块已截断，禁止把初始音色误记为目标预制。");
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(preset.ContentHash) && !hash.Equals(preset.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("源预制在扫描后发生变化，请重新扫描后再生成预览。");
        byte[] name = bytes.AsSpan(28, 28).ToArray();
        int terminator = Array.IndexOf(name, (byte)0);
        if (terminator >= 0) name = name[..terminator];
        string? program = name.Length > 0 && name.All(value => value is >= 32 and <= 126)
            ? Encoding.ASCII.GetString(name).Trim() : null;
        return (bytes, program);
    }

    private static async Task<WaveData> CaptureSushiAsync(RenderRequest request, string executable, string plugin,
        string? preset, string? expectedProgram, int pitch, int captureIndex, string scratch, CancellationToken cancellationToken)
    {
        var template = request.Template;
        int rate = template.SampleRate;
        int holdFrames = checked((int)Math.Round(template.HoldSeconds * rate));
        int tailFrames = checked((int)Math.Round(template.TailSeconds * rate));
        int clipFrames = checked(holdFrames + tailFrames);
        int warmupFrames = AlignSushiFrames((int)Math.Ceiling(0.15 * rate));
        int totalFrames = AlignSushiFrames(checked(warmupFrames + clipFrames + SushiBlockFrames));
        object[] events =
        [
            new { time = warmupFrames / (double)rate, type = "note_on",
                data = new { plugin_name = "synth", note = pitch, velocity = template.Velocity / 127.0 } },
            new { time = (warmupFrames + holdFrames) / (double)rate, type = "note_off",
                data = new { plugin_name = "synth", note = pitch, velocity = 0.0 } }
        ];
        if (checked(totalFrames * 2L * sizeof(float) + 56) > 256L * 1024 * 1024)
            throw new InvalidDataException("Sushi 原始采集超出 256 MiB 限制。");
        string captureDirectory = Path.Combine(scratch, "c" + captureIndex);
        Directory.CreateDirectory(captureDirectory);
        string input = Path.Combine(captureDirectory, "i.wav");
        string nativeOutput = Path.Combine(captureDirectory, "n.wav");
        string stateOutput = Path.Combine(captureDirectory, "b.bin");
        string configPath = Path.Combine(captureDirectory, "c.json");
        string logPath = Path.Combine(captureDirectory, "l.log");
        WaveFile.WriteFloat32(input, new WaveData { SampleRate = rate, Channels = 2, Samples = new float[checked(totalFrames * 2)] }, cancellationToken);
        var initial = new Dictionary<string, object?> { ["processor"] = "synth", ["state_binary_dump_file"] = SushiNativePath(stateOutput) };
        if (preset is not null) initial["state_fxp_file"] = SushiNativePath(preset);
        var config = new
        {
            host_config = new { samplerate = rate, tempo = template.Bpm, playing_mode = "playing", tempo_sync = "internal" },
            tracks = new[] { new { name = "main", channels = 2, inputs = Array.Empty<object>(),
                outputs = new[] { new { engine_bus = 0, track_bus = 0 } },
                plugins = new[] { new { path = SushiNativePath(plugin), name = "synth", type = "vst2x" } } } },
            initial_state = new[] { initial }, events
        };
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, LibraryJson.Options), cancellationToken);
        var outcome = await ProcessRunner.RunAsync(executable,
            ["-o", "-i", SushiNativePath(input), "-O", SushiNativePath(nativeOutput), "-c", SushiNativePath(configPath),
             "--no-osc", "--no-grpc", "--no-zmq", "--log-file", SushiNativePath(logPath), "--log-level", "info",
             "--log-file-size", "64", "--log-flush-interval", "0"],
            Path.GetDirectoryName(executable)!, TimeSpan.FromSeconds(Math.Clamp(request.Plugin.TimeoutSeconds, 5, 3600)), cancellationToken);
        string log = File.Exists(logPath) && new FileInfo(logPath).Length <= 1024 * 1024
            ? await File.ReadAllTextAsync(logPath, cancellationToken) : "";
        if (outcome.ExitCode != 0)
        {
            string detail = outcome.StandardError + " " + log;
            if (detail.Length > 4000) detail = detail[^4000..];
            throw new IOException($"Sushi 生成失败（{outcome.ExitCode} / 0x{unchecked((uint)outcome.ExitCode):X8}）：{detail.Trim()}");
        }
        const string applied = "Research state applied: processor=synth, program=";
        int programStart = log.IndexOf(applied, StringComparison.Ordinal);
        if (!File.Exists(stateOutput) || new FileInfo(stateOutput).Length is <= 0 or > SushiMaximumPresetBytes || programStart < 0)
            throw new InvalidDataException("Sushi 未返回有界插件状态或加载记录，请配置支持本工具状态接口的适配版本。");
        if (!string.IsNullOrEmpty(expectedProgram))
        {
            programStart += applied.Length;
            int programEnd = log.IndexOf(", input bytes=", programStart, StringComparison.Ordinal);
            if (programEnd < 0 || !log[programStart..programEnd].Trim().Equals(expectedProgram, StringComparison.Ordinal))
                throw new InvalidDataException("Sushi 加载后报告的程序名与 FXP 不一致，未提交预览。");
        }
        if (!File.Exists(nativeOutput) || new FileInfo(nativeOutput).Length > totalFrames * 8L + 4096)
            throw new InvalidDataException("Sushi 未生成音频或输出大小超出请求范围。");
        var audio = WaveFile.Read(nativeOutput, cancellationToken);
        if (audio.SampleRate != rate || audio.Channels != 2 || audio.Frames != totalFrames ||
            WaveFile.Analyze(audio, cancellationToken).HasNonFinite)
            throw new InvalidDataException("Sushi 输出采样率、声道、帧数或数值与采集请求不一致。");
        cancellationToken.ThrowIfCancellationRequested();
        return SliceSushiAudio(audio, warmupFrames, clipFrames);
    }

    private static int AlignSushiFrames(int frames) => checked((frames + SushiBlockFrames - 1) / SushiBlockFrames * SushiBlockFrames);

    private static string SushiNativePath(string path) => path.Length < 240 || path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    private static WaveData SliceSushiAudio(WaveData source, int startFrame, int frames) => new()
    {
        SampleRate = source.SampleRate, Channels = source.Channels,
        Samples = source.Samples.AsSpan(checked(startFrame * source.Channels), checked(frames * source.Channels)).ToArray()
    };
}
