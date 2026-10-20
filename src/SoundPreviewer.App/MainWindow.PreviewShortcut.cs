using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private bool _previewSpaceHeld;

    private void InitializePreviewShortcut()
    {
        // Handle before the grid or a focused playback button consumes Space.
        AddHandler(KeyDownEvent, PreviewShortcut_KeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, PreviewShortcut_KeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        Deactivated += (_, _) => _previewSpaceHeld = false;
    }

    private void PreviewShortcut_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        var repeated = _previewSpaceHeld;
        _previewSpaceHeld = true;
        if (_isClosing || Pages.SelectedIndex != 0 || e.KeyModifiers != KeyModifiers.None ||
            e.Source is not Visual source || source.GetVisualRoot() != this ||
            source.GetVisualAncestors().Prepend(source).Any(control =>
                control is TextBox or ComboBox or ToggleButton or Slider or MenuItem)) return;

        e.Handled = true;
        if (!repeated && CanPlay) StartPlayback(0, null);
    }

    private void PreviewShortcut_KeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) _previewSpaceHeld = false;
    }
}
