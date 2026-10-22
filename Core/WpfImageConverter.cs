using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nasaq.Core;

/// <summary>
/// WIC-based image conversion used for formats that ship with Windows
/// (PNG, JPEG, BMP, GIF, TIFF) and for building multi-resolution ICO files.
/// Everything runs on a dedicated STA thread because WPF imaging objects
/// are happiest there.
/// </summary>
public static class WpfImageConverter
{
    private static readonly HashSet<string> WicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".wdp", ".jxr"
    };

    public static bool CanDecode(string path) => WicExtensions.Contains(Path.GetExtension(path));

    public static Task<string> ConvertAsync(string source, OutputFormat target,
        ConversionOptions options, IProgress<ConversionProgress>? progress, CancellationToken ct)
    {
        return RunStaAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ConversionProgress(0.15, "Reading image"));
            using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
                throw new ConversionException("The image contains no frames.", "Decoder returned zero frames.");

            var frame = NormaliseOrientation(decoder.Frames[0]);
            var temp = Path.Combine(options.OutputDirectory,
                $".nasaq-{Guid.NewGuid():N}{target.Extension switch { "jpeg" => ".jpg", _ => "." + target.Extension }}");

            if (target.Id == "ico")
            {
                progress?.Report(new ConversionProgress(0.55, "Building icon"));
                WriteIco(temp, frame);
            }
            else
            {
                progress?.Report(new ConversionProgress(0.55, "Encoding " + target.Label));
                WriteImage(temp, frame, target, options.ImageQuality);
            }

            ct.ThrowIfCancellationRequested();
            progress?.Report(new ConversionProgress(0.95, "Saving"));
            return temp;
        });
    }

    private static void WriteImage(string path, BitmapSource frame, OutputFormat target, int quality)
    {
        BitmapEncoder encoder = target.Id switch
        {
            "png" => new PngBitmapEncoder(),
            "jpg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 20, 100) },
            "bmp" => new BmpBitmapEncoder(),
            "gif" => new GifBitmapEncoder(),
            "tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
            _ => throw new ConversionException($"The {target.Label} encoder is not available in the Windows imaging engine."),
        };

        BitmapSource output = frame;
        if (target.Id == "jpg" && HasAlpha(frame))
            output = FlattenOntoWhite(frame);

        encoder.Frames.Add(BitmapFrame.Create(output));
        using var outStream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(outStream);
    }

    private static void WriteIco(string path, BitmapSource frame)
    {
        int[] sizes = { 256, 128, 64, 48, 40, 32, 24, 20, 16 };
        var pngs = new List<(int Size, byte[] Data)>();
        foreach (var size in sizes)
        {
            var scaled = ScaleToFit(frame, size);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(scaled));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            pngs.Add((size, ms.ToArray()));
        }

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);           // reserved
        writer.Write((ushort)1);           // icon
        writer.Write((ushort)pngs.Count);  // count
        var offset = 6 + 16 * pngs.Count;
        foreach (var (size, data) in pngs)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);          // palette
            writer.Write((byte)0);          // reserved
            writer.Write((ushort)1);        // planes
            writer.Write((ushort)32);       // bpp
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in pngs) writer.Write(data);
    }

    private static BitmapSource ScaleToFit(BitmapSource source, int size)
    {
        var ratio = Math.Min((double)size / source.PixelWidth, (double)size / source.PixelHeight);
        var w = Math.Max(1, (int)Math.Round(source.PixelWidth * ratio));
        var h = Math.Max(1, (int)Math.Round(source.PixelHeight * ratio));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect((size - w) / 2.0, (size - h) / 2.0, w, h));
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static bool HasAlpha(BitmapSource source)
    {
        var format = source.Format;
        return format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 ||
               format == PixelFormats.Rgba64 || format == PixelFormats.Prgba64;
    }

    private static BitmapSource FlattenOntoWhite(BitmapSource source)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }
        var rtb = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    public static BitmapSource NormaliseOrientation(BitmapSource frame)
    {
        int orientation = 1;
        try
        {
            if (frame is BitmapFrame bf && bf.Metadata is BitmapMetadata meta)
            {
                object? value = null;
                foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                {
                    try { value = meta.GetQuery(query); } catch { }
                    if (value is not null) break;
                }
                if (value is ushort u) orientation = u;
                else if (value is int i) orientation = i;
            }
        }
        catch { }

        if (orientation is < 2 or > 8) return frame;

        var w = frame.PixelWidth;
        var h = frame.PixelHeight;
        var swap = orientation is 5 or 6 or 7 or 8;
        var dw = swap ? h : w;
        var dh = swap ? w : h;

        Matrix matrix = orientation switch
        {
            2 => new Matrix(-1, 0, 0, 1, w, 0),
            3 => new Matrix(-1, 0, 0, -1, w, h),
            4 => new Matrix(1, 0, 0, -1, 0, h),
            5 => new Matrix(0, 1, 1, 0, 0, 0),
            6 => new Matrix(0, 1, -1, 0, h, 0),
            7 => new Matrix(0, -1, -1, 0, h, w),
            8 => new Matrix(0, -1, 1, 0, 0, w),
            _ => Matrix.Identity,
        };

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new MatrixTransform(matrix));
            dc.DrawImage(frame, new Rect(0, 0, w, h));
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(dw, dh, frame.DpiX > 0 ? frame.DpiX : 96,
            frame.DpiY > 0 ? frame.DpiY : 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    public static Task<T> RunStaAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (OperationCanceledException) { tcs.SetCanceled(); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "Nasaq WIC worker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
