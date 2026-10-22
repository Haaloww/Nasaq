using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Nasaq.Core;
using Nasaq.Services;

namespace Nasaq.Controls;

public partial class AutoImageDropZone : UserControl
{
    public static readonly DependencyProperty HasImageProperty = DependencyProperty.Register(
        nameof(HasImage), typeof(bool), typeof(AutoImageDropZone), new PropertyMetadata(false));

    public static readonly DependencyProperty IsDragOverProperty = DependencyProperty.Register(
        nameof(IsDragOver), typeof(bool), typeof(AutoImageDropZone), new PropertyMetadata(false));

    private int _resizeToken;
    private double _restingWidth;
    private double _targetWidth;
    private double _targetHeight;

    public AutoImageDropZone()
    {
        InitializeComponent();
    }

    /// <summary>Raised with every valid image path so the queue can add them.</summary>
    public event Action<IReadOnlyList<string>>? ImagesAccepted;

    public bool HasImage
    {
        get => (bool)GetValue(HasImageProperty);
        private set => SetValue(HasImageProperty, value);
    }

    public bool IsDragOver
    {
        get => (bool)GetValue(IsDragOverProperty);
        private set => SetValue(IsDragOverProperty, value);
    }

    // ───────────────────────── public API ─────────────────────────

    public void AcceptPaths(IEnumerable<string> paths)
    {
        var images = new List<(string Path, int Width, int Height)>();
        var invalid = new List<string>();
        var unreadable = new List<string>();

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = raw.Trim();

            if (FormatCatalog.Detect(path) != MediaCategory.Image)
            {
                invalid.Add(path);
                continue;
            }

            if (!TryReadImageInfo(path, out var width, out var height, out _))
            {
                unreadable.Add(path);
                continue;
            }

            images.Add((path, width, height));
        }

        if (images.Count > 0)
        {
            ShowImageCore(images[0].Path, images[0].Width, images[0].Height);
            ImagesAccepted?.Invoke(images.Select(image => image.Path).ToList());
            if (images.Count > 1)
            {
                ToastService.Info("Showing the first image",
                    $"{images.Count} images were added. The preview is showing {Path.GetFileName(images[0].Path)}.");
            }
        }

        if (invalid.Count > 0)
        {
            var name = invalid.Count == 1
                ? $"“{Path.GetFileName(invalid[0])}”"
                : $"{invalid.Count} files";
            ToastService.Error("That's not an image",
                $"{name} can't be previewed. Drop a PNG, JPG, WebP, GIF, BMP, TIFF or ICO file.");
        }

        if (unreadable.Count > 0)
        {
            var name = unreadable.Count == 1
                ? $"“{Path.GetFileName(unreadable[0])}”"
                : $"{unreadable.Count} images";
            ToastService.Error("Couldn't open that image",
                $"{name} could not be decoded. The file may be damaged or use an unsupported codec.");
        }
    }

    public bool ShowImage(string path)
    {
        if (!TryReadImageInfo(path, out var naturalWidth, out var naturalHeight, out var error))
        {
            Log.Warn("preview.image", "Image could not be read", ("path", path), ("reason", error ?? "unknown"));
            ToastService.Error("Couldn't open that image",
                $"“{Path.GetFileName(path)}” could not be read. Try a PNG, JPG, WebP, GIF, BMP, TIFF or ICO file.");
            return false;
        }

        ShowImageCore(path, naturalWidth, naturalHeight);
        return true;
    }

    private void ShowImageCore(string path, int naturalWidth, int naturalHeight)
    {
        var (targetWidth, targetHeight) = CalculateTargetSize(naturalWidth, naturalHeight);

        if (double.IsNaN(Width) || Width <= 0)
        {
            var currentWidth = ActualWidth > 0 ? ActualWidth : GetAvailableWidth();
            var currentHeight = ActualHeight > 0 ? ActualHeight : 116;
            if (currentWidth <= 0) currentWidth = GetAvailableWidth();
            _restingWidth = currentWidth;
            Width = currentWidth;
            Height = currentHeight;
        }
        else if (_restingWidth <= 0)
        {
            _restingWidth = ActualWidth > 0 ? ActualWidth : GetAvailableWidth();
        }

        var bitmap = LoadPreviewBitmap(path, naturalWidth, targetWidth);
        PreviewImage.Source = bitmap;
        FileNameText.Text = Path.GetFileName(path);
        var meta = BuildMeta(path, naturalWidth, naturalHeight);
        ImageMetaText.Text = meta;
        ImageMetaText.ToolTip = $"{meta} · preview box {targetWidth:0} × {targetHeight:0} DIP";

        HasImage = true;
        IdlePanel.Visibility = Visibility.Collapsed;
        DashOutline.Visibility = Visibility.Collapsed;
        PreviewPanel.Visibility = Visibility.Visible;
        AnimatePreviewIn();
        AnimateSize(targetWidth, targetHeight, restore: false);

        Log.Debug("preview.image", "Image preview auto-fitted",
            ("file", Path.GetFileName(path)),
            ("natural", $"{naturalWidth}x{naturalHeight}"),
            ("display", $"{targetWidth:0}x{targetHeight:0}"));
    }

    public void ClearPreview()
    {
        if (!HasImage) return;

        HasImage = false;
        PreviewImage.Source = null;
        PreviewPanel.BeginAnimation(OpacityProperty, null);
        PreviewPanel.Opacity = 1;
        PreviewPanel.Visibility = Visibility.Collapsed;
        IdlePanel.Visibility = Visibility.Visible;
        DashOutline.Visibility = Visibility.Visible;

        var restingWidth = _restingWidth > 0 ? _restingWidth : GetAvailableWidth();
        AnimateSize(restingWidth, 116, restore: true);
    }

    // ───────────────────────── sizing ─────────────────────────

    private (double Width, double Height) CalculateTargetSize(int naturalWidth, int naturalHeight)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var availableWidth = GetAvailableWidth();
        var availableHeight = GetAvailableHeight();

        // One image pixel should never be stretched over more than one physical
        // device pixel, so the display stays crisp on any DPI setting.
        var naturalDipWidth = naturalWidth / Math.Max(1.0, dpi.DpiScaleX);
        var naturalDipHeight = naturalHeight / Math.Max(1.0, dpi.DpiScaleY);

        var fit = Math.Min(1.0, Math.Min(
            availableWidth / Math.Max(1.0, naturalDipWidth),
            availableHeight / Math.Max(1.0, naturalDipHeight)));

        var imageWidth = Math.Max(1.0, naturalDipWidth * fit);
        var imageHeight = Math.Max(1.0, naturalDipHeight * fit);

        // Pad the container to a readable minimum without ever upscaling the
        // image: the Image control uses DownOnly with Uniform stretch, so very
        // small or extremely tall files stay centred and pixel-perfect.
        const double minimumWidth = 300;
        const double minimumHeight = 170;
        var width = Math.Min(availableWidth, Math.Max(minimumWidth, imageWidth));
        var height = Math.Min(availableHeight, Math.Max(minimumHeight, imageHeight));

        return (Math.Max(1, Math.Round(width)), Math.Max(1, Math.Round(height)));
    }

    private double GetAvailableWidth()
    {
        // The parent is the left-hand queue column. If the control has an
        // explicit preview width, the parent still reports the full slot.
        var parentWidth = (Parent as FrameworkElement)?.ActualWidth ?? 0;
        if (parentWidth <= 0) parentWidth = ActualWidth;
        if (parentWidth <= 0) parentWidth = 760;

        var screenLimit = SystemParameters.WorkArea.Width * 0.9;
        return Math.Max(220, Math.Min(parentWidth, screenLimit));
    }

    private double GetAvailableHeight()
    {
        var work = SystemParameters.WorkArea;
        var limit = work.Height * 0.52;

        // Leave the queue, its header and the action row visible in the app.
        var window = Window.GetWindow(this);
        if (window is not null && window.ActualHeight > 0)
            limit = Math.Min(limit, window.ActualHeight - 390);

        return Math.Max(170, limit);
    }

    private void AnimateSize(double targetWidth, double targetHeight, bool restore)
    {
        var token = ++_resizeToken;
        _targetWidth = targetWidth;
        _targetHeight = targetHeight;

        var fromWidth = double.IsNaN(Width) || Width <= 0 ? ActualWidth : Width;
        var fromHeight = double.IsNaN(Height) || Height <= 0 ? ActualHeight : Height;
        if (fromWidth <= 0) fromWidth = targetWidth;
        if (fromHeight <= 0) fromHeight = targetHeight;

        Width = fromWidth;
        Height = fromHeight;

        if (!AnimationGate.IsEnabled)
        {
            FinishResize(token, targetWidth, targetHeight, restore);
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(restore ? 260 : 380);

        var widthAnimation = new DoubleAnimation(fromWidth, targetWidth, duration) { EasingFunction = ease };
        widthAnimation.Completed += (_, _) => FinishResize(token, targetWidth, targetHeight, restore);
        BeginAnimation(WidthProperty, widthAnimation);

        var heightAnimation = new DoubleAnimation(fromHeight, targetHeight, duration) { EasingFunction = ease };
        BeginAnimation(HeightProperty, heightAnimation);
    }

    private void FinishResize(int token, double targetWidth, double targetHeight, bool restore)
    {
        if (token != _resizeToken) return;

        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);

        if (restore)
        {
            // Returning to an automatic width lets the zone stretch with the
            // queue column again; the height returns to its compact drop state.
            Width = double.NaN;
            Height = 116;
        }
        else
        {
            Width = targetWidth;
            Height = targetHeight;
        }
    }

    private void AnimatePreviewIn()
    {
        PreviewPanel.BeginAnimation(OpacityProperty, null);
        if (!AnimationGate.IsEnabled)
        {
            PreviewPanel.Opacity = 1;
            return;
        }

        PreviewPanel.Opacity = 0;
        PreviewPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    // ───────────────────────── image loading ─────────────────────────

    private static bool TryReadImageInfo(string path, out int width, out int height, out string? error)
    {
        width = 0;
        height = 0;
        error = null;

        try
        {
            if (!File.Exists(path))
            {
                error = "The file does not exist.";
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                error = "The file has no image frames.";
                return false;
            }

            width = decoder.Frames[0].PixelWidth;
            height = decoder.Frames[0].PixelHeight;
            if (width <= 0 || height <= 0)
            {
                error = "The image has invalid dimensions.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private BitmapImage? LoadPreviewBitmap(string path, int naturalWidth, double targetWidth)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var decodedWidth = (int)Math.Round(Math.Min(naturalWidth, Math.Max(1.0, targetWidth * dpi.DpiScaleX)));

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.DecodePixelWidth = Math.Max(1, decodedWidth);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Warn("preview.image", "Preview bitmap could not be created", ("path", path), ("reason", ex.Message));
            return null;
        }
    }

    private static string BuildMeta(string path, int naturalWidth, int naturalHeight)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var bytes = 0L;
        try { bytes = new FileInfo(path).Length; } catch { }
        var megapixels = naturalWidth * (double)naturalHeight / 1_000_000;
        return $"{naturalWidth} × {naturalHeight} px · {megapixels:0.0} MP · {FileSize.Format(bytes)} · {extension}";
    }

    // ───────────────────────── drop / browse ─────────────────────────

    private void Zone_DragEnter(object sender, DragEventArgs e) => SetDragState(true, e);

    private void Zone_DragOver(object sender, DragEventArgs e) => SetDragState(true, e);

    private void Zone_DragLeave(object sender, DragEventArgs e) => IsDragOver = false;

    private void SetDragState(bool active, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            IsDragOver = false;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        IsDragOver = active;
    }

    private void Zone_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        IsDragOver = false;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        Log.Info("preview.drop", $"Dropped {paths.Length} item(s) on the image preview zone",
            ("first", paths[0]));
        AcceptPaths(paths);
    }

    private void Zone_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        Browse();
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Browse();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ClearPreview();
    }

    private void Browse()
    {
        var extensions = string.Join(";", FormatCatalog.InputExtensionsFor(MediaCategory.Image).Select(ext => "*" + ext));
        var dialog = new OpenFileDialog
        {
            Title = "Preview an image",
            Multiselect = false,
            CheckFileExists = true,
            Filter = $"Images|{extensions}|All files|*.*",
        };

        var owner = Window.GetWindow(this);
        var accepted = owner is null ? dialog.ShowDialog() == true : dialog.ShowDialog(owner) == true;
        if (accepted) AcceptPaths(dialog.FileNames);
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    // ───────────────────────── lifecycle ─────────────────────────

    private void Zone_Loaded(object sender, RoutedEventArgs e)
    {
        if (!HasImage && ActualWidth > 0) _restingWidth = ActualWidth;
        AnimationGate.Changed -= AnimationGate_Changed;
        AnimationGate.Changed += AnimationGate_Changed;
    }

    private void Zone_Unloaded(object sender, RoutedEventArgs e)
    {
        AnimationGate.Changed -= AnimationGate_Changed;
    }

    private void AnimationGate_Changed(object? sender, EventArgs e)
    {
        if (AnimationGate.IsEnabled) return;

        PreviewPanel.BeginAnimation(OpacityProperty, null);
        PreviewPanel.Opacity = 1;
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);

        if (HasImage)
        {
            Width = _targetWidth;
            Height = _targetHeight;
        }
        else
        {
            Width = double.NaN;
            Height = 116;
        }
    }

    private void PreviewImage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0)
        {
            PreviewImage.Clip = null;
            return;
        }

        PreviewImage.Clip = new RectangleGeometry(
            new Rect(0, 0, PreviewImage.ActualWidth, PreviewImage.ActualHeight), 12, 12);
    }
}
