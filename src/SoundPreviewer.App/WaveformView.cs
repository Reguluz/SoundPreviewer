using System.Windows;
using System.Windows.Media;
using SoundPreviewer.Core.Audio;

namespace SoundPreviewer.App;

public sealed class WaveformView : FrameworkElement
{
    private WaveData? _audio;
    private double _position;
    public void SetAudio(WaveData? audio) { _audio = audio; InvalidateVisual(); }
    public void SetPosition(double seconds) { _position = seconds; InvalidateVisual(); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(new SolidColorBrush(Color.FromRgb(242, 247, 244)), null, new Rect(RenderSize));
        var mid = ActualHeight / 2;
        drawingContext.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(211, 224, 215)), 1), new Point(0, mid), new Point(ActualWidth, mid));
        if (_audio is null || _audio.Samples.Length == 0 || ActualWidth < 1) return;
        var columns = Math.Max(1, (int)ActualWidth);
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(22, 124, 105)), 1);
        for (var x = 0; x < columns; x++)
        {
            var from = (int)((long)x * _audio.Samples.Length / columns);
            var to = (int)((long)(x + 1) * _audio.Samples.Length / columns);
            float peak = 0;
            for (var i = from; i < to; i++) peak = Math.Max(peak, Math.Abs(_audio.Samples[i]));
            var height = Math.Min(peak, 1) * (mid - 3);
            drawingContext.DrawLine(pen, new Point(x, mid - height), new Point(x, mid + height));
        }
        var playhead = Math.Clamp(_position / _audio.DurationSeconds, 0, 1) * ActualWidth;
        drawingContext.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(205, 99, 60)), 2), new Point(playhead, 0), new Point(playhead, ActualHeight));
    }
}
