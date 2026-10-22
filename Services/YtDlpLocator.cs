using System.Diagnostics;
using System.IO;
using Nasaq.Core;

namespace Nasaq.Services;

public sealed record YtDlpInfo(string? Path, string? Version, string Source)
{
    public bool IsAvailable => !string.IsNullOrWhiteSpace(Path) && File.Exists(Path);
    public string DisplayVersion => string.IsNullOrWhiteSpace(Version) ? "yt-dlp" : $"yt-dlp {Version}";
    public bool IsManaged =>
        IsAvailable && string.Equals(Path, YtDlpLocator.ManagedPath, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Finds the yt-dlp executable used by the Installer. A managed copy lives in
/// %APPDATA%\Nasaq\tools and can be installed or refreshed from the Installer page.
/// </summary>
public static class YtDlpLocator
{
    public static string ManagedDirectory => Path.Combine(AppInfo.AppDataDirectory, "tools");
    public static string ManagedPath => Path.Combine(ManagedDirectory, "yt-dlp.exe");

    public static YtDlpInfo Locate(string? configuredPath)
    {
        var candidates = new List<(string Path, string Source)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path, string source)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                if (Directory.Exists(path)) path = Path.Combine(path, "yt-dlp.exe");
                if (!File.Exists(path)) return;
                var full = Path.GetFullPath(path);
                if (seen.Add(full)) candidates.Add((full, source));
            }
            catch { }
        }

        Add(configuredPath, "settings");
        Add(ManagedPath, "Nasaq tools");
        Add(Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe"), "application folder");
        Add(Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp.exe"), "bundled tools");

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            Add(Path.Combine(dir.Trim(), "yt-dlp.exe"), "PATH");

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "yt-dlp.exe"), "WinGet");
        try
        {
            var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
            {
                foreach (var dir in Directory.EnumerateDirectories(packages, "yt-dlp*"))
                {
                    var found = Directory.EnumerateFiles(dir, "yt-dlp.exe", SearchOption.AllDirectories).FirstOrDefault();
                    Add(found, "WinGet package");
                }
            }
        }
        catch { }

        Add(@"C:\Program Files\yt-dlp\yt-dlp.exe", "Program Files");
        Add(@"C:\yt-dlp\yt-dlp.exe", "C:\\yt-dlp");

        foreach (var (path, source) in candidates)
        {
            var version = ProbeVersion(path);
            if (version is null) continue;

            var info = new YtDlpInfo(path, version, source);
            Log.Info("ytdlp.locate", "Installer engine ready",
                ("path", path), ("source", source), ("version", version));
            return info;
        }

        Log.Info("ytdlp.locate", "Installer engine not found; it can be set up from the Installer page.",
            ("configured", configuredPath ?? "auto"));
        return new YtDlpInfo(null, null, "not found");
    }

    private static string? ProbeVersion(string path)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(path, "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(6000))
            {
                try { process.Kill(true); } catch { }
                return null;
            }
            if (process.ExitCode != 0) return null;
            var version = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(version) || version.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                return null;
            if (!string.IsNullOrWhiteSpace(error))
                Log.Debug("ytdlp.probe", "yt-dlp emitted diagnostics while reporting its version",
                    ("detail", error.Trim()));
            return version.Length > 60 ? version[..60] : version;
        }
        catch
        {
            return null;
        }
    }
}
