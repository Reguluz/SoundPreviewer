using Avalonia.Interactivity;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private bool _isCompressingPreviews;

    public bool CanCompressPreviews => IsIdle && !_isClosing && Presets.Any(row => row.HasPreview &&
        Path.GetExtension(row.Record.PreviewPath).Equals(".wav", StringComparison.OrdinalIgnoreCase));

    private const string CompressionConfirmation = "将把当前音色库已有的 WAV 预览转换为 AAC 压缩格式。转换并校验成功后会永久删除对应的原 WAV，此操作不可逆，无法恢复原始无损音质。建议先备份 Preview 文件夹。收藏、标签、分类和音区保持不变；失败的项目保留原 WAV。是否继续？";

    private bool _compressionConfirmationOpen;

    private async void CompressPreviews_Click(object? sender, RoutedEventArgs e)
    {
        if (!CanCompressPreviews || _compressionConfirmationOpen) return;
        _compressionConfirmationOpen = true;
        try
        {
            if (await Dialogs.ShowAsync(this, CompressionConfirmation, "压缩并永久删除原 WAV", question: true,
                acceptLabel: "压缩并删除 WAV", defaultToCancel: true))
                await CompressExistingPreviewsAsync();
        }
        finally { _compressionConfirmationOpen = false; }
    }

    private async Task CompressExistingPreviewsAsync()
    {
        if (!CanCompressPreviews) return;
        await RunTask(async token =>
        {
            _isStorageMaintenance = true;
            _isCompressingPreviews = true;
            _taskTitle = "正在压缩已有预览";
            _statusMessage = "保留采样率与音区；成功写入 AAC 并更新记录后才回收本库旧 WAV。";
            UnloadForStorageMaintenance();
            NotifyAll();
            PreviewCompressionProgress? pending = null;
            var progress = new CompressionReporter(value => Interlocked.Exchange(ref pending, value));
            _flushTaskProgress = () =>
            {
                var value = Interlocked.Exchange(ref pending, null);
                if (value is null) return;
                _progressIndeterminate = false;
                _progressValue = value.Total == 0 ? 0 : value.Processed * 100d / value.Total;
                _taskProgressText = $"已处理 {value.Processed} / {value.Total} · 已压缩 {value.Compressed} · 已释放 {value.FreedBytes / 1048576d:0.0} MiB";
                NotifyTaskProgress();
            };
            try
            {
                var result = await Task.Run(() => PreviewCompressionService.CompressAsync(_store, _settings, progress, token), token);
                _flushTaskProgress();
                _taskTitle = "预览压缩完成";
                _taskProgressText = $"已压缩 {result.Compressed} · 跳过保留 {result.Skipped} · 失败 {result.Failed} · 已释放 {result.FreedBytes / 1048576d:0.0} MiB";
                _statusMessage = "音色记录、收藏、标签及音区保持不变。" + FormatStorageWarnings(result.Warnings, App.DataDirectory);
                Log(_taskProgressText);
                if (result.Warnings.Count > 0) Log(_statusMessage);
            }
            finally
            {
                _flushTaskProgress?.Invoke();
                _flushTaskProgress = null;
                try { SetPresets(await Task.Run(_store.LoadPresets)); }
                catch (Exception error) { Log("压缩记录未能刷新：" + error.Message); }
                _isStorageMaintenance = false;
                _isCompressingPreviews = false;
                NotifyAll();
            }
        });
    }

    private sealed class CompressionReporter(Action<PreviewCompressionProgress> report) : IProgress<PreviewCompressionProgress>
    {
        public void Report(PreviewCompressionProgress value) => report(value);
    }
}
