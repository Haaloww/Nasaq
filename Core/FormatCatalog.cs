using System.IO;

namespace Nasaq.Core;

public enum MediaCategory
{
    Image,
    Audio,
    Video,
    Document,
    Unknown
}

public sealed record OutputFormat(
    string Id,
    string Label,
    MediaCategory Category,
    string Extension,
    bool Lossy,
    string Hint)
{
    public override string ToString() => Label;
}

public static class FormatCatalog
{
    public static readonly IReadOnlyList<OutputFormat> ImageFormats = new[]
    {
        new OutputFormat("png",  "PNG",  MediaCategory.Image, "png",  false, "Lossless, sharp edges"),
        new OutputFormat("jpg",  "JPG",  MediaCategory.Image, "jpg",  true,  "Small photos, no transparency"),
        new OutputFormat("webp", "WEBP", MediaCategory.Image, "webp", true,  "Modern, tiny files"),
        new OutputFormat("bmp",  "BMP",  MediaCategory.Image, "bmp",  false, "Uncompressed bitmap"),
        new OutputFormat("tiff", "TIFF", MediaCategory.Image, "tiff", false, "Print & archival"),
        new OutputFormat("gif",  "GIF",  MediaCategory.Image, "gif",  false, "Legacy, 256 colours"),
        new OutputFormat("ico",  "ICO",  MediaCategory.Image, "ico",  false, "Windows application icon"),
    };

    public static readonly IReadOnlyList<OutputFormat> AudioFormats = new[]
    {
        new OutputFormat("mp3",  "MP3",  MediaCategory.Audio, "mp3",  true,  "Universal compatibility"),
        new OutputFormat("wav",  "WAV",  MediaCategory.Audio, "wav",  false, "Uncompressed PCM"),
        new OutputFormat("m4a",  "M4A",  MediaCategory.Audio, "m4a",  true,  "AAC in an MP4 container"),
        new OutputFormat("flac", "FLAC", MediaCategory.Audio, "flac", false, "Lossless compression"),
        new OutputFormat("ogg",  "OGG",  MediaCategory.Audio, "ogg",  true,  "Vorbis, open format"),
        new OutputFormat("opus", "OPUS", MediaCategory.Audio, "opus", true,  "Efficient low bitrate"),
    };

    public static readonly IReadOnlyList<OutputFormat> VideoFormats = new[]
    {
        new OutputFormat("mp4",  "MP4",  MediaCategory.Video, "mp4",  true,  "Plays everywhere"),
        new OutputFormat("mkv",  "MKV",  MediaCategory.Video, "mkv",  true,  "Flexible container"),
        new OutputFormat("webm", "WEBM", MediaCategory.Video, "webm", true,  "Open web video"),
        new OutputFormat("mov",  "MOV",  MediaCategory.Video, "mov",  true,  "Apple / editing"),
        new OutputFormat("avi",  "AVI",  MediaCategory.Video, "avi",  true,  "Legacy container"),
        new OutputFormat("gifv", "GIF",  MediaCategory.Video, "gif",  false, "Silent looping clip"),
    };

    public static readonly IReadOnlyList<OutputFormat> DocumentFormats = new[]
    {
        new OutputFormat("pdf",  "PDF",  MediaCategory.Document, "pdf",  false, "Share-ready document"),
        new OutputFormat("docx", "DOCX", MediaCategory.Document, "docx", false, "Editable in Word"),
        new OutputFormat("txt",  "TXT",  MediaCategory.Document, "txt",  false, "Plain text"),
        new OutputFormat("html", "HTML", MediaCategory.Document, "html", false, "Styled web page"),
        new OutputFormat("md",   "MD",   MediaCategory.Document, "md",   false, "Markdown notes"),
    };

    public static readonly IReadOnlyList<OutputFormat> All =
        ImageFormats.Concat(AudioFormats).Concat(VideoFormats).Concat(DocumentFormats).ToList();

    private static readonly Dictionary<string, MediaCategory> ExtensionMap = new(StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyDictionary<MediaCategory, IReadOnlyList<OutputFormat>> ByCategory =
        new Dictionary<MediaCategory, IReadOnlyList<OutputFormat>>
        {
            [MediaCategory.Image] = ImageFormats,
            [MediaCategory.Audio] = AudioFormats,
            [MediaCategory.Video] = VideoFormats,
            [MediaCategory.Document] = DocumentFormats,
        };

    static FormatCatalog()
    {
        Add(MediaCategory.Image,
            ".png", ".jpg", ".jpeg", ".jpe", ".bmp", ".gif", ".tif", ".tiff", ".webp",
            ".ico", ".heic", ".heif", ".avif", ".jfif");
        Add(MediaCategory.Audio,
            ".mp3", ".wav", ".flac", ".aac", ".m4a", ".m4b", ".ogg", ".oga", ".opus",
            ".wma", ".aiff", ".aif", ".amr", ".ac3", ".mid", ".midi");
        Add(MediaCategory.Video,
            ".mp4", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".flv", ".m4v", ".mpg",
            ".mpeg", ".ts", ".m2ts", ".3gp", ".ogv", ".vob");
        Add(MediaCategory.Document,
            ".pdf", ".docx", ".doc", ".txt", ".md", ".markdown", ".html", ".htm",
            ".rtf", ".csv", ".json", ".xml", ".log", ".yaml", ".yml", ".srt", ".ini");
    }

    private static void Add(MediaCategory category, params string[] extensions)
    {
        foreach (var ext in extensions) ExtensionMap[ext] = category;
    }

    public static MediaCategory Detect(string path)
    {
        var ext = Path.GetExtension(path);
        return ExtensionMap.TryGetValue(ext, out var category) ? category : MediaCategory.Unknown;
    }

    public static bool IsSupported(string path) => Detect(path) != MediaCategory.Unknown;

    public static IReadOnlyCollection<string> InputExtensions => ExtensionMap.Keys;

    public static IEnumerable<string> InputExtensionsFor(MediaCategory category)
        => ExtensionMap.Where(kv => kv.Value == category).Select(kv => kv.Key);

    public static string BuildFileDialogFilter()
        => "All supported files|" + string.Join(";", ExtensionMap.Keys.Select(e => "*" + e)) + "|" +
           "Images|" + string.Join(";", InputExtensionsFor(MediaCategory.Image).Select(e => "*" + e)) + "|" +
           "Audio|" + string.Join(";", InputExtensionsFor(MediaCategory.Audio).Select(e => "*" + e)) + "|" +
           "Video|" + string.Join(";", InputExtensionsFor(MediaCategory.Video).Select(e => "*" + e)) + "|" +
           "Documents|" + string.Join(";", InputExtensionsFor(MediaCategory.Document).Select(e => "*" + e)) + "|" +
           "All files|*.*";

    public static string CategoryName(MediaCategory category) => category switch
    {
        MediaCategory.Image => "Image",
        MediaCategory.Audio => "Audio",
        MediaCategory.Video => "Video",
        MediaCategory.Document => "Document",
        _ => "Unsupported",
    };

    public static string CategoryGlyph(MediaCategory category) => category switch
    {
        MediaCategory.Image => "\uEB9F",
        MediaCategory.Audio => "\uE8D6",
        MediaCategory.Video => "\uE714",
        MediaCategory.Document => "\uE8A5",
        _ => "\uE7BA",
    };

    public static OutputFormat? Find(MediaCategory category, string id)
        => ByCategory.TryGetValue(category, out var list)
            ? list.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            : null;

    public static IReadOnlyList<OutputFormat> For(MediaCategory category)
        => ByCategory.TryGetValue(category, out var list) ? list : Array.Empty<OutputFormat>();

    public static OutputFormat? FindForExtension(MediaCategory category, string extension)
        => For(category).FirstOrDefault(f => f.Extension.Equals(extension.TrimStart('.'), StringComparison.OrdinalIgnoreCase));
}
