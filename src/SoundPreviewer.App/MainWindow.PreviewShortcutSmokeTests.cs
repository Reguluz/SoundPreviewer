using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private async Task<bool> CheckPreviewShortcutAsync()
    {
        int page = Pages.SelectedIndex;
        double volume = _player.Volume;
        bool playbackAvailable = _mediaOpened && CanPlay;
        double marker = playbackAvailable ? _player.DurationSeconds / 3 : 0;
        double? segment = playbackAvailable ? _player.DurationSeconds * .8 : null;
        try
        {
            Pages.SelectedIndex = 0;
            await SettleLayoutAsync();
            _player.Volume = 0;
            StopPlayback();
            foreach (var target in new Control[] { SearchBox, SynthFilter, ShowDuplicates, SeekSlider })
                CheckIgnored(target, KeyModifiers.None);
            if (SearchBox.GetVisualDescendants().OfType<Control>().FirstOrDefault() is { } textChild)
                CheckIgnored(textChild, KeyModifiers.None);
            foreach (var modifiers in new[] { KeyModifiers.Control, KeyModifiers.Shift, KeyModifiers.Alt, KeyModifiers.Meta })
                CheckIgnored(PresetGrid, modifiers);
            Pages.SelectedIndex = 1;
            await SettleLayoutAsync();
            CheckIgnored(Pages, KeyModifiers.None);
            Pages.SelectedIndex = 0;
            await SettleLayoutAsync();

            StartPlayback(marker, segment);
            var gridHandled = RaiseShortcutKey(PresetGrid, KeyDownEvent);
            if (!gridHandled ||
                _openPosition != 0 || _segmentEnd is not null || _isPlaying != playbackAvailable)
                throw new InvalidOperationException($"空格键重播失败：handled={gridHandled}, page={Pages.SelectedIndex}, root={PresetGrid.GetVisualRoot()?.GetType().Name}, sameRoot={PresetGrid.GetVisualRoot() == this}, position={_openPosition}, end={_segmentEnd}, playing={_isPlaying}, available={playbackAvailable}, ancestors={string.Join(',', PresetGrid.GetVisualAncestors().Select(control => control.GetType().Name))}。");
            if (playbackAvailable)
            {
                if (_player.PositionSeconds > .2) throw new InvalidOperationException("空格键没有重置原生播放器位置。");
                StartPlayback(marker, segment);
                if (!RaiseShortcutKey(PresetGrid, KeyDownEvent) || _openPosition != marker || _segmentEnd != segment)
                    throw new InvalidOperationException("按住空格键时系统重复按键反复重启了预览。");
            }
            RaiseShortcutKey(PresetGrid, KeyUpEvent);
            var playButton = PlayPreviewButton;
            if (!RaiseShortcutKey(playButton, KeyDownEvent) || _openPosition != 0 || _segmentEnd is not null)
                throw new InvalidOperationException("播放按钮获得焦点时空格键没有从头播放。");
            RaiseShortcutKey(playButton, KeyUpEvent);
            if (_isPlaying != playbackAvailable)
                throw new InvalidOperationException("空格键被播放按钮再次处理并暂停了预览。");
            return playbackAvailable;
        }
        finally
        {
            // Cleanup must not mask a routing failure if a tab has detached its old content.
            PreviewShortcut_KeyUp(this, new KeyEventArgs { Key = Key.Space });
            StopPlayback();
            _player.Volume = volume;
            Pages.SelectedIndex = page;
        }

        void CheckIgnored(Control target, KeyModifiers modifiers)
        {
            StartPlayback(marker, segment);
            double position = _openPosition;
            double? end = _segmentEnd;
            if (RaiseShortcutKey(target, KeyDownEvent, modifiers, suppressTarget: true) ||
                _openPosition != position || _segmentEnd != end)
                throw new InvalidOperationException("空格快捷键干扰了输入、控件操作、组合键或其他页面。");
            RaiseShortcutKey(target, KeyUpEvent, modifiers, suppressTarget: true);
        }
    }

    private bool RaiseShortcutKey(Control target, RoutedEvent<KeyEventArgs> keyEvent,
        KeyModifiers modifiers = KeyModifiers.None, bool suppressTarget = false)
    {
        bool? handledByShortcut = null;
        void Observe(object? sender, KeyEventArgs args)
        {
            handledByShortcut = args.Handled;
            // Inspect the window's shortcut before native controls process the synthetic key.
            if (suppressTarget) args.Handled = true;
        }
        target.AddHandler(keyEvent, Observe, RoutingStrategies.Tunnel, handledEventsToo: true);
        try
        {
            target.RaiseEvent(new KeyEventArgs { RoutedEvent = keyEvent, Source = target, Key = Key.Space, KeyModifiers = modifiers });
            return handledByShortcut ?? throw new InvalidOperationException("快捷键检查没有经过窗口的真实事件路由。");
        }
        finally { target.RemoveHandler(keyEvent, Observe); }
    }

    private void CheckPreviewShortcutHint()
    {
        var bounds = SmokeBounds(PreviewShortcutHint);
        if (!PreviewShortcutHint.IsEffectivelyVisible || bounds.Height < 14 ||
            !new Rect(((Control)Content!).Bounds.Size).Contains(bounds) ||
            PreviewShortcutHint.GetVisualParent() is not Control parent || !SmokeBounds(parent).Contains(bounds) ||
            PreviewShortcutHint.Child is not TextBlock text || text.Text?.Contains("空格键", StringComparison.Ordinal) != true ||
            text.Text?.Contains("从头播放", StringComparison.Ordinal) != true ||
            text.DesiredSize.Width > text.Bounds.Width + .5 || text.DesiredSize.Height > text.Bounds.Height + .5)
            throw SmokeLayoutFailure("空格键提示缺失、被裁剪或超出播放区域。");
    }
}
