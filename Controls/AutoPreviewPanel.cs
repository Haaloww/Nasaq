using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Nasaq.Services;

namespace Nasaq.Controls;

/// <summary>
/// A preview surface that animates itself to the shape of the media attached to
/// it. Give it an aspect from width/height values, or bind <see cref="AspectSource"/>
/// to an image and it will use that image's natural pixel dimensions. Without an
/// attached file it falls back to filling the available workspace.
/// </summary>
public sealed class AutoPreviewPanel : Decorator
{
    public static readonly DependencyProperty AspectWidthProperty = DependencyProperty.Register(
        nameof(AspectWidth), typeof(double), typeof(AutoPreviewPanel),
        new FrameworkPropertyMetadata(0.0, OnSizingChanged));

    public static readonly DependencyProperty AspectHeightProperty = DependencyProperty.Register(
        nameof(AspectHeight), typeof(double), typeof(AutoPreviewPanel),
        new FrameworkPropertyMetadata(0.0, OnSizingChanged));

    public static readonly DependencyProperty AspectSourceProperty = DependencyProperty.Register(
        nameof(AspectSource), typeof(ImageSource), typeof(AutoPreviewPanel),
        new FrameworkPropertyMetadata(null, OnSizingChanged));

    public static readonly DependencyProperty MinimumPreviewWidthProperty = DependencyProperty.Register(
        nameof(MinimumPreviewWidth), typeof(double), typeof(AutoPreviewPanel),
        new FrameworkPropertyMetadata(0.0, OnSizingChanged));

    public static readonly DependencyProperty MinimumPreviewHeightProperty = DependencyProperty.Register(
        nameof(MinimumPreviewHeight), typeof(double), typeof(AutoPreviewPanel),
        new FrameworkPropertyMetadata(0.0, OnSizingChanged));

    public static readonly DependencyProperty AnimateResizeProperty = DependencyProperty.Register(
        nameof(AnimateResize), typeof(bool), typeof(AutoPreviewPanel),
        new FrameworkPropertyMetadata(true, OnSizingChanged));

    private FrameworkElement? _parent;
    private int _resizeToken;
    private double _targetWidth;
    private double _targetHeight;
    private bool _targetFill;

    public AutoPreviewPanel()
    {
        ClipToBounds = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public double AspectWidth
    {
        get => (double)GetValue(AspectWidthProperty);
        set => SetValue(AspectWidthProperty, value);
    }

    public double AspectHeight
    {
        get => (double)GetValue(AspectHeightProperty);
        set => SetValue(AspectHeightProperty, value);
    }

    public ImageSource? AspectSource
    {
        get => (ImageSource?)GetValue(AspectSourceProperty);
        set => SetValue(AspectSourceProperty, value);
    }

    public double MinimumPreviewWidth
    {
        get => (double)GetValue(MinimumPreviewWidthProperty);
        set => SetValue(MinimumPreviewWidthProperty, value);
    }

    public double MinimumPreviewHeight
    {
        get => (double)GetValue(MinimumPreviewHeightProperty);
        set => SetValue(MinimumPreviewHeightProperty, value);
    }

    public bool AnimateResize
    {
        get => (bool)GetValue(AnimateResizeProperty);
        set => SetValue(AnimateResizeProperty, value);
    }

    private static void OnSizingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AutoPreviewPanel panel) panel.UpdateSize();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _parent = Parent as FrameworkElement;
        if (_parent is not null) _parent.SizeChanged += Parent_SizeChanged;
        AnimationGate.Changed -= AnimationGate_Changed;
        AnimationGate.Changed += AnimationGate_Changed;
        Dispatcher.BeginInvoke(new Action(UpdateSize), DispatcherPriority.Loaded);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_parent is not null) _parent.SizeChanged -= Parent_SizeChanged;
        _parent = null;
        AnimationGate.Changed -= AnimationGate_Changed;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            Dispatcher.BeginInvoke(new Action(UpdateSize), DispatcherPriority.Loaded);
    }

    private void Parent_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSize();

    private void UpdateSize()
    {
        if (!IsLoaded || !IsVisible) return;

        var parent = _parent ?? Parent as FrameworkElement;
        if (parent is null) return;

        var availableWidth = parent.ActualWidth - Margin.Left - Margin.Right;
        var availableHeight = parent.ActualHeight - Margin.Top - Margin.Bottom;
        if (availableWidth <= 0 || availableHeight <= 0) return;

        var (aspectWidth, aspectHeight) = ResolveAspect();

        if (aspectWidth <= 0 || aspectHeight <= 0)
        {
            AnimateTo(availableWidth, availableHeight, fill: true);
            return;
        }

        var scale = Math.Min(availableWidth / aspectWidth, availableHeight / aspectHeight);
        var targetWidth = Math.Max(1.0, aspectWidth * scale);
        var targetHeight = Math.Max(1.0, aspectHeight * scale);

        // Keep tiny attachments readable without ever exceeding the workspace.
        var minimumWidth = Math.Min(MinimumPreviewWidth, availableWidth);
        var minimumHeight = Math.Min(MinimumPreviewHeight, availableHeight);
        if (targetWidth < minimumWidth || targetHeight < minimumHeight)
        {
            var grow = Math.Max(minimumWidth / targetWidth, minimumHeight / targetHeight);
            var allowed = Math.Min(availableWidth / targetWidth, availableHeight / targetHeight);
            grow = Math.Clamp(grow, 1.0, Math.Max(1.0, allowed));
            targetWidth *= grow;
            targetHeight *= grow;
        }

        targetWidth = Math.Min(availableWidth, Math.Round(targetWidth));
        targetHeight = Math.Min(availableHeight, Math.Round(targetHeight));
        AnimateTo(targetWidth, targetHeight, fill: false);
    }

    private (double Width, double Height) ResolveAspect()
    {
        if (AspectWidth > 0 && AspectHeight > 0)
            return (AspectWidth, AspectHeight);

        if (AspectSource is BitmapSource bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
            return (bitmap.PixelWidth, bitmap.PixelHeight);

        return (0, 0);
    }

    private void AnimateTo(double targetWidth, double targetHeight, bool fill)
    {
        _targetWidth = targetWidth;
        _targetHeight = targetHeight;
        _targetFill = fill;

        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;

        var fromWidth = double.IsNaN(Width) || Width <= 0 ? ActualWidth : Width;
        var fromHeight = double.IsNaN(Height) || Height <= 0 ? ActualHeight : Height;
        if (fromWidth <= 0) fromWidth = targetWidth;
        if (fromHeight <= 0) fromHeight = targetHeight;

        Width = fromWidth;
        Height = fromHeight;

        var token = ++_resizeToken;

        if (!AnimateResize || !AnimationGate.IsEnabled)
        {
            FinishResize(token, targetWidth, targetHeight, fill);
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(320);

        var widthAnimation = new DoubleAnimation(fromWidth, targetWidth, duration) { EasingFunction = ease };
        widthAnimation.Completed += (_, _) => FinishResize(token, targetWidth, targetHeight, fill);
        BeginAnimation(WidthProperty, widthAnimation);

        BeginAnimation(HeightProperty,
            new DoubleAnimation(fromHeight, targetHeight, duration) { EasingFunction = ease });
    }

    private void FinishResize(int token, double targetWidth, double targetHeight, bool fill)
    {
        if (token != _resizeToken) return;

        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);

        if (fill)
        {
            Width = double.NaN;
            Height = double.NaN;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            Width = targetWidth;
            Height = targetHeight;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
        }
    }

    private void AnimationGate_Changed(object? sender, EventArgs e)
    {
        if (AnimationGate.IsEnabled) return;
        if (_targetWidth <= 0 || _targetHeight <= 0)
        {
            UpdateSize();
            return;
        }

        FinishResize(_resizeToken, _targetWidth, _targetHeight, _targetFill);
    }
}
