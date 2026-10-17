using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SoundPreviewer.Core.Audio;

namespace SoundPreviewer.App;

public sealed class WaveformView : Control
{
    private WaveData? _audio;
    private double _position;
    public void SetAudio(WaveData? audio) { _audio = audio; InvalidateVisual(); }
    public void SetPosition(double seconds) { _position = seconds; InvalidateVisual(); }

    public override void Render(DrawingContext drawingContext)
    {
        base.Render(drawingContext);
        drawingContext.DrawRectangle(new SolidColorBrush(Color.FromRgb(242, 247, 244)), null, new Rect(Bounds.Size));
        var mid = Bounds.Height / 2;
        drawingContext.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(211, 224, 215)), 1), new Point(0, mid), new Point(Bounds.Width, mid));
        if (_audio is null || _audio.Samples.Length == 0 || Bounds.Width < 1) return;
        var columns = Math.Max(1, (int)Bounds.Width);
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
        var playhead = Math.Clamp(_position / _audio.DurationSeconds, 0, 1) * Bounds.Width;
        drawingContext.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(205, 99, 60)), 2), new Point(playhead, 0), new Point(playhead, Bounds.Height));
    }
}
