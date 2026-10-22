using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Nasaq.Services;

namespace Nasaq.Core;

/// <summary>
/// FFmpeg-backed video trimming/captioning and audio trimming/merging.
/// </summary>
public sealed class MediaEditor
{
    private readonly ConversionService _engine;

    public MediaEditor(ConversionService engine) => _engine = engine;

    public bool Available => _engine.Ffmpeg.IsAvailable;

    // ───────────────────────── probing ─────────────────────────

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("The media file no longer exists.");
        var size = EditorFileNaming.SafeSize(path);
        var ffmpeg = _engine.Ffmpeg;
        if (!ffmpeg.IsAvailable)
            throw new InvalidOperationException(
                "Audio and video tools need FFmpeg. Install it or set its path in Settings → Advanced.");

        if (!string.IsNullOrWhiteSpace(ffmpeg.FfprobePath) && File.Exists(ffmpeg.FfprobePath))
        {
            var probe = await ProcessRunner.RunAsync(ffmpeg.FfprobePath,
                new[]
                {
                    "-v", "error", "-show_entries", "format=duration,size",
                    "-show_entries", "stream=codec_type,codec_name,width,height",
                    "-of", "json", path,
                }, null, null, ct);

            if (probe.ExitCode == 0 && !string.IsNullOrWhiteSpace(probe.StdOut))
            {
                try { return ParseProbeJson(path, probe.StdOut, size); }
                catch (Exception ex)
                {
                    Log.Debug("editor.media", "ffprobe JSON was not usable; trying ffmpeg",
                        ("path", path), ("reason", ex.Message));
                }
            }
        }

        return await ProbeWithFfmpegAsync(path, size, ct);
    }

    private static MediaInfo ParseProbeJson(string path, string json, long fallbackSize)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var duration = 0.0;
        var size = fallbackSize;
        if (root.TryGetProperty("format", out var format))
        {
            duration = GetDouble(format, "duration");
            var reported = GetLong(format, "size");
            if (reported > 0) size = reported;
        }

        var hasVideo = false;
        var hasAudio = false;
        var width = 0;
        var height = 0;
        var codec = "";
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = GetString(stream, "codec_type");
                if (type == "video" && !hasVideo)
                {
                    hasVideo = true;
                    width = (int)GetLong(stream, "width");
                    height = (int)GetLong(stream, "height");
                    codec = GetString(stream, "codec_name");
                }
                else if (type == "audio")
                {
                    hasAudio = true;
                    if (codec.Length == 0) codec = GetString(stream, "codec_name");
                }
            }
        }

        if (duration <= 0) throw new InvalidDataException("No duration was reported.");
        return new MediaInfo(path, duration, Math.Max(0, width), Math.Max(0, height),
            hasVideo, hasAudio, size, codec);
    }

    private async Task<MediaInfo> ProbeWithFfmpegAsync(string path, long size, CancellationToken ct)
    {
        var probe = await ProcessRunner.RunAsync(_engine.Ffmpeg.FfmpegPath!,
            new[] { "-hide_banner", "-nostdin", "-i", path },
            null, null, ct);
        var text = probe.StdErr;
        var duration = ParseDuration(text);
        var video = Regex.Match(text, @"Stream #.*Video:.*?(\d{2,5})x(\d{2,5})", RegexOptions.IgnoreCase);
        var hasVideo = video.Success;
        var hasAudio = Regex.IsMatch(text, @"Stream #.*Audio:", RegexOptions.IgnoreCase);
        var width = hasVideo ? int.Parse(video.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var height = hasVideo ? int.Parse(video.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        return new MediaInfo(path, duration, width, height, hasVideo, hasAudio, size,
            hasVideo ? "video" : hasAudio ? "audio" : "");
    }

    private static double ParseDuration(string stderr)
    {
        var match = Regex.Match(stderr, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (!match.Success) return 0;
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
               + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60
               + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static double GetDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return number;
        return 0;
    }

    private static long GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number;
        return 0;
    }

    // ───────────────────────── video ─────────────────────────

    public async Task<BitmapSource> ExtractFrameAsync(string source, double seconds, int maxWidth,
        CancellationToken ct)
    {
        if (!Available) throw new InvalidOperationException("FFmpeg is needed for video previews.");
        var directory = Path.Combine(Path.GetTempPath(), "Nasaq");
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, $"frame-{Guid.NewGuid():N}.png");
        try
        {
            var time = Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture);
            var result = await ProcessRunner.RunAsync(_engine.Ffmpeg.FfmpegPath!,
                new[]
                {
                    "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                    "-ss", time, "-i", source, "-frames:v", "1",
                    "-vf", $"scale=w='min({Math.Clamp(maxWidth, 160, 1920)},iw)':h=-2",
                    output,
                }, null, null, ct);

            if (result.ExitCode != 0 || !File.Exists(output))
                throw new InvalidOperationException("The video frame could not be read.", new Exception(LastError(result.StdErr)));

            return await WpfImageConverter.RunStaAsync(() =>
            {
                var bitmap = new BitmapImage();
                using (var stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                bitmap.Freeze();
                return (BitmapSource)bitmap;
            });
        }
        finally
        {
            TryDelete(output);
        }
    }

    public async Task<EditorResult> ExportVideoAsync(VideoEditRequest request, IProgress<EditorProgress>? progress,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string? temporary = null;
        string? textFile = null;
        try
        {
            if (!Available) throw new InvalidOperationException("FFmpeg is needed to export video.");
            var start = Math.Max(0, request.StartSeconds);
            var end = Math.Max(start + 0.05, request.EndSeconds);
            var duration = end - start;
            if (duration < 0.05) throw new InvalidOperationException("Choose a longer clip.");

            var width = request.SourceWidth;
            var height = request.SourceHeight;
            if (width <= 0 || height <= 0)
            {
                var info = await ProbeAsync(request.SourcePath, ct);
                width = info.Width;
                height = info.Height;
            }

            if (request.Caption.HasText)
            {
                var directory = Path.Combine(Path.GetTempPath(), "Nasaq");
                Directory.CreateDirectory(directory);
                textFile = Path.Combine(directory, $"caption-{Guid.NewGuid():N}.txt");
                await File.WriteAllTextAsync(textFile, request.Caption.Text.Trim(), new UTF8Encoding(false), ct);
            }

            var outputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory)
                ? AppSettings.DefaultOutputDirectory()
                : request.OutputDirectory;
            var output = EditorFileNaming.UniquePath(outputDirectory,
                EditorFileNaming.SafeStem(request.SourcePath) + "-edited", "mp4", request.Overwrite);
            temporary = Path.Combine(Path.GetDirectoryName(output)!, $".nasaq-{Guid.NewGuid():N}.mp4");

            var args = new List<string>
            {
                "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", request.SourcePath,
                "-t", duration.ToString("0.###", CultureInfo.InvariantCulture),
                "-map", "0:v:0", "-map", "0:a?",
            };

            var filter = BuildVideoFilter(request.Caption, textFile, width, height);
            if (filter is not null) args.AddRange(new[] { "-vf", filter });

            args.AddRange(new[]
            {
                "-c:v", "libx264", "-preset", "veryfast", "-crf",
                Math.Clamp(request.Quality, 14, 35).ToString(CultureInfo.InvariantCulture),
                "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a",
                Math.Clamp(request.AudioBitrate, 64, 320).ToString(CultureInfo.InvariantCulture) + "k",
                "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", temporary,
            });

            progress?.Report(new EditorProgress(0.04, "Preparing video"));
            var result = await ProcessRunner.RunAsync(_engine.Ffmpeg.FfmpegPath!, args,
                line => ReportProgress(line, duration, progress, "Rendering"),
                null, ct);

            if (result.Canceled || ct.IsCancellationRequested)
            {
                stopwatch.Stop();
                return EditorResult.Cancelled(stopwatch.Elapsed);
            }

            if (result.ExitCode != 0 || !File.Exists(temporary))
            {
                stopwatch.Stop();
                return EditorResult.Fail("The video could not be exported.", LastError(result.StdErr), stopwatch.Elapsed);
            }

            progress?.Report(new EditorProgress(0.97, "Saving"));
            EditorFileNaming.MoveWithRetry(temporary, output, overwrite: true);
            temporary = null;
            stopwatch.Stop();
            progress?.Report(new EditorProgress(1, "Saved"));
            Log.Info("editor.video", "Video edit exported",
                ("source", request.SourcePath), ("output", output),
                ("clip", $"{start:0.###}-{end:0.###}s"), ("caption", request.Caption.HasText.ToString()));
            return EditorResult.Ok(output, stopwatch.Elapsed, 0, 0);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return EditorResult.Cancelled(stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log.Warn("editor.video", "Video edit failed", ("source", request.SourcePath), ("reason", ex.Message));
            return EditorResult.Fail("The video could not be exported.", ex.Message, stopwatch.Elapsed);
        }
        finally
        {
            if (temporary is not null) TryDelete(temporary);
            if (textFile is not null) TryDelete(textFile);
        }
    }

    private static string? BuildVideoFilter(EditorCaption caption, string? textFile, int width, int height)
    {
        if (!caption.HasText || textFile is null) return null;
        var font = FindFont();
        if (font is null) return null;

        var fontSize = Math.Max(12, (int)Math.Round(Math.Min(width, height) * Math.Clamp(caption.ScalePercent, 1, 18) / 100.0));
        var border = Math.Max(4, (int)Math.Round(fontSize * 0.35));
        var y = caption.Placement switch
        {
            EditorTextPlacement.Top => "h*0.08",
            EditorTextPlacement.Center => "(h-text_h)/2",
            _ => "h-text_h-h*0.08",
        };

        var parts = new List<string>
        {
            "drawtext=fontfile=" + QuoteFilterPath(font),
            "textfile=" + QuoteFilterPath(textFile),
            "fontcolor=" + EditorTextColors.ToFfmpeg(caption.ColorId),
            "fontsize=" + fontSize.ToString(CultureInfo.InvariantCulture),
            "box=1",
            "boxcolor=" + EditorTextColors.BoxFfmpeg(caption.ColorId),
            "boxborderw=" + border.ToString(CultureInfo.InvariantCulture),
            "x=(w-text_w)/2",
            "y=" + y,
        };

        if (EditorTextColors.HasShadow(caption.ColorId))
            parts.AddRange(new[] { "shadowcolor=black@0.55", "shadowx=2", "shadowy=2" });

        return string.Join(":", parts);
    }

    private static string QuoteFilterPath(string path)
    {
        var escaped = path.Replace("\\", "/")
            .Replace(":", "\\:")
            .Replace("'", "\\'");
        return $"'{escaped}'";
    }

    private static string? FindFont()
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var name in new[] { "segoeuib.ttf", "segoeui.ttf", "arialbd.ttf", "arial.ttf", "calibrib.ttf" })
        {
            var candidate = Path.Combine(fonts, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ───────────────────────── audio ─────────────────────────

    public async Task<BitmapSource?> WaveformAsync(string source, CancellationToken ct)
    {
        if (!Available) return null;
        var directory = Path.Combine(Path.GetTempPath(), "Nasaq");
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, $"wave-{Guid.NewGuid():N}.png");
        try
        {
            var result = await ProcessRunner.RunAsync(_engine.Ffmpeg.FfmpegPath!,
                new[]
                {
                    "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                    "-i", source,
                    "-filter_complex", "aformat=channel_layouts=mono,showwavespic=s=900x160:colors=0x7C9CFF",
                    "-frames:v", "1", output,
                }, null, null, ct);

            if (result.ExitCode != 0 || !File.Exists(output)) return null;
            return await WpfImageConverter.RunStaAsync(() =>
            {
                var bitmap = new BitmapImage();
                using (var stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                bitmap.Freeze();
                return (BitmapSource)bitmap;
            });
        }
        finally
        {
            TryDelete(output);
        }
    }

    public async Task<EditorResult> ExportAudioTrimAsync(AudioTrimRequest request,
        IProgress<EditorProgress>? progress, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string? temporary = null;
        try
        {
            if (!Available) throw new InvalidOperationException("FFmpeg is needed to trim audio.");
            var start = Math.Max(0, request.StartSeconds);
            var end = Math.Max(start + 0.05, request.EndSeconds);
            var duration = end - start;
            if (duration < 0.05) throw new InvalidOperationException("Choose a longer clip.");

            var extension = NormalizeAudioFormat(request.Format);
            var outputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory)
                ? AppSettings.DefaultOutputDirectory()
                : request.OutputDirectory;
            var output = EditorFileNaming.UniquePath(outputDirectory,
                EditorFileNaming.SafeStem(request.SourcePath) + "-trimmed", extension, request.Overwrite);
            temporary = Path.Combine(Path.GetDirectoryName(output)!, $".nasaq-{Guid.NewGuid():N}.{extension}");

            var args = new List<string>
            {
                "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", request.SourcePath,
                "-t", duration.ToString("0.###", CultureInfo.InvariantCulture),
                "-vn", "-map", "0:a:0",
            };
            args.AddRange(AudioEncoderArgs(extension, request.Bitrate));
            args.AddRange(new[] { "-progress", "pipe:1", "-nostats", temporary });

            progress?.Report(new EditorProgress(0.04, "Preparing audio"));
            var result = await ProcessRunner.RunAsync(_engine.Ffmpeg.FfmpegPath!, args,
                line => ReportProgress(line, duration, progress, "Encoding"), null, ct);

            if (result.Canceled || ct.IsCancellationRequested)
            {
                stopwatch.Stop();
                return EditorResult.Cancelled(stopwatch.Elapsed);
            }

            if (result.ExitCode != 0 || !File.Exists(temporary))
            {
                stopwatch.Stop();
                return EditorResult.Fail("The audio could not be trimmed.", LastError(result.StdErr), stopwatch.Elapsed);
            }

            progress?.Report(new EditorProgress(0.97, "Saving"));
            EditorFileNaming.MoveWithRetry(temporary, output, overwrite: true);
            temporary = null;
            stopwatch.Stop();
            progress?.Report(new EditorProgress(1, "Saved"));
            Log.Info("editor.audio", "Audio trim exported",
                ("source", request.SourcePath), ("output", output),
                ("clip", $"{start:0.###}-{end:0.###}s"), ("format", extension));
            return EditorResult.Ok(output, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return EditorResult.Cancelled(stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log.Warn("editor.audio", "Audio trim failed", ("source", request.SourcePath), ("reason", ex.Message));
            return EditorResult.Fail("The audio could not be trimmed.", ex.Message, stopwatch.Elapsed);
        }
        finally
        {
            if (temporary is not null) TryDelete(temporary);
        }
    }

    public async Task<EditorResult> MergeAudioAsync(AudioMergeRequest request,
        IProgress<EditorProgress>? progress, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string? temporary = null;
        try
        {
            if (!Available) throw new InvalidOperationException("FFmpeg is needed to merge audio.");
            var requested = request.Clips is { Count: > 0 }
                ? request.Clips.Where(c => File.Exists(c.Path)).ToList()
                : request.Sources.Where(File.Exists).Select(path => new AudioMergeClip(path, 0, 0)).ToList();
            if (requested.Count < 2)
                return EditorResult.Fail("Add at least two audio clips to merge.", null, stopwatch.Elapsed);

            progress?.Report(new EditorProgress(0.04, "Reading tracks"));
            var total = 0.0;
            var clips = new List<AudioMergeClip>(requested.Count);
            foreach (var clip in requested)
            {
                var info = await ProbeAsync(clip.Path, ct);
                if (!info.HasAudio)
                    return EditorResult.Fail($"{Path.GetFileName(clip.Path)} has no audio.", null, stopwatch.Elapsed);
                var start = Math.Clamp(clip.StartSeconds, 0, Math.Max(0, info.Duration - 0.01));
                var end = clip.EndSeconds > clip.StartSeconds
                    ? Math.Clamp(clip.EndSeconds, start + 0.01, info.Duration)
                    : info.Duration;
                if (end - start < 0.01) continue;
                clips.Add(new AudioMergeClip(clip.Path, start, end));
                total += end - start;
            }

            if (clips.Count < 2)
                return EditorResult.Fail("Add at least two audio clips to merge.", null, stopwatch.Elapsed);

            var extension = NormalizeAudioFormat(request.Format);
            var outputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory)
                ? AppSettings.DefaultOutputDirectory()
                : request.OutputDirectory;
            var output = EditorFileNaming.UniquePath(outputDirectory,
                EditorFileNaming.SafeStem(clips[0].Path) + "-mix", extension, request.Overwrite);
            temporary = Path.Combine(Path.GetDirectoryName(output)!, $".nasaq-{Guid.NewGuid():N}.{extension}");

            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" };
            foreach (var clip in clips) args.AddRange(new[] { "-i", clip.Path });

            var filters = new List<string>();
            for (var i = 0; i < clips.Count; i++)
            {
                var clip = clips[i];
                filters.Add($"[{i}:a]atrim=start={Seconds(clip.StartSeconds)}:end={Seconds(clip.EndSeconds)}," +
                            $"asetpts=PTS-STARTPTS,aresample=48000," +
                            $"aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}]");
            }
            var inputs = string.Concat(Enumerable.Range(0, clips.Count).Select(i => $"[a{i}]"));
            filters.Add($"{inputs}concat=n={clips.Count}:v=0:a=1[out]");

            args.AddRange(new[]
            {
                "-filter_complex", string.Join(";", filters),
                "-map", "[out]", "-vn",
            });
            args.AddRange(AudioEncoderArgs(extension, request.Bitrate));
            args.AddRange(new[] { "-progress", "pipe:1", "-nostats", temporary });

            var result = await ProcessRunner.RunAsync(_engine.Ffmpeg.FfmpegPath!, args,
                line => ReportProgress(line, total, progress, "Mixing"), null, ct);

            if (result.Canceled || ct.IsCancellationRequested)
            {
                stopwatch.Stop();
                return EditorResult.Cancelled(stopwatch.Elapsed);
            }

            if (result.ExitCode != 0 || !File.Exists(temporary))
            {
                stopwatch.Stop();
                return EditorResult.Fail("The tracks could not be merged.", LastError(result.StdErr), stopwatch.Elapsed);
            }

            progress?.Report(new EditorProgress(0.97, "Saving"));
            EditorFileNaming.MoveWithRetry(temporary, output, overwrite: true);
            temporary = null;
            stopwatch.Stop();
            progress?.Report(new EditorProgress(1, "Saved"));
            Log.Info("editor.audio", "Audio merge exported",
                ("clips", clips.Count.ToString()), ("output", output),
                ("duration", total.ToString("0.##", CultureInfo.InvariantCulture)), ("format", extension));
            return EditorResult.Ok(output, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return EditorResult.Cancelled(stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log.Warn("editor.audio", "Audio merge failed", ("reason", ex.Message));
            return EditorResult.Fail("The tracks could not be merged.", ex.Message, stopwatch.Elapsed);
        }
        finally
        {
            if (temporary is not null) TryDelete(temporary);
        }
    }

    private static string Seconds(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string NormalizeAudioFormat(string format) => format?.ToLowerInvariant() switch
    {
        "wav" => "wav",
        "m4a" => "m4a",
        "flac" => "flac",
        "ogg" => "ogg",
        "opus" => "opus",
        _ => "mp3",
    };

    private static IEnumerable<string> AudioEncoderArgs(string format, int bitrate)
    {
        var rate = Math.Clamp(bitrate, 64, 320).ToString(CultureInfo.InvariantCulture) + "k";
        return format switch
        {
            "wav" => new[] { "-c:a", "pcm_s16le" },
            "flac" => new[] { "-c:a", "flac" },
            "m4a" => new[] { "-c:a", "aac", "-b:a", rate },
            "ogg" => new[] { "-c:a", "libvorbis", "-b:a", rate },
            "opus" => new[] { "-c:a", "libopus", "-b:a", rate },
            _ => new[] { "-c:a", "libmp3lame", "-b:a", rate },
        };
    }

    // ───────────────────────── helpers ─────────────────────────

    private static void ReportProgress(string line, double totalSeconds, IProgress<EditorProgress>? progress,
        string stage)
    {
        if (progress is null || totalSeconds <= 0) return;
        double seconds;
        if (line.StartsWith("out_time_us=", StringComparison.Ordinal) ||
            line.StartsWith("out_time_ms=", StringComparison.Ordinal))
        {
            var value = line[(line.IndexOf('=') + 1)..];
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micro)) return;
            seconds = micro / 1_000_000.0;
        }
        else if (line.StartsWith("out_time=", StringComparison.Ordinal))
        {
            if (!TimeSpan.TryParse(line[9..], CultureInfo.InvariantCulture, out var time)) return;
            seconds = time.TotalSeconds;
        }
        else
        {
            return;
        }

        var fraction = Math.Clamp(seconds / totalSeconds, 0, 0.98);
        progress.Report(new EditorProgress(fraction, stage));
    }

    private static string LastError(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "No detail was returned." : string.Join(" ", lines.TakeLast(5));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
