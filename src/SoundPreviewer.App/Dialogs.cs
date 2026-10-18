using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

internal static class Dialogs
{
    public static Task<PreviewDirectoryChangeMode?> ChoosePreviewDirectoryChangeAsync(Window owner, string source, string destination)
    {
        var dialog = new Window
        {
            Title = "更改数据与预览目录", Width = 560, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        foreach (var (label, mode) in new[] { ("迁移旧预览", PreviewDirectoryChangeMode.Migrate), ("清除旧预览", PreviewDirectoryChangeMode.Clear) })
        {
            var button = new Button { Content = label, IsDefault = mode == PreviewDirectoryChangeMode.Migrate };
            button.Click += (_, _) => dialog.Close((PreviewDirectoryChangeMode?)mode);
            buttons.Children.Add(button);
        }
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = default };
        cancel.Click += (_, _) => dialog.Close(null);
        buttons.Children.Add(cancel);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24), Children =
            {
                new TextBlock { Text = $"当前位置：{source}\n新位置：{destination}", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "音色记录、收藏、标签和分类会复制到新目录。请选择旧预览的处理方式：\n\n迁移：复制并校验音频，再清理旧预览。\n清除：移除旧预览，新目录保留记录以便重新生成。\n\n旧数据库保留作回退备份。操作后重新打开音色库；未保存的设置请先保存。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) },
                buttons
            }
        };
        return dialog.ShowDialog<PreviewDirectoryChangeMode?>(owner);
    }

    public static Task<bool> ShowAsync(Window owner, string text, string title, bool question = false)
    {
        var dialog = new Window
        {
            Title = title, Width = 460, MaxWidth = 640, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var accept = new Button { Content = question ? "继续" : "确定", IsDefault = true, MinWidth = 80 };
        accept.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(accept);
        if (question)
        {
            var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 80, Margin = default };
            cancel.Click += (_, _) => dialog.Close(false);
            buttons.Children.Add(cancel);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24), Children =
            {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxHeight = 440 }, buttons
            }
        };
        return dialog.ShowDialog<bool>(owner);
    }
}
