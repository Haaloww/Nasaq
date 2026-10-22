using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Nasaq.Core;

namespace Nasaq.Services;

/// <summary>
/// Watches for Ctrl being held while a file is being dragged out of File
/// Explorer or off the desktop. The drop shelf can then appear right next to
/// the cursor, even when the main window is hidden in the tray.
///
/// A small dispatcher poll is used instead of a global keyboard hook: it works
/// while Nasaq is in the background, cannot be starved by a drag operation, and
/// needs no injected DLL.
/// </summary>
public sealed class DragShelfWatcher : IDisposable
{
    private const int VkControl = 0x11;
    private const int VkLeftButton = 0x01;

    /// <summary>Poll interval while Nasaq is idle.</summary>
    private const int PollMilliseconds = 90;

    /// <summary>How long Ctrl + mouse must be held before the shelf appears.</summary>
    private const int HoldMilliseconds = 220;

    /// <summary>Ignore repeat triggers while the gesture is still held.</summary>
    private const int TriggerCooldownMilliseconds = 1500;

    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CabinetWClass",          // File Explorer window
        "ExploreWClass",          // legacy Explorer
        "Progman",                // desktop
        "WorkerW",                // desktop wallpaper host
        "Shell_TrayWnd",          // taskbar / jump lists
        "ApplicationFrameWindow", // shell-hosted surfaces
    };

    private readonly DispatcherTimer _timer;
    private readonly object _gate = new();

    private bool _enabled = true;
    private bool _disposed;
    private DateTime _pendingSince = DateTime.MinValue;
    private DateTime _lastTrigger = DateTime.MinValue;

    public event EventHandler? Triggered;

    public DragShelfWatcher()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(PollMilliseconds),
        };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();

        Log.Info("shelf.watch", "Ctrl-drag shelf detection active",
            ("gesture", "hold Ctrl while dragging files"));
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            _pendingSince = DateTime.MinValue;
            Log.Info("shelf.watch", value ? "Ctrl-drag shelf detection enabled" : "Ctrl-drag shelf detection disabled");
        }
    }

    public bool IsActive => _enabled;

    private void Poll()
    {
        if (!_enabled || _disposed) return;

        try
        {
            var ctrl = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
            var mouse = (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;

            if (!ctrl || !mouse)
            {
                _pendingSince = DateTime.MinValue;
                return;
            }

            var className = GetWindowClassName(GetForegroundWindow());
            if (!ShellWindowClasses.Contains(className))
            {
                _pendingSince = DateTime.MinValue;
                return;
            }

            // Hold the gesture briefly so a plain Ctrl+click never fires.
            if (_pendingSince == DateTime.MinValue) _pendingSince = DateTime.UtcNow;
            if ((DateTime.UtcNow - _pendingSince).TotalMilliseconds < HoldMilliseconds) return;

            lock (_gate)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastTrigger).TotalMilliseconds < TriggerCooldownMilliseconds) return;
                _lastTrigger = now;
            }

            _pendingSince = DateTime.MinValue;
            Log.Debug("shelf.ctrl", "Ctrl held during a shell drag; summoning the shelf",
                ("class", className));
            Triggered?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warn("shelf.ctrl", "Ctrl-drag detection failed", ("reason", ex.Message));
        }
    }

    private static string GetWindowClassName(IntPtr window)
    {
        if (window == IntPtr.Zero) return "";
        var buffer = new StringBuilder(256);
        var length = GetClassName(window, buffer, buffer.Capacity);
        return length <= 0 ? "" : buffer.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _timer.Stop();
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}
