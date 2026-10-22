using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Nasaq.Services;

namespace Nasaq.Core;

public sealed record InstallerDownloadRequest(
    string Url,
    InstallerMediaMode Mode,
    string Quality,
    string OutputDirectory,
    string CookiesBrowser,
    string? FfmpegPath,
    bool Overwrite,
    bool PreserveMetadata,
    int AudioBitrate);

public sealed record InstallerProgress(
    double Fraction,
    string Stage,
    string? Title = null,
    string? FileName = null,
    string? Speed = null,
    string? Eta = null,
    long? DownloadedBytes = null,
    long? TotalBytes = null)
{
    public bool IsIndeterminate => Fraction < 0;
}

public sealed record InstallerDownloadResult(
    bool Success,
    bool Canceled,
    IReadOnlyList<string> OutputPaths,
    string? Title,
    string? Error,
    string? Detail,
    string Engine,
    TimeSpan Elapsed)
{
    public string? OutputPath => OutputPaths.Count > 0 ? OutputPaths[0] : null;

    public static InstallerDownloadResult Ok(IReadOnlyList<string> paths, string? title, TimeSpan elapsed, string engine) =>
        new(true, false, paths, title, null, null, engine, elapsed);

    public static InstallerDownloadResult Cancelled(TimeSpan elapsed, string engine) =>
        new(false, true, Array.Empty<string>(), null, "Download cancelled.", null, engine, elapsed);

    public static InstallerDownloadResult Fail(string error, string? detail, TimeSpan elapsed, string engine) =>
        new(false, false, Array.Empty<string>(), null, error, detail, engine, elapsed);
}

public sealed record InstallerEngineResult(
    bool Success,
    bool Canceled,
    string? Error,
    string? Detail,
    YtDlpInfo Info);

public class InstallerException : Exception
{
    public string FriendlyMessage { get; }
    public string? Hint { get; }

    public InstallerException(string friendlyMessage, string? detail = null, Exception? inner = null,
        string? hint = null) : base(detail ?? friendlyMessage, inner)
    {
        FriendlyMessage = friendlyMessage;
        Hint = hint;
    }
}

internal sealed class InstallerNotMediaException : InstallerException
{
    public InstallerNotMediaException(string detail)
        : base("That link doesn't point straight to a media file.", detail) { }
}

internal sealed class InstallerModeMismatchException : InstallerException
{
    public InstallerModeMismatchException(InstallerMediaMode mode, InstallerMediaKind kind)
        : base(MismatchMessage(mode, kind), $"Mode {mode} cannot accept {kind}.") { }

    private static string MismatchMessage(InstallerMediaMode mode, InstallerMediaKind kind)
    {
        var what = kind switch
        {
            InstallerMediaKind.Image => "an image",
            InstallerMediaKind.Audio => "audio",
            InstallerMediaKind.Video => "a video",
            InstallerMediaKind.Stream => "a streaming video",
            _ => "different media",
        };
        var suggestion = kind switch
        {
            InstallerMediaKind.Image => "Photo",
            InstallerMediaKind.Audio => "Audio",
            InstallerMediaKind.Video or InstallerMediaKind.Stream => "Video",
            _ => "Auto",
        };
        return $"That link points to {what}, not {ModeNoun(mode)}. Choose {suggestion} or Auto instead.";
    }

    private static string ModeNoun(InstallerMediaMode mode) => mode switch
    {
        InstallerMediaMode.Video => "a video",
        InstallerMediaMode.Audio => "audio",
        InstallerMediaMode.Photo => "a photo",
        _ => "that kind of media",
    };
}

/// <summary>
/// Downloads a link's photo, audio or video. Direct media URLs are streamed
/// over HTTP; webpages go through yt-dlp, with an Open Graph / Twitter-card
/// fallback for ordinary sites that expose their media in the page head.
/// </summary>
public sealed class InstallerService
{
    private const string EngineDownloadUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    private const string EngineChecksumUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly Regex UrlRegex = new(@"https?://[^\s<>""']+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MetaRegex = new(@"<meta\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LinkRegex = new(@"<link\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex MediaTagRegex = new(@"<(video|audio|source)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ImgRegex = new(@"<img\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AttrRegex = new(
        @"(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s""'=<>`]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JsonMediaRegex = new(
        @"""(?:contentUrl|thumbnailUrl)""\s*:\s*""(?<url>(?:\\.|[^""\\])*)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff",
        ".ico", ".avif", ".heic", ".heif", ".svg",
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".oga", ".opus",
        ".wma", ".aiff", ".aif", ".amr",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".flv",
        ".mpg", ".mpeg", ".ts", ".3gp", ".ogv",
    };

    private static readonly HashSet<string> StreamExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m3u8", ".mpd", ".ism",
    };

    private readonly object _reserveLock = new();
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private YtDlpInfo _ytDlp;

    public InstallerService(YtDlpInfo ytDlp) => _ytDlp = ytDlp;

    public YtDlpInfo YtDlp => _ytDlp;

    public YtDlpInfo RefreshYtDlp(string? configuredPath)
    {
        _ytDlp = YtDlpLocator.Locate(configuredPath);
        return _ytDlp;
    }

    // ─────────────────────────── downloads ───────────────────────────

    public async Task<InstallerDownloadResult> DownloadAsync(InstallerDownloadRequest request,
        IProgress<InstallerProgress>? progress, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var engine = "Nasaq";
        string? lastError = null;
        string? lastDetail = null;

        try
        {
            var uri = ParseUrl(request.Url)
                      ?? throw new InstallerException(
                          "That doesn't look like a web link.",
                          "No http(s) URL was found in the pasted text.",
                          hint: "Paste a link that starts with http:// or https://.");

            Directory.CreateDirectory(request.OutputDirectory);
            var mode = request.Mode;

            Log.Info("installer.start", $"Installing media from {uri.Host}",
                ("host", uri.Host),
                ("url", SafeUrl(uri)),
                ("mode", InstallerCatalog.ModeId(mode)),
                ("quality", request.Quality),
                ("output", request.OutputDirectory),
                ("cookies", string.IsNullOrWhiteSpace(request.CookiesBrowser) ? "none" : request.CookiesBrowser));

            progress?.Report(new InstallerProgress(-1, "Checking link", FileName: uri.Host));

            var probe = await ProbeAsync(uri, ct);
            var directEligible = probe.Kind is InstallerMediaKind.Image
                or InstallerMediaKind.Audio or InstallerMediaKind.Video;

            if (directEligible)
            {
                var allowed = mode == InstallerMediaMode.Auto || InstallerCatalog.Allows(mode, probe.Kind);
                if (allowed)
                {
                    try
                    {
                        var direct = await DownloadDirectAsync(uri, request, probe, progress, ct);
                        stopwatch.Stop();
                        Log.Info("installer.done", $"Saved {Path.GetFileName(direct.Path)}",
                            ("output", direct.Path),
                            ("kind", direct.Kind.ToString()),
                            ("elapsed", $"{stopwatch.ElapsedMilliseconds} ms"),
                            ("size", FileSize.Format(new FileInfo(direct.Path).Length)),
                            ("engine", "HTTP"));
                        return InstallerDownloadResult.Ok(new[] { direct.Path }, direct.Title,
                            stopwatch.Elapsed, "HTTP");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (InstallerModeMismatchException) { throw; }
                    catch (InstallerNotMediaException ex)
                    {
                        lastError = ex.FriendlyMessage;
                        lastDetail = ex.Message;
                    }
                    catch (InstallerException ex)
                    {
                        lastError = ex.FriendlyMessage;
                        lastDetail = ex.Message;
                        Log.Warn("installer.http", "Direct download failed; trying the fallback",
                            ("url", SafeUrl(uri)), ("reason", ex.Message));
                    }
                    catch (Exception ex)
                    {
                        lastError = "The direct download failed.";
                        lastDetail = ex.Message;
                        Log.Warn("installer.http", "Direct download failed; trying the fallback",
                            ("url", SafeUrl(uri)), ("reason", ex.Message));
                    }
                }
                else if (mode == InstallerMediaMode.Audio && probe.Kind == InstallerMediaKind.Video)
                {
                    // A direct video can still become audio through yt-dlp + FFmpeg.
                }
                else
                {
                    throw new InstallerModeMismatchException(mode, probe.Kind);
                }
            }
            else if (mode == InstallerMediaMode.Photo && probe.Kind == InstallerMediaKind.Stream)
            {
                throw new InstallerModeMismatchException(mode, probe.Kind);
            }

            if (mode == InstallerMediaMode.Photo)
            {
                var page = await TryPageMediaAsync(uri, request, progress, ct);
                if (page is not null)
                {
                    stopwatch.Stop();
                    return page with { Elapsed = stopwatch.Elapsed, Engine = "Web page" };
                }
            }

            if (_ytDlp.IsAvailable)
            {
                engine = string.IsNullOrWhiteSpace(_ytDlp.Version) ? "yt-dlp" : $"yt-dlp {_ytDlp.Version}";
                var yt = await RunYtDlpAsync(uri, request, progress, ct);
                if (yt.Success || yt.Canceled)
                {
                    stopwatch.Stop();
                    return yt with { Elapsed = stopwatch.Elapsed, Engine = engine };
                }

                lastError = yt.Error ?? lastError;
                lastDetail = yt.Detail ?? lastDetail;
            }
            else if (lastError is null)
            {
                lastError = "The installer engine isn't set up yet.";
                lastDetail = "Nasaq can download direct media links without it, but websites need the one-time engine setup.";
            }

            if (mode != InstallerMediaMode.Photo &&
                probe.Kind is InstallerMediaKind.Page or InstallerMediaKind.Unknown)
            {
                var page = await TryPageMediaAsync(uri, request, progress, ct);
                if (page is not null)
                {
                    stopwatch.Stop();
                    return page with { Elapsed = stopwatch.Elapsed, Engine = "Web page" };
                }
            }

            stopwatch.Stop();
            return InstallerDownloadResult.Fail(
                lastError ?? "Nothing could be downloaded from that link.",
                lastDetail, stopwatch.Elapsed, engine);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            Log.Info("installer.cancel", "Installer download cancelled",
                ("url", request.Url));
            return InstallerDownloadResult.Cancelled(stopwatch.Elapsed, engine);
        }
        catch (InstallerException ex)
        {
            stopwatch.Stop();
            Log.Warn("installer.fail", ex.FriendlyMessage, ("detail", ex.Message));
            return InstallerDownloadResult.Fail(ex.FriendlyMessage, ex.Message, stopwatch.Elapsed, engine);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log.Error("installer.fail", "Unexpected installer failure", ex,
                ("url", request.Url));
            return InstallerDownloadResult.Fail(
                "Something went wrong while installing that link. The full details were written to the log.",
                ex.Message, stopwatch.Elapsed, engine);
        }
    }

    // ─────────────────────────── probing ───────────────────────────

    private async Task<ProbeResult> ProbeAsync(Uri uri, CancellationToken ct)
    {
        var byExtension = KindFromExtension(uri.AbsolutePath);
        if (byExtension != InstallerMediaKind.Unknown)
            return new ProbeResult(byExtension, null, null, null, true);

        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await Http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode)
                return FromResponse(response, uri, false);

            if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden
                or HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            {
                using var get = new HttpRequestMessage(HttpMethod.Get, uri);
                using var getResponse = await Http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
                if (getResponse.IsSuccessStatusCode)
                    return FromResponse(getResponse, uri, false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug("installer.probe", "Could not probe the link; letting yt-dlp try it",
                ("url", SafeUrl(uri)), ("reason", ex.Message));
        }

        return new ProbeResult(InstallerMediaKind.Unknown, null, null, null, false);
    }

    private static ProbeResult FromResponse(HttpResponseMessage response, Uri fallback, bool fromExtension)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var kind = KindFromContentType(mediaType);
        if (kind == InstallerMediaKind.Unknown)
        {
            var finalUri = response.RequestMessage?.RequestUri ?? fallback;
            kind = KindFromExtension(finalUri.AbsolutePath);
        }

        return new ProbeResult(
            kind,
            mediaType,
            response.Content.Headers.ContentLength,
            response.Content.Headers.ContentDisposition,
            fromExtension);
    }

    // ─────────────────────────── direct HTTP ───────────────────────────

    private async Task<DirectResult> DownloadDirectAsync(Uri uri, InstallerDownloadRequest request,
        ProbeResult probe, IProgress<InstallerProgress>? progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var kind = KindFromContentType(mediaType);
        if (kind == InstallerMediaKind.Unknown) kind = probe.Kind;
        if (kind == InstallerMediaKind.Unknown)
            kind = KindFromExtension((response.RequestMessage?.RequestUri ?? uri).AbsolutePath);

        if (kind is InstallerMediaKind.Page or InstallerMediaKind.Unknown)
            throw new InstallerNotMediaException(
                $"The response was {mediaType ?? "not a media type"}; a webpage extractor is needed.");

        if (kind == InstallerMediaKind.Stream)
            throw new InstallerNotMediaException("The link is a streaming manifest, not a file.");

        if (request.Mode != InstallerMediaMode.Auto && !InstallerCatalog.Allows(request.Mode, kind))
            throw new InstallerModeMismatchException(request.Mode, kind);

        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        var fileName = BuildFileName(response, finalUri, kind, mediaType);
        var finalPath = ReserveOutputPath(request.OutputDirectory, fileName, request.Overwrite);
        var temp = Path.Combine(request.OutputDirectory, $".nasaq-{Guid.NewGuid():N}.part");
        var total = response.Content.Headers.ContentLength;
        var downloaded = 0L;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = -1.0;

        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write,
                             FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloaded += read;

                    var fraction = total is > 0 ? (double)downloaded / total.Value : -1;
                    var speed = downloaded / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                    var eta = total is > 0 && speed > 0
                        ? TimeSpan.FromSeconds(Math.Max(0, (total.Value - downloaded) / speed))
                        : (TimeSpan?)null;

                    if (fraction < 0 || fraction - lastReport >= 0.008 || fraction >= 1)
                    {
                        lastReport = fraction;
                        progress?.Report(new InstallerProgress(
                            fraction,
                            "Downloading",
                            FileName: Path.GetFileName(finalPath),
                            Speed: FormatSpeed(speed),
                            Eta: eta is null ? null : FormatEta(eta.Value),
                            DownloadedBytes: downloaded,
                            TotalBytes: total));
                    }
                }
            }

            File.Move(temp, finalPath, overwrite: true);
            progress?.Report(new InstallerProgress(1, "Done", FileName: Path.GetFileName(finalPath)));
            return new DirectResult(finalPath, Path.GetFileName(finalPath), kind, stopwatch.Elapsed);
        }
        catch
        {
            Cleanup(temp);
            throw;
        }
        finally
        {
            _reserved.Remove(finalPath);
        }
    }

    // ─────────────────────────── webpage fallback ───────────────────────────

    private async Task<InstallerDownloadResult?> TryPageMediaAsync(Uri pageUri, InstallerDownloadRequest request,
        IProgress<InstallerProgress>? progress, CancellationToken ct)
    {
        try
        {
            progress?.Report(new InstallerProgress(-1, "Looking for media on the page", FileName: pageUri.Host));
            using var response = await Http.GetAsync(pageUri, HttpCompletionOption.ResponseContentRead, ct);
            if (!response.IsSuccessStatusCode) return null;

            var finalUri = response.RequestMessage?.RequestUri ?? pageUri;
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var kind = KindFromContentType(mediaType);
            if (kind is InstallerMediaKind.Image or InstallerMediaKind.Audio or InstallerMediaKind.Video)
            {
                if (request.Mode == InstallerMediaMode.Auto || InstallerCatalog.Allows(request.Mode, kind))
                {
                    var direct = await DownloadDirectAsync(finalUri, request,
                        new ProbeResult(kind, mediaType, response.Content.Headers.ContentLength, null, false),
                        progress, ct);
                    return InstallerDownloadResult.Ok(new[] { direct.Path }, direct.Title, TimeSpan.Zero, "HTTP");
                }
            }

            if (kind != InstallerMediaKind.Page && kind != InstallerMediaKind.Unknown) return null;

            var length = response.Content.Headers.ContentLength;
            if (length is > 12 * 1024 * 1024)
            {
                Log.Debug("installer.page", "Skipping a very large HTML page", ("bytes", length.ToString()));
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            var candidates = ExtractMediaCandidates(html, finalUri, request.Mode);
            if (candidates.Count == 0)
            {
                Log.Debug("installer.page", "No media metadata was found on the page", ("url", SafeUrl(pageUri)));
                return null;
            }

            foreach (var candidate in candidates.Take(6))
            {
                try
                {
                    var candidateKind = KindFromExtension(candidate.AbsolutePath);
                    var direct = await DownloadDirectAsync(candidate, request,
                        new ProbeResult(candidateKind, null, null, null, false), progress, ct);
                    Log.Info("installer.page", "Downloaded media exposed by the webpage",
                        ("page", SafeUrl(pageUri)), ("media", SafeUrl(candidate)));
                    return InstallerDownloadResult.Ok(new[] { direct.Path }, direct.Title, TimeSpan.Zero, "Web page");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Debug("installer.page", "A page media candidate could not be downloaded",
                        ("candidate", SafeUrl(candidate)), ("reason", ex.Message));
                }
            }

            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug("installer.page", "Page media extraction failed",
                ("url", SafeUrl(pageUri)), ("reason", ex.Message));
            return null;
        }
    }

    /// <summary>
    /// Pulls Open Graph, Twitter-card, JSON-LD and ordinary media URLs out of
    /// a page. Relative URLs are resolved against <paramref name="baseUri"/>.
    /// This is a fallback for sites yt-dlp doesn't know; it is intentionally
    /// conservative and returns at most a handful of candidates.
    /// </summary>
    public static IReadOnlyList<Uri> ExtractMediaCandidates(string html, Uri baseUri,
        InstallerMediaMode mode)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<Uri>();

        var found = new List<Candidate>();
        var order = 0;

        void Add(string? raw, int priority, InstallerMediaKind kind)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            raw = WebUtility.HtmlDecode(raw.Trim());
            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                return;
            if (!Uri.TryCreate(baseUri, raw, out var uri)) return;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
            found.Add(new Candidate(priority, order++, uri, kind));
        }

        static bool Allowed(InstallerMediaMode m, InstallerMediaKind k) =>
            m == InstallerMediaMode.Auto
                ? k is InstallerMediaKind.Image or InstallerMediaKind.Audio or InstallerMediaKind.Video
                : InstallerCatalog.Allows(m, k);

        foreach (Match match in MetaRegex.Matches(html))
        {
            var attrs = ParseAttributes(match.Value);
            var key = (attrs.GetValueOrDefault("property")
                       ?? attrs.GetValueOrDefault("name")
                       ?? attrs.GetValueOrDefault("itemprop")
                       ?? "").ToLowerInvariant();
            var content = attrs.GetValueOrDefault("content");
            if (content is null) continue;

            var (kind, priority) = key switch
            {
                "og:video" or "og:video:url" or "og:video:secure_url" => (InstallerMediaKind.Video, 0),
                "twitter:player:stream" => (InstallerMediaKind.Video, 0),
                "og:audio" or "og:audio:url" or "og:audio:secure_url" => (InstallerMediaKind.Audio, 1),
                "og:image" or "og:image:url" or "og:image:secure_url" => (InstallerMediaKind.Image, 2),
                "twitter:image" or "twitter:image:src" => (InstallerMediaKind.Image, 2),
                "image" => (InstallerMediaKind.Image, 2),
                _ => (InstallerMediaKind.Unknown, -1),
            };
            if (kind == InstallerMediaKind.Unknown || !Allowed(mode, kind)) continue;
            Add(content, priority, kind);
        }

        foreach (Match match in LinkRegex.Matches(html))
        {
            var attrs = ParseAttributes(match.Value);
            var rel = attrs.GetValueOrDefault("rel")?.ToLowerInvariant() ?? "";
            var href = attrs.GetValueOrDefault("href");
            var (kind, priority) = rel switch
            {
                "video_src" => (InstallerMediaKind.Video, 0),
                "audio_src" => (InstallerMediaKind.Audio, 1),
                "image_src" => (InstallerMediaKind.Image, 2),
                _ => (InstallerMediaKind.Unknown, -1),
            };
            if (kind == InstallerMediaKind.Unknown || !Allowed(mode, kind)) continue;
            Add(href, priority, kind);
        }

        foreach (Match match in MediaTagRegex.Matches(html))
        {
            var tag = match.Groups[1].Value.ToLowerInvariant();
            var attrs = ParseAttributes(match.Value);
            var src = attrs.GetValueOrDefault("src");
            if (src is null) continue;

            var type = attrs.GetValueOrDefault("type");
            var kind = KindFromContentType(type);
            if (kind == InstallerMediaKind.Unknown)
                kind = KindFromExtension(src);
            if (kind == InstallerMediaKind.Unknown)
                kind = tag == "audio" ? InstallerMediaKind.Audio : InstallerMediaKind.Video;
            if (!Allowed(mode, kind)) continue;
            Add(src, kind switch
            {
                InstallerMediaKind.Audio => 1,
                InstallerMediaKind.Video => 3,
                _ => 4,
            }, kind);
        }

        foreach (Match match in JsonMediaRegex.Matches(html))
        {
            var raw = Regex.Unescape(match.Groups["url"].Value);
            var kind = KindFromExtension(raw);
            if (kind == InstallerMediaKind.Unknown)
                kind = match.Value.Contains("thumbnailUrl", StringComparison.OrdinalIgnoreCase)
                    ? InstallerMediaKind.Image
                    : InstallerMediaKind.Unknown;
            if (kind == InstallerMediaKind.Unknown || !Allowed(mode, kind)) continue;
            Add(raw, 4, kind);
        }

        if (mode is InstallerMediaMode.Auto or InstallerMediaMode.Photo)
        {
            var imageCount = 0;
            foreach (Match match in ImgRegex.Matches(html))
            {
                if (imageCount >= 8) break;
                var attrs = ParseAttributes(match.Value);
                if (LooksLikeTinyAsset(attrs)) continue;

                var raw = attrs.GetValueOrDefault("src")
                          ?? attrs.GetValueOrDefault("data-src")
                          ?? FirstSrcSetUrl(attrs.GetValueOrDefault("srcset"));
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                if (raw.Contains("logo", StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("icon", StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("sprite", StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("favicon", StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("avatar", StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("pixel", StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("tracking", StringComparison.OrdinalIgnoreCase)) continue;
                if (raw.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
                    raw.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) continue;

                Add(raw, 5, InstallerMediaKind.Image);
                imageCount++;
            }
        }

        return found
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.Order)
            .Select(c => c.Uri)
            .DistinctBy(u => u.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool LooksLikeTinyAsset(Dictionary<string, string> attrs)
    {
        foreach (var key in new[] { "width", "height" })
        {
            if (attrs.TryGetValue(key, out var raw) &&
                int.TryParse(raw.TrimEnd('p', 'x', 'P', 'X'), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var size) && size > 0 && size < 140)
                return true;
        }
        return false;
    }

    private static string? FirstSrcSetUrl(string? srcset)
    {
        if (string.IsNullOrWhiteSpace(srcset)) return null;
        var first = srcset.Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return first?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private static Dictionary<string, string> ParseAttributes(string tag)
    {
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttrRegex.Matches(tag))
        {
            var name = match.Groups["name"].Value;
            var value = match.Groups["value"].Value;
            if (!attrs.ContainsKey(name)) attrs[name] = value;
        }
        return attrs;
    }

    // ─────────────────────────── yt-dlp ───────────────────────────

    private async Task<InstallerDownloadResult> RunYtDlpAsync(Uri uri, InstallerDownloadRequest request,
        IProgress<InstallerProgress>? progress, CancellationToken ct)
    {
        if (!_ytDlp.IsAvailable || string.IsNullOrWhiteSpace(_ytDlp.Path))
            return InstallerDownloadResult.Fail(
                "The installer engine isn't set up yet.",
                "Open the Installer page and choose Set up engine.",
                TimeSpan.Zero, "yt-dlp");

        var stopwatch = Stopwatch.StartNew();
        var args = BuildYtDlpArguments(uri, request);
        var stderr = new StringBuilder();
        var outputPaths = new List<string>();
        var snapshot = SnapshotFiles(request.OutputDirectory);
        string? title = null;
        string? targetPath = null;
        var lastFraction = -1.0;

        void HandleStdout(string line)
        {
            if (line.StartsWith("nasaq-title:", StringComparison.Ordinal))
            {
                title = CleanMeta(line[12..]);
                progress?.Report(new InstallerProgress(-1, "Downloading", Title: title, FileName: title));
                return;
            }
            if (line.StartsWith("nasaq-target:", StringComparison.Ordinal))
            {
                var value = CleanMeta(line[13..]);
                if (!string.IsNullOrWhiteSpace(value) && value != "NA") targetPath = value;
                return;
            }
            if (line.StartsWith("nasaq-file:", StringComparison.Ordinal))
            {
                var value = CleanMeta(line[11..]);
                if (!string.IsNullOrWhiteSpace(value) && value != "NA" &&
                    !outputPaths.Contains(value, StringComparer.OrdinalIgnoreCase))
                    outputPaths.Add(value);
                return;
            }
            if (line.StartsWith("nasaq-progress:", StringComparison.Ordinal))
            {
                lastFraction = ParseYtDlpProgress(line[15..], title, progress, lastFraction);
                return;
            }
            if (line.StartsWith("[download] Destination:", StringComparison.OrdinalIgnoreCase))
            {
                targetPath = line[(line.IndexOf(':') + 1)..].Trim();
                return;
            }
            if (line.StartsWith("[Merger] Merging formats into", StringComparison.OrdinalIgnoreCase))
            {
                targetPath = line[(line.IndexOf("into", StringComparison.OrdinalIgnoreCase) + 4)..].Trim().Trim('"');
                return;
            }
            if (line.StartsWith("[ExtractAudio] Destination:", StringComparison.OrdinalIgnoreCase))
            {
                targetPath = line[(line.IndexOf(':') + 1)..].Trim();
                return;
            }
            if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            {
                stderr.AppendLine(line);
                return;
            }
            if (line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
            {
                stderr.AppendLine(line);
                Log.Debug("installer.ytdlp", line);
                return;
            }
            Log.Debug("installer.ytdlp", line);
        }

        void HandleStderr(string line)
        {
            stderr.AppendLine(line);
            Log.Debug("installer.ytdlp", line);
        }

        if (title is null) progress?.Report(new InstallerProgress(-1, "Preparing download", FileName: uri.Host));

        Log.Debug("installer.ytdlp", "Starting the installer engine",
            ("exe", _ytDlp.Path), ("arguments", string.Join(" ", args.Select(Quote))));

        var result = await ProcessRunner.RunAsync(_ytDlp.Path, args, HandleStdout, HandleStderr, ct);
        stopwatch.Stop();

        if (result.Canceled)
        {
            CleanupPartFiles(request.OutputDirectory, snapshot);
            Log.Info("installer.cancel", "yt-dlp download cancelled", ("host", uri.Host));
            return InstallerDownloadResult.Cancelled(stopwatch.Elapsed, _ytDlp.DisplayVersion);
        }

        if (result.ExitCode != 0)
        {
            var detail = Tail(stderr.ToString(), 18);
            CleanupPartFiles(request.OutputDirectory, snapshot);
            Log.Warn("installer.ytdlp", "The installer engine reported an error",
                ("host", uri.Host), ("exit", result.ExitCode.ToString()), ("detail", detail));
            return InstallerDownloadResult.Fail(FriendlyYtDlpError(detail),
                detail, stopwatch.Elapsed, _ytDlp.DisplayVersion);
        }

        var newFiles = DiffFiles(request.OutputDirectory, snapshot);
        outputPaths = outputPaths
            .Where(File.Exists)
            .Concat(newFiles)
            .Concat(targetPath is not null && File.Exists(targetPath) ? new[] { targetPath } : Array.Empty<string>())
            .Where(p => !p.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
                        !p.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (outputPaths.Count == 0)
        {
            return InstallerDownloadResult.Fail(
                "The download finished, but Nasaq couldn't find the saved file.",
                Tail(stderr.ToString(), 12), stopwatch.Elapsed, _ytDlp.DisplayVersion);
        }

        foreach (var path in outputPaths)
        {
            Log.Info("installer.done", $"Saved {Path.GetFileName(path)}",
                ("output", path),
                ("engine", _ytDlp.DisplayVersion),
                ("elapsed", $"{stopwatch.ElapsedMilliseconds} ms"),
                ("size", FileSize.Format(new FileInfo(path).Length)));
        }

        return InstallerDownloadResult.Ok(outputPaths, title, stopwatch.Elapsed, _ytDlp.DisplayVersion);
    }

    private List<string> BuildYtDlpArguments(Uri uri, InstallerDownloadRequest request)
    {
        var args = new List<string>
        {
            "--no-playlist",
            "--newline",
            "--no-colors",
            "--progress",
            "--progress-delta", "0.2",
            "--windows-filenames",
            "--trim-filenames", "180",
            "--retries", "5",
            "--fragment-retries", "5",
            "--concurrent-fragments", "4",
            "--socket-timeout", "25",
            "-o", Path.Combine(request.OutputDirectory, "%(title).160B [%(id)s].%(ext)s"),
            "--print", "before_dl:nasaq-title:%(title)s",
            "--print", "before_dl:nasaq-target:%(filepath)s",
            "--print", "after_move:nasaq-file:%(filepath)s",
            "--progress-template",
            "download:nasaq-progress:%(progress.downloaded_bytes)s|%(progress.total_bytes_estimate)s|" +
            "%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
        };

        var ffmpeg = request.FfmpegPath;
        var hasFfmpeg = !string.IsNullOrWhiteSpace(ffmpeg) && File.Exists(ffmpeg);
        if (hasFfmpeg)
        {
            var directory = Path.GetDirectoryName(ffmpeg);
            if (!string.IsNullOrWhiteSpace(directory))
                args.AddRange(new[] { "--ffmpeg-location", directory });
        }

        var jsRuntime = FindJsRuntime();
        if (jsRuntime is not null)
        {
            args.AddRange(new[] { "--js-runtimes", jsRuntime });
            Log.Debug("installer.ytdlp", "Using a JavaScript runtime for the engine", ("runtime", jsRuntime));
        }

        if (!string.IsNullOrWhiteSpace(request.CookiesBrowser) &&
            !request.CookiesBrowser.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(new[] { "--cookies-from-browser", request.CookiesBrowser.ToLowerInvariant() });
        }

        var height = request.Quality switch
        {
            "1080" => 1080,
            "720" => 720,
            "480" => 480,
            _ => 0,
        };

        switch (request.Mode)
        {
            case InstallerMediaMode.Audio:
                args.AddRange(new[] { "-f", "ba/b" });
                if (hasFfmpeg)
                {
                    args.AddRange(new[]
                    {
                        "-x", "--audio-format", "mp3",
                        "--audio-quality",
                        $"{Math.Clamp(request.AudioBitrate, 64, 320)}K",
                    });
                }
                break;

            case InstallerMediaMode.Photo:
                // yt-dlp is only a fallback for photo links; keep it single-file.
                args.AddRange(new[] { "-f", "b" });
                break;

            case InstallerMediaMode.Video:
                if (hasFfmpeg)
                {
                    args.AddRange(new[] { "-f", VideoFormat(height), "--merge-output-format", "mp4/mkv" });
                }
                else
                {
                    args.AddRange(new[] { "-f", height > 0 ? $"b[height<={height}]/b" : "b" });
                }
                break;

            default:
                if (hasFfmpeg)
                {
                    args.AddRange(new[] { "-f", height > 0 ? $"bv*[height<={height}]+ba/b[height<={height}]/b" : "bv*+ba/b",
                        "--merge-output-format", "mp4/mkv" });
                }
                else
                {
                    args.AddRange(new[] { "-f", height > 0 ? $"b[height<={height}]/b" : "b" });
                }
                break;
        }

        if (request.PreserveMetadata && hasFfmpeg)
            args.Add("--embed-metadata");

        args.Add(uri.AbsoluteUri);
        return args;
    }

    private static string VideoFormat(int height)
    {
        if (height <= 0) return "bv*+ba/b";
        return $"bv*[height<={height}]+ba/b[height<={height}]/b";
    }

    private static double ParseYtDlpProgress(string payload, string? title,
        IProgress<InstallerProgress>? progress, double lastFraction)
    {
        var parts = payload.Split('|');
        if (parts.Length < 5) return lastFraction;

        var downloaded = ParseLong(parts[0]);
        var total = ParseLong(parts[1]);
        var percent = ParsePercent(parts[2]);
        var speed = CleanMeta(parts[3]);
        var eta = CleanMeta(parts[4]);

        var fraction = total is > 0 && downloaded is not null
            ? Math.Clamp((double)downloaded.Value / total.Value, 0, 1)
            : percent is not null ? Math.Clamp(percent.Value / 100.0, 0, 1) : -1;

        if (fraction < 0) return lastFraction;
        if (fraction < lastFraction && fraction < 0.999) return lastFraction;
        if (fraction - lastFraction < 0.006 && fraction < 0.999) return lastFraction;

        progress?.Report(new InstallerProgress(
            fraction,
            "Downloading",
            Title: title,
            FileName: title,
            Speed: speed,
            Eta: eta,
            DownloadedBytes: downloaded,
            TotalBytes: total));
        return fraction;
    }

    private static long? ParseLong(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value is "NA" or "N/A" or "None" or "null") return null;
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static double? ParsePercent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Replace("%", "").Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static string? CleanMeta(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return text is "NA" or "N/A" or "None" or "null" ? null : text;
    }

    // ─────────────────────────── engine setup ───────────────────────────

    public async Task<InstallerEngineResult> InstallEngineAsync(
        IProgress<InstallerProgress>? progress, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string? temp = null;

        try
        {
            Directory.CreateDirectory(YtDlpLocator.ManagedDirectory);
            var target = YtDlpLocator.ManagedPath;
            temp = target + ".download";

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            var token = timeout.Token;

            progress?.Report(new InstallerProgress(-1, "Contacting GitHub"));
            using var response = await Http.GetAsync(EngineDownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            var downloaded = 0L;
            var lastReport = -1.0;

            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write,
                             FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    downloaded += read;
                    var fraction = total is > 0 ? (double)downloaded / total.Value : -1;
                    if (fraction < 0 || fraction - lastReport >= 0.01 || fraction >= 1)
                    {
                        lastReport = fraction;
                        var speed = downloaded / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                        progress?.Report(new InstallerProgress(
                            fraction,
                            "Downloading installer engine",
                            FileName: "yt-dlp.exe",
                            Speed: FormatSpeed(speed),
                            DownloadedBytes: downloaded,
                            TotalBytes: total));
                    }
                }
            }

            progress?.Report(new InstallerProgress(-1, "Verifying the installer engine",
                FileName: "yt-dlp.exe"));
            await VerifyChecksumAsync(temp, token);

            File.Move(temp, target, overwrite: true);
            temp = null;

            _ytDlp = YtDlpLocator.Locate(target);
            if (!_ytDlp.IsAvailable)
            {
                return new InstallerEngineResult(false, false,
                    "The installer engine downloaded, but it couldn't be started.",
                    $"Probing {target} failed. The file may be blocked by security software.",
                    _ytDlp);
            }

            stopwatch.Stop();
            Log.Info("installer.engine", "Installer engine installed",
                ("path", target),
                ("version", _ytDlp.Version),
                ("elapsed", $"{stopwatch.ElapsedMilliseconds} ms"),
                ("size", FileSize.Format(new FileInfo(target).Length)));
            return new InstallerEngineResult(true, false, null, null, _ytDlp);
        }
        catch (OperationCanceledException)
        {
            Cleanup(temp);
            Log.Info("installer.engine", "Installer engine setup cancelled");
            return new InstallerEngineResult(false, true, "Engine setup was cancelled.", null, _ytDlp);
        }
        catch (Exception ex)
        {
            Cleanup(temp);
            Log.Error("installer.engine", "Installer engine setup failed", ex);
            return new InstallerEngineResult(false, false,
                "The installer engine couldn't be downloaded.",
                ex.Message, _ytDlp);
        }
    }

    private static async Task VerifyChecksumAsync(string file, CancellationToken ct)
    {
        string? sums;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            sums = await Http.GetStringAsync(EngineChecksumUrl, timeout.Token);
        }
        catch (Exception ex)
        {
            Log.Warn("installer.engine", "The engine checksum couldn't be fetched; continuing over HTTPS.",
                ("reason", ex.Message));
            return;
        }

        var line = sums.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.EndsWith("yt-dlp.exe", StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            Log.Warn("installer.engine", "The checksum list didn't include yt-dlp.exe; continuing over HTTPS.");
            return;
        }

        var expected = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(expected)) return;

        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, true);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (!actual.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerException(
                "The installer engine failed its security check.",
                $"Expected {expected}, got {actual}.");
        }

        Log.Info("installer.engine", "Installer engine checksum verified", ("sha256", actual));
    }

    // ─────────────────────────── helpers ───────────────────────────

    public static Uri? ParseUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var match = UrlRegex.Match(input);
        if (!match.Success) return null;

        var raw = match.Value.Trim()
            .TrimEnd('.', ',', ';', ')', ']', '}', '!', '?', '\'', '"');
        if (raw.Length < 8) return null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrWhiteSpace(uri.Host)) return null;
        return uri;
    }

    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "download";
        name = name.Trim().Trim('"');
        name = Path.GetFileName(name);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        name = name.Trim().TrimEnd('.', ' ');
        if (name.Length == 0) return "download";

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        if (ReservedNames.Contains(stem)) name = "_" + name;

        const int max = 150;
        if (name.Length > max)
        {
            var keep = Math.Max(1, max - extension.Length);
            var trimmedStem = stem.Length > keep ? stem[..keep].TrimEnd() : stem;
            name = trimmedStem + extension;
        }
        return name;
    }

    public static InstallerMediaKind KindFromExtension(string? pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) return InstallerMediaKind.Unknown;
        var path = pathOrUrl;
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri)) path = uri.AbsolutePath;
        var extension = path.StartsWith('.') && !path.Contains('/') && !path.Contains('\\')
            ? path.Trim()
            : Path.GetExtension(path);
        if (ImageExtensions.Contains(extension)) return InstallerMediaKind.Image;
        if (AudioExtensions.Contains(extension)) return InstallerMediaKind.Audio;
        if (VideoExtensions.Contains(extension)) return InstallerMediaKind.Video;
        if (StreamExtensions.Contains(extension)) return InstallerMediaKind.Stream;
        return InstallerMediaKind.Unknown;
    }

    private static bool IsKnownMediaExtension(string extension) =>
        ImageExtensions.Contains(extension) || AudioExtensions.Contains(extension) ||
        VideoExtensions.Contains(extension) || StreamExtensions.Contains(extension);

    public static InstallerMediaKind KindFromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return InstallerMediaKind.Unknown;
        var type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        if (type.StartsWith("image/")) return InstallerMediaKind.Image;
        if (type.StartsWith("audio/")) return InstallerMediaKind.Audio;
        if (type.StartsWith("video/")) return InstallerMediaKind.Video;
        if (type is "application/mp4" or "application/x-m4v" or "application/ogg")
            return type == "application/ogg" ? InstallerMediaKind.Audio : InstallerMediaKind.Video;
        if (type is "application/vnd.apple.mpegurl" or "application/x-mpegurl"
            or "application/dash+xml" or "application/vnd.ms-sstr+xml")
            return InstallerMediaKind.Stream;
        if (type is "text/html" or "application/xhtml+xml") return InstallerMediaKind.Page;
        return InstallerMediaKind.Unknown;
    }

    private static string BuildFileName(HttpResponseMessage response, Uri uri,
        InstallerMediaKind kind, string? mediaType)
    {
        var name = response.Content.Headers.ContentDisposition?.FileNameStar
                   ?? response.Content.Headers.ContentDisposition?.FileName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            name = name.Trim().Trim('"');
            if (name.StartsWith("UTF-8''", StringComparison.OrdinalIgnoreCase)) name = name[7..];
            try { name = Uri.UnescapeDataString(name); } catch { }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            try { name = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)); }
            catch { name = null; }
        }

        if (string.IsNullOrWhiteSpace(name) || name is "/" or "\\")
            name = $"nasaq-{DateTime.Now:yyyyMMdd-HHmmss}";

        name = SanitizeFileName(name);
        var extension = Path.GetExtension(name);
        if (extension.Length == 0 || !IsKnownMediaExtension(extension))
        {
            var mapped = ExtensionFor(kind, mediaType);
            if (extension.Length > 0 && extension.Length <= 8)
                name = Path.GetFileNameWithoutExtension(name) + mapped;
            else
                name += mapped;
        }
        return SanitizeFileName(name);
    }

    private static string ExtensionFor(InstallerMediaKind kind, string? mediaType)
    {
        var type = mediaType?.Split(';')[0].Trim().ToLowerInvariant();
        return type switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/avif" => ".avif",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tiff",
            "image/svg+xml" => ".svg",
            "image/heic" => ".heic",
            "audio/mpeg" => ".mp3",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/flac" or "audio/x-flac" => ".flac",
            "audio/ogg" => ".ogg",
            "audio/opus" => ".opus",
            "audio/aac" => ".aac",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/quicktime" => ".mov",
            "video/x-matroska" => ".mkv",
            "application/vnd.apple.mpegurl" or "application/x-mpegurl" => ".m3u8",
            _ => kind switch
            {
                InstallerMediaKind.Image => ".jpg",
                InstallerMediaKind.Audio => ".mp3",
                InstallerMediaKind.Video => ".mp4",
                _ => ".bin",
            },
        };
    }

    private string ReserveOutputPath(string directory, string fileName, bool overwrite)
    {
        var name = SanitizeFileName(fileName);
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);

        lock (_reserveLock)
        {
            for (var index = 0; index < 10_000; index++)
            {
                var candidate = Path.GetFullPath(Path.Combine(directory,
                    index == 0 ? name : $"{stem} ({index}){extension}"));
                if (_reserved.Contains(candidate)) continue;
                if (!overwrite && File.Exists(candidate)) continue;
                _reserved.Add(candidate);
                return candidate;
            }
        }

        throw new InstallerException("Every possible filename is already taken.",
            "Exhausted 10,000 numbered names in the download folder.");
    }

    private static Dictionary<string, (long Length, DateTime Stamp)> SnapshotFiles(string directory)
    {
        var result = new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory)) return result;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var info = new FileInfo(file);
                    result[file] = (info.Length, info.LastWriteTimeUtc);
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    private static List<string> DiffFiles(string directory,
        Dictionary<string, (long Length, DateTime Stamp)> snapshot)
    {
        var result = new List<string>();
        if (!Directory.Exists(directory)) return result;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length == 0) continue;
                    if (snapshot.TryGetValue(file, out var old) &&
                        old.Length == info.Length &&
                        old.Stamp == info.LastWriteTimeUtc) continue;
                    result.Add(file);
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    private static void CleanupPartFiles(string directory,
        Dictionary<string, (long Length, DateTime Stamp)>? before = null)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.part", SearchOption.TopDirectoryOnly))
                if (before is null || !before.ContainsKey(file)) Cleanup(file);
            foreach (var file in Directory.EnumerateFiles(directory, "*.ytdl", SearchOption.TopDirectoryOnly))
                if (before is null || !before.ContainsKey(file)) Cleanup(file);
        }
        catch { }
    }

    private static string FriendlyYtDlpError(string detail)
    {
        var d = detail.ToLowerInvariant();
        if (d.Contains("unsupported url")) return "This site isn't supported by the installer engine yet. Try a direct media link.";
        if (d.Contains("is not a valid url")) return "That link doesn't look valid.";
        if (d.Contains("private video") || d.Contains("private content") || d.Contains("login required") ||
            d.Contains("sign in") || d.Contains("logged-in") || d.Contains("account credentials"))
            return "This item needs a signed-in session. Choose a browser under Sign-in cookies, or try a public link.";
        if (d.Contains("requested format is not available"))
            return "No matching quality was available. Try Best quality.";
        if (d.Contains("video unavailable") || d.Contains("not available") || d.Contains("has been removed"))
            return "That item isn't available. It may be private, deleted or region-locked.";
        if (d.Contains("http error 403") || d.Contains("forbidden"))
            return "The site refused the request (403). Try browser cookies, or update the installer engine.";
        if (d.Contains("http error 404") || d.Contains("not found"))
            return "The link could not be found (404). Check that it still opens in a browser.";
        if (d.Contains("drm"))
            return "This item is DRM-protected and can't be downloaded.";
        if (d.Contains("could not copy") && d.Contains("cookie"))
            return "The browser cookie database is locked. Close the browser completely and try again.";
        if (d.Contains("ffmpeg") && (d.Contains("not found") || d.Contains("not installed")))
            return "FFmpeg is required for this download. Set its path in Settings → Advanced.";
        if (d.Contains("no supported javascript runtime"))
            return "The site needs a JavaScript runtime for this item. Install Node.js or Deno, or try a different link.";
        if (d.Contains("timed out") || d.Contains("timeout"))
            return "The site took too long to respond. Check your connection and try again.";
        if (d.Contains("certificate"))
            return "The site's security certificate couldn't be verified.";
        if (d.Contains("urlopen error") || d.Contains("connection") || d.Contains("network"))
            return "Nasaq couldn't reach that site. Check your connection and try again.";
        return "The installer engine couldn't download this link.";
    }

    private static string? FindJsRuntime()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var name in new[] { "node", "deno", "bun" })
        {
            foreach (var dir in path.Split(';'))
            {
                try
                {
                    if (File.Exists(Path.Combine(dir.Trim(), name + ".exe"))) return name;
                }
                catch { }
            }
        }
        return null;
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "—";
        return FileSize.Format((long)bytesPerSecond) + "/s";
    }

    private static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalHours >= 1) return $"{(int)eta.TotalHours}h {eta.Minutes:00}m";
        if (eta.TotalMinutes >= 1) return $"{(int)eta.TotalMinutes}m {eta.Seconds:00}s";
        return $"{Math.Max(0, eta.Seconds)}s";
    }

    private static string Tail(string text, int lines)
    {
        var split = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .TakeLast(lines);
        return string.Join("\n", split);
    }

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;

    private static string SafeUrl(Uri uri)
    {
        try { return uri.GetLeftPart(UriPartial.Path); }
        catch { return uri.Host; }
    }

    private static void Cleanup(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(25),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Nasaq/1.0");
        return client;
    }

    private sealed record ProbeResult(
        InstallerMediaKind Kind,
        string? MediaType,
        long? ContentLength,
        System.Net.Http.Headers.ContentDispositionHeaderValue? ContentDisposition,
        bool FromExtension);

    private sealed record DirectResult(
        string Path,
        string? Title,
        InstallerMediaKind Kind,
        TimeSpan Elapsed);

    private sealed record Candidate(int Priority, int Order, Uri Uri, InstallerMediaKind Kind);
}
