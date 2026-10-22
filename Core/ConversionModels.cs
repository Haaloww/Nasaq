using System.IO;

namespace Nasaq.Core;

public sealed record ConversionOptions(
    string OutputDirectory,
    int ImageQuality,
    int AudioBitrate,
    int VideoQuality,
    bool Overwrite,
    bool PreserveMetadata);

public sealed record ConversionProgress(double Fraction, string Stage);

public sealed record ConversionResult(
    bool Success,
    bool Canceled,
    string? OutputPath,
    string? Error,
    string? Detail,
    TimeSpan Elapsed,
    string Engine)
{
    public static ConversionResult Ok(string path, TimeSpan elapsed, string engine) =>
        new(true, false, path, null, null, elapsed, engine);

    public static ConversionResult Fail(string error, string? detail, TimeSpan elapsed, string engine) =>
        new(false, false, null, error, detail, elapsed, engine);

    public static ConversionResult Cancelled(TimeSpan elapsed, string engine) =>
        new(false, true, null, "Conversion was cancelled.", null, elapsed, engine);
}

public sealed class ConversionException : Exception
{
    public string FriendlyMessage { get; }
    public string? Hint { get; }

    public ConversionException(string friendlyMessage, string? detail = null, Exception? inner = null,
        string? hint = null) : base(detail ?? friendlyMessage, inner)
    {
        FriendlyMessage = friendlyMessage;
        Hint = hint;
    }
}

public static class FileSize
{
    public static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value:0.#} {units[unit]}";
    }

    public static string FormatPath(string path, int max = 46)
    {
        if (path.Length <= max) return path;
        var root = Path.GetPathRoot(path) ?? "";
        var name = Path.GetFileName(path);
        var available = Math.Max(6, max - root.Length - name.Length - 4);
        return $"{root}…{path.Substring(root.Length, Math.Min(available, path.Length - root.Length))}…{name}";
    }
}
