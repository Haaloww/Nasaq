using System.IO;
using System.Windows.Media.Imaging;
using Nasaq.Controls;
using Nasaq.Core;

namespace Nasaq.ViewModels;

public sealed class AudioTrackItem : ObservableObject, ITimelineSegment
{
    private double _barWidth = 120;
    private string _barColor = "#7C9CFF";
    private int _order;
    private double _startOffset;

    public AudioTrackItem(string path, double sourceDuration, long bytes,
        double startSeconds = 0, double endSeconds = double.NaN)
    {
        Path = path;
        Bytes = bytes;
        SourceDuration = Math.Max(0, sourceDuration);
        StartSeconds = Math.Clamp(startSeconds, 0, SourceDuration);
        var minimumEnd = Math.Min(SourceDuration, StartSeconds + 0.01);
        EndSeconds = double.IsNaN(endSeconds)
            ? SourceDuration
            : Math.Clamp(endSeconds, minimumEnd, Math.Max(minimumEnd, SourceDuration));
        FileName = System.IO.Path.GetFileName(path);
        Duration = Math.Max(0.01, EndSeconds - StartSeconds);
        DurationText = EditorTime.Format(Duration);
        RangeText = IsTrimmed
            ? $"{EditorTime.Format(StartSeconds)} – {EditorTime.Format(EndSeconds)}"
            : DurationText;
        Meta = IsTrimmed
            ? $"{DurationText} of {EditorTime.Format(SourceDuration)} · {FileSize.Format(bytes)}"
            : $"{DurationText} · {FileSize.Format(bytes)}";
    }

    public string Path { get; }
    public string FileName { get; }
    public double SourceDuration { get; }
    public double StartSeconds { get; }
    public double EndSeconds { get; }
    public double Duration { get; }
    public long Bytes { get; }
    public string DurationText { get; }
    public string RangeText { get; }
    public string Meta { get; }
    public string Glyph => "\uE8D6";
    public string Label => FileName;
    public bool IsTrimmed => StartSeconds > 0.005 || EndSeconds < SourceDuration - 0.005;

    public int Order
    {
        get => _order;
        set
        {
            if (!Set(ref _order, value)) return;
            OnPropertyChanged(nameof(OrderText));
        }
    }

    public string OrderText => $"{Order:00}";

    public double StartOffset
    {
        get => _startOffset;
        set
        {
            if (!Set(ref _startOffset, Math.Max(0, value))) return;
            OnPropertyChanged(nameof(EndOffset));
        }
    }

    public double EndOffset => StartOffset + Duration;

    public double BarWidth
    {
        get => _barWidth;
        set => Set(ref _barWidth, value);
    }

    public string BarColor
    {
        get => _barColor;
        set => Set(ref _barColor, value);
    }

    public AudioTrackItem Slice(double localStart, double localEnd)
    {
        return new AudioTrackItem(Path, SourceDuration, Bytes,
            StartSeconds + Math.Clamp(localStart, 0, Duration),
            StartSeconds + Math.Clamp(localEnd, 0, Duration));
    }
}

public sealed class PdfQueueItem : ObservableObject
{
    private int _order;

    public PdfQueueItem(string path, int pageCount, long bytes)
    {
        Path = path;
        PageCount = pageCount;
        Bytes = bytes;
        FileName = System.IO.Path.GetFileName(path);
        PageText = pageCount == 1 ? "1 page" : $"{pageCount} pages";
        Meta = $"{PageText} · {FileSize.Format(bytes)}";
    }

    public string Path { get; }
    public string FileName { get; }
    public int PageCount { get; }
    public long Bytes { get; }
    public string PageText { get; }
    public string Meta { get; }
    public string Glyph => "\uE8A5";

    public int Order
    {
        get => _order;
        set
        {
            if (!Set(ref _order, value)) return;
            OnPropertyChanged(nameof(OrderText));
        }
    }

    public string OrderText => $"{Order:00}";
}

public sealed class PdfThumbnailItem
{
    public PdfThumbnailItem(int page, BitmapSource? image)
    {
        Page = page;
        Image = image;
        PageText = page.ToString();
    }

    public int Page { get; }
    public BitmapSource? Image { get; }
    public string PageText { get; }
}
