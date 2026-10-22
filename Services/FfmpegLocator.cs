using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Nasaq.Core;

namespace Nasaq.Services;

public sealed record FfmpegInfo(string? FfmpegPath, string? FfprobePath, string? Version, string Source)
{
    public bool IsAvailable => !string.IsNullOrWhiteSpace(FfmpegPath) && File.Exists(FfmpegPath);
    public string DisplayVersion => string.IsNullOrWhiteSpace(Version) ? "FFmpeg" : $"FFmpeg {Version}";
}

public static class FfmpegLocator
{
    public static FfmpegInfo Locate(string? configuredPath)
    {
        var candidates = new List<(string Path, string Source)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path, string source)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                if (Directory.Exists(path)) path = Path.Combine(path, "ffmpeg.exe");
                if (!File.Exists(path)) return;
                var full = Path.GetFullPath(path);
                if (seen.Add(full)) candidates.Add((full, source));
            }
            catch { }
        }

        Add(configuredPath, "settings");
        Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"), "application folder");
        Add(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"), "bundled tools");

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            Add(Path.Combine(dir.Trim(), "ffmpeg.exe"), "PATH");

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe"), "WinGet");
        try
        {
            var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
            {
                foreach (var dir in Directory.EnumerateDirectories(packages, "Gyan.FFmpeg*"))
                {
                    var found = Directory.EnumerateFiles(dir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                    Add(found, "WinGet package");
                }
            }
        }
        catch { }

        Add(@"C:\Program Files\ffmpeg\bin\ffmpeg.exe", "Program Files");
        Add(@"C:\ffmpeg\bin\ffmpeg.exe", "C:\\ffmpeg");

        foreach (var (path, source) in candidates)
        {
            var version = ProbeVersion(path);
            if (version is null) continue;

            var probe = Path.Combine(Path.GetDirectoryName(path)!, "ffprobe.exe");
            if (!File.Exists(probe)) probe = null;
            var info = new FfmpegInfo(path, probe, version, source);
            Log.Info("ffmpeg.locate", "FFmpeg engine ready",
                ("path", path), ("source", source), ("version", version),
                ("ffprobe", probe ?? "not found"));
            return info;
        }

        Log.Warn("ffmpeg.locate", "FFmpeg was not found. Audio and video conversion will be unavailable.",
            ("configured", configuredPath ?? "auto"));
        return new FfmpegInfo(null, null, null, "not found");
    }

    private static string? ProbeVersion(string path)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(path, "-version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(4000))
            {
                try { process.Kill(true); } catch { }
                return null;
            }
            if (process.ExitCode != 0) return null;
            var match = Regex.Match(output, @"ffmpeg version\s+(\S+)");
            return match.Success ? match.Groups[1].Value : "unknown";
        }
        catch
        {
            return null;
        }
    }
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool Canceled);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        Action<string>? onStdout,
        Action<string>? onStderr,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        var canceled = false;

        using var registration = cancellationToken.Register(() =>
        {
            canceled = true;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
        });

        process.Start();
        try { process.StandardInput.Close(); } catch { }

        var outTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                stdout.AppendLine(line);
                onStdout?.Invoke(line);
            }
        });

        var errTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                stderr.AppendLine(line);
                onStderr?.Invoke(line);
            }
        });

        await Task.WhenAll(outTask, errTask);
        await process.WaitForExitAsync(CancellationToken.None);
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(),
            canceled || cancellationToken.IsCancellationRequested);
    }
}
