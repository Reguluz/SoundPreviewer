using System.Text.Json;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Audio;

public sealed class WorkerProcessClient
{
    public Task<RenderResult> RenderAsync(RenderRequest request, string workerPath, CancellationToken cancellationToken) =>
        RenderAsync(request, workerPath, cancellationToken, null);

    public async Task<RenderResult> RenderAsync(RenderRequest request, string workerPath, CancellationToken cancellationToken,
        Func<RenderResult, WaveData, CancellationToken, Task>? beforeCommit, Action<int>? processStarted = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                IsInitializationBaseline = request.IsInitializationBaseline,
                CacheKey = request.CacheKey, OutputPath = stagedAudio, MaxOutputBytes = request.MaxOutputBytes
            };
            await File.WriteAllTextAsync(requestFile, JsonSerializer.Serialize(stagedRequest, LibraryJson.Options), cancellationToken);
            string executable = Path.GetFullPath(workerPath);
            if (!File.Exists(executable)) throw new FileNotFoundException("缺少 SoundPreviewer.Worker 工作进程。", executable);
            // CreateProcess still rejects a long working directory even when
            // .NET can read every absolute request/output path successfully.
            string workingDirectory = staging;
            if (OperatingSystem.IsWindows() && staging.Length >= 240)
            {
                workingDirectory = Path.GetDirectoryName(executable)!;
                if (workingDirectory.Length >= 240) workingDirectory = Path.GetPathRoot(executable)!;
            }
            var arguments = new List<string>();
            if (Path.GetExtension(executable).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                arguments.Add(executable);
                executable = "dotnet";
            }
            arguments.AddRange(["--request", requestFile, "--result", resultFile]);
            int maximumCaptures = request.Template.QuickPreview ? (request.Plugin.Backend == RenderBackend.VitalCli ? 3 : 4) : 1;
            var outcome = await ProcessRunner.RunAsync(executable, arguments, workingDirectory,
                TimeSpan.FromSeconds(Math.Clamp(request.Plugin.TimeoutSeconds, 5, 3600) * maximumCaptures), cancellationToken,
                processStarted: processStarted);
            cancellationToken.ThrowIfCancellationRequested();
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
            if (!string.Equals(Path.GetFullPath(result.OutputPath), stagedAudio, ApplicationPaths.PathComparison))
                throw new InvalidDataException("渲染进程返回的音频路径与请求不符。");
            if (new FileInfo(stagedAudio).Length > request.MaxOutputBytes)
                throw new InvalidDataException("预览输出超出存储剩余额度，未提交音频。");
            cancellationToken.ThrowIfCancellationRequested();
            var audio = WaveFile.Read(stagedAudio, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            result.Metrics = WaveFile.Analyze(audio, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Metrics.HasNonFinite) throw new InvalidDataException("音频包含 NaN / Infinity，未提交预览。");
            if (result.Metrics.SampleRate != request.Template.SampleRate)
                throw new InvalidDataException("音频采样率与试听模板不一致。");
            ValidateNotes(result, request.Template);
            foreach (var note in result.Notes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int first = Math.Clamp((int)Math.Round(note.StartSeconds * audio.SampleRate), 0, audio.Frames - 1);
                int count = Math.Min(audio.Frames - first, (int)Math.Round(note.DurationSeconds * audio.SampleRate));
                note.Metrics = WaveFile.Analyze(new WaveData
                {
                    SampleRate = audio.SampleRate, Channels = audio.Channels,
                    Samples = audio.Samples.AsSpan(first * audio.Channels, count * audio.Channels).ToArray()
                }, cancellationToken);
                result.NeedsReview |= note.Metrics.IsSilent || note.Metrics.HasClipping;
            }
            result.NeedsReview |= result.Metrics.IsSilent || result.Metrics.HasClipping;
            if (beforeCommit is not null) await beforeCommit(result, audio, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // After the atomic move, return success so the queue can persist this completed item even if cancellation arrives.
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
            // Windows can briefly retain the terminated process's working-directory handle
            // after WaitForExitAsync completes. Finish cleanup before returning from Stop.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try { Directory.Delete(staging, recursive: true); break; }
                catch (DirectoryNotFoundException) { break; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (attempt < 9) await Task.Delay(50, CancellationToken.None);
                }
            }
        }
    }

    public static void ValidateNotes(RenderResult result, PreviewTemplate template)
    {
        if (result.Metrics is null || result.Notes is null || result.Notes.Count != 3 || result.Notes.Any(note => note is null))
            throw new InvalidDataException("结果须包含音频指标及三个音区的来源映射。");
        if (string.IsNullOrWhiteSpace(result.AdapterVersion)) throw new InvalidDataException("工作进程未标明适配器版本。");
        double previousEnd = 0;
        bool quickPolicy = result.AdapterVersion.StartsWith(Fingerprints.QuickPreviewPolicy + "/", StringComparison.Ordinal);
        bool compact = quickPolicy || result.Notes.Any(note => note.TrimmedLeadingSeconds != 0 || note.TrimmedTrailingSeconds != 0);
        if (compact && (!template.QuickPreview || !quickPolicy))
            throw new InvalidDataException("裁剪预览须标明快速预览策略，不能修改旧式来源映射。");
        double frameTolerance = 1.5 / template.SampleRate;
        double maximumHold = QuickPreviewAudio.MaximumCaptureHoldSeconds(template.QuickPreviewSeconds);
        for (int i = 0; i < 3; i++)
        {
            var note = result.Notes[i];
            if (note.MidiNote != template.Pitches[i] || !double.IsFinite(note.StartSeconds) ||
                !double.IsFinite(note.DurationSeconds) || !double.IsFinite(note.ActualHoldSeconds) ||
                !double.IsFinite(note.ActualTailSeconds) || !double.IsFinite(note.TrimmedLeadingSeconds) ||
                !double.IsFinite(note.TrimmedTrailingSeconds) || note.TrimmedLeadingSeconds < 0 || note.TrimmedTrailingSeconds < 0 ||
                note.StartSeconds < 0 || note.StartSeconds < previousEnd - 0.01 ||
                note.DurationSeconds <= 0 || note.ActualHoldSeconds <= 0 || note.ActualTailSeconds < 0 ||
                note.ActualVelocity is < 1 or > 127 ||
                note.StartSeconds + note.DurationSeconds > result.Metrics.DurationSeconds + 0.01 ||
                Math.Abs(note.DurationSeconds + note.TrimmedLeadingSeconds + note.TrimmedTrailingSeconds -
                    note.ActualHoldSeconds - note.ActualTailSeconds) > (compact ? frameTolerance : 0.02))
                throw new InvalidDataException("工作进程的音符映射无效或超出音频范围。");
            if (compact && (Math.Abs(note.StartSeconds - previousEnd) > frameTolerance || note.DurationSeconds > template.QuickPreviewSeconds + frameTolerance ||
                note.ActualHoldSeconds > maximumHold + 64.0 / 44100 + frameTolerance || note.ActualTailSeconds > maximumHold * 0.3 + 64.0 / 44100 + frameTolerance))
                throw new InvalidDataException("快速预览存在空白间隔，或超出设定试听时长 / 有界捕获上限。");
            if (note.ActualNormalizedVelocity is double normalized && (!double.IsFinite(normalized) || normalized is <= 0 or > 1))
                throw new InvalidDataException("工作进程的 normalized 力度无效。");
            previousEnd = note.StartSeconds + note.DurationSeconds;
        }
        if (compact && Math.Abs(previousEnd - result.Metrics.DurationSeconds) > frameTolerance)
            throw new InvalidDataException("快速预览音区映射没有覆盖完整音频。");
    }
}
