namespace Nasaq.Core;

/// <summary>A selectable global shortcut used to summon the drop shelf.</summary>
public sealed record ShelfHotkey(string Id, string Label, string ShortLabel, uint Modifiers, uint VirtualKey);

public static class ShelfHotkeys
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;

    public static readonly IReadOnlyList<ShelfHotkey> All = new[]
    {
        new ShelfHotkey("ctrl-alt-d", "Ctrl + Alt + D", "Ctrl+Alt+D",
            ModControl | ModAlt | ModNoRepeat, 0x44),
        new ShelfHotkey("ctrl-shift-d", "Ctrl + Shift + D", "Ctrl+Shift+D",
            ModControl | ModShift | ModNoRepeat, 0x44),
        new ShelfHotkey("alt-shift-d", "Alt + Shift + D", "Alt+Shift+D",
            ModAlt | ModShift | ModNoRepeat, 0x44),
        new ShelfHotkey("ctrl-alt-s", "Ctrl + Alt + S", "Ctrl+Alt+S",
            ModControl | ModAlt | ModNoRepeat, 0x53),
        new ShelfHotkey("ctrl-shift-space", "Ctrl + Shift + Space", "Ctrl+Shift+Space",
            ModControl | ModShift | ModNoRepeat, 0x20),
    };

    public static ShelfHotkey Default => All[0];

    public static bool IsKnown(string? id) =>
        All.Any(hotkey => hotkey.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static ShelfHotkey Find(string? id) =>
        All.FirstOrDefault(hotkey => hotkey.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? Default;
}
