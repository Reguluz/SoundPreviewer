using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Services;

public sealed partial class RenderQueue
{
    private sealed partial class Execution
    {
        public async Task<InitBaselineState> RunBaselineAsync(PluginConfiguration originalPlugin)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            using var monitorCancellation = new CancellationTokenSource();
            Task monitor = Task.CompletedTask;
            batchCancellation = cancellation;
            token = cancellation.Token;
            var plugin = Clone(originalPlugin);
            string previousPath = plugin.InitBaselinePath;
            var item = new QueueItem(new PresetRecord { Id = "startup-baseline-" + plugin.Synth, Name = "默认启动基准", Synth = plugin.Synth });
            var job = new RenderJob("", plugin, [item]);
            Report(QueueStages.Preparing, "正在准备默认启动音色基准...", item, true);
            try
            {
                token.ThrowIfCancellationRequested();
                settings.Template.Validate();
                CaptureResources();
                ThrowIfResourceStopped();
                monitor = MonitorResourcesAsync(monitorCancellation.Token);
                var waiting = new List<RenderJob> { job };
                while (true)
                {
                    var resourceWake = ResourceChangedTask();
                    if (TryStartJob(waiting, [], 0, out _)) break;
                    ThrowIfResourceStopped();
                    await resourceWake.WaitAsync(token).ConfigureAwait(false);
                }
                Report(QueueStages.Validating, "正在建立三份独立声音参照...", item, true);
                var state = await InitBaselineService.GenerateAsync(settings, plugin, workerPath, token,
                    processId => RegisterWorker(job, processId)).ConfigureAwait(false);
                if (plugin.InitBaselinePath != previousPath)
                {
                    store.SaveInitializationBaseline(settings, plugin.Synth, previousPath, plugin.InitBaselinePath);
                    originalPlugin.InitBaselinePath = plugin.InitBaselinePath;
                }
                CaptureResources();
                monitorCancellation.Cancel();
                await monitor.ConfigureAwait(false);
                ThrowIfResourceStopped();
                summary.ElapsedSeconds = elapsed.Elapsed.TotalSeconds;
                Report(state.CanCompare ? QueueStages.Completed : QueueStages.Failed, state.Detail, item);
                return state;
            }
            catch (Exception error)
            {
                cancellation.Cancel();
                monitorCancellation.Cancel();
                await monitor.ConfigureAwait(false);
                summary.ElapsedSeconds = elapsed.Elapsed.TotalSeconds;
                if (Volatile.Read(ref resourceFailure) is string reason)
                {
                    Report(QueueStages.Failed, reason, item);
                    throw new RenderResourceLimitException(reason, summary);
                }
                Report(error is OperationCanceledException ? QueueStages.Stopped : QueueStages.Failed, error.Message, item);
                throw;
            }
            finally
            {
                monitorCancellation.Cancel();
                await monitor.ConfigureAwait(false);
                lock (gate) Release(job);
                summary.ElapsedSeconds = elapsed.Elapsed.TotalSeconds;
            }
        }
    }
}
