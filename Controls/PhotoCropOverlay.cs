using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Nasaq.Controls;

/// <summary>
/// Interactive crop box that sits directly over the photo preview. The crop is
/// stored as percentages, so it survives resizing and matches the export path.
/// </summary>
public sealed class PhotoCropOverlay : FrameworkElement
{
    private const double HandleHit = 11;
    private const double MinimumPercent = 0.8;

    public static readonly DependencyProperty CropRectProperty = DependencyProperty.Register(
        nameof(CropRect), typeof(Rect), typeof(PhotoCropOverlay),
        new FrameworkPropertyMetadata(new Rect(0, 0, 100, 100),
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnCropChanged));

    public static readonly DependencyProperty SourceWidthProperty = DependencyProperty.Register(
        nameof(SourceWidth), typeof(int), typeof(PhotoCropOverlay),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SourceHeightProperty = DependencyProperty.Register(
        nameof(SourceHeight), typeof(int), typeof(PhotoCropOverlay),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(PhotoCropOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private DragMode _drag = DragMode.None;
    private Point _dragStart;
    private Point _dragStartPercent;
    private Rect _dragStartCrop;
    private bool _dragging;

    public event EventHandler? CropChangedByUser;

    public Rect CropRect
    {
        get => (Rect)GetValue(CropRectProperty);
        set => SetValue(CropRectProperty, value);
    }

    public int SourceWidth
    {
        get => (int)GetValue(SourceWidthProperty);
        set => SetValue(SourceWidthProperty, value);
    }

    public int SourceHeight
    {
        get => (int)GetValue(SourceHeightProperty);
        set => SetValue(SourceHeightProperty, value);
    }

    public Brush? Accent
    {
        get => (Brush?)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private static void OnCropChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PhotoCropOverlay overlay) overlay.InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (ActualWidth <= 2 || ActualHeight <= 2) return;

        var imageRect = GetImageRect();
        if (imageRect.Width <= 2 || imageRect.Height <= 2) return;

        // A transparent fill keeps the whole photo hit-testable.
        context.DrawRectangle(Brushes.Transparent, null, imageRect);

        var crop = Normalise(CropRect);
        var selection = ToSelectionRect(imageRect, crop);
        var accent = Accent ?? new SolidColorBrush(Color.FromRgb(0x7C, 0x9C, 0xFF));
        var shade = new SolidColorBrush(Color.FromArgb(148, 3, 7, 14));
        shade.Freeze();

        // Dim only the parts of the photo that will be removed.
        if (selection.Top > imageRect.Top)
            context.DrawRectangle(shade, null,
                new Rect(imageRect.Left, imageRect.Top, imageRect.Width, selection.Top - imageRect.Top));
        if (selection.Bottom < imageRect.Bottom)
            context.DrawRectangle(shade, null,
                new Rect(imageRect.Left, selection.Bottom, imageRect.Width, imageRect.Bottom - selection.Bottom));
        if (selection.Left > imageRect.Left)
            context.DrawRectangle(shade, null,
                new Rect(imageRect.Left, selection.Top, selection.Left - imageRect.Left, selection.Height));
        if (selection.Right < imageRect.Right)
            context.DrawRectangle(shade, null,
                new Rect(selection.Right, selection.Top, imageRect.Right - selection.Right, selection.Height));

        var border = new Pen(accent, 1.7);
        border.Freeze();
        context.DrawRectangle(null, border, selection);

        // Rule-of-thirds guides make composition easier without adding UI.
        var guide = new Pen(new SolidColorBrush(Color.FromArgb(108, 255, 255, 255)), 0.8);
        guide.Freeze();
        for (var i = 1; i <= 2; i++)
        {
            var x = selection.Left + selection.Width * i / 3.0;
            var y = selection.Top + selection.Height * i / 3.0;
            context.DrawLine(guide, new Point(x, selection.Top), new Point(x, selection.Bottom));
            context.DrawLine(guide, new Point(selection.Left, y), new Point(selection.Right, y));
        }

        DrawHandles(context, selection, accent);
        DrawSizeLabel(context, selection, crop);
    }

    private void DrawHandles(DrawingContext context, Rect selection, Brush accent)
    {
        var fill = Brushes.White;
        var pen = new Pen(accent, 1.2);
        pen.Freeze();
        var points = new[]
        {
            new Point(selection.Left, selection.Top),
            new Point(selection.Left + selection.Width / 2, selection.Top),
            new Point(selection.Right, selection.Top),
            new Point(selection.Right, selection.Top + selection.Height / 2),
            new Point(selection.Right, selection.Bottom),
            new Point(selection.Left + selection.Width / 2, selection.Bottom),
            new Point(selection.Left, selection.Bottom),
            new Point(selection.Left, selection.Top + selection.Height / 2),
        };

        foreach (var point in points)
        {
            var handle = new Rect(point.X - 5, point.Y - 5, 10, 10);
            context.DrawRoundedRectangle(fill, pen, handle, 2, 2);
        }
    }

    private void DrawSizeLabel(DrawingContext context, Rect selection, Rect crop)
    {
        if (SourceWidth <= 0 || SourceHeight <= 0) return;
        var width = Math.Max(1, (int)Math.Round(SourceWidth * crop.Width / 100.0));
        var height = Math.Max(1, (int)Math.Round(SourceHeight * crop.Height / 100.0));
        var text = new FormattedText($"{width} × {height} px", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10.5, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var padding = new Size(7, 3);
        var widthWithPadding = text.Width + padding.Width * 2;
        var heightWithPadding = text.Height + padding.Height * 2;
        var x = Math.Clamp(selection.Left + 6, 2, Math.Max(2, ActualWidth - widthWithPadding - 2));
        var y = selection.Top - heightWithPadding - 6;
        if (y < 2) y = selection.Top + 6;

        var background = new SolidColorBrush(Color.FromArgb(214, 7, 12, 22));
        background.Freeze();
        context.DrawRoundedRectangle(background, null,
            new Rect(x, y, widthWithPadding, heightWithPadding), 4, 4);
        context.DrawText(text, new Point(x + padding.Width, y + padding.Height));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEnabled || SourceWidth <= 0 || SourceHeight <= 0) return;

        var imageRect = GetImageRect();
        var point = e.GetPosition(this);
        if (!imageRect.Contains(point)) return;

        var crop = Normalise(CropRect);
        var selection = ToSelectionRect(imageRect, crop);
        _dragStart = point;
        _dragStartCrop = crop;
        _dragStartPercent = ToPercent(imageRect, point);
        _dragging = true;
        _drag = HitTest(selection, point);
        if (_drag == DragMode.None)
        {
            // Starting outside the box creates a fresh selection, just like a
            // native crop tool. The rect only changes once the mouse moves.
            _drag = DragMode.New;
        }

        CaptureMouse();
        e.Handled = true;
        UpdateCursor(point, selection);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        var imageRect = GetImageRect();
        if (!_dragging)
        {
            if (SourceWidth > 0 && SourceHeight > 0 && imageRect.Contains(point))
                UpdateCursor(point, ToSelectionRect(imageRect, Normalise(CropRect)));
            return;
        }

        if (imageRect.Width <= 1 || imageRect.Height <= 1) return;
        var current = ToPercent(imageRect, point);

        switch (_drag)
        {
            case DragMode.New:
            {
                var start = _dragStartPercent;
                var x = Math.Min(start.X, current.X);
                var y = Math.Min(start.Y, current.Y);
                var width = Math.Abs(current.X - start.X);
                var height = Math.Abs(current.Y - start.Y);
                SetCrop(new Rect(x, y, width, height));
                break;
            }
            case DragMode.Move:
            {
                var dx = (point.X - _dragStart.X) / imageRect.Width * 100.0;
                var dy = (point.Y - _dragStart.Y) / imageRect.Height * 100.0;
                SetCrop(new Rect(_dragStartCrop.X + dx, _dragStartCrop.Y + dy,
                    _dragStartCrop.Width, _dragStartCrop.Height));
                break;
            }
            default:
            {
                var crop = _dragStartCrop;
                var left = crop.Left;
                var top = crop.Top;
                var right = crop.Right;
                var bottom = crop.Bottom;

                if (_drag is DragMode.Left or DragMode.TopLeft or DragMode.BottomLeft)
                    left = Math.Min(current.X, right - MinimumPercent);
                if (_drag is DragMode.Right or DragMode.TopRight or DragMode.BottomRight)
                    right = Math.Max(current.X, left + MinimumPercent);
                if (_drag is DragMode.Top or DragMode.TopLeft or DragMode.TopRight)
                    top = Math.Min(current.Y, bottom - MinimumPercent);
                if (_drag is DragMode.Bottom or DragMode.BottomLeft or DragMode.BottomRight)
                    bottom = Math.Max(current.Y, top + MinimumPercent);

                SetCrop(new Rect(left, top, right - left, bottom - top));
                break;
            }
        }

        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        _drag = DragMode.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        CropChangedByUser?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_dragging) Cursor = Cursors.Arrow;
    }

    private void SetCrop(Rect value)
    {
        var clamped = Normalise(value);
        if (Math.Abs(clamped.X - CropRect.X) < 0.001 &&
            Math.Abs(clamped.Y - CropRect.Y) < 0.001 &&
            Math.Abs(clamped.Width - CropRect.Width) < 0.001 &&
            Math.Abs(clamped.Height - CropRect.Height) < 0.001) return;

        SetCurrentValue(CropRectProperty, clamped);
    }

    private static Rect Normalise(Rect value)
    {
        var x = Math.Clamp(value.X, 0, 100 - MinimumPercent);
        var y = Math.Clamp(value.Y, 0, 100 - MinimumPercent);
        var width = Math.Clamp(value.Width, MinimumPercent, 100 - x);
        var height = Math.Clamp(value.Height, MinimumPercent, 100 - y);
        return new Rect(x, y, width, height);
    }

    private Rect GetImageRect()
    {
        if (SourceWidth <= 0 || SourceHeight <= 0) return Rect.Empty;
        var scale = Math.Min(ActualWidth / SourceWidth, ActualHeight / SourceHeight);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) return Rect.Empty;
        var width = SourceWidth * scale;
        var height = SourceHeight * scale;
        return new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height);
    }

    private static Rect ToSelectionRect(Rect imageRect, Rect crop) => new(
        imageRect.Left + imageRect.Width * crop.X / 100.0,
        imageRect.Top + imageRect.Height * crop.Y / 100.0,
        imageRect.Width * crop.Width / 100.0,
        imageRect.Height * crop.Height / 100.0);

    private static Point ToPercent(Rect imageRect, Point point) => new(
        Math.Clamp((point.X - imageRect.Left) / imageRect.Width * 100.0, 0, 100),
        Math.Clamp((point.Y - imageRect.Top) / imageRect.Height * 100.0, 0, 100));

    private static DragMode HitTest(Rect selection, Point point)
    {
        var nearLeft = Math.Abs(point.X - selection.Left) <= HandleHit;
        var nearRight = Math.Abs(point.X - selection.Right) <= HandleHit;
        var nearTop = Math.Abs(point.Y - selection.Top) <= HandleHit;
        var nearBottom = Math.Abs(point.Y - selection.Bottom) <= HandleHit;
        var insideX = point.X >= selection.Left - HandleHit && point.X <= selection.Right + HandleHit;
        var insideY = point.Y >= selection.Top - HandleHit && point.Y <= selection.Bottom + HandleHit;

        if (nearLeft && nearTop) return DragMode.TopLeft;
        if (nearRight && nearTop) return DragMode.TopRight;
        if (nearLeft && nearBottom) return DragMode.BottomLeft;
        if (nearRight && nearBottom) return DragMode.BottomRight;
        if (nearLeft && insideY) return DragMode.Left;
        if (nearRight && insideY) return DragMode.Right;
        if (nearTop && insideX) return DragMode.Top;
        if (nearBottom && insideX) return DragMode.Bottom;
        return selection.Contains(point) ? DragMode.Move : DragMode.None;
    }

    private void UpdateCursor(Point point, Rect selection)
    {
        Cursor = HitTest(selection, point) switch
        {
            DragMode.TopLeft or DragMode.BottomRight => Cursors.SizeNWSE,
            DragMode.TopRight or DragMode.BottomLeft => Cursors.SizeNESW,
            DragMode.Left or DragMode.Right => Cursors.SizeWE,
            DragMode.Top or DragMode.Bottom => Cursors.SizeNS,
            DragMode.Move => Cursors.SizeAll,
            _ => Cursors.Cross,
        };
    }

    private enum DragMode
    {
        None,
        New,
        Move,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }
}
