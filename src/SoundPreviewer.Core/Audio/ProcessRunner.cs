using System.Diagnostics;
using System.Text;

namespace SoundPreviewer.Core.Audio;

public sealed record ProcessOutcome(int ExitCode, string StandardOutput, string StandardError);

public static class ProcessRunner
{
    public static async Task<ProcessOutcome> RunAsync(string executable, IEnumerable<string> arguments,
        string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo
        {
            FileName = executable, WorkingDirectory = workingDirectory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environmentOverrides is not null)
            foreach (var (name, value) in environmentOverrides)
                if (value is null) start.Environment.Remove(name);
                else start.Environment[name] = value;
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("无法启动渲染工作进程。");
        ProcessJob job;
        try { job = new ProcessJob(process); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            throw;
        }
        using var containment = job;
        var stdout = ReadBoundedAsync(process.StandardOutput);
        var stderr = ReadBoundedAsync(process.StandardError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            containment.Dispose();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"渲染进程超过 {timeout.TotalSeconds:0} 秒，已结束该进程及其子进程。");
        }
        return new ProcessOutcome(process.ExitCode, await stdout, await stderr);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        const int maximum = 16000;
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) != 0)
        {
            int retained = Math.Min(count, maximum - text.Length);
            if (retained > 0) text.Append(buffer, 0, retained);
        }
        return text.ToString();
    }
}
