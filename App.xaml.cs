using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Nasaq.Core;
using Nasaq.Services;
using Nasaq.ViewModels;

namespace Nasaq;

public partial class App : Application
{
    private Mutex? _instanceMutex;

    public static new App Current => (App)Application.Current;

    public SettingsStore Store { get; private set; } = null!;
    public ConversionService Engine { get; private set; } = null!;
    public MainViewModel ViewModel { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Init(Path.Combine(AppInfo.AppDataDirectory, "logs"));

        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            await RunSelfTestAsync(e.Args);
            return;
        }

        Log.Info("app.start", "Nasaq is starting",
            ("version", AppInfo.Version),
            ("args", string.Join(" ", e.Args)));

        if (!AcquireSingleInstance()) return;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            Store = new SettingsStore();
            Log.Verbose = Store.Settings.VerboseLogging;
            AnimationGate.Apply(Store.Settings.AnimationsEnabled);

            ThemeManager.Apply(Store.Settings.ThemeId);
            Store.Settings.PropertyChanged += OnSettingChanged;
            Store.Attach();

            Engine = new ConversionService(FfmpegLocator.Locate(Store.Settings.FfmpegPath));
            ViewModel = new MainViewModel(Store, Engine);

            var window = new MainWindow(Store, ViewModel);
            MainWindow = window;
            if (Store.Settings.StartMinimized)
            {
                window.Opacity = 0;
                window.Show();
                window.HideToTray(showHint: false);
                window.Opacity = 1;
                Log.Info("app.background", "Nasaq started hidden in the tray");
            }
            else
            {
                window.Show();
            }

            Log.Info("app.ready", "Nasaq is ready",
                ("theme", Store.Settings.ThemeId),
                ("output", Store.Settings.OutputDirectory),
                ("concurrency", Store.Settings.MaxConcurrentTasks.ToString()),
                ("hotkey", ViewModel.HotkeyHint),
                ("background", Store.Settings.KeepRunningInBackground.ToString()));
        }
        catch (Exception ex)
        {
            var report = Log.WriteCrash(ex, "Application startup", true);
            MessageBox.Show(
                "Nasaq could not start.\n\n" +
                ex.Message + "\n\n" +
                "A formatted crash report was saved to:\n" + report,
                "Nasaq — Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task RunSelfTestAsync(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        var directory = index >= 0 && index + 1 < args.Length
            ? args[index + 1]
            : Path.Combine(Path.GetTempPath(), "NasaqSelfTest");

        var code = 0;
        try
        {
            var ok = await SelfTestRunner.RunAsync(directory);
            code = ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error("selftest.fail", "Self-test crashed", ex);
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "selftest-report.txt"),
                    "NASAQ SELF-TEST\n\n  FATAL: " + ex);
            }
            catch { }
            code = 2;
        }
        Shutdown(code);
    }

    private bool AcquireSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(true, @"Local\Nasaq.SingleInstance", out var createdNew);
            if (createdNew) return true;

            Log.Warn("app.instance", "Nasaq is already running; activating the existing window.");
            ActivateExistingWindow();
            Shutdown();
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("app.instance", "Single-instance check failed; continuing.", ("reason", ex.Message));
            return true;
        }
    }

    private static void ActivateExistingWindow()
    {
        try
        {
            var handle = FindWindow(null, "Nasaq");
            if (handle == IntPtr.Zero) return;

            // The first instance may be living in the tray with its window hidden.
            // Ask it to restore itself; the Win32 calls are a fallback for older builds.
            var message = RegisterWindowMessage("Nasaq.ActivateMainWindow");
            if (message != 0) PostMessage(handle, message, IntPtr.Zero, IntPtr.Zero);

            ShowWindow(handle, 9); // SW_RESTORE
            SetForegroundWindow(handle);
        }
        catch { }
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ThemeId))
        {
            ThemeManager.Apply(Store.Settings.ThemeId);
            Log.Info("theme.change", "Theme changed", ("theme", Store.Settings.ThemeId));
        }
        else if (e.PropertyName == nameof(AppSettings.VerboseLogging))
        {
            Log.Verbose = Store.Settings.VerboseLogging;
            Log.Info("logging.verbose", "Verbose logging changed", ("enabled", Log.Verbose.ToString()));
        }
        else if (e.PropertyName == nameof(AppSettings.AnimationsEnabled))
        {
            AnimationGate.Apply(Store.Settings.AnimationsEnabled);
            Log.Info("ui.motion", "Interface motion changed",
                ("enabled", Store.Settings.AnimationsEnabled.ToString()));
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var report = Log.WriteCrash(e.Exception, "WPF dispatcher", true);
        e.Handled = true;
        try
        {
            MessageBox.Show(
                "Nasaq hit an unexpected error and needs to close.\n\n" +
                e.Exception.Message + "\n\n" +
                "A formatted crash report was saved to:\n" + report,
                "Nasaq — Crash report", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
        Shutdown(1);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception
                 ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown fatal error");
        var report = Log.WriteCrash(ex, "AppDomain unhandled", e.IsTerminating);
        if (!e.IsTerminating) return;
        try
        {
            MessageBox.Show(
                "A fatal error stopped Nasaq.\n\n" + ex.Message +
                "\n\nA formatted crash report was saved to:\n" + report,
                "Nasaq — Fatal error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.WriteCrash(e.Exception, "Unobserved background task", false);
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Store?.Flush();
            Log.Info("app.stop", "Nasaq stopped", ("exitCode", e.ApplicationExitCode.ToString()));
        }
        catch { }

        try
        {
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch { }

        base.OnExit(e);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
