namespace Nasaq.Core;

public enum InstallerMediaMode
{
    Auto,
    Video,
    Audio,
    Photo,
}

public enum InstallerMediaKind
{
    Unknown,
    Image,
    Audio,
    Video,
    Stream,
    Page,
}

public sealed record InstallerOption(string Id, string Label, string Description, string Glyph = "");

/// <summary>
/// The small set of choices the Installer exposes. Kept in one place so the
/// view model, the settings page and the downloader agree on valid values.
/// </summary>
public static class InstallerCatalog
{
    public static IReadOnlyList<InstallerOption> Modes { get; } = new[]
    {
        new InstallerOption("auto", "Auto", "Let Nasaq pick the best media on the link", "\uE8B9"),
        new InstallerOption("video", "Video", "Download the picture and its sound", "\uE714"),
        new InstallerOption("audio", "Audio", "Keep only the soundtrack", "\uE8D6"),
        new InstallerOption("photo", "Photo", "Download an image from the link", "\uEB9F"),
    };

    public static IReadOnlyList<InstallerOption> Qualities { get; } = new[]
    {
        new InstallerOption("best", "Best", "Highest quality the site offers"),
        new InstallerOption("1080", "1080p", "Cap video at 1080p"),
        new InstallerOption("720", "720p", "Cap video at 720p"),
        new InstallerOption("480", "480p", "Cap video at 480p"),
    };

    public static IReadOnlyList<InstallerOption> Browsers { get; } = new[]
    {
        new InstallerOption("none", "No cookies", "Public links only"),
        new InstallerOption("chrome", "Chrome", "Read Chrome cookies"),
        new InstallerOption("edge", "Edge", "Read Edge cookies"),
        new InstallerOption("firefox", "Firefox", "Read Firefox cookies"),
        new InstallerOption("brave", "Brave", "Read Brave cookies"),
        new InstallerOption("opera", "Opera", "Read Opera cookies"),
        new InstallerOption("vivaldi", "Vivaldi", "Read Vivaldi cookies"),
        new InstallerOption("chromium", "Chromium", "Read Chromium cookies"),
    };

    public static InstallerMediaMode ParseMode(string? id) => id?.ToLowerInvariant() switch
    {
        "video" => InstallerMediaMode.Video,
        "audio" => InstallerMediaMode.Audio,
        "photo" => InstallerMediaMode.Photo,
        _ => InstallerMediaMode.Auto,
    };

    public static string ModeId(InstallerMediaMode mode) => mode switch
    {
        InstallerMediaMode.Video => "video",
        InstallerMediaMode.Audio => "audio",
        InstallerMediaMode.Photo => "photo",
        _ => "auto",
    };

    public static string ModeLabel(InstallerMediaMode mode) => mode switch
    {
        InstallerMediaMode.Video => "Video",
        InstallerMediaMode.Audio => "Audio",
        InstallerMediaMode.Photo => "Photo",
        _ => "Auto",
    };

    public static bool IsKnownQuality(string? id) =>
        !string.IsNullOrWhiteSpace(id) && Qualities.Any(q => q.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static bool IsKnownBrowser(string? id) =>
        !string.IsNullOrWhiteSpace(id) && Browsers.Any(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static bool Allows(InstallerMediaMode mode, InstallerMediaKind kind) => mode switch
    {
        InstallerMediaMode.Auto => kind is InstallerMediaKind.Image or InstallerMediaKind.Audio
            or InstallerMediaKind.Video or InstallerMediaKind.Stream,
        InstallerMediaMode.Video => kind is InstallerMediaKind.Video or InstallerMediaKind.Stream,
        InstallerMediaMode.Audio => kind is InstallerMediaKind.Audio,
        InstallerMediaMode.Photo => kind is InstallerMediaKind.Image,
        _ => false,
    };
}
