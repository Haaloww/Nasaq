using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Nasaq.Controls;

/// <summary>A segment drawn as a block on <see cref="MediaTimeline"/>.</summary>
public interface ITimelineSegment
{
    string Label { get; }
    double StartOffset { get; }
    double Duration { get; }
}

public sealed class SeekRequestedEventArgs(double seconds) : EventArgs
{
    public double Seconds { get; } = seconds;
}

/// <summary>
/// Playback timeline with a draggable playhead and, for trim mode, two range
/// handles. It intentionally has no chrome so it can sit inside the preview.
/// </summary>
public sealed class MediaTimeline : FrameworkElement
{
    private const double HandleHit = 12;
    private const double MinimumSpan = 0.05;

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration), typeof(double), typeof(MediaTimeline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StartProperty = DependencyProperty.Register(
        nameof(Start), typeof(double), typeof(MediaTimeline),
        new FrameworkPropertyMetadata(0.0,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty EndProperty = DependencyProperty.Register(
        nameof(End), typeof(double), typeof(MediaTimeline),
        new FrameworkPropertyMetadata(0.0,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(double), typeof(MediaTimeline),
        new FrameworkPropertyMetadata(0.0,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty BackgroundImageProperty = DependencyProperty.Register(
        nameof(BackgroundImage), typeof(ImageSource), typeof(MediaTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(MediaTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowSelectionProperty = DependencyProperty.Register(
        nameof(ShowSelection), typeof(bool), typeof(MediaTimeline),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IEnumerable), typeof(MediaTimeline),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender, OnSegmentsChanged));

    private DragMode _drag;
    private bool _dragging;

    public MediaTimeline()
    {
        Focusable = false;
        SnapsToDevicePixels = true;
    }

    public event EventHandler<SeekRequestedEventArgs>? SeekRequested;
    public event EventHandler? RangeChanged;

    public double Duration
    {
        get => (double)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public double Start
    {
        get => (double)GetValue(StartProperty);
        set => SetValue(StartProperty, value);
    }

    public double End
    {
        get => (double)GetValue(EndProperty);
        set => SetValue(EndProperty, value);
    }

    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public ImageSource? BackgroundImage
    {
        get => (ImageSource?)GetValue(BackgroundImageProperty);
        set => SetValue(BackgroundImageProperty, value);
    }

    public Brush? Accent
    {
        get => (Brush?)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public bool ShowSelection
    {
        get => (bool)GetValue(ShowSelectionProperty);
        set => SetValue(ShowSelectionProperty, value);
    }

    public IEnumerable? Segments
    {
        get => (IEnumerable?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MediaTimeline timeline) return;
        timeline.Unhook(e.OldValue as IEnumerable);
        timeline.Hook(e.NewValue as IEnumerable);
        timeline.InvalidateVisual();
    }

    private void Hook(IEnumerable? segments)
    {
        if (segments is INotifyCollectionChanged collection)
            collection.CollectionChanged += OnSegmentsCollectionChanged;
        foreach (var item in Enumerate(segments))
        {
            if (item is INotifyPropertyChanged notify)
                notify.PropertyChanged += OnSegmentPropertyChanged;
        }
    }

    private void Unhook(IEnumerable? segments)
    {
        if (segments is INotifyCollectionChanged collection)
            collection.CollectionChanged -= OnSegmentsCollectionChanged;
        foreach (var item in Enumerate(segments))
        {
            if (item is INotifyPropertyChanged notify)
                notify.PropertyChanged -= OnSegmentPropertyChanged;
        }
    }

    private void OnSegmentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Unhook(e.OldItems);
        Hook(e.NewItems);
        InvalidateVisual();
    }

    private void OnSegmentPropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private static IEnumerable Enumerate(IEnumerable? source)
    {
        if (source is null) yield break;
        foreach (var item in source) yield return item;
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var track = TrackRect();
        if (track.Width <= 2 || track.Height < 4) return;

        var accent = Accent ?? new SolidColorBrush(Color.FromRgb(0x7C, 0x9C, 0xFF));
        var radius = Math.Min(8, track.Height / 2);
        context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x15, 0x1C, 0x2C)), null, track, radius, radius);

        if (BackgroundImage is not null)
        {
            context.PushClip(new RectangleGeometry(track, radius, radius));
            context.DrawImage(BackgroundImage, track);
            context.Pop();
        }
        else
        {
            context.PushClip(new RectangleGeometry(track, radius, radius));
            for (var i = 1; i < 12; i++)
            {
                var x = track.Left + track.Width * i / 12.0;
                var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(i % 3 == 0 ? 45 : 20), 255, 255, 255)), 0.7);
                pen.Freeze();
                context.DrawLine(pen, new Point(x, track.Top), new Point(x, track.Bottom));
            }
            context.Pop();
        }

        DrawSegments(context, track, accent);

        if (ShowSelection)
        {
            var start = Fraction(Start);
            var end = Fraction(End);
            if (end < start) (start, end) = (end, start);
            var left = track.Left + track.Width * start;
            var right = track.Left + track.Width * end;
            var selection = new Rect(left, track.Top, Math.Max(2, right - left), track.Height);

            var shade = new SolidColorBrush(Color.FromArgb(150, 3, 7, 14));
            shade.Freeze();
            if (left > track.Left)
                context.DrawRectangle(shade, null, new Rect(track.Left, track.Top, left - track.Left, track.Height));
            if (right < track.Right)
                context.DrawRectangle(shade, null, new Rect(right, track.Top, track.Right - right, track.Height));

            var wash = accent.Clone();
            wash.Opacity = 0.20;
            wash.Freeze();
            context.DrawRectangle(wash, null, selection);

            var border = new Pen(accent, 1.6);
            border.Freeze();
            context.DrawRoundedRectangle(null, border, selection, 4, 4);
            DrawRangeHandle(context, new Point(left, track.Top), track.Height);
            DrawRangeHandle(context, new Point(right, track.Top), track.Height);
        }

        DrawPlayhead(context, track);
    }

    private void DrawSegments(DrawingContext context, Rect track, Brush accent)
    {
        if (Segments is null) return;
        var duration = Math.Max(0.001, Duration);
        var index = 0;
        foreach (var raw in Segments)
        {
            if (raw is not ITimelineSegment segment || segment.Duration <= 0) continue;
            var left = track.Left + track.Width * Math.Clamp(segment.StartOffset / duration, 0, 1);
            var right = track.Left + track.Width * Math.Clamp((segment.StartOffset + segment.Duration) / duration, 0, 1);
            var rect = new Rect(left + 1, track.Top + 1, Math.Max(1, right - left - 2), track.Height - 2);
            var color = index % 2 == 0
                ? Color.FromArgb(80, 124, 156, 255)
                : Color.FromArgb(56, 198, 168, 255);
            var fill = new SolidColorBrush(color);
            fill.Freeze();
            context.DrawRoundedRectangle(fill, null, rect, 3, 3);

            if (rect.Width > 48)
            {
                var label = new FormattedText(segment.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 9, new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)),
                    VisualTreeHelper.GetDpi(this).PixelsPerDip)
                {
                    MaxTextWidth = rect.Width - 10,
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                context.DrawText(label, new Point(rect.Left + 5, rect.Top + (rect.Height - label.Height) / 2));
            }

            if (index > 0)
            {
                var boundary = new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), 0.9);
                boundary.Freeze();
                context.DrawLine(boundary, new Point(left, track.Top), new Point(left, track.Bottom));
            }
            index++;
        }
    }

    private static void DrawRangeHandle(DrawingContext context, Point point, double height)
    {
        var fill = Brushes.White;
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x7C, 0x9C, 0xFF)), 1.2);
        pen.Freeze();
        var rect = new Rect(point.X - 4, point.Y + 3, 8, Math.Max(8, height - 6));
        context.DrawRoundedRectangle(fill, pen, rect, 2, 2);
        var grip = new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x63, 0x80)), 1);
        grip.Freeze();
        context.DrawLine(grip, new Point(point.X, point.Y + 8), new Point(point.X, point.Y + height - 7));
    }

    private void DrawPlayhead(DrawingContext context, Rect track)
    {
        var fraction = Fraction(Position);
        var x = track.Left + track.Width * fraction;
        var line = new Pen(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), 1.5);
        line.Freeze();
        context.DrawLine(line, new Point(x, track.Top - 1), new Point(x, track.Bottom + 1));

        var marker = new StreamGeometry();
        using (var geometry = marker.Open())
        {
            geometry.BeginFigure(new Point(x - 5, track.Top - 7), true, true);
            geometry.LineTo(new Point(x + 5, track.Top - 7), true, false);
            geometry.LineTo(new Point(x, track.Top - 1), true, false);
        }
        marker.Freeze();
        context.DrawGeometry(Brushes.White, null, marker);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Duration <= 0) return;
        var track = TrackRect();
        var point = e.GetPosition(this);
        if (!track.Contains(point) && !new Rect(track.Left, 0, track.Width, ActualHeight).Contains(point)) return;

        _drag = DragMode.Scrub;
        if (ShowSelection)
        {
            var startX = track.Left + track.Width * Fraction(Start);
            var endX = track.Left + track.Width * Fraction(End);
            if (Math.Abs(point.X - startX) <= HandleHit)
                _drag = DragMode.Start;
            else if (Math.Abs(point.X - endX) <= HandleHit)
                _drag = DragMode.End;
        }

        _dragging = true;
        CaptureMouse();
        ApplyDrag(point, track);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var track = TrackRect();
        var point = e.GetPosition(this);
        if (!_dragging)
        {
            if (!track.Contains(point))
            {
                Cursor = Cursors.Arrow;
                return;
            }
            if (ShowSelection)
            {
                var startX = track.Left + track.Width * Fraction(Start);
                var endX = track.Left + track.Width * Fraction(End);
                if (Math.Abs(point.X - startX) <= HandleHit || Math.Abs(point.X - endX) <= HandleHit)
                {
                    Cursor = Cursors.SizeWE;
                    return;
                }
            }
            Cursor = Cursors.Hand;
            return;
        }

        ApplyDrag(point, track);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        _drag = DragMode.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (ShowSelection) RangeChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void ApplyDrag(Point point, Rect track)
    {
        var seconds = Math.Clamp((point.X - track.Left) / Math.Max(1, track.Width) * Duration, 0, Duration);
        switch (_drag)
        {
            case DragMode.Start:
            {
                var value = Math.Min(seconds, Math.Max(0, End - MinimumSpan));
                if (Math.Abs(value - Start) < 0.0001) return;
                SetCurrentValue(StartProperty, value);
                break;
            }
            case DragMode.End:
            {
                var value = Math.Max(seconds, Math.Min(Duration, Start + MinimumSpan));
                if (Math.Abs(value - End) < 0.0001) return;
                SetCurrentValue(EndProperty, value);
                break;
            }
            default:
            {
                if (Math.Abs(seconds - Position) < 0.0001) return;
                SetCurrentValue(PositionProperty, seconds);
                SeekRequested?.Invoke(this, new SeekRequestedEventArgs(seconds));
                return;
            }
        }

        RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    private double Fraction(double seconds) =>
        Duration <= 0 ? 0 : Math.Clamp(seconds / Duration, 0, 1);

    private Rect TrackRect()
    {
        var top = 7.0;
        var height = Math.Max(8, ActualHeight - 14);
        return new Rect(0, top, Math.Max(1, ActualWidth), height);
    }

    private enum DragMode
    {
        None,
        Scrub,
        Start,
        End,
    }
}
