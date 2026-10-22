using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nasaq.Services;

namespace Nasaq.Core;

/// <summary>
/// WPF/WIC photo editing: crop, caption and export to PNG or JPG.
/// Unsupported WIC formats are decoded through FFmpeg when it is available.
/// </summary>
public sealed class PhotoEditor
{
    private readonly ConversionService _engine;

    public PhotoEditor(ConversionService engine) => _engine = engine;

    public async Task<PhotoSourceInfo> LoadInfoAsync(string path, CancellationToken ct)
    {
        using var source = await PrepareAsync(path, ct);
        return source.Info;
    }

    public async Task<BitmapSource> RenderPreviewAsync(PhotoEditRequest request, int maxWidth, int maxHeight,
        CancellationToken ct)
    {
        using var source = await PrepareAsync(request.SourcePath, ct);
        ct.ThrowIfCancellationRequested();
        return await WpfImageConverter.RunStaAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            var image = Decode(source.Path);
            var crop = ResolveCrop(image.PixelWidth, image.PixelHeight, request);
            var preview = Compose(image, crop, maxWidth, maxHeight, request.Caption, flatten: false);
            preview.Freeze();
            return preview;
        });
    }

    public async Task<EditorResult> ExportAsync(PhotoEditRequest request, IProgress<EditorProgress>? progress,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string? temporary = null;
        try
        {
            progress?.Report(new EditorProgress(0.08, "Reading photo"));
            using var source = await PrepareAsync(request.SourcePath, ct);
            ct.ThrowIfCancellationRequested();

            var output = EditorFileNaming.UniquePath(
                string.IsNullOrWhiteSpace(request.OutputDirectory)
                    ? AppSettings.DefaultOutputDirectory()
                    : request.OutputDirectory,
                EditorFileNaming.SafeStem(request.SourcePath) + "-edited",
                request.Format, request.Overwrite);

            temporary = Path.Combine(Path.GetDirectoryName(output)!, $".nasaq-{Guid.NewGuid():N}.{request.Format}");
            var width = 0;
            var height = 0;

            await WpfImageConverter.RunStaAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new EditorProgress(0.38, "Cropping"));
                var image = Decode(source.Path);
                var crop = ResolveCrop(image.PixelWidth, image.PixelHeight, request);
                progress?.Report(new EditorProgress(0.62, request.Caption.HasText ? "Adding caption" : "Composing"));
                var composed = Compose(image, crop, 0, 0, request.Caption,
                    flatten: request.Format.Equals("jpg", StringComparison.OrdinalIgnoreCase));
                width = composed.PixelWidth;
                height = composed.PixelHeight;
                ct.ThrowIfCancellationRequested();
                progress?.Report(new EditorProgress(0.86, "Encoding " + request.Format.ToUpperInvariant()));
                Encode(composed, temporary, request.Format, request.Quality);
                return true;
            });

            ct.ThrowIfCancellationRequested();
            EditorFileNaming.MoveWithRetry(temporary, output, overwrite: true);
            temporary = null;
            stopwatch.Stop();
            progress?.Report(new EditorProgress(1, "Saved"));
            Log.Info("editor.photo", "Photo edit exported",
                ("source", request.SourcePath), ("output", output),
                ("size", $"{width}x{height}"), ("format", request.Format));
            return EditorResult.Ok(output, stopwatch.Elapsed, width, height);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return EditorResult.Cancelled(stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log.Warn("editor.photo", "Photo edit failed", ("source", request.SourcePath), ("reason", ex.Message));
            return EditorResult.Fail("The photo could not be saved.", ex.Message, stopwatch.Elapsed);
        }
        finally
        {
            if (temporary is not null) TryDelete(temporary);
        }
    }

    // ───────────────────────── decoding ─────────────────────────

    private sealed record PreparedSource(string Path, PhotoSourceInfo Info, string? Temporary) : IDisposable
    {
        public void Dispose()
        {
            if (Temporary is not null) TryDelete(Temporary);
        }
    }

    private async Task<PreparedSource> PrepareAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("The photo no longer exists.");

        if (WpfImageConverter.CanDecode(path))
        {
            try
            {
                var info = await WpfImageConverter.RunStaAsync(() => ReadInfo(path));
                return new PreparedSource(path, info, null);
            }
            catch (Exception ex) when (_engine.Ffmpeg.IsAvailable)
            {
                Log.Debug("editor.photo", "WIC could not read the photo; trying FFmpeg",
                    ("path", path), ("reason", ex.Message));
            }
        }

        if (!_engine.Ffmpeg.IsAvailable)
            throw new InvalidOperationException(
                "This photo format needs FFmpeg. Install FFmpeg or point to it in Settings → Advanced.");

        var tempDirectory = Path.Combine(Path.GetTempPath(), "Nasaq");
        Directory.CreateDirectory(tempDirectory);
        var temporary = Path.Combine(tempDirectory, $"photo-{Guid.NewGuid():N}.png");
        var result = await ProcessRunner.RunAsync(_engine.Ffmpeg.FfmpegPath!,
            new[]
            {
                "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                "-i", path, "-frames:v", "1", temporary,
            }, null, null, ct);

        if (result.ExitCode != 0 || !File.Exists(temporary))
        {
            TryDelete(temporary);
            throw new InvalidOperationException("FFmpeg could not decode this photo.",
                new Exception(LastError(result.StdErr)));
        }

        var decoded = await WpfImageConverter.RunStaAsync(() => ReadInfo(temporary));
        return new PreparedSource(temporary, decoded with { Path = path }, temporary);
    }

    private static PhotoSourceInfo ReadInfo(string path)
    {
        var image = Decode(path);
        return new PhotoSourceInfo(path, image.PixelWidth, image.PixelHeight, EditorFileNaming.SafeSize(path));
    }

    private static BitmapSource Decode(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidOperationException("The image contains no frames.");
        var frame = decoder.Frames[0];
        return WpfImageConverter.NormaliseOrientation(frame);
    }

    // ───────────────────────── composition ─────────────────────────

    private static Int32Rect ResolveCrop(int width, int height, PhotoEditRequest request)
    {
        var left = Math.Clamp(request.CropLeft, 0, 99);
        var top = Math.Clamp(request.CropTop, 0, 99);
        var right = Math.Clamp(request.CropRight, left + 1, 100);
        var bottom = Math.Clamp(request.CropBottom, top + 1, 100);
        if (right - left < 1 || bottom - top < 1)
            throw new InvalidOperationException("The crop area is too small.");

        var x = (int)Math.Round(width * left / 100.0);
        var y = (int)Math.Round(height * top / 100.0);
        var w = Math.Max(1, (int)Math.Round(width * (right - left) / 100.0));
        var h = Math.Max(1, (int)Math.Round(height * (bottom - top) / 100.0));
        x = Math.Clamp(x, 0, Math.Max(0, width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, height - 1));
        w = Math.Min(w, width - x);
        h = Math.Min(h, height - y);
        return new Int32Rect(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    private static BitmapSource Compose(BitmapSource source, Int32Rect crop, int maxWidth, int maxHeight,
        EditorCaption caption, bool flatten)
    {
        var scale = 1.0;
        if (maxWidth > 0 && maxHeight > 0)
            scale = Math.Min(1.0, Math.Min((double)maxWidth / crop.Width, (double)maxHeight / crop.Height));

        var width = Math.Max(1, (int)Math.Round(crop.Width * scale));
        var height = Math.Max(1, (int)Math.Round(crop.Height * scale));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (flatten) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.DrawImage(new CroppedBitmap(source, crop), new Rect(0, 0, width, height));
            if (caption.HasText) DrawCaption(dc, width, height, caption);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawCaption(DrawingContext dc, int width, int height, EditorCaption caption)
    {
        var text = caption.Text.Trim();
        if (text.Length == 0) return;

        var fontSize = Math.Max(8, Math.Min(width, height) * Math.Clamp(caption.ScalePercent, 1, 18) / 100.0);
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
            FontWeights.SemiBold, FontStretches.Normal);
        var foreground = BrushFromHex(EditorTextColors.ToHex(caption.ColorId));
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, fontSize, foreground, 1.0)
        {
            MaxTextWidth = width * 0.88,
            TextAlignment = TextAlignment.Center,
            LineHeight = fontSize * 1.16,
        };

        var boxWidth = Math.Min(formatted.Width, width * 0.88);
        var boxHeight = formatted.Height;
        var paddingX = fontSize * 0.50;
        var paddingY = fontSize * 0.30;
        var x = (width - boxWidth) / 2.0;
        var y = caption.Placement switch
        {
            EditorTextPlacement.Top => height * 0.06,
            EditorTextPlacement.Center => (height - boxHeight) / 2.0,
            _ => height - boxHeight - height * 0.06,
        };

        var box = new Rect(x - paddingX, y - paddingY, boxWidth + paddingX * 2, boxHeight + paddingY * 2);
        var boxBrush = BrushFromHex(EditorTextColors.BoxHex(caption.ColorId));
        var radius = Math.Max(2, fontSize * 0.28);
        dc.DrawRoundedRectangle(boxBrush, null, box, radius, radius);

        var origin = new Point(x, y);
        if (EditorTextColors.HasShadow(caption.ColorId))
        {
            var shadow = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, fontSize, BrushFromHex("#99000000"), 1.0)
            {
                MaxTextWidth = width * 0.88,
                TextAlignment = TextAlignment.Center,
                LineHeight = fontSize * 1.16,
            };
            dc.DrawText(shadow, new Point(origin.X + fontSize * 0.045, origin.Y + fontSize * 0.05));
        }

        dc.DrawText(formatted, origin);
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static void Encode(BitmapSource bitmap, string path, string format, int quality)
    {
        BitmapEncoder encoder = format.Equals("jpg", StringComparison.OrdinalIgnoreCase)
            ? new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 30, 100) }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static string LastError(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "No detail was returned." : string.Join(" ", lines.TakeLast(4));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
