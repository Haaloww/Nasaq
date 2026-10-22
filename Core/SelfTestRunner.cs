using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nasaq.Services;

namespace Nasaq.Core;

/// <summary>
/// Headless verification pass used during development and support:
/// exercises every conversion pipeline and writes a readable report.
/// </summary>
public static class SelfTestRunner
{
    private sealed record TestResult(string Name, bool Passed, bool Skipped, string Detail, TimeSpan Elapsed);

    public static async Task<bool> RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "output");
        Directory.CreateDirectory(output);

        var ffmpeg = FfmpegLocator.Locate("");
        var engine = new ConversionService(ffmpeg);
        var options = new ConversionOptions(output, 84, 192, 23, true, true);
        var results = new List<TestResult>();
        var report = new StringBuilder();

        Log.Info("selftest.start", "Running conversion self-test", ("directory", directory));

        var png = await CreatePngAsync(Path.Combine(directory, "sample.png"));
        var wav = CreateWav(Path.Combine(directory, "sample.wav"));
        var md = CreateText(Path.Combine(directory, "sample.md"), SampleMarkdown);
        var html = CreateText(Path.Combine(directory, "sample.html"), SampleHtml);
        var corrupt = CreateText(Path.Combine(directory, "corrupt.png"), "this is definitely not a png");
        var mp4 = ffmpeg.IsAvailable ? await CreateVideoAsync(ffmpeg, Path.Combine(directory, "sample.mp4")) : null;

        results.Add(await TestAsync("PNG → WebP", png, "webp", options, ffmpeg.IsAvailable,
            p => CheckFile(p, 1000) && HasBytes(p, "RIFF")));
        results.Add(await TestAsync("PNG → ICO", png, "ico", options, true,
            p => CheckFile(p, 1000) && HasBytes(p, "\u0000\u0000\u0001\u0000")));
        results.Add(await TestAsync("PNG → JPG", png, "jpg", options, true,
            p => CheckFile(p, 1000) && HasBytes(p, "\u00FF\u00D8")));
        results.Add(await TestAsync("WAV → MP3", wav, "mp3", options, ffmpeg.IsAvailable,
            p => CheckFile(p, 1000)));
        results.Add(await TestAsync("MP4 → MKV", mp4, "mkv", options, ffmpeg.IsAvailable && mp4 is not null,
            p => CheckFile(p, 1000)));
        results.Add(await TestAsync("MP4 → GIF", mp4, "gifv", options, ffmpeg.IsAvailable && mp4 is not null,
            p => CheckFile(p, 1000)));
        results.Add(await TestAsync("MP4 → MP3 (audio)", mp4, "mp3", options, ffmpeg.IsAvailable && mp4 is not null,
            p => CheckFile(p, 1000), MediaCategory.Audio));
        results.Add(await TestAsync("MD → PDF", md, "pdf", options, true,
            p => CheckFile(p, 800) && HasBytes(p, "%PDF-1.4") && HasText(p, "%%EOF")));
        results.Add(await TestAsync("MD → DOCX", md, "docx", options, true,
            p => CheckFile(p, 1000) && IsValidDocx(p)));

        var docx = Path.Combine(output, "sample.docx");
        results.Add(await TestAsync("DOCX → TXT", File.Exists(docx) ? docx : null, "txt", options, File.Exists(docx),
            p => CheckFile(p, 20) && HasText(p, "Nasaq self-test")));
        results.Add(await TestAsync("HTML → TXT", html, "txt", options, true,
            p => CheckFile(p, 20) && HasText(p, "Self-test")));

        results.Add(Offline("Installer: link parsing", () =>
        {
            var share = "Watch this https://youtu.be/abc123?si=xyz, it's great!";
            var parsed = InstallerService.ParseUrl(share);
            return parsed is not null
                   && parsed.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
                   && parsed.AbsoluteUri.Contains("si=xyz", StringComparison.Ordinal)
                   && InstallerService.ParseUrl("not a link") is null
                   && InstallerService.ParseUrl("ftp://example.com/a.jpg") is null
                   && InstallerService.ParseUrl("javascript:alert(1)") is null;
        }, "Share text parsed; non-http input rejected", "Share text or URL rejection failed"));

        results.Add(Offline("Installer: filename guard", () =>
        {
            var reserved = InstallerService.SanitizeFileName("CON");
            var cleaned = InstallerService.SanitizeFileName("bad:name?.jpg");
            var longName = InstallerService.SanitizeFileName(new string('a', 220) + ".mp4");
            var blank = InstallerService.SanitizeFileName("   ");
            return reserved == "_CON"
                   && cleaned == "bad_name_.jpg"
                   && longName.Length <= 150
                   && longName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                   && blank == "download";
        }, "Reserved, invalid, long and blank names normalized", "Filename normalization failed"));

        results.Add(Offline("Installer: media detection", () =>
            InstallerService.KindFromExtension("https://cdn.example/photo.JPG?size=large") == InstallerMediaKind.Image
            && InstallerService.KindFromExtension("clip.MP4") == InstallerMediaKind.Video
            && InstallerService.KindFromExtension("song.flac") == InstallerMediaKind.Audio
            && InstallerService.KindFromExtension("stream.m3u8") == InstallerMediaKind.Stream
            && InstallerService.KindFromExtension("page.html") == InstallerMediaKind.Unknown
            && InstallerService.KindFromContentType("video/mp4; charset=utf-8") == InstallerMediaKind.Video
            && InstallerService.KindFromContentType("text/html") == InstallerMediaKind.Page,
            "Extensions and content types mapped", "Media type detection failed"));

        results.Add(Offline("Installer: page media", () =>
        {
            var page = """
                <html><head>
                <meta property="og:title" content="Self-test post">
                <meta property="og:video" content="/media/clip.mp4">
                <meta property="og:image" content="https://cdn.example.com/photo.jpg">
                <meta name="twitter:image" content="/img/card.png">
                <script type="application/ld+json">{"contentUrl":"\/media\/json.mp4","thumbnailUrl":"\/img\/thumb.jpg"}</script>
                </head><body>
                <img src="/img/inline.jpg" width="1200" height="800">
                <img src="/img/tiny.png" width="32" height="32">
                </body></html>
                """;
            var candidates = InstallerService.ExtractMediaCandidates(page,
                new Uri("https://example.com/post/1"), InstallerMediaMode.Auto);
            return candidates.Count >= 4
                   && candidates[0].AbsoluteUri == "https://example.com/media/clip.mp4"
                   && candidates.Any(u => u.AbsoluteUri == "https://cdn.example.com/photo.jpg")
                   && candidates.Any(u => u.AbsoluteUri == "https://example.com/img/inline.jpg")
                   && candidates.All(u => !u.AbsoluteUri.Contains("tiny", StringComparison.OrdinalIgnoreCase));
        }, "Open Graph, JSON-LD and inline images extracted", "Page media extraction failed"));

        results.Add(Offline("Installer: mode filters", () =>
        {
            var page = """
                <html><body>
                <video src="/media/clip.mp4"></video>
                <audio src="/media/song.mp3"></audio>
                <img src="/media/cover.jpg" width="1200" height="800">
                </body></html>
                """;
            var baseUri = new Uri("https://example.com/post/2");
            var audio = InstallerService.ExtractMediaCandidates(page, baseUri, InstallerMediaMode.Audio);
            var photo = InstallerService.ExtractMediaCandidates(page, baseUri, InstallerMediaMode.Photo);
            return audio.Count == 1 && audio[0].AbsolutePath == "/media/song.mp3"
                   && photo.Count == 1 && photo[0].AbsolutePath == "/media/cover.jpg";
        }, "Audio and Photo modes keep only their media", "Mode filtering failed"));

        results.Add(Offline("Editor: page ranges", () =>
        {
            var pages = EditorPageRange.Parse("1-3, 5", 5);
            var suffix = EditorPageRange.Parse("4-", 5);
            var all = EditorPageRange.Parse("", 3);
            var compact = EditorPageRange.ToRangeString(new[] { 1, 2, 3, 5 });
            var rejected = false;
            try { EditorPageRange.Parse("0,2", 5); }
            catch (FormatException) { rejected = true; }
            return pages.SequenceEqual(new[] { 1, 2, 3, 5 })
                   && suffix.SequenceEqual(new[] { 4, 5 })
                   && all.SequenceEqual(new[] { 1, 2, 3 })
                   && compact == "1-3,5"
                   && rejected;
        }, "Ranges, open ends, all pages and invalid input handled", "Page range parsing failed"));

        var photoEditor = new PhotoEditor(engine);
        results.Add(await EditorTestAsync("Editor: photo crop + text", true, async () =>
        {
            var request = new PhotoEditRequest(png, 10, 10, 90, 90,
                new EditorCaption("Nasaq", 5, EditorTextPlacement.Bottom, "white"),
                output, "png", 84, true);
            return await photoEditor.ExportAsync(request, null, CancellationToken.None);
        }, result =>
        {
            var path = result.FirstOutput;
            return path is not null && CheckFile(path, 1000) && PngSize(path) is { Width: 768, Height: 512 };
        }, "Crop and caption applied at 768 × 512"));

        var mediaEditor = new MediaEditor(engine);
        results.Add(await EditorTestAsync("Editor: video cut + text",
            ffmpeg.IsAvailable && mp4 is not null, async () =>
        {
            var request = new VideoEditRequest(mp4!, 0, 0.6,
                new EditorCaption("Nasaq", 6, EditorTextPlacement.Bottom, "white"),
                output, 23, 192, true, 320, 240);
            return await mediaEditor.ExportVideoAsync(request, null, CancellationToken.None);
        }, result => result.FirstOutput is { } path && CheckFile(path, 1000),
            "Trimmed MP4 with a drawtext caption"));

        results.Add(await EditorTestAsync("Editor: audio trim", ffmpeg.IsAvailable, async () =>
        {
            var request = new AudioTrimRequest(wav, 0, 0.5, output, "mp3", 192, true);
            return await mediaEditor.ExportAudioTrimAsync(request, null, CancellationToken.None);
        }, result => result.FirstOutput is { } path && CheckFile(path, 1000),
            "Half-second MP3 excerpt"));

        results.Add(await EditorTestAsync("Editor: audio merge", ffmpeg.IsAvailable, async () =>
        {
            var request = new AudioMergeRequest(new[] { wav, wav }, output, "mp3", 192, true);
            return await mediaEditor.MergeAudioAsync(request, null, CancellationToken.None);
        }, result => result.FirstOutput is { } path && CheckFile(path, 1000),
            "Two tracks concatenated into one MP3"));

        var pdfEditor = new PdfEditor();
        results.Add(await EditorTestAsync("Editor: PDF merge/split/render", true, async () =>
        {
            var sample = Path.Combine(output, "sample.pdf");
            if (!File.Exists(sample)) throw new FileNotFoundException("The sample PDF was not created.", sample);
            var sourceInfo = await pdfEditor.ProbeAsync(sample, CancellationToken.None);
            var merge = await pdfEditor.MergeAsync(new PdfMergeRequest(new[] { sample, sample }, output, true),
                null, CancellationToken.None);
            if (!merge.Success) return merge;
            var mergedInfo = await pdfEditor.ProbeAsync(merge.FirstOutput!, CancellationToken.None);
            if (mergedInfo.PageCount < 2) throw new InvalidOperationException("Merge lost pages.");

            var split = await pdfEditor.SplitAsync(new PdfSplitRequest(merge.FirstOutput!,
                new[] { 1, 2 }, false, output, true), null, CancellationToken.None);
            if (!split.Success) return split;
            var splitInfo = await pdfEditor.ProbeAsync(split.FirstOutput!, CancellationToken.None);
            if (splitInfo.PageCount != 2) throw new InvalidOperationException("Split returned the wrong page count.");

            var perPage = await pdfEditor.SplitAsync(new PdfSplitRequest(merge.FirstOutput!,
                new[] { 1, 2 }, true, output, true), null, CancellationToken.None);
            if (!perPage.Success || perPage.Outputs.Count != 2) return perPage;

            var images = await pdfEditor.ExportImagesAsync(new PdfImageRequest(sample,
                new[] { 1 }, output, "png", 900, 84, true), null, CancellationToken.None);
            if (!images.Success) return images;
            if (images.FirstOutput is null || !CheckFile(images.FirstOutput, 1000))
                throw new InvalidOperationException("The page image was not written.");

            return EditorResult.Ok(new[]
            {
                merge.FirstOutput!, split.FirstOutput!, perPage.Outputs[0], images.FirstOutput!,
            }, split.Elapsed, pageCount: mergedInfo.PageCount);
        }, result => result.Outputs.All(File.Exists) && result.PageCount >= 2,
            "Merged, split, extracted per page and rendered an image"));

        var corruptResult = await engine.ConvertAsync(corrupt,
            FormatCatalog.ImageFormats.First(f => f.Id == "webp"), options, null, CancellationToken.None);
        results.Add(new TestResult("Corrupt image is rejected", !corruptResult.Success, false,
            corruptResult.Success ? "A corrupt PNG unexpectedly converted." : "Handled gracefully: " + corruptResult.Error,
            corruptResult.Elapsed));

        var passed = results.Count(r => r.Passed && !r.Skipped);
        var failed = results.Count(r => !r.Passed && !r.Skipped);
        var skipped = results.Count(r => r.Skipped);

        report.AppendLine("NASAQ SELF-TEST");
        report.AppendLine($"  started      {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"  directory    {directory}");
        report.AppendLine($"  ffmpeg       {(ffmpeg.IsAvailable ? $"{ffmpeg.Version} · {ffmpeg.FfmpegPath}" : "not found")}");
        var ytDlp = YtDlpLocator.Locate("");
        report.AppendLine($"  yt-dlp       {(ytDlp.IsAvailable ? $"{ytDlp.DisplayVersion} · {ytDlp.Path}" : "not set up")}");
        report.AppendLine($"  runtime      .NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        report.AppendLine();
        report.AppendLine("RESULTS");
        foreach (var result in results)
        {
            var state = result.Skipped ? "SKIP" : result.Passed ? "PASS" : "FAIL";
            report.AppendLine($"  [{state}] {result.Name,-28} {result.Elapsed.TotalMilliseconds,7:0} ms   {result.Detail}");
        }
        report.AppendLine();
        report.AppendLine($"SUMMARY  {passed} passed · {failed} failed · {skipped} skipped");

        var reportPath = Path.Combine(directory, "selftest-report.txt");
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        Log.Info("selftest.done", $"Self-test finished: {passed} passed, {failed} failed, {skipped} skipped",
            ("report", (string?)reportPath));

        return failed == 0;
    }

    private static async Task<TestResult> TestAsync(string name, string? source, string targetId,
        ConversionOptions options, bool enabled, Func<string, bool> validate,
        MediaCategory? targetCategory = null)
    {
        if (!enabled || source is null || !File.Exists(source))
            return new TestResult(name, false, true, "Skipped (source or FFmpeg unavailable)", TimeSpan.Zero);

        var category = FormatCatalog.Detect(source);
        var target = FormatCatalog.Find(targetCategory ?? category, targetId);
        if (target is null)
            return new TestResult(name, false, false, $"No catalog entry for {targetId}", TimeSpan.Zero);

        try
        {
            var engine = new ConversionService(FfmpegLocator.Locate(""));
            var result = await engine.ConvertAsync(source, target, options, null, CancellationToken.None);
            if (!result.Success)
                return new TestResult(name, false, false, result.Error ?? "Conversion failed", result.Elapsed);

            var valid = result.OutputPath is not null && validate(result.OutputPath);
            var size = result.OutputPath is null ? 0 : new FileInfo(result.OutputPath).Length;
            return new TestResult(name, valid, false,
                valid ? $"{result.Engine} · {FileSize.Format(size)}" : "Output failed validation",
                result.Elapsed);
        }
        catch (Exception ex)
        {
            return new TestResult(name, false, false, ex.Message, TimeSpan.Zero);
        }
    }

    private static async Task<TestResult> EditorTestAsync(string name, bool enabled,
        Func<Task<EditorResult>> action, Func<EditorResult, bool> validate, string success)
    {
        if (!enabled)
            return new TestResult(name, false, true, "Skipped (FFmpeg or sample unavailable)", TimeSpan.Zero);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await action();
            stopwatch.Stop();
            if (result.Canceled)
                return new TestResult(name, false, false, "The edit was canceled.", stopwatch.Elapsed);
            if (!result.Success)
                return new TestResult(name, false, false,
                    (result.Error ?? "Edit failed") + (result.Detail is null ? "" : ": " + result.Detail),
                    stopwatch.Elapsed);
            var valid = validate(result);
            var detail = valid
                ? $"{success} · {result.Outputs.Count} file(s)"
                : "Output failed validation";
            return new TestResult(name, valid, false, detail, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new TestResult(name, false, false, ex.Message, stopwatch.Elapsed);
        }
    }

    private static bool CheckFile(string path, long minimum) =>
        File.Exists(path) && new FileInfo(path).Length >= minimum;

    private static (int Width, int Height)? PngSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) != header.Length) return null;
            if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47) return null;
            var width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            return (width, height);
        }
        catch { return null; }
    }

    private static TestResult Offline(string name, Func<bool> test, string success, string failure)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var passed = test();
            stopwatch.Stop();
            return new TestResult(name, passed, false, passed ? success : failure, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new TestResult(name, false, false, ex.Message, stopwatch.Elapsed);
        }
    }

    private static bool HasBytes(string path, string expected)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var prefix = Encoding.Latin1.GetBytes(expected);
            return bytes.Length >= prefix.Length && bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix);
        }
        catch { return false; }
    }

    private static bool HasText(string path, string expected)
    {
        try { return File.ReadAllText(path).Contains(expected, StringComparison.Ordinal); }
        catch { return false; }
    }

    private static bool IsValidDocx(string path)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(path);
            return archive.GetEntry("word/document.xml") is not null;
        }
        catch { return false; }
    }

    private static async Task<string> CreatePngAsync(string path)
    {
        await WpfImageConverter.RunStaAsync(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var gradient = new LinearGradientBrush(
                    Color.FromRgb(0x1B, 0x22, 0x33), Color.FromRgb(0x7C, 0x9C, 0xFF), 55);
                dc.DrawRectangle(gradient, null, new Rect(0, 0, 960, 640));
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), null,
                    new Point(640, 220), 120, 120);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(180, 10, 12, 18)), null,
                    new Rect(80, 420, 520, 90));
                dc.DrawText(new FormattedText("Nasaq", System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 64,
                        Brushes.White, 1.0),
                    new Point(96, 430));
            }
            var bitmap = new RenderTargetBitmap(960, 640, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
            return true;
        });
        return path;
    }

    private static string CreateWav(string path)
    {
        const int rate = 44100;
        const int seconds = 1;
        const short channels = 1;
        const short bits = 16;
        var samples = rate * seconds;
        var dataSize = samples * channels * (bits / 8);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(rate);
        writer.Write(rate * channels * bits / 8);
        writer.Write((short)(channels * bits / 8));
        writer.Write(bits);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        for (var i = 0; i < samples; i++)
        {
            var value = Math.Sin(2 * Math.PI * 440 * i / rate) * 0.35;
            writer.Write((short)(value * short.MaxValue));
        }
        return path;
    }

    private static string CreateText(string path, string content)
    {
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static async Task<string?> CreateVideoAsync(FfmpegInfo ffmpeg, string path)
    {
        if (!ffmpeg.IsAvailable) return null;
        try
        {
            var result = await ProcessRunner.RunAsync(ffmpeg.FfmpegPath!,
                new[]
                {
                    "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                    "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=24",
                    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
                    "-t", "1", "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-shortest", path,
                }, null, null, CancellationToken.None);
            return result.ExitCode == 0 && File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    private const string SampleMarkdown = """
# Nasaq self-test

This document verifies the native document pipeline. It includes a heading,
a list and a little punctuation: “quotes”, dashes — and an ellipsis…

- Background conversion
- Drop shelf
- Formatted logs

## Closing

Made by Abdullah Abdulmanea. The quick brown fox jumps over the lazy dog.
""";

    private const string SampleHtml = """
<!doctype html>
<html><head><title>Nasaq self-test</title><style>body{font:16px sans-serif}</style></head>
<body><h1>Self-test</h1><p>HTML is converted to readable text.</p><script>alert('no')</script></body></html>
""";
}
