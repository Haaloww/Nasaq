using System.Drawing;
using Nasaq.Core;
using Forms = System.Windows.Forms;

namespace Nasaq.Services;

/// <summary>
/// Keeps Nasaq alive in the notification area so conversion, the global hotkey
/// and the drop shelf keep working after the main window is closed.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notify;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon _icon;
    private bool _disposed;

    public event EventHandler? OpenRequested;
    public event EventHandler? ShelfRequested;
    public event EventHandler? InstallerRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public bool MiddleClickOpensShelf { get; set; }

    public TrayIcon()
    {
        _icon = LoadIcon();
        _menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9f),
            Padding = new Forms.Padding(2),
        };

        var open = new Forms.ToolStripMenuItem("Open Nasaq")
        {
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        };
        open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        var shelf = new Forms.ToolStripMenuItem("Drop shelf")
        {
            ShortcutKeyDisplayString = "Ctrl+Alt+D",
        };
        shelf.Click += (_, _) => ShelfRequested?.Invoke(this, EventArgs.Empty);

        var settings = new Forms.ToolStripMenuItem("Settings…");
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        var installer = new Forms.ToolStripMenuItem("Installer")
        {
            ShortcutKeyDisplayString = "Ctrl+I",
        };
        installer.Click += (_, _) => InstallerRequested?.Invoke(this, EventArgs.Empty);

        var editor = new Forms.ToolStripMenuItem("Editor — coming soon")
        {
            Enabled = false,
        };

        var exit = new Forms.ToolStripMenuItem("Exit Nasaq");
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _menu.Items.Add(open);
        _menu.Items.Add(shelf);
        _menu.Items.Add(installer);
        _menu.Items.Add(editor);
        _menu.Items.Add(settings);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(exit);

        _notify = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "Nasaq — file toolkit",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notify.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Middle && MiddleClickOpensShelf)
            {
                ShelfRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke(this, EventArgs.Empty);
        };

        Log.Info("tray.ready", "Tray icon is active");
    }

    public void ShowBalloon(string title, string message)
    {
        if (_disposed) return;
        try
        {
            _notify.BalloonTipTitle = title;
            _notify.BalloonTipText = message;
            _notify.BalloonTipIcon = Forms.ToolTipIcon.None;
            _notify.ShowBalloonTip(5000);
        }
        catch (Exception ex)
        {
            Log.Debug("tray.balloon", "Could not show a tray notification", ("reason", ex.Message));
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/nasaq.ico"));
            if (resource?.Stream is not null)
            {
                using var stream = resource.Stream;
                return new Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("tray.icon", "Could not load the embedded tray icon", ("reason", ex.Message));
        }

        try
        {
            var executable = Environment.ProcessPath;
            if (executable is not null)
                return Icon.ExtractAssociatedIcon(executable) ?? SystemIcons.Application;
        }
        catch { }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _notify.Visible = false;
            _notify.Dispose();
        }
        catch { }

        try { _menu.Dispose(); } catch { }
        try { _icon.Dispose(); } catch { }
    }
}
