using Avalonia;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private Point? _presetDragStart;
    private bool _isDraggingPreset;

    public bool CanLocatePreset => !_isClosing && !_isStorageMaintenance && !_storageRelocated &&
        Selected is { } row && File.Exists(row.Record.SourcePath);
    public string LocatePresetLabel
    {
        get
        {
            var preset = Selected?.Record;
            bool bank = preset is not null && (preset.InternalPath.Length > 0 || preset.BankSlot is not null ||
                Path.GetExtension(preset.SourcePath).ToLowerInvariant() is ".vitalbank" or ".fxb" or ".sbf" or ".swzip");
            return bank ? "定位音色库文件" : "定位音色文件";
        }
    }
    public string LocatePresetToolTip => (OperatingSystem.IsWindows() ? "在资源管理器中" : "在访达中") +
        "选中原文件；音色包内条目会定位到所属音色库文件。";

    private static ProcessStartInfo PrepareSourceReveal(string path, bool windows)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("原音色或音色库文件已不存在。", fullPath);
        var start = new ProcessStartInfo(windows ? "explorer.exe" : "/usr/bin/open") { UseShellExecute = false };
        start.ArgumentList.Add(windows ? "/select," : "-R");
        start.ArgumentList.Add(fullPath);
        return start;
    }

    public bool CanDragPreset => !_isClosing && !_isStorageMaintenance && !_storageRelocated && !_isDraggingPreset &&
        PresetDragUnavailableReason(Selected?.Record).Length == 0;
    public string PresetDragHint => PresetDragUnavailableReason(Selected?.Record) is { Length: > 0 } reason
        ? reason : "拖出原音色文件。先在 DAW 打开对应音源；接收位置取决于音源和 DAW 版本。";

    private static string PresetDragUnavailableReason(PresetRecord? preset)
    {
        if (preset is null) return "选中一个独立音色后，可将原文件拖出。";
        if (preset.InternalPath.Length > 0 || preset.BankSlot is not null)
            return "音色包或银行内的条目，请先在音源中导出为独立音色。";
        if (preset.Synth == SynthKind.Unknown || preset.Status == PresetStatus.Unidentified)
            return "请先确认此音色所属的音源。";
        string extension = Path.GetExtension(preset.SourcePath);
        bool supported = preset.Synth switch
        {
            SynthKind.Vital => extension.Equals(".vital", StringComparison.OrdinalIgnoreCase),
            SynthKind.Serum1 or SynthKind.Sylenth1 => extension.Equals(".fxp", StringComparison.OrdinalIgnoreCase),
            SynthKind.Serum2 => extension.Equals(".SerumPreset", StringComparison.OrdinalIgnoreCase),
            SynthKind.Spire => extension.Equals(".spf", StringComparison.OrdinalIgnoreCase) || extension.Equals(".spf2", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        if (!supported) return "此项尚不支持作为独立音色拖出，请先在对应音源中导出。";
        return File.Exists(preset.SourcePath) ? "" : "找不到原音色文件，请先恢复来源位置或重新扫描。";
    }

    private async Task<DataTransfer> PreparePresetDragDataAsync(PresetRecord preset)
    {
        string reason = PresetDragUnavailableReason(preset);
        if (reason.Length > 0) throw new InvalidOperationException(reason);
        var file = await StorageProvider.TryGetFileFromPathAsync(Path.GetFullPath(preset.SourcePath))
            ?? throw new FileNotFoundException("无法访问原音色文件。", preset.SourcePath);
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateFile(file));
        return data;
    }

    private void PresetDrag_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _presetDragStart = CanDragPreset && e.GetCurrentPoint(PresetDragHandle).Properties.IsLeftButtonPressed
            ? e.GetPosition(PresetDragHandle) : null;
        if (_presetDragStart is not null) e.Pointer.Capture(PresetDragHandle);
    }

    private void PresetDrag_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _presetDragStart = null;
        if (e.Pointer.Captured == PresetDragHandle) e.Pointer.Capture(null);
    }

    private async void PresetDrag_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_presetDragStart is not { } start || _isDraggingPreset) return;
        if (!e.GetCurrentPoint(PresetDragHandle).Properties.IsLeftButtonPressed || !CanDragPreset)
        {
            _presetDragStart = null;
            if (e.Pointer.Captured == PresetDragHandle) e.Pointer.Capture(null);
            return;
        }
        var offset = e.GetPosition(PresetDragHandle) - start;
        if (Math.Abs(offset.X) < 6 && Math.Abs(offset.Y) < 6) return;
        _presetDragStart = null;
        if (e.Pointer.Captured == PresetDragHandle) e.Pointer.Capture(null);
        var preset = Selected!.Record;
        _isDraggingPreset = true;
        e.Handled = true;
        try
        {
            var data = await PreparePresetDragDataAsync(preset);
            if (_isClosing || _isStorageMaintenance || _storageRelocated) { ((IDisposable)data).Dispose(); return; }
            // Only offer a copy: the original file and its external-resource location remain intact.
            var effect = await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
            if (!_isClosing && effect != DragDropEffects.None)
                Log($"已拖出音色文件：{preset.Name}。请在接收音源中核对加载结果。");
        }
        catch (Exception error) { if (!_isClosing) ShowError(error); }
        finally { _isDraggingPreset = false; NotifyAll(); }
    }

    private async Task CheckPresetDragDataAsync()
    {
        string directory = Path.Combine(App.DataDirectory, "drag-file-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "TEST ONLY 音色 #1.vital");
        string preview = Path.Combine(directory, "TEST ONLY preview.wav");
        await File.WriteAllTextAsync(source, "TEST ONLY file-transfer fixture; never loaded by a plugin.");
        await File.WriteAllTextAsync(preview, "TEST ONLY preview sentinel");
        try
        {
            byte[] original = await File.ReadAllBytesAsync(source);
            var preset = new PresetRecord { Synth = SynthKind.Vital, SourcePath = source, PreviewPath = preview };
            foreach (bool windows in new[] { true, false })
            {
                var reveal = PrepareSourceReveal(source, windows);
                if (reveal.UseShellExecute || reveal.ArgumentList.Count != 2 || reveal.ArgumentList[1] != source ||
                    reveal.ArgumentList[0] != (windows ? "/select," : "-R"))
                    throw new InvalidOperationException("定位来源的原始路径或平台参数错误。");
            }
            using var data = await PreparePresetDragDataAsync(preset);
            var files = data.TryGetFiles()?.ToArray() ?? [];
            if (files.Length != 1 || !string.Equals(files[0].TryGetLocalPath(), source, ApplicationPaths.PathComparison) ||
                !(await File.ReadAllBytesAsync(source)).SequenceEqual(original) || !File.Exists(preview))
                throw new InvalidOperationException("音色拖出未使用原始文件，或改动了来源及预览。");
            preset.InternalPath = "TEST ONLY bank entry";
            if (PresetDragUnavailableReason(preset).Length == 0) throw new InvalidOperationException("银行条目被当成独立音色拖出。");
            preset.InternalPath = ""; preset.BankSlot = 0;
            if (PresetDragUnavailableReason(preset).Length == 0) throw new InvalidOperationException("银行槽位被当成独立音色拖出。");
            preset.BankSlot = null; preset.SourcePath = preview;
            if (PresetDragUnavailableReason(preset).Length == 0) throw new InvalidOperationException("预览音频被当成可加载音色拖出。");
            preset.SourcePath = source; preset.Synth = SynthKind.Unknown;
            if (PresetDragUnavailableReason(preset).Length == 0) throw new InvalidOperationException("待识别音色可以直接拖出。");
            preset.Synth = SynthKind.Vital; File.Delete(source);
            if (PresetDragUnavailableReason(preset).Length == 0) throw new InvalidOperationException("缺失音色仍可拖出。");
        }
        finally
        {
            File.Delete(source);
            File.Delete(preview);
            Directory.Delete(directory);
        }
    }
}
