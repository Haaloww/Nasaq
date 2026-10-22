using System.Globalization;
using System.IO;
using System.Threading;

namespace Nasaq.Core;

public enum EditorTool
{
    Photo,
    Video,
    Audio,
    Pdf,
}

public enum AudioEditMode
{
    Trim,
    Merge,
}

public enum PdfEditMode
{
    Merge,
    Split,
    Images,
}

public enum EditorTextPlacement
{
    Top,
    Center,
    Bottom,
}

public sealed record EditorOption(string Id, string Label, string Glyph = "", string Detail = "")
{
    public override string ToString() => Label;
}

public sealed record EditorProgress(double Fraction, string Stage);

public sealed record EditorResult(
    bool Success,
    bool Canceled,
    IReadOnlyList<string> Outputs,
    string? Error,
    string? Detail,
    TimeSpan Elapsed,
    int Width = 0,
    int Height = 0,
    int PageCount = 0)
{
    public string? FirstOutput => Outputs.Count > 0 ? Outputs[0] : null;

    public static EditorResult Ok(string path, TimeSpan elapsed, int width = 0, int height = 0,
        int pageCount = 0) =>
        new(true, false, new[] { path }, null, null, elapsed, width, height, pageCount);

    public static EditorResult Ok(IReadOnlyList<string> paths, TimeSpan elapsed, int pageCount = 0) =>
        new(true, false, paths, null, null, elapsed, 0, 0, pageCount);

    public static EditorResult Fail(string error, string? detail, TimeSpan elapsed) =>
        new(false, false, Array.Empty<string>(), error, detail, elapsed);

    public static EditorResult Cancelled(TimeSpan elapsed) =>
        new(false, true, Array.Empty<string>(), "The edit was canceled.", null, elapsed);
}

public sealed record EditorCaption(
    string Text,
    double ScalePercent,
    EditorTextPlacement Placement,
    string ColorId)
{
    public static EditorCaption None { get; } =
        new("", 5, EditorTextPlacement.Bottom, "white");

    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}

public sealed record PhotoEditRequest(
    string SourcePath,
    double CropLeft,
    double CropTop,
    double CropRight,
    double CropBottom,
    EditorCaption Caption,
    string OutputDirectory,
    string Format,
    int Quality,
    bool Overwrite);

public sealed record PhotoSourceInfo(string Path, int Width, int Height, long Bytes);

public sealed record MediaInfo(
    string Path,
    double Duration,
    int Width,
    int Height,
    bool HasVideo,
    bool HasAudio,
    long Bytes,
    string Codec)
{
    public string Resolution => HasVideo && Width > 0 && Height > 0 ? $"{Width} × {Height}" : "Audio";
    public string DurationText => EditorTime.Format(Duration);
}

public sealed record VideoEditRequest(
    string SourcePath,
    double StartSeconds,
    double EndSeconds,
    EditorCaption Caption,
    string OutputDirectory,
    int Quality,
    int AudioBitrate,
    bool Overwrite,
    int SourceWidth = 0,
    int SourceHeight = 0);

public sealed record AudioTrimRequest(
    string SourcePath,
    double StartSeconds,
    double EndSeconds,
    string OutputDirectory,
    string Format,
    int Bitrate,
    bool Overwrite);

public sealed record AudioMergeClip(string Path, double StartSeconds, double EndSeconds);

public sealed record AudioMergeRequest(
    IReadOnlyList<string> Sources,
    string OutputDirectory,
    string Format,
    int Bitrate,
    bool Overwrite,
    IReadOnlyList<AudioMergeClip>? Clips = null);

public sealed record AudioTrackInfo(string Path, double Duration, long Bytes);

public sealed record PdfInfo(string Path, int PageCount, long Bytes);

public sealed record PdfMergeRequest(
    IReadOnlyList<string> Sources,
    string OutputDirectory,
    bool Overwrite);

public sealed record PdfSplitRequest(
    string SourcePath,
    IReadOnlyList<int> Pages,
    bool OneFilePerPage,
    string OutputDirectory,
    bool Overwrite);

public sealed record PdfImageRequest(
    string SourcePath,
    IReadOnlyList<int> Pages,
    string OutputDirectory,
    string Format,
    int LongEdge,
    int Quality,
    bool Overwrite);

public static class EditorTextColors
{
    public static string ToHex(string? id) => id?.ToLowerInvariant() switch
    {
        "black" => "#111827",
        "accent" => "#7C9CFF",
        _ => "#FFFFFF",
    };

    public static string ToFfmpeg(string? id) => id?.ToLowerInvariant() switch
    {
        "black" => "0x111827",
        "accent" => "0x7C9CFF",
        _ => "white",
    };

    public static string BoxHex(string? id) =>
        id?.Equals("black", StringComparison.OrdinalIgnoreCase) == true ? "#B8FFFFFF" : "#66000000";

    public static string BoxFfmpeg(string? id) =>
        id?.Equals("black", StringComparison.OrdinalIgnoreCase) == true ? "white@0.72" : "black@0.40";

    public static bool HasShadow(string? id) =>
        !string.Equals(id, "black", StringComparison.OrdinalIgnoreCase);
}

public static class EditorTime
{
    public static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 100}"
            : $"{time.Minutes}:{time.Seconds:00}.{time.Milliseconds / 100}";
    }

    public static string FormatLong(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    public static double Clamp(double value, double min, double max) =>
        double.IsNaN(value) || double.IsInfinity(value) ? min : Math.Clamp(value, min, max);
}

/// <summary>
/// Parses human page selections such as <c>1-3, 5, 8-</c>.
/// Ranges are one-based, inclusive and returned in the order the user wrote them.
/// </summary>
public static class EditorPageRange
{
    public static IReadOnlyList<int> Parse(string? text, int pageCount)
    {
        if (pageCount <= 0) throw new FormatException("Choose a PDF with at least one page.");
        if (string.IsNullOrWhiteSpace(text)) return Enumerable.Range(1, pageCount).ToList();

        var normalized = text.Replace('–', '-').Replace('—', '-').Replace(" ", "");
        var pages = new List<int>();
        foreach (var raw in normalized.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw == "*")
            {
                pages.AddRange(Enumerable.Range(1, pageCount));
                continue;
            }

            var dash = raw.IndexOf('-');
            int start;
            int end;
            if (dash > 0)
            {
                var startText = raw[..dash];
                var endText = raw[(dash + 1)..];
                if (!int.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out start))
                    throw new FormatException($"'{raw}' is not a page number.");
                if (endText.Length == 0)
                    end = pageCount;
                else if (!int.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out end))
                    throw new FormatException($"'{raw}' is not a page range.");
            }
            else
            {
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out start))
                    throw new FormatException($"'{raw}' is not a page number.");
                end = start;
            }

            if (start < 1 || end < 1 || start > pageCount || end > pageCount)
                throw new FormatException($"Pages must be between 1 and {pageCount}.");
            if (start > end)
                throw new FormatException($"'{raw}' runs backwards.");
            for (var page = start; page <= end; page++)
            {
                pages.Add(page);
                if (pages.Count > 5000)
                    throw new FormatException("That selection contains more than 5,000 pages.");
            }
        }

        if (pages.Count == 0) throw new FormatException("Enter at least one page or range.");
        return pages;
    }

    /// <summary>Builds a compact range string for PDFium, preserving order.</summary>
    public static string ToRangeString(IEnumerable<int> pages)
    {
        var list = pages.ToList();
        var parts = new List<string>();
        for (var i = 0; i < list.Count; i++)
        {
            var start = list[i];
            var end = start;
            while (i + 1 < list.Count && list[i + 1] == end + 1)
            {
                end = list[++i];
            }

            parts.Add(start == end ? start.ToString(CultureInfo.InvariantCulture)
                : $"{start}-{end}");
        }
        return string.Join(",", parts);
    }

    public static string Describe(IReadOnlyList<int> pages)
    {
        if (pages.Count == 0) return "No pages selected";
        if (pages.Count == 1) return $"Page {pages[0]}";
        return $"{pages.Count} pages selected";
    }
}

public static class EditorFileNaming
{
    public static string UniquePath(string directory, string baseName, string extension, bool overwrite)
    {
        Directory.CreateDirectory(directory);
        var ext = extension.TrimStart('.');
        var safe = string.IsNullOrWhiteSpace(baseName) ? "Nasaq edit" : baseName;
        var candidate = Path.Combine(directory, $"{safe}.{ext}");
        if (overwrite || !File.Exists(candidate)) return candidate;

        for (var index = 2; index < 10_000; index++)
        {
            candidate = Path.Combine(directory, $"{safe} ({index}).{ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(directory, $"{safe}-{Guid.NewGuid():N}.{ext}");
    }

    /// <summary>
    /// Moves a freshly written file into place. Antivirus and shell thumbnail
    /// providers can briefly hold a new file, so retry a sharing violation
    /// instead of failing an otherwise successful edit.
    /// </summary>
    public static void MoveWithRetry(string source, string destination, bool overwrite)
    {
        const int attempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < attempts)
            {
                Thread.Sleep(100);
            }
        }
    }

    public static string SafeStem(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(stem)) return "Nasaq edit";
        foreach (var c in Path.GetInvalidFileNameChars())
            stem = stem.Replace(c, '_');
        stem = stem.Trim().TrimEnd('.');
        if (stem.Length > 90) stem = stem[..90].TrimEnd();
        return string.IsNullOrWhiteSpace(stem) ? "Nasaq edit" : stem;
    }

    public static long SafeSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }
}
