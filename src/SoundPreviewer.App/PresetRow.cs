using System.ComponentModel;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public sealed class PresetRow(PresetRecord record) : INotifyPropertyChanged
{
    public PresetRecord Record { get; } = record;
    public string Name => Record.Name;
    public string SynthLabel => Labels.Synth(Record.Synth);
    public string Package => Record.Package;
    public string StatusLabel => Labels.Status(Record.Status);
    public string Message => Record.Message;
    public string FeedbackLabel => Record.Feedback switch { 1 => "喜欢", -1 => "不喜欢", _ => "" };
    public string FavoriteLabel => Record.Favorite ? "★" : "";
    public string AudioLabel => Record.Metrics is { } m ? $"{m.DurationSeconds:0.0} 秒 · {m.SampleRate} Hz · {m.Channels} 声道" : "尚未生成";
    public string QualityLabel => Record.Metrics is { } m
        ? string.Join(" · ", new[] { m.IsSilent ? "静音待查" : "", m.HasClipping ? "削波风险" : "", m.HasNonFinite ? "异常采样" : "" }.Where(s => s.Length > 0)) : "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}

public sealed record Choice<T>(string Label, T Value);
public sealed record CompatibilityRow(string Synth, string Status, string Detail);

public static class Labels
{
    public static string Synth(SynthKind s) => s switch { SynthKind.Serum1 => "Serum 1", SynthKind.Serum2 => "Serum 2", _ => s.ToString() };
    public static string PresetExtensions(SynthKind s) => s switch
    {
        SynthKind.Vital => ".vital / .vitalbank",
        SynthKind.Serum1 => ".fxp",
        SynthKind.Serum2 => ".SerumPreset / .fxp",
        SynthKind.Sylenth1 => ".fxp / .fxb",
        _ => ""
    };
    public static string Status(PresetStatus s) => s switch
    {
        PresetStatus.Pending => "待生成", PresetStatus.Rendering => "生成中", PresetStatus.Ready => "已就绪",
        PresetStatus.Failed => "失败", PresetStatus.NeedsReview => "待确认", PresetStatus.Unsupported => "待适配",
        PresetStatus.Missing => "来源缺失", _ => s.ToString()
    };
}
