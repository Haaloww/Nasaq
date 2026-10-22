using System.Windows;
using System.Windows.Media;

namespace Nasaq.Controls;

/// <summary>
/// Lightweight selection overlay used by the audio editor. It dims the parts
/// outside the trim range and draws a crisp frame around the selected span.
/// </summary>
public sealed class RangeOverlay : FrameworkElement
{
    public static readonly DependencyProperty StartFractionProperty = DependencyProperty.Register(
        nameof(StartFraction), typeof(double), typeof(RangeOverlay),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EndFractionProperty = DependencyProperty.Register(
        nameof(EndFraction), typeof(double), typeof(RangeOverlay),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(RangeOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double StartFraction
    {
        get => (double)GetValue(StartFractionProperty);
        set => SetValue(StartFractionProperty, value);
    }

    public double EndFraction
    {
        get => (double)GetValue(EndFractionProperty);
        set => SetValue(EndFractionProperty, value);
    }

    public Brush? Accent
    {
        get => (Brush?)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 1 || height <= 1) return;

        var start = Math.Clamp(StartFraction, 0, 1);
        var end = Math.Clamp(EndFraction, 0, 1);
        if (end < start) (start, end) = (end, start);

        var left = start * width;
        var right = end * width;
        var span = Math.Max(1, right - left);
        var shade = new SolidColorBrush(Color.FromArgb(150, 4, 7, 13));
        shade.Freeze();

        if (left > 0)
            drawingContext.DrawRectangle(shade, null, new Rect(0, 0, left, height));
        if (right < width)
            drawingContext.DrawRectangle(shade, null, new Rect(right, 0, width - right, height));

        var accent = Accent ?? new SolidColorBrush(Color.FromRgb(0x7C, 0x9C, 0xFF));
        var wash = accent.Clone();
        wash.Opacity = 0.16;
        wash.Freeze();
        drawingContext.DrawRectangle(wash, null, new Rect(left, 0, span, height));

        var pen = new Pen(accent, 1.6);
        pen.Freeze();
        drawingContext.DrawRectangle(null, pen, new Rect(left + 0.8, 0.8, Math.Max(0, span - 1.6), Math.Max(0, height - 1.6)));

        var handle = new SolidColorBrush(Colors.White);
        handle.Freeze();
        drawingContext.DrawRectangle(handle, null, new Rect(left + 1, height / 2 - 9, 2.5, 18));
        drawingContext.DrawRectangle(handle, null, new Rect(right - 3.5, height / 2 - 9, 2.5, 18));
    }
}
