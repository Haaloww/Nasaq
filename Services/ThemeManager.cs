using System.Windows;
using Nasaq.Core;

namespace Nasaq.Services;

public sealed record ThemeInfo(string Id, string Name, string Description, string Accent, string Backdrop);

public static class ThemeManager
{
    public static readonly IReadOnlyList<ThemeInfo> Themes = new[]
    {
        new ThemeInfo("midnight",  "Midnight",  "Deep navy with cool blue accents",  "#7C9CFF", "#0B0E13"),
        new ThemeInfo("graphite",  "Graphite",  "Neutral charcoal with warm amber",   "#E4B363", "#121212"),
        new ThemeInfo("ocean",     "Ocean",     "Dark petrol with a teal current",    "#4FD1C5", "#07131A"),
        new ThemeInfo("porcelain", "Porcelain", "Crisp paper white with indigo",      "#4F6BFF", "#F3F5F9"),
        new ThemeInfo("sand",      "Sand",      "Warm linen with terracotta",         "#C46A43", "#F6F0E6"),
    };

    public static string CurrentId { get; private set; } = "midnight";

    public static bool IsKnown(string? id) => Themes.Any(t => t.Id == id);

    public static ThemeInfo Current =>
        Themes.FirstOrDefault(t => t.Id == CurrentId) ?? Themes[0];

    public static void Apply(string id)
    {
        if (!IsKnown(id)) id = "midnight";
        var app = Application.Current;
        if (app is null) return;

        try
        {
            var uri = new Uri($"pack://application:,,,/Nasaq;component/Themes/{Capitalise(id)}.xaml", UriKind.Absolute);
            var theme = new ResourceDictionary { Source = uri };
            var dictionaries = app.Resources.MergedDictionaries;

            var existing = dictionaries.FirstOrDefault(d => d.Contains("Theme.Marker"));
            if (existing is not null) dictionaries.Remove(existing);
            dictionaries.Add(theme);

            CurrentId = id;
            Log.Debug("theme.apply", "Theme applied", ("theme", id));
        }
        catch (Exception ex)
        {
            Log.Warn("theme.apply", "Theme could not be applied", ("theme", id), ("reason", ex.Message));
        }
    }

    private static string Capitalise(string id) => id.Length == 0
        ? id
        : char.ToUpperInvariant(id[0]) + id[1..];
}
