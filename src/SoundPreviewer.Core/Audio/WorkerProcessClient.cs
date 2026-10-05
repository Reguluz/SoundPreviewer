using System.Text.Json;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Audio;

public sealed class WorkerProcessClient
{
    public async Task<RenderResult> RenderAsync(RenderRequest request, string workerPath, CancellationToken cancellationToken)
    {
        request.Template.Validate();
        string finalPath = Path.GetFullPath(request.OutputPath);
        string parent = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, $".soundpreviewer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            string requestFile = Path.Combine(staging, "request.json");
            string resultFile = Path.Combine(staging, "result.json");
            string stagedAudio = Path.Combine(staging, "preview.wav");
            var stagedRequest = new RenderRequest
            {
                Preset = request.Preset, Plugin = request.Plugin, Template = request.Template,
                CacheKey = request.CacheKey, OutputPath = stagedAudio, MaxOutputBytes = request.MaxOutputBytes
            };
            await File.WriteAllTextAsync(requestFile, JsonSerializer.Serialize(stagedRequest, LibraryJson.Options), cancellationToken);
            string executable = Path.GetFullPath(workerPath);
            if (!File.Exists(executable)) throw new FileNotFoundException("缺少 SoundPreviewer.Worker 工作进程。", executable);
            var arguments = new List<string>();
            if (Path.GetExtension(executable).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                arguments.Add(executable);
                executable = "dotnet";
            }
            arguments.AddRange(["--request", requestFile, "--result", resultFile]);
            var outcome = await ProcessRunner.RunAsync(executable, arguments, staging,
                TimeSpan.FromSeconds(Math.Clamp(request.Plugin.TimeoutSeconds, 5, 3600)), cancellationToken);
            if (!File.Exists(resultFile))
                throw new IOException($"渲染进程未返回结果（退出码 {outcome.ExitCode}）：{outcome.StandardError}");
            if (new FileInfo(resultFile).Length > 1024 * 1024) throw new InvalidDataException("渲染结果文件超过限制。");
            var result = JsonSerializer.Deserialize<RenderResult>(await File.ReadAllTextAsync(resultFile, cancellationToken), LibraryJson.Options)
                ?? throw new InvalidDataException("渲染结果 JSON 无效。");
            if (!result.Success)
            {
                if (string.IsNullOrWhiteSpace(result.Message)) result.Message = "渲染工作进程返回失败，未提供原因。";
                return result;
            }
            if (outcome.ExitCode != 0) throw new IOException($"渲染进程异常退出（{outcome.ExitCode}）。");
            if (!string.Equals(Path.GetFullPath(result.OutputPath), stagedAudio, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("渲染进程返回的音频路径与请求不符。");
            if (new FileInfo(stagedAudio).Length > request.MaxOutputBytes)
                throw new InvalidDataException("预览输出超出存储剩余额度，未提交音频。");
            var audio = WaveFile.Read(stagedAudio);
            result.Metrics = WaveFile.Analyze(audio);
            if (result.Metrics.HasNonFinite) throw new InvalidDataException("音频包含 NaN / Infinity，未提交预览。");
            if (result.Metrics.SampleRate != request.Template.SampleRate)
                throw new InvalidDataException("音频采样率与试听模板不一致。");
            ValidateNotes(result, request.Template);
            foreach (var note in result.Notes)
            {
                int first = Math.Clamp((int)Math.Round(note.StartSeconds * audio.SampleRate), 0, audio.Frames - 1);
                int count = Math.Min(audio.Frames - first, (int)Math.Round(note.DurationSeconds * audio.SampleRate));
                note.Metrics = WaveFile.Analyze(new WaveData
                {
                    SampleRate = audio.SampleRate, Channels = audio.Channels,
                    Samples = audio.Samples.AsSpan(first * audio.Channels, count * audio.Channels).ToArray()
                });
                result.NeedsReview |= note.Metrics.IsSilent || note.Metrics.HasClipping;
            }
            result.NeedsReview |= result.Metrics.IsSilent || result.Metrics.HasClipping;
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagedAudio, finalPath, overwrite: true);
            result.OutputPath = finalPath;
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return new RenderResult { Message = error.Message };
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void ValidateNotes(RenderResult result, PreviewTemplate template)
    {
        if (result.Metrics is null || result.Notes is null || result.Notes.Count != 3 || result.Notes.Any(note => note is null))
            throw new InvalidDataException("结果须包含音频指标及三个音区的来源映射。");
        double previousEnd = 0;
        for (int i = 0; i < 3; i++)
        {
            var note = result.Notes[i];
            if (note.MidiNote != template.Pitches[i] || !double.IsFinite(note.StartSeconds) ||
                !double.IsFinite(note.DurationSeconds) || !double.IsFinite(note.ActualHoldSeconds) ||
                !double.IsFinite(note.ActualTailSeconds) || note.StartSeconds < 0 || note.StartSeconds < previousEnd - 0.01 ||
                note.DurationSeconds <= 0 || note.ActualHoldSeconds <= 0 || note.ActualTailSeconds < 0 ||
                note.ActualVelocity is < 1 or > 127 ||
                note.StartSeconds + note.DurationSeconds > result.Metrics.DurationSeconds + 0.01 ||
                Math.Abs(note.DurationSeconds - note.ActualHoldSeconds - note.ActualTailSeconds) > 0.02)
                throw new InvalidDataException("工作进程的音符映射无效或超出音频范围。");
            if (note.ActualNormalizedVelocity is double normalized && (!double.IsFinite(normalized) || normalized is <= 0 or > 1))
                throw new InvalidDataException("工作进程的 normalized 力度无效。");
            previousEnd = note.StartSeconds + note.DurationSeconds;
        }
        if (string.IsNullOrWhiteSpace(result.AdapterVersion)) throw new InvalidDataException("工作进程未标明适配器版本。");
    }
}
