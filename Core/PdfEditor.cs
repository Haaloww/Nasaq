using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;
using Nasaq.Services;

namespace Nasaq.Core;

/// <summary>
/// PDF merge, split and page-to-image using the bundled PDFium engine.
/// PDFium is not thread-safe, so every engine call is serialized.
/// </summary>
public sealed class PdfEditor
{
    private static readonly object Gate = new();

    public Task<PdfInfo> ProbeAsync(string path, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path)) throw new InvalidOperationException("The PDF no longer exists.");
            lock (Gate)
            {
                using var document = DocLib.Instance.GetDocReader(File.ReadAllBytes(path),
                    new PageDimensions(32, 32));
                return new PdfInfo(path, document.GetPageCount(), EditorFileNaming.SafeSize(path));
            }
        }, ct);
    }

    public Task<BitmapSource> RenderPreviewAsync(string path, int page, int maxDimension, CancellationToken ct)
    {
        return WpfImageConverter.RunStaAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            lock (Gate)
            {
                using var document = DocLib.Instance.GetDocReader(File.ReadAllBytes(path),
                    new PageDimensions(Math.Clamp(maxDimension, 120, 2400), Math.Clamp(maxDimension, 120, 2400)));
                var count = document.GetPageCount();
                if (count == 0) throw new InvalidOperationException("This PDF has no pages.");
                var index = Math.Clamp(page, 1, count) - 1;
                using var reader = document.GetPageReader(index);
                return Render(reader);
            }
        });
    }

    /// <summary>
    /// Renders a small page strip in one PDFium pass. This is used by the
    /// editor so navigating a document does not mean re-reading the file for
    /// every thumbnail.
    /// </summary>
    public Task<IReadOnlyList<BitmapSource>> RenderThumbnailsAsync(string path, int maxDimension,
        int maxPages, CancellationToken ct)
    {
        return WpfImageConverter.RunStaAsync<IReadOnlyList<BitmapSource>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path)) throw new InvalidOperationException("The PDF no longer exists.");
            lock (Gate)
            {
                using var document = DocLib.Instance.GetDocReader(File.ReadAllBytes(path),
                    new PageDimensions(Math.Clamp(maxDimension, 80, 800), Math.Clamp(maxDimension, 80, 800)));
                var count = Math.Min(document.GetPageCount(), Math.Clamp(maxPages, 1, 200));
                var thumbnails = new List<BitmapSource>(count);
                for (var index = 0; index < count; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    using var reader = document.GetPageReader(index);
                    thumbnails.Add(Render(reader));
                }
                return thumbnails;
            }
        });
    }

    public Task<EditorResult> MergeAsync(PdfMergeRequest request, IProgress<EditorProgress>? progress,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            string? temporary = null;
            try
            {
                var sources = request.Sources.Where(File.Exists).ToList();
                if (sources.Count < 2)
                    return EditorResult.Fail("Add at least two PDFs to merge.", null, stopwatch.Elapsed);

                progress?.Report(new EditorProgress(0.08, "Reading PDFs"));
                var pagesTotal = 0;
                var documents = new List<byte[]>(sources.Count);
                foreach (var source in sources)
                {
                    ct.ThrowIfCancellationRequested();
                    documents.Add(File.ReadAllBytes(source));
                }

                progress?.Report(new EditorProgress(0.35, "Merging pages"));
                byte[] merged;
                lock (Gate)
                {
                    merged = DocLib.Instance.Merge(documents);
                    using var check = DocLib.Instance.GetDocReader(merged, new PageDimensions(32, 32));
                    pagesTotal = check.GetPageCount();
                }

                ct.ThrowIfCancellationRequested();
                var outputDirectory = OutputDirectory(request.OutputDirectory);
                var output = EditorFileNaming.UniquePath(outputDirectory,
                    EditorFileNaming.SafeStem(sources[0]) + "-merged", "pdf", request.Overwrite);
                temporary = Path.Combine(Path.GetDirectoryName(output)!, $".nasaq-{Guid.NewGuid():N}.pdf");
                progress?.Report(new EditorProgress(0.88, "Saving"));
                File.WriteAllBytes(temporary, merged);
                ct.ThrowIfCancellationRequested();
                EditorFileNaming.MoveWithRetry(temporary, output, overwrite: true);
                temporary = null;
                stopwatch.Stop();
                progress?.Report(new EditorProgress(1, "Saved"));
                Log.Info("editor.pdf", "PDFs merged",
                    ("documents", sources.Count.ToString()), ("pages", pagesTotal.ToString()), ("output", output));
                return EditorResult.Ok(output, stopwatch.Elapsed, pageCount: pagesTotal);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                return EditorResult.Cancelled(stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Log.Warn("editor.pdf", "PDF merge failed", ("reason", ex.Message), ("trace", ex.ToString()));
                return EditorResult.Fail("The PDFs could not be merged.", ex.Message, stopwatch.Elapsed);
            }
            finally
            {
                if (temporary is not null) TryDelete(temporary);
            }
        }, ct);
    }

    public Task<EditorResult> SplitAsync(PdfSplitRequest request, IProgress<EditorProgress>? progress,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            var outputs = new List<string>();
            var temporaries = new List<string>();
            try
            {
                if (!File.Exists(request.SourcePath))
                    return EditorResult.Fail("The PDF no longer exists.", null, stopwatch.Elapsed);
                var pages = request.Pages.Distinct().ToList();
                if (pages.Count == 0)
                    return EditorResult.Fail("Choose at least one page.", null, stopwatch.Elapsed);

                var outputDirectory = OutputDirectory(request.OutputDirectory);
                var stem = EditorFileNaming.SafeStem(request.SourcePath);
                progress?.Report(new EditorProgress(0.05, "Reading PDF"));
                var sourceBytes = File.ReadAllBytes(request.SourcePath);

                if (request.OneFilePerPage)
                {
                    for (var i = 0; i < pages.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var page = pages[i];
                        byte[] bytes;
                        lock (Gate)
                        {
                            bytes = DocLib.Instance.Split(sourceBytes, page - 1, page - 1);
                        }

                        var output = EditorFileNaming.UniquePath(outputDirectory,
                            $"{stem}-page-{page:000}", "pdf", request.Overwrite);
                        var temp = Path.Combine(Path.GetDirectoryName(output)!, $".nasaq-{Guid.NewGuid():N}.pdf");
                        temporaries.Add(temp);
                        File.WriteAllBytes(temp, bytes);
                        EditorFileNaming.MoveWithRetry(temp, output, overwrite: true);
                        temporaries.Remove(temp);
                        outputs.Add(output);
                        progress?.Report(new EditorProgress(0.08 + 0.88 * (i + 1) / pages.Count, "Splitting"));
                    }
                }
                else
                {
                    ct.ThrowIfCancellationRequested();
                    byte[] bytes;
                    lock (Gate)
                    {
                        bytes = DocLib.Instance.Split(sourceBytes, EditorPageRange.ToRangeString(pages));
                    }

                    var label = pages.Count == 1 ? $"page-{pages[0]:000}" : "pages";
                    var output = EditorFileNaming.UniquePath(outputDirectory, $"{stem}-{label}", "pdf", request.Overwrite);
                    var temp = Path.Combine(Path.GetDirectoryName(output)!, $".nasaq-{Guid.NewGuid():N}.pdf");
                    temporaries.Add(temp);
                    progress?.Report(new EditorProgress(0.80, "Saving"));
                    File.WriteAllBytes(temp, bytes);
                    ct.ThrowIfCancellationRequested();
                    EditorFileNaming.MoveWithRetry(temp, output, overwrite: true);
                    temporaries.Remove(temp);
                    outputs.Add(output);
                }

                stopwatch.Stop();
                progress?.Report(new EditorProgress(1, "Saved"));
                Log.Info("editor.pdf", "PDF split",
                    ("source", request.SourcePath), ("pages", pages.Count.ToString()),
                    ("perPage", request.OneFilePerPage.ToString()), ("outputs", outputs.Count.ToString()));
                return EditorResult.Ok(outputs, stopwatch.Elapsed, pageCount: pages.Count);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                return EditorResult.Cancelled(stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Log.Warn("editor.pdf", "PDF split failed", ("source", request.SourcePath), ("reason", ex.Message));
                return EditorResult.Fail("The PDF could not be split.", ex.Message, stopwatch.Elapsed);
            }
            finally
            {
                foreach (var temp in temporaries) TryDelete(temp);
            }
        }, ct);
    }

    public Task<EditorResult> ExportImagesAsync(PdfImageRequest request, IProgress<EditorProgress>? progress,
        CancellationToken ct)
    {
        return WpfImageConverter.RunStaAsync(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            var outputs = new List<string>();
            var temporaries = new List<string>();
            try
            {
                if (!File.Exists(request.SourcePath))
                    return EditorResult.Fail("The PDF no longer exists.", null, stopwatch.Elapsed);
                var pages = request.Pages.Distinct().ToList();
                if (pages.Count == 0)
                    return EditorResult.Fail("Choose at least one page.", null, stopwatch.Elapsed);

                var extension = request.Format.Equals("jpg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";
                var outputDirectory = OutputDirectory(request.OutputDirectory);
                var stem = EditorFileNaming.SafeStem(request.SourcePath);
                var edge = Math.Clamp(request.LongEdge, 400, 4000);
                progress?.Report(new EditorProgress(0.02, "Opening PDF"));

                var sourceBytes = File.ReadAllBytes(request.SourcePath);
                lock (Gate)
                {
                    using var document = DocLib.Instance.GetDocReader(sourceBytes,
                        new PageDimensions(edge, edge));
                    var available = document.GetPageCount();
                    for (var i = 0; i < pages.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var page = pages[i];
                        if (page < 1 || page > available)
                            throw new InvalidOperationException($"Page {page} is outside this PDF.");

                        using var reader = document.GetPageReader(page - 1);
                        var bitmap = Render(reader);
                        var output = EditorFileNaming.UniquePath(outputDirectory,
                            $"{stem}-page-{page:000}", extension, request.Overwrite);
                        var temp = Path.Combine(Path.GetDirectoryName(output)!,
                            $".nasaq-{Guid.NewGuid():N}.{extension}");
                        temporaries.Add(temp);
                        Encode(bitmap, temp, extension, request.Quality);
                        ct.ThrowIfCancellationRequested();
                        EditorFileNaming.MoveWithRetry(temp, output, overwrite: true);
                        temporaries.Remove(temp);
                        outputs.Add(output);
                        progress?.Report(new EditorProgress(0.05 + 0.90 * (i + 1) / pages.Count, "Rendering pages"));
                    }
                }

                stopwatch.Stop();
                progress?.Report(new EditorProgress(1, "Saved"));
                Log.Info("editor.pdf", "PDF pages exported as images",
                    ("source", request.SourcePath), ("pages", pages.Count.ToString()),
                    ("format", extension), ("edge", edge.ToString()));
                return EditorResult.Ok(outputs, stopwatch.Elapsed, pageCount: pages.Count);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                return EditorResult.Cancelled(stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Log.Warn("editor.pdf", "PDF page export failed", ("source", request.SourcePath), ("reason", ex.Message));
                return EditorResult.Fail("The pages could not be exported.", ex.Message, stopwatch.Elapsed);
            }
            finally
            {
                foreach (var temp in temporaries) TryDelete(temp);
            }
        });
    }

    private static BitmapSource Render(Docnet.Core.Readers.IPageReader reader)
    {
        var width = reader.GetPageWidth();
        var height = reader.GetPageHeight();
        var bytes = reader.GetImage();
        if (width <= 0 || height <= 0 || bytes.Length < width * height * 4)
            throw new InvalidOperationException("This page could not be rendered.");
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        bitmap.Freeze();

        // PDFium pages arrive with a transparent background. Flatten them onto
        // white so previews and exported images match how the page is printed.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.DrawImage(bitmap, new Rect(0, 0, width, height));
        }
        var flattened = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        flattened.Render(visual);
        flattened.Freeze();
        return flattened;
    }

    private static void Encode(BitmapSource source, string path, string extension, int quality)
    {
        BitmapSource output = source;
        if (extension == "jpg")
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
                dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            }
            var flattened = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            flattened.Render(visual);
            flattened.Freeze();
            output = flattened;
        }

        BitmapEncoder encoder = extension == "jpg"
            ? new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 30, 100) }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(output));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static string OutputDirectory(string? directory) =>
        string.IsNullOrWhiteSpace(directory) ? AppSettings.DefaultOutputDirectory() : directory;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
