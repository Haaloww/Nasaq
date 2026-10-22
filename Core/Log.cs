using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Nasaq.Core;

public sealed record LogRecord(DateTime Time, string Level, string Area, string Message, string Text);

/// <summary>
/// Thread-safe, human-readable logging. Every entry is a short header plus
/// indented key/value fields so log files can be scanned quickly.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string _directory = "";
    private static string _sessionHeader = "";
    private static bool _initialised;

    public static bool Verbose { get; set; }

    public static event EventHandler<LogRecord>? Written;

    public static string DirectoryPath => _directory;

    public static string CurrentFile =>
        Path.Combine(_directory, $"nasaq-{DateTime.Now:yyyy-MM-dd}.log");

    public static string CrashDirectory => Path.Combine(_directory, "crashes");

    public static void Init(string directory)
    {
        if (_initialised) return;
        _directory = directory;
        _initialised = true;

        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            System.IO.Directory.CreateDirectory(CrashDirectory);
            Prune();

            _sessionHeader = string.Join(Environment.NewLine, new[]
            {
                "",
                "════════════════════════════════════════════════════════════════════",
                $"  NASAQ SESSION · {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                "════════════════════════════════════════════════════════════════════",
                $"    version      {AppInfo.Version}",
                $"    os           {RuntimeInformation.OSDescription} ({Environment.OSVersion.Version})",
                $"    runtime      .NET {Environment.Version}",
                $"    process      {Environment.ProcessId}",
                $"    arch         {RuntimeInformation.ProcessArchitecture}",
                $"    base         {AppContext.BaseDirectory}",
                "",
            });
            File.AppendAllText(CurrentFile, _sessionHeader, Encoding.UTF8);
        }
        catch
        {
            // Logging must never take the application down.
        }
    }

    private static void Prune()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-30);
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "nasaq-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
            foreach (var file in System.IO.Directory.EnumerateFiles(CrashDirectory, "crash-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch { /* best effort */ }
    }

    public static void Debug(string area, string message, params (string Key, string? Value)[] fields)
    {
        if (Verbose) Write("DEBUG", area, message, fields);
    }

    public static void Info(string area, string message, params (string Key, string? Value)[] fields)
        => Write("INFO", area, message, fields);

    public static void Warn(string area, string message, params (string Key, string? Value)[] fields)
        => Write("WARN", area, message, fields);

    public static void Error(string area, string message, Exception? ex = null,
        params (string Key, string? Value)[] fields)
    {
        var all = new List<(string, string?)>(fields);
        if (ex is not null)
        {
            all.Add(("type", ex.GetType().FullName));
            all.Add(("reason", Shorten(ex.Message, 600)));
        }
        Write("ERROR", area, message, all.ToArray());
        if (ex is not null) WriteTrace(area, ex);
    }

    private static string Shorten(string value, int limit)
    {
        var flat = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= limit ? flat : flat[..limit] + "… (see detail)";
    }

    public static void WriteTrace(string area, Exception ex)
    {
        var sb = new StringBuilder();
        AppendException(sb, ex, 0);
        var text = sb.ToString().TrimEnd();
        if (string.IsNullOrWhiteSpace(text)) return;
        Write("TRACE", area, "Exception detail", new[] { ("detail", (string?)null) }, text);
    }

    private static void AppendException(StringBuilder sb, Exception ex, int depth)
    {
        var pad = new string(' ', depth * 4);
        sb.AppendLine($"{pad}{ex.GetType().FullName}: {ex.Message}");
        if (!string.IsNullOrWhiteSpace(ex.Source)) sb.AppendLine($"{pad}  source: {ex.Source}");
        if (ex.HResult != 0) sb.AppendLine($"{pad}  hresult: 0x{ex.HResult:X8}");
        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            sb.AppendLine($"{pad}  stack:");
            foreach (var line in ex.StackTrace!.Split('\n'))
                sb.AppendLine($"{pad}    {line.Trim()}");
        }
        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions) AppendException(sb, inner, depth + 1);
        }
        else if (ex.InnerException is not null)
        {
            sb.AppendLine($"{pad}  inner:");
            AppendException(sb, ex.InnerException, depth + 1);
        }
    }

    private static void Write(string level, string area, string message,
        (string Key, string? Value)[] fields, string? block = null)
    {
        if (!_initialised) return;

        var time = DateTime.Now;
        var sb = new StringBuilder();
        sb.Append('[').Append(time.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
        sb.Append(level.PadRight(5)).Append("  ").Append(area);
        if (!string.IsNullOrWhiteSpace(message))
        {
            sb.AppendLine();
            sb.Append("    message      ").AppendLine(message);
        }
        else sb.AppendLine();

        foreach (var (key, value) in fields)
        {
            if (string.IsNullOrEmpty(key)) continue;
            if (key == "detail" && block is not null)
            {
                sb.Append("    detail").AppendLine();
                foreach (var line in block.Split('\n'))
                    sb.Append("        ").AppendLine(line.TrimEnd('\r'));
                continue;
            }
            if (value is null) continue;
            var flat = value.Replace("\r", " ").Replace("\n", " ");
            if (flat.Length > 4000) flat = flat[..4000] + "…";
            sb.Append("    ").Append(key.PadRight(13)).Append(flat).AppendLine();
        }
        sb.AppendLine();

        var text = sb.ToString();
        lock (Gate)
        {
            try
            {
                File.AppendAllText(CurrentFile, text, Encoding.UTF8);
            }
            catch { }
        }
        try { Written?.Invoke(null, new LogRecord(time, level, area, message, text)); }
        catch { }
    }

    public static string WriteCrash(Exception ex, string context, bool fatal)
    {
        var stamp = DateTime.Now;
        var name = $"crash-{stamp:yyyyMMdd-HHmmss}.log";
        var path = Path.Combine(CrashDirectory, name);
        var sb = new StringBuilder();
        try
        {
            sb.AppendLine("╔══════════════════════════════════════════════════════════════════════╗");
            sb.AppendLine("║                        NASAQ CRASH REPORT                            ║");
            sb.AppendLine("╚══════════════════════════════════════════════════════════════════════╝");
            sb.AppendLine();
            sb.AppendLine($"    time         {stamp:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"    context      {context}");
            sb.AppendLine($"    fatal        {fatal}");
            sb.AppendLine($"    version      {AppInfo.Version}");
            sb.AppendLine($"    os           {RuntimeInformation.OSDescription}");
            sb.AppendLine($"    runtime      .NET {Environment.Version}");
            sb.AppendLine($"    process      {Environment.ProcessId}");
            sb.AppendLine($"    memory       {Environment.WorkingSet / 1024 / 1024} MB working set");
            sb.AppendLine();
            sb.AppendLine("──────────────────────────────────────────────────────────────────────");
            sb.AppendLine("  EXCEPTION");
            sb.AppendLine("──────────────────────────────────────────────────────────────────────");
            AppendException(sb, ex, 1);
            sb.AppendLine();
            sb.AppendLine("──────────────────────────────────────────────────────────────────────");
            sb.AppendLine("  RECENT LOG");
            sb.AppendLine("──────────────────────────────────────────────────────────────────────");
            foreach (var line in ReadTail(40).Split('\n'))
                sb.Append("    ").AppendLine(line.TrimEnd('\r'));
            sb.AppendLine();
            sb.AppendLine("══════════════════════════════════════════════════════════════════════");

            System.IO.Directory.CreateDirectory(CrashDirectory);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), name), sb.ToString(), Encoding.UTF8);
                return Path.Combine(Path.GetTempPath(), name);
            }
            catch { }
        }

        try
        {
            Write("FATAL", "crash", fatal ? "Unhandled fatal exception" : "Unhandled exception",
                new[] { ("report", (string?)path), ("type", ex.GetType().FullName), ("reason", ex.Message) });
        }
        catch { }
        return path;
    }

    public static string ReadTail(int maxLines = 300)
    {
        try
        {
            var path = CurrentFile;
            if (!File.Exists(path)) return "(log file not created yet)";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new LinkedList<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.AddLast(line);
                if (lines.Count > maxLines) lines.RemoveFirst();
            }

            // Start at a real record header so the tail doesn't begin mid-entry.
            if (lines.Count >= maxLines)
            {
                while (lines.Count > 1 && !lines.First!.Value.StartsWith("[", StringComparison.Ordinal))
                    lines.RemoveFirst();
            }

            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return $"(unable to read log: {ex.Message})";
        }
    }

    public static void OpenLogFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            Process.Start(new ProcessStartInfo("explorer.exe", _directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Warn("log.folder", "Unable to open the log folder", ("reason", ex.Message));
        }
    }
}

public static class AppInfo
{
    public const string Name = "Nasaq";
    public const string Version = "1.0.0";
    public const string Author = "Abdullah Abdulmanea";
    public const string Tagline = "Fast, quiet conversion for Windows.";
    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nasaq");
}
