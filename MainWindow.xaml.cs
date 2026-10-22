using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Nasaq.Core;
using Nasaq.Services;
using Nasaq.ViewModels;
using Nasaq.Views;

namespace Nasaq;

public partial class MainWindow : Window
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x4E51;

    private readonly MainViewModel _vm;
    private readonly SettingsStore _store;
    private readonly DispatcherTimer _dragLeaveTimer;
    private readonly DragShelfWatcher _dragWatcher;
    private readonly TrayIcon _tray;
    private readonly uint _activateMessage;
    private DropShelfWindow? _shelf;
    private HwndSource? _hwnd;
    private Storyboard? _ambient;
    private bool _hotkeyRegistered;
    private bool _allowClose;
    private bool _settingsOpen;
    private bool _installerOpen;
    private bool _editorOpen;
    private FrameworkElement? _currentPage;
    private bool _trayHintShown;
    private DateTime _lastDragOver = DateTime.MinValue;

    public MainWindow(SettingsStore store, MainViewModel vm)
    {
        _store = store;
        _vm = vm;
        InitializeComponent();
        AnimationGate.Adopt(this);
        AnimationGate.Changed += AnimationGate_Changed;
        DataContext = vm;
        _currentPage = MainPage;
        UpdateSectionNav();

        _activateMessage = RegisterWindowMessage("Nasaq.ActivateMainWindow");

        _dragLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _dragLeaveTimer.Tick += (_, _) =>
        {
            if (DateTime.Now - _lastDragOver < TimeSpan.FromMilliseconds(100)) return;
            _dragLeaveTimer.Stop();
            DropOverlay.Visibility = Visibility.Collapsed;
            _vm.IsDragOver = false;
        };

        _tray = new TrayIcon
        {
            MiddleClickOpensShelf = vm.Settings.ShelfOnTrayMiddleClick,
        };
        _tray.OpenRequested += (_, _) => Dispatcher.BeginInvoke(new Action(RestoreWindow));
        _tray.ShelfRequested += (_, _) => Dispatcher.BeginInvoke(new Action(() => ShowShelf("tray")));
        _tray.InstallerRequested += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            RestoreWindow();
            OpenInstaller();
        }));
        _tray.SettingsRequested += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            RestoreWindow();
            OpenSettings(0);
        }));
        _tray.ExitRequested += (_, _) => Dispatcher.BeginInvoke(new Action(ExitApplication));

        _dragWatcher = new DragShelfWatcher { Enabled = vm.Settings.ShowShelfOnCtrlDrag };
        _dragWatcher.Triggered += (_, _) => Dispatcher.BeginInvoke(new Action(ShowShelfForDrag));

        vm.ShelfRequested += (_, _) => ToggleShelf();
        vm.BatchStarted += Vm_BatchStarted;
        vm.BatchCompleted += Vm_BatchCompleted;
        vm.Installer.DownloadCompleted += Vm_DownloadCompleted;
        vm.Settings.PropertyChanged += Settings_PropertyChanged;
        SettingsPage.DoneRequested += (_, _) => ShowQueue();

        Log.Info("window.ready", "Main window created",
            ("theme", vm.Settings.ThemeId),
            ("engine", vm.EngineTitle),
            ("ctrlDrag", _dragWatcher.IsActive.ToString()),
            ("ambient", vm.Settings.AnimatedBackground.ToString()));
    }

    // ───────────────────────── title bar ─────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase) return true;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Appearance_Click(object sender, RoutedEventArgs e) => OpenSettings(1);

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsOpen) ShowQueue();
        else OpenSettings(0);
    }

    private void Installer_Click(object sender, RoutedEventArgs e)
    {
        if (_installerOpen) ShowQueue();
        else OpenInstaller();
    }

    private void Editor_Click(object sender, RoutedEventArgs e) => ShowEditorComingSoon();

    private void Queue_Click(object sender, RoutedEventArgs e) => ShowQueue();

    // ───────────────────────── pages ─────────────────────────

    private void OpenSettings(int tab)
    {
        _settingsOpen = true;
        _installerOpen = false;
        _editorOpen = false;
        SwitchPage(SettingsPage, 3);
        SettingsPage.SelectTab(tab);
        SettingsPage.RefreshLog();
        UpdateFooterButtons();
        Log.Debug("window.settings", "Settings opened", ("tab", tab.ToString()));
    }

    private void OpenInstaller()
    {
        _settingsOpen = false;
        _installerOpen = true;
        _editorOpen = false;
        SwitchPage(InstallerPage, 1);
        InstallerPage.FocusLink();
        UpdateFooterButtons();
        Log.Debug("window.installer", "Installer opened");
    }

    private void ShowEditorComingSoon()
    {
        Log.Debug("window.editor", "Editor requested while marked as coming soon");
        ToastService.Info("Editor is coming soon",
            "The media editor is still being polished. Conversion, the installer and the drop shelf are ready to use.");
    }

    private void ShowQueue()
    {
        _settingsOpen = false;
        _installerOpen = false;
        _editorOpen = false;
        SwitchPage(MainPage, 0);
        UpdateFooterButtons();
    }

    private void SwitchPage(FrameworkElement page, int index)
    {
        var previous = _currentPage;
        if (ReferenceEquals(previous, page))
        {
            page.Visibility = Visibility.Visible;
            AnimatePageIn(page, forward: true);
            return;
        }

        var previousIndex = previous switch
        {
            null => 0,
            _ when ReferenceEquals(previous, InstallerPage) => 1,
            _ when ReferenceEquals(previous, EditorPage) => 2,
            _ when ReferenceEquals(previous, SettingsPage) => 3,
            _ => 0,
        };

        MainPage.Visibility = Visibility.Collapsed;
        InstallerPage.Visibility = Visibility.Collapsed;
        EditorPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
        _currentPage = page;
        AnimatePageIn(page, forward: index >= previousIndex);
        UpdateSectionNav();
    }

    private static void AnimatePageIn(FrameworkElement page, bool forward)
    {
        page.BeginAnimation(OpacityProperty, null);
        if (!AnimationGate.IsEnabled)
        {
            page.Opacity = 1;
            if (page.RenderTransform is System.Windows.Media.TranslateTransform stillTransform)
            {
                stillTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
                stillTransform.X = 0;
            }
            return;
        }

        page.Opacity = 0;
        if (page.RenderTransform is not System.Windows.Media.TranslateTransform transform)
        {
            transform = new System.Windows.Media.TranslateTransform();
            page.RenderTransform = transform;
        }

        transform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        transform.X = forward ? 18 : -18;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Clear the animations once they finish. A held animation keeps the page
        // on an off-screen composition surface; collapsing and re-showing the
        // page can then reuse a stale (and visibly tiled) surface.
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = ease,
        };
        fade.Completed += (_, _) =>
        {
            page.BeginAnimation(OpacityProperty, null);
            page.Opacity = 1;
        };
        page.BeginAnimation(OpacityProperty, fade);

        var slide = new DoubleAnimation(transform.X, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = ease,
        };
        slide.Completed += (_, _) =>
        {
            transform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            transform.X = 0;
        };
        transform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
    }

    private void UpdateFooterButtons()
    {
        FooterSettingsLabel.Text = _settingsOpen ? "Back to queue" : "Settings";
        FooterSettingsIcon.Text = _settingsOpen ? "\uE72B" : "\uE713";
        FooterInstallerLabel.Text = _installerOpen ? "Back to queue" : "Installer";
        FooterInstallerIcon.Text = _installerOpen ? "\uE72B" : "\uE896";
    }

    private void UpdateSectionNav()
    {
        NavQueueButton.Tag = _settingsOpen || _installerOpen || _editorOpen ? null : "Active";
        NavInstallerButton.Tag = _installerOpen ? "Active" : null;
        NavEditorButton.Tag = _editorOpen ? "Active" : null;
    }

    // ───────────────────────── drop handling ─────────────────────────

    private void Window_PreviewDragEnter(object sender, DragEventArgs e) => ShowDropState(e);

    private void Window_PreviewDragOver(object sender, DragEventArgs e) => ShowDropState(e);

    private void ShowDropState(DragEventArgs e)
    {
        // The image preview zone owns drops over its own surface so it can
        // validate file types and show an inline toast without the window-wide
        // overlay getting in the way.
        if (MainPage.IsPointOverImageZone(e))
        {
            _lastDragOver = DateTime.Now;
            _dragLeaveTimer.Stop();
            DropOverlay.Visibility = Visibility.Collapsed;
            _vm.IsDragOver = false;
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        _lastDragOver = DateTime.Now;
        _dragLeaveTimer.Stop();
        if (DropOverlay.Visibility != Visibility.Visible)
        {
            DropOverlay.Visibility = Visibility.Visible;
            _vm.IsDragOver = true;
        }
    }

    private void Window_PreviewDragLeave(object sender, DragEventArgs e)
    {
        _dragLeaveTimer.Stop();
        _dragLeaveTimer.Start();
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        _dragLeaveTimer.Stop();
        DropOverlay.Visibility = Visibility.Collapsed;
        _vm.IsDragOver = false;

        // Leave the drop unhandled over the preview zone: its own Drop handler
        // validates the files, raises a toast for rejects and queues the images.
        if (MainPage.IsPointOverImageZone(e)) return;

        e.Handled = true;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        ShowQueue();
        var added = _vm.AddPaths(paths);
        Log.Info("window.drop", $"Dropped {paths.Length} item(s) on the window",
            ("added", added.ToString()), ("first", paths[0]));
    }

    // ───────────────────────── shelf ─────────────────────────

    private void Shelf_Click(object sender, RoutedEventArgs e) => ToggleShelf();

    private void ToggleShelf()
    {
        _shelf ??= CreateShelf();
        _shelf.Toggle();
    }

    private void ShowShelf(string reason)
    {
        _shelf ??= CreateShelf();
        _shelf.ShowShelf();
        Log.Debug("shelf.show", "Drop shelf shown", ("via", reason));
    }

    private void ShowShelfForDrag()
    {
        if (_shelf is { IsVisible: true }) return;
        ShowShelf("ctrl-drag");
    }

    private DropShelfWindow CreateShelf()
    {
        var shelf = new DropShelfWindow { DataContext = _vm };
        shelf.FilesDropped += paths => Dispatcher.BeginInvoke(() =>
        {
            var added = _vm.AddPaths(paths, startIfIdle: false);
            Log.Info("shelf.queue", $"Shelf added {added} file(s); waiting for Enter / Convert",
                ("added", added.ToString()), ("pending", _vm.PendingCount.ToString()));
        });
        shelf.OpenAppRequested += (_, _) => Dispatcher.BeginInvoke(RestoreWindow);
        return shelf;
    }

    // ───────────────────────── tray & background ─────────────────────────

    public void RestoreWindow()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();

        if (_vm.Settings.AnimatedBackground && AnimationGate.IsEnabled) StartAmbient();

        Log.Debug("window.restore", "Main window restored from the tray");
    }

    public void HideToTray(bool showHint)
    {
        StopAmbient();
        _shelf?.HideShelf();
        Hide();

        if (showHint && !_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloon(
                "Nasaq is still running",
                "Conversions continue in the background. Press Ctrl+Alt+D for the drop shelf, or right-click the tray icon to quit.");
        }

        Log.Info("window.tray", "Main window hidden; Nasaq keeps running in the background");
    }

    private void Vm_BatchStarted(object? sender, EventArgs e)
    {
        if (IsVisible) return;
        var count = _vm.PendingCount;
        if (count <= 0) return;

        _tray.ShowBalloon(
            "Converting in the background",
            $"{count} {(count == 1 ? "file is" : "files are")} being converted. Nasaq will notify you when the batch finishes.");
    }

    private void Vm_BatchCompleted(object? sender, EventArgs e)
    {
        if (IsVisible) return;
        var failed = _vm.FailedCount;
        _tray.ShowBalloon(
            failed == 0 ? "Conversion finished" : "Conversion finished with errors",
            failed == 0
                ? $"{_vm.DoneCount} {(_vm.DoneCount == 1 ? "file is" : "files are")} ready in {FileSize.FormatPath(_vm.Settings.OutputDirectory, 38)}"
                : $"{_vm.DoneCount} done · {failed} failed. Open Nasaq for details.");
    }

    private void Vm_DownloadCompleted(InstallerHistoryItem item)
    {
        if (IsVisible) return;
        _tray.ShowBalloon(
            "Download finished",
            $"{item.FileName} is ready in {FileSize.FormatPath(_vm.Settings.DownloadDirectory, 38)}");
    }

    // ───────────────────────── hotkey ─────────────────────────

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            _hwnd = HwndSource.FromHwnd(handle);
            _hwnd?.AddHook(WndProc);
            ApplyHotkey();
        }
        catch (Exception ex)
        {
            Log.Warn("hotkey.init", "Global hotkey could not be initialised", ("reason", ex.Message));
        }
    }

    private void ApplyHotkey()
    {
        var handle = _hwnd?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        if (_hotkeyRegistered)
        {
            UnregisterHotKey(handle, HotkeyId);
            _hotkeyRegistered = false;
        }

        if (!_vm.Settings.EnableShelfHotkey)
        {
            _vm.HotkeyRegistered = false;
            return;
        }

        var hotkey = ShelfHotkeys.Find(_vm.Settings.ShelfHotkeyId);
        _hotkeyRegistered = RegisterHotKey(handle, HotkeyId, hotkey.Modifiers, hotkey.VirtualKey);
        _vm.HotkeyRegistered = _hotkeyRegistered;
        if (_hotkeyRegistered)
            Log.Info("hotkey.register", "Drop shelf hotkey registered", ("shortcut", hotkey.ShortLabel));
        else
            Log.Warn("hotkey.register", $"{hotkey.ShortLabel} is already in use. The drop shelf button still works.",
                ("win32", Marshal.GetLastWin32Error().ToString()));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Dispatcher.BeginInvoke(() =>
            {
                Log.Debug("hotkey.pressed", "Drop shelf hotkey pressed");
                ToggleShelf();
            });
        }
        else if (_activateMessage != 0 && (uint)msg == _activateMessage)
        {
            handled = true;
            Dispatcher.BeginInvoke(RestoreWindow);
        }
        return IntPtr.Zero;
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.EnableShelfHotkey):
                ApplyHotkey();
                break;
            case nameof(AppSettings.ShelfHotkeyId):
                ApplyHotkey();
                break;
            case nameof(AppSettings.ShowShelfOnCtrlDrag):
                _dragWatcher.Enabled = _vm.Settings.ShowShelfOnCtrlDrag;
                break;
            case nameof(AppSettings.ShelfOnTrayMiddleClick):
                _tray.MiddleClickOpensShelf = _vm.Settings.ShelfOnTrayMiddleClick;
                break;
            case nameof(AppSettings.AnimatedBackground):
                if (_vm.Settings.AnimatedBackground && AnimationGate.IsEnabled && IsVisible) StartAmbient();
                else StopAmbient();
                break;
            case nameof(AppSettings.AnimationsEnabled):
                if (!_vm.Settings.AnimationsEnabled)
                {
                    StopAmbient();
                }
                else if (_vm.Settings.AnimatedBackground && IsVisible)
                {
                    StartAmbient();
                }
                break;
        }
    }

    // ───────────────────────── ambient background ─────────────────────────

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_vm.Settings.AnimatedBackground && AnimationGate.IsEnabled && IsVisible) StartAmbient();
    }

    private void StartAmbient()
    {
        if (!AnimationGate.IsEnabled) return;
        if (_ambient is not null) return;
        if (FindResource("AmbientStoryboard") is not Storyboard storyboard) return;

        _ambient = storyboard;
        try
        {
            storyboard.Begin(this, true);
            Log.Debug("ui.ambient", "Animated background started");
        }
        catch (Exception ex)
        {
            _ambient = null;
            Log.Warn("ui.ambient", "Animated background could not start", ("reason", ex.Message));
        }
    }

    private void StopAmbient()
    {
        if (_ambient is null) return;
        _ambient.Remove(this);
        _ambient = null;
        Log.Debug("ui.ambient", "Animated background stopped");
    }

    private void AnimationGate_Changed(object? sender, EventArgs e)
    {
        if (AnimationGate.IsEnabled) return;

        StopAmbient();
        SnapPage(MainPage);
        SnapPage(InstallerPage);
        SnapPage(EditorPage);
        SnapPage(SettingsPage);
    }

    private static void SnapPage(FrameworkElement page)
    {
        page.BeginAnimation(OpacityProperty, null);
        page.Opacity = 1;
        if (page.RenderTransform is System.Windows.Media.TranslateTransform transform)
        {
            transform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            transform.X = 0;
        }
    }

    // ───────────────────────── keyboard & lifecycle ─────────────────────────

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (_settingsOpen || _installerOpen || _editorOpen))
        {
            ShowQueue();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.O:
                    if (_editorOpen)
                    {
                        _vm.Editor.PickCurrentCommand.Execute(null);
                    }
                    else
                    {
                        if (_settingsOpen || _installerOpen) ShowQueue();
                        _vm.BrowseFiles();
                    }
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (_editorOpen) _vm.Editor.ExportCommand.Execute(null);
                    else _ = _vm.StartAsync();
                    e.Handled = true;
                    break;
                case Key.I:
                    if (_installerOpen) ShowQueue();
                    else OpenInstaller();
                    e.Handled = true;
                    break;
                case Key.E:
                    ShowEditorComingSoon();
                    e.Handled = true;
                    break;
                case Key.OemComma:
                    if (_settingsOpen) ShowQueue();
                    else OpenSettings(0);
                    e.Handled = true;
                    break;
            }
        }
        else if (e.Key == Key.F1)
        {
            OpenSettings(5);
            e.Handled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            if (_vm.Settings.KeepRunningInBackground)
            {
                e.Cancel = true;
                HideToTray(showHint: true);
                return;
            }

            if (!ConfirmStopBatch())
            {
                e.Cancel = true;
                return;
            }

            _allowClose = true;
        }

        _vm.FlushSettings();
        _store.Flush();
        Log.Info("app.closing", "Main window closing");
        base.OnClosing(e);
    }

    public void ExitApplication()
    {
        if (_allowClose) return;
        if (!ConfirmStopBatch()) return;

        _allowClose = true;
        _vm.FlushSettings();
        Close();
    }

    private bool ConfirmStopBatch()
    {
        var converting = _vm.IsConverting;
        var installing = _vm.Installer.IsBusy || _vm.Installer.IsEngineBusy;
        var editing = _vm.Editor.IsBusy;
        if (!converting && !installing && !editing) return true;
        if (!IsVisible) RestoreWindow();

        var what = converting && installing
            ? "A conversion batch and an installer task are still running."
            : editing && !converting && !installing
                ? "An edit is still being exported."
                : converting
                    ? "A conversion batch is still running."
                    : "A download or installer setup is still running.";
        var result = MessageBox.Show(this,
            $"{what}\n\nQuit Nasaq and stop it?",
            "Nasaq", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return false;

        if (converting) _vm.CancelOnShutdown();
        if (installing) _vm.Installer.CancelOnShutdown();
        if (editing) _vm.Editor.CancelOnShutdown();
        return true;
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        try
        {
            if (_hotkeyRegistered && _hwnd is not null)
                UnregisterHotKey(_hwnd.Handle, HotkeyId);
            _hwnd?.RemoveHook(WndProc);
        }
        catch { }

        StopAmbient();
        AnimationGate.Changed -= AnimationGate_Changed;
        _vm.BatchStarted -= Vm_BatchStarted;
        _vm.BatchCompleted -= Vm_BatchCompleted;
        _vm.Installer.DownloadCompleted -= Vm_DownloadCompleted;
        _vm.Installer.Dispose();
        _vm.Editor.Dispose();
        _dragWatcher.Dispose();
        _tray.Dispose();
        _shelf?.Close();

        Log.Info("app.exit", "Main window closed; shutting Nasaq down");
        Application.Current?.Shutdown();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);
}
