using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private static string FormatRenderProgress(QueueProgress progress)
    {
        string text = $"已处理 {progress.Completed} / {progress.Total}";
        if (progress.Total > 0) text += $" ({progress.Completed * 100d / progress.Total:0}%)";
        text += $" · 成功 {progress.Succeeded}（新生成 {progress.Generated} / 复用 {progress.Reused}） · 失败 {progress.Failed}";
        if (progress.Unconfigured > 0) text += $" · 未配置 {progress.Unconfigured}";
        if (progress.RetryLimited > 0) text += $" · 达重试限制 {progress.RetryLimited}";
        if (progress.ActiveCount > 0 || progress.EffectiveConcurrency > 0)
            text += $" · 运行 {progress.ActiveCount}/{progress.EffectiveConcurrency}";
        if (progress.PreparingCount > 0) text += $" · 准备 {progress.PreparingCount}";
        return text;
    }

    private static string FormatRenderResources(QueueProgress progress) => progress.MemoryBudgetBytes <= 0 ? "" :
        $"总内存 {progress.WorkingSetBytes / 1048576d:0} / {progress.MemoryBudgetBytes / 1048576d:0} MiB" +
        $" · 当前并行上限 {progress.EffectiveConcurrency} 条（设定 {progress.MaxConcurrency}，内存允许 {progress.AllowedConcurrency}）";
}
