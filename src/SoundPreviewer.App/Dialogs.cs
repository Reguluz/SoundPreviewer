using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SoundPreviewer.App;

internal static class Dialogs
{
    public static Task<bool> ShowAsync(Window owner, string text, string title, bool question = false)
    {
        var dialog = new Window
        {
            Title = T(title), Width = 460, MaxWidth = 640, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var accept = new Button { Content = T(question ? "继续" : "确定"), IsDefault = true, MinWidth = 80 };
        accept.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(accept);
        if (question)
        {
            var cancel = new Button { Content = T("取消"), IsCancel = true, MinWidth = 80, Margin = default };
            cancel.Click += (_, _) => dialog.Close(false);
            buttons.Children.Add(cancel);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24), Children =
            {
                new SelectableTextBlock { Text = T(text), TextWrapping = TextWrapping.Wrap, MaxHeight = 440 }, buttons
            }
        };
        return dialog.ShowDialog<bool>(owner);
    }
}
