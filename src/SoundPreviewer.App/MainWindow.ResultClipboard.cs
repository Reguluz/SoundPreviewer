using Avalonia.Interactivity;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private string GenerationResultText => string.Join(Environment.NewLine,
        new[] { TaskTitle, TaskProgressText, TaskSelectionText, StatusMessage, TaskMemoryText }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string PresetResultText(PresetRow row) => string.Join(Environment.NewLine,
        new[] { $"音色：{row.Name}", $"音源：{row.SynthLabel}", $"音源版本：{row.PluginVersionLabel}",
            $"源文件：{row.Record.SourcePath}", $"最近任务：{row.LastRenderOutcomeLabel}",
            row.LastRenderMessageLabel, row.Message }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));

    private async Task CopyResultTextAsync(string text)
    {
        var clipboard = Clipboard ?? throw new InvalidOperationException("系统剪贴板暂不可用，请稍后重试。");
        await clipboard.SetTextAsync(text);
    }

    private async void CopyGenerationResult_Click(object? sender, RoutedEventArgs e)
    {
        try { await CopyResultTextAsync(GenerationResultText); }
        catch (Exception error) { if (!_isClosing) ShowError(error); }
    }

    private async void CopyPresetResult_Click(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        try { await CopyResultTextAsync(PresetResultText(row)); }
        catch (Exception error) { if (!_isClosing) ShowError(error); }
    }
}
