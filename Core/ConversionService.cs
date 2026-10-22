using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Nasaq.Services;

namespace Nasaq.Core;

public sealed class ConversionService
{
    private FfmpegInfo _ffmpeg;
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _reserveLock = new();

    public ConversionService(FfmpegInfo ffmpeg) => _ffmpeg = ffmpeg;

    public FfmpegInfo Ffmpeg => _ffmpeg;

    public FfmpegInfo RefreshFfmpeg(string? configuredPath)
    {
        _ffmpeg = FfmpegLocator.Locate(configuredPath);
        return _ffmpeg;
    }

    public async Task<ConversionResult> ConvertAsync(
        string source,
        OutputFormat target,
        ConversionOptions options,
        IProgress<ConversionProgress>? progress,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var engine = "Nasaq";
        string? output = null;
        string? temp = null;

        try
        {
            if (!File.Exists(source))
                throw new ConversionException("The source file no longer exists.",
                    $"Missing file: {source}");

            var sourceCategory = FormatCatalog.Detect(source);
            if (sourceCategory == MediaCategory.Unknown)
                throw new ConversionException("This file type isn't supported.", $"Unknown extension: {Path.GetExtension(source)}");

            var categoryAllowed = target.Category == sourceCategory ||
                                  (sourceCategory == MediaCategory.Video && target.Category == MediaCategory.Audio);
            if (!categoryAllowed)
                throw new ConversionException(
                    $"A {FormatCatalog.CategoryName(sourceCategory).ToLowerInvariant()} can't be converted to {target.Label}.",
                    $"Category mismatch: {sourceCategory} -> {target.Category}");

            Directory.CreateDirectory(options.OutputDirectory);
            output = ReserveOutput(source, target, options);
            var outputExtension = target.Extension;
            temp = Path.Combine(options.OutputDirectory, $".nasaq-{Guid.NewGuid():N}.{outputExtension}");

            Log.Info("convert.start", $"Converting {Path.GetFileName(source)} to {target.Label}",
                ("source", source),
                ("target", $"{target.Category}/{target.Label}"),
                ("output", output),
                ("size", FileSize.Format(new FileInfo(source).Length)),
                ("options", $"quality={options.ImageQuality} bitrate={options.AudioBitrate}k crf={options.VideoQuality} metadata={options.PreserveMetadata}"));

            ct.ThrowIfCancellationRequested();

            switch (target.Category)
            {
                case MediaCategory.Document:
                    engine = "Nasaq documents";
                    progress?.Report(new ConversionProgress(0.05, "Preparing"));
                    temp = await DocumentConverter.ConvertAsync(source, target, options, Scale(progress, 0.05, 0.95), ct);
                    break;

                case MediaCategory.Image:
                    engine = await ConvertImageAsync(source, target, options, temp, Scale(progress, 0.05, 0.95), ct);
                    break;

                case MediaCategory.Audio:
                case MediaCategory.Video:
                    engine = "FFmpeg";
                    await ConvertWithFfmpegAsync(source, target, options, temp,
                        Scale(progress, 0.04, 0.97), ct);
                    break;

                default:
                    throw new ConversionException("This conversion isn't supported yet.");
            }

            ct.ThrowIfCancellationRequested();

            if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                throw new ConversionException("The converter produced an empty file.",
                    $"Temporary output missing or empty: {temp}");

            progress?.Report(new ConversionProgress(0.985, "Saving"));
            if (File.Exists(output) && !options.Overwrite)
            {
                var replacement = ReserveOutput(source, target, options);
                _reserved.Remove(output);
                output = replacement;
            }
            File.Move(temp, output, overwrite: options.Overwrite);
            temp = null;
            _reserved.Remove(output);

            progress?.Report(new ConversionProgress(1, "Done"));
            stopwatch.Stop();
            Log.Info("convert.done", $"Saved {Path.GetFileName(output)}",
                ("output", output),
                ("elapsed", $"{stopwatch.ElapsedMilliseconds} ms"),
                ("size", FileSize.Format(new FileInfo(output).Length)),
                ("engine", engine));

            return ConversionResult.Ok(output, stopwatch.Elapsed, engine);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            Cleanup(temp);
            if (output is not null) _reserved.Remove(output);
            Log.Info("convert.cancel", $"Cancelled {Path.GetFileName(source)}",
                ("source", source), ("target", target.Label));
            return ConversionResult.Cancelled(stopwatch.Elapsed, engine);
        }
        catch (ConversionException ex)
        {
            stopwatch.Stop();
            Cleanup(temp);
            if (output is not null) _reserved.Remove(output);
            Log.Error("convert.fail", ex.FriendlyMessage,
                ex, ("source", source), ("target", target.Label),
                ("detail", (string?)ex.Message), ("hint", ex.Hint));
            return ConversionResult.Fail(ex.FriendlyMessage, ex.Message, stopwatch.Elapsed, engine);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Cleanup(temp);
            if (output is not null) _reserved.Remove(output);
            Log.Error("convert.fail", "Unexpected conversion failure", ex,
                ("source", source), ("target", target.Label));
            return ConversionResult.Fail(
                "Something went wrong while converting this file. The full details were written to the log.",
                ex.Message, stopwatch.Elapsed, engine);
        }
    }

    private async Task<string> ConvertImageAsync(string source, OutputFormat target,
        ConversionOptions options, string temp, IProgress<ConversionProgress>? progress, CancellationToken ct)
    {
        var wicSource = WpfImageConverter.CanDecode(source);

        // ICO is packed by Nasaq so we can embed every resolution Windows asks for.
        if (target.Id == "ico")
        {
            if (wicSource)
            {
                var result = await WpfImageConverter.ConvertAsync(source, target, options, progress, ct);
                File.Move(result, temp, overwrite: true);
                return "Windows imaging";
            }

            var png = await DecodeToPngAsync(source, options.OutputDirectory, progress, ct);
            try
            {
                var result = await WpfImageConverter.ConvertAsync(png, target, options, progress, ct);
                File.Move(result, temp, overwrite: true);
                return "FFmpeg + Windows imaging";
            }
            finally { Cleanup(png); }
        }

        if (target.Id == "webp")
        {
            EnsureFfmpeg("WebP");
            await RunFfmpegAsync(BuildImageArgs(source, target, options, temp), source,
                0, progress, ct);
            return "FFmpeg";
        }

        if (wicSource)
        {
            var result = await WpfImageConverter.ConvertAsync(source, target, options, progress, ct);
            File.Move(result, temp, overwrite: true);
            return "Windows imaging";
        }

        EnsureFfmpeg(target.Label);
        await RunFfmpegAsync(BuildImageArgs(source, target, options, temp), source, 0, progress, ct);
        return "FFmpeg";
    }

    private async Task<string> DecodeToPngAsync(string source, string outputDirectory,
        IProgress<ConversionProgress>? progress, CancellationToken ct)
    {
        EnsureFfmpeg("this image");
        var temp = Path.Combine(outputDirectory, $".nasaq-src-{Guid.NewGuid():N}.png");
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "warning",
            "-progress", "pipe:1",
            "-i", source,
            "-frames:v", "1",
            "-c:v", "png",
            temp,
        };
        await RunFfmpegAsync(args, source, 0, progress, ct);
        return temp;
    }

    private async Task ConvertWithFfmpegAsync(string source, OutputFormat target,
        ConversionOptions options, string temp, IProgress<ConversionProgress>? progress, CancellationToken ct)
    {
        EnsureFfmpeg(target.Label);
        var args = BuildMediaArgs(source, target, options, temp);
        await RunFfmpegAsync(args, source, 1, progress, ct);
    }

    private void EnsureFfmpeg(string purpose)
    {
        if (_ffmpeg.IsAvailable) return;
        throw new ConversionException(
            $"FFmpeg is required for {purpose} conversion, but it wasn't found.",
            "No ffmpeg.exe in the configured path, app folder, PATH, or WinGet packages.",
            hint: "Open Settings → Advanced to point Nasaq at ffmpeg.exe.");
    }

    private List<string> BuildImageArgs(string source, OutputFormat target, ConversionOptions options, string output)
    {
        var args = BaseArgs(source);
        var animated = Path.GetExtension(source).Equals(".gif", StringComparison.OrdinalIgnoreCase);
        if (!animated) { args.Add("-frames:v"); args.Add("1"); }

        switch (target.Id)
        {
            case "webp":
                args.AddRange(new[]
                {
                    "-c:v", "libwebp",
                    "-quality", Math.Clamp(options.ImageQuality, 1, 100).ToString(CultureInfo.InvariantCulture),
                    "-compression_level", "5",
                    "-preset", "picture",
                });
                if (animated) args.AddRange(new[] { "-loop", "0" });
                break;
            case "jpg":
                args.AddRange(new[] { "-q:v", JpegQuality(options.ImageQuality).ToString(CultureInfo.InvariantCulture) });
                break;
            case "png":
                args.AddRange(new[] { "-compression_level", "6" });
                break;
        }

        if (options.PreserveMetadata && target.Id is "webp" or "jpg" or "png")
            AddMetadata(args);
        args.Add(output);
        return args;
    }

    private List<string> BuildMediaArgs(string source, OutputFormat target, ConversionOptions options, string output)
    {
        var args = BaseArgs(source);
        var isAudio = target.Category == MediaCategory.Audio;
        var bitrate = Math.Clamp(options.AudioBitrate, 64, 320).ToString(CultureInfo.InvariantCulture);
        var crf = Math.Clamp(options.VideoQuality, 14, 35).ToString(CultureInfo.InvariantCulture);

        if (isAudio)
        {
            args.AddRange(new[] { "-vn", "-map", "0:a:0?" });
            switch (target.Id)
            {
                case "mp3": args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", $"{bitrate}k" }); break;
                case "wav": args.AddRange(new[] { "-c:a", "pcm_s16le" }); break;
                case "flac": args.AddRange(new[] { "-c:a", "flac", "-compression_level", "5" }); break;
                case "m4a": args.AddRange(new[] { "-c:a", "aac", "-b:a", $"{bitrate}k", "-movflags", "+faststart" }); break;
                case "ogg": args.AddRange(new[] { "-c:a", "libvorbis", "-q:a", "5" }); break;
                case "opus": args.AddRange(new[] { "-c:a", "libopus", "-b:a", $"{bitrate}k" }); break;
                default: throw new ConversionException($"Audio conversion to {target.Label} isn't supported.");
            }
        }
        else
        {
            // A GIF conversion builds its own filtergraph, so FFmpeg maps the
            // filtergraph output. Adding input maps here would consume the video
            // stream before the unlabeled filtergraph output gets mapped.
            if (target.Id != "gifv")
                args.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?" });
            switch (target.Id)
            {
                case "mp4":
                    AddVideo(args, "libx264", crf);
                    args.AddRange(new[] { "-c:a", "aac", "-b:a", $"{bitrate}k", "-movflags", "+faststart" });
                    break;
                case "mkv":
                    AddVideo(args, "libx264", crf);
                    args.AddRange(new[] { "-c:a", "aac", "-b:a", $"{bitrate}k" });
                    break;
                case "mov":
                    AddVideo(args, "libx264", crf);
                    args.AddRange(new[] { "-c:a", "aac", "-b:a", $"{bitrate}k", "-movflags", "+faststart" });
                    break;
                case "webm":
                    args.AddRange(new[]
                    {
                        "-c:v", "libvpx-vp9", "-crf", Math.Clamp(options.VideoQuality + 8, 20, 45)
                            .ToString(CultureInfo.InvariantCulture),
                        "-b:v", "0", "-row-mt", "1", "-cpu-used", "2",
                        "-c:a", "libopus", "-b:a", "128k",
                    });
                    break;
                case "avi":
                    args.AddRange(new[] { "-c:v", "mpeg4", "-q:v", "5", "-c:a", "libmp3lame", "-b:a", $"{bitrate}k" });
                    break;
                case "gifv":
                    args.AddRange(new[]
                    {
                        "-filter_complex",
                        "fps=12,scale=720:-1:force_original_aspect_ratio=decrease,split[s0][s1];[s0]palettegen=max_colors=160[p];[s1][p]paletteuse=dither=sierra2_4a",
                        "-an", "-loop", "0",
                    });
                    break;
                default:
                    throw new ConversionException($"Video conversion to {target.Label} isn't supported.");
            }
        }

        if (options.PreserveMetadata) AddMetadata(args);
        args.Add(output);
        return args;
    }

    private static void AddVideo(List<string> args, string codec, string crf)
    {
        args.AddRange(new[]
        {
            "-c:v", codec, "-preset", "medium", "-crf", crf, "-pix_fmt", "yuv420p",
            "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2",
        });
    }

    private static void AddMetadata(List<string> args) => args.AddRange(new[] { "-map_metadata", "0" });

    private static List<string> BaseArgs(string source) => new()
    {
        "-hide_banner", "-nostdin", "-y", "-loglevel", "warning",
        "-progress", "pipe:1",
        "-i", source,
    };

    private async Task RunFfmpegAsync(List<string> args, string? mediaSource, int kind,
        IProgress<ConversionProgress>? progress, CancellationToken ct)
    {
        var ffmpeg = _ffmpeg.FfmpegPath
                     ?? throw new ConversionException("FFmpeg isn't available.");

        double? duration = null;
        if (mediaSource is not null && kind != 0)
            duration = await ProbeDurationAsync(mediaSource, ct);

        var stderr = new StringBuilder();
        var lastFraction = 0.0;
        var progressLock = new object();

        void HandleStdout(string line)
        {
            if (progress is null) return;
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
            {
                if (!long.TryParse(line[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var us)) return;
                double fraction;
                if (duration is > 0)
                    fraction = Math.Clamp(us / (duration.Value * 1_000_000d), 0, 1);
                else
                    fraction = 0.5; // unknown duration: pulse in the middle
                lock (progressLock)
                {
                    if (fraction <= lastFraction) return;
                    lastFraction = fraction;
                }
                progress.Report(new ConversionProgress(0.04 + 0.93 * fraction, "Converting"));
            }
            else if (line.StartsWith("progress=end", StringComparison.Ordinal))
            {
                progress.Report(new ConversionProgress(0.97, "Finalising"));
            }
        }

        void HandleStderr(string line)
        {
            stderr.AppendLine(line);
            if (stderr.Length > 40_000) stderr.Remove(0, stderr.Length - 30_000);
            Log.Debug("ffmpeg.output", "ffmpeg: " + line);
        }

        Log.Debug("ffmpeg.run", "Starting FFmpeg",
            ("exe", ffmpeg), ("arguments", string.Join(" ", args.Select(Quote))));

        var result = await ProcessRunner.RunAsync(ffmpeg, args, HandleStdout, HandleStderr, ct);
        if (result.Canceled) throw new OperationCanceledException(ct);

        if (result.ExitCode != 0)
        {
            var detail = Tail(result.StdErr, 14);
            throw new ConversionException(FriendlyFfmpegError(detail), detail,
                hint: "The complete FFmpeg output is in the debug log.");
        }

        if (!string.IsNullOrWhiteSpace(result.StdErr))
            Log.Debug("ffmpeg.stderr", "FFmpeg warnings", ("detail", Tail(result.StdErr, 6)));
    }

    private async Task<double?> ProbeDurationAsync(string path, CancellationToken ct)
    {
        var probe = _ffmpeg.FfprobePath;
        if (string.IsNullOrWhiteSpace(probe) || !File.Exists(probe)) return null;
        try
        {
            var result = await ProcessRunner.RunAsync(probe, new[]
            {
                "-v", "error", "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1", path,
            }, null, null, ct);
            if (result.ExitCode != 0) return null;
            var text = result.StdOut.Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? seconds
                : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static string FriendlyFfmpegError(string detail)
    {
        var d = detail.ToLowerInvariant();
        if (d.Contains("no such file or directory")) return "The source file could not be read by FFmpeg.";
        if (d.Contains("permission denied")) return "Windows denied access to the file or the output folder.";
        if (d.Contains("does not contain any stream")) return "No compatible audio or video stream was found in this file.";
        if (d.Contains("invalid data found")) return "The file is corrupt or uses a codec that isn't supported.";
        if (d.Contains("unknown encoder")) return "Your FFmpeg build is missing the codec required for this format.";
        if (d.Contains("error while opening encoder")) return "The selected format couldn't encode this media. Try different quality settings.";
        if (d.Contains("could not write header")) return "The output container couldn't be written for this file.";
        return "FFmpeg reported an error while converting this file.";
    }

    private static int JpegQuality(int quality) =>
        Math.Clamp(31 - (int)Math.Round(Math.Clamp(quality, 1, 100) * 0.29), 2, 30);

    private string ReserveOutput(string source, OutputFormat target, ConversionOptions options)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var baseName = Path.GetFileNameWithoutExtension(source);
        var extension = target.Extension;
        var sourceFull = Path.GetFullPath(source);

        lock (_reserveLock)
        {
            for (var index = 0; index < 10_000; index++)
            {
                var suffix = index == 0 ? "" : $" ({index})";
                var candidate = Path.GetFullPath(Path.Combine(options.OutputDirectory, $"{baseName}{suffix}.{extension}"));
                if (candidate.Equals(sourceFull, StringComparison.OrdinalIgnoreCase)) continue;
                if (_reserved.Contains(candidate)) continue;
                if (!options.Overwrite && File.Exists(candidate)) continue;
                _reserved.Add(candidate);
                return candidate;
            }
        }
        throw new ConversionException("Every possible output filename is already taken.",
            "Exhausted 10,000 numbered output names.");
    }

    private static IProgress<ConversionProgress>? Scale(IProgress<ConversionProgress>? progress, double start, double end)
        => progress is null ? null : new Progress<ConversionProgress>(p =>
            progress.Report(new ConversionProgress(start + (end - start) * Math.Clamp(p.Fraction, 0, 1), p.Stage)));

    private static string Tail(string text, int lines)
    {
        var split = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .TakeLast(lines);
        return string.Join("\n", split);
    }

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;

    private static void Cleanup(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
