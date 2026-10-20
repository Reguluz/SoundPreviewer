namespace SoundPreviewer.Core.Services;

public sealed record QueueSelectionCounts(int Requested, int Eligible, int Duplicates, int Unidentified,
    int Unsupported, int Missing, int RepeatedSelection)
{
    public int Unique => Math.Max(0, Requested - Duplicates - RepeatedSelection);
    public int Excluded => Duplicates + Unidentified + Unsupported + Missing + RepeatedSelection;
    public int Unclassified => Math.Max(0, Requested - Eligible - Excluded);
    public string Description => Unclassified > 0 ? $"本次所选 {Requested} 条 · 正在统计（剩余 {Unclassified} 条）" :
        $"本次所选 {Requested} 条 · 去重 {Unique} 个 · 处理队列 {Eligible} 个" +
        (Duplicates > 0 ? $" · 重复副本 {Duplicates}" : "") +
        (Unidentified > 0 ? $" · 待识别 {Unidentified}" : "") +
        (Unsupported > 0 ? $" · 暂不支持生成 {Unsupported}" : "") +
        (Missing > 0 ? $" · 来源缺失 {Missing}" : "") +
        (RepeatedSelection > 0 ? $" · 重复选择 {RepeatedSelection}" : "");
}
