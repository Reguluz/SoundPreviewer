using Avalonia.Input;
using Avalonia.Input.Platform;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private async Task<bool> CheckPresetCopyAsync()
    {
        string directory = Path.Combine(App.DataDirectory, "clipboard-path-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "TEST ONLY 音色 #1.vital");
        string bank = Path.Combine(directory, "TEST ONLY 音色库 #2.vitalbank");
        string preview = Path.Combine(directory, "TEST ONLY preview.wav");
        await File.WriteAllTextAsync(source, "TEST ONLY source fixture; never loaded by a plugin.");
        await File.WriteAllTextAsync(bank, "TEST ONLY bank fixture; never opened as an archive.");
        await File.WriteAllTextAsync(preview, "TEST ONLY preview sentinel");
        try
        {
            byte[] originalSource = await File.ReadAllBytesAsync(source);
            byte[] originalBank = await File.ReadAllBytesAsync(bank);
            byte[] originalPreview = await File.ReadAllBytesAsync(preview);
            var preset = new PresetRecord { Synth = SynthKind.Vital, SourcePath = source, PreviewPath = preview };
            preset.Name = "TEST ONLY result copy";
            preset.PluginVersion = "TEST ONLY version";
            preset.LastRenderOutcome = "失败";
            preset.LastRenderMessage = "TEST ONLY first line\n" + new string('长', 5000) + "\nTEST ONLY last line";
            preset.Message = "TEST ONLY old preview retained";
            string resultText = PresetResultText(new PresetRow(preset));
            if (!resultText.Contains(preset.LastRenderMessage, StringComparison.Ordinal) ||
                !resultText.Contains(source, StringComparison.Ordinal) || !resultText.Contains(preset.PluginVersion, StringComparison.Ordinal) ||
                !resultText.Contains(preset.Message, StringComparison.Ordinal))
                throw new InvalidOperationException("复制生成结果遗漏了完整多行错误、来源、音源版本或旧预览状态。");
            if (GetPresetSourceFilePath(preset) != source || PresetSourceUnavailableReason(preset).Length > 0)
                throw new InvalidOperationException("复制路径未返回原始音色的完整本地地址。");
            foreach (bool windows in new[] { true, false })
            {
                var reveal = PrepareSourceReveal(source, windows);
                if (reveal.UseShellExecute || reveal.ArgumentList.Count != 2 || reveal.ArgumentList[1] != source ||
                    reveal.ArgumentList[0] != (windows ? "/select," : "-R"))
                    throw new InvalidOperationException("定位来源的原始路径或平台参数错误。");
            }

            bool nativeChecked = false;
            var clipboard = Clipboard;
            // Preserve a user's rich clipboard without reading its contents. CI normally starts empty.
            if (clipboard is not null && (await clipboard.GetDataFormatsAsync()).All(format => format == DataFormat.Text))
            {
                string? previousText = await clipboard.TryGetTextAsync();
                string? writtenText = null;
                try
                {
                    writtenText = source;
                    await CopyPresetFilePathAsync(preset);
                    if (await clipboard.TryGetTextAsync() != source || (await clipboard.TryGetFilesAsync())?.Any() == true)
                        throw new InvalidOperationException("复制路径没有写入纯文本地址，或错误地写入了文件复制对象。");
                    preset.SourcePath = bank;
                    preset.InternalPath = "TEST ONLY/folder/entry.vital";
                    preset.BankSlot = 0;
                    writtenText = bank;
                    await CopyPresetFilePathAsync(preset);
                    await SettleLayoutAsync();
                    string? copiedBankText = await clipboard.TryGetTextAsync();
                    bool copiedBankFiles = (await clipboard.TryGetFilesAsync())?.Any() == true;
                    if (copiedBankText != bank || copiedBankFiles)
                        throw new InvalidOperationException($"音色包条目未复制所属音色库地址，或把内部路径拼入了文件地址。目标地址匹配：{copiedBankText == bank}；前次地址匹配：{copiedBankText == source}；文本为空：{string.IsNullOrEmpty(copiedBankText)}；包含文件对象：{copiedBankFiles}。");
                    writtenText = resultText;
                    await CopyResultTextAsync(resultText);
                    await SettleLayoutAsync();
                    if (await clipboard.TryGetTextAsync() != resultText)
                        throw new InvalidOperationException("生成结果未完整写入剪贴板。");
                    nativeChecked = true;
                }
                finally
                {
                    // A later user copy takes priority over restoring the smoke-test snapshot.
                    if (writtenText is not null && await clipboard.TryGetTextAsync() == writtenText)
                    {
                        if (previousText is null) await clipboard.ClearAsync();
                        else await ClipboardExtensions.SetTextAsync(clipboard, previousText);
                    }
                }
            }

            preset.SourcePath = bank;
            preset.InternalPath = "TEST ONLY/folder/entry.vital";
            preset.BankSlot = 0;
            if (GetPresetSourceFilePath(preset) != bank)
                throw new InvalidOperationException("银行条目地址包含内部文件名或槽位。");
            preset.InternalPath = "";
            preset.BankSlot = null;
            preset.SourcePath = source;
            preset.Synth = SynthKind.Unknown;
            CheckAvailable();
            preset.Synth = (SynthKind)int.MaxValue;
            CheckAvailable();
            preset.Synth = SynthKind.Vital;
            preset.Status = PresetStatus.Unidentified;
            CheckAvailable();
            preset.Status = PresetStatus.Missing;
            CheckAvailable();
            preset.Status = PresetStatus.Pending;
            preset.SourcePath = preview;
            CheckUnavailable();
            preset.SourcePath = "TEST ONLY relative.vital";
            CheckUnavailable();
            preset.SourcePath = Path.Combine(directory, "TEST ONLY missing.vital");
            CheckUnavailable();
            if (PresetSourceUnavailableReason(null).Length == 0 ||
                !(await File.ReadAllBytesAsync(source)).SequenceEqual(originalSource) ||
                !(await File.ReadAllBytesAsync(bank)).SequenceEqual(originalBank) ||
                !(await File.ReadAllBytesAsync(preview)).SequenceEqual(originalPreview))
                throw new InvalidOperationException("复制路径修改了来源、音色库或现有预览。");
            return nativeChecked;

            void CheckAvailable()
            {
                if (PresetSourceUnavailableReason(preset).Length > 0 || GetPresetSourceFilePath(preset) != source)
                    throw new InvalidOperationException("已存在的音色文件被音源识别状态或历史缺失状态阻止复制路径。");
            }

            void CheckUnavailable()
            {
                if (PresetSourceUnavailableReason(preset).Length == 0)
                    throw new InvalidOperationException("实际缺失、虚拟路径或非音色文件仍可复制路径。");
                try { GetPresetSourceFilePath(preset); }
                catch (InvalidOperationException) { return; }
                throw new InvalidOperationException("无效来源仍返回了可复制路径。");
            }
        }
        finally
        {
            File.Delete(source);
            File.Delete(bank);
            File.Delete(preview);
            Directory.Delete(directory);
        }
    }
}
