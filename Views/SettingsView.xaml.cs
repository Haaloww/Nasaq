using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Nasaq.Core;
using Nasaq.Services;
using Nasaq.ViewModels;

namespace Nasaq.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshLog();
    }

    public event EventHandler? DoneRequested;

    private MainViewModel? Vm => DataContext as MainViewModel;

    public void SelectTab(int index)
    {
        if (index >= 0 && index < Tabs.Items.Count) Tabs.SelectedIndex = index;
    }

    public void RefreshLog()
    {
        if (LogOutput is null) return;
        try { LogOutput.Text = Log.ReadTail(400); }
        catch (Exception ex) { LogOutput.Text = "(unable to read log: " + ex.Message + ")"; }
        Dispatcher.BeginInvoke(new Action(() => LogScroll?.ScrollToEnd()), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Done_Click(object sender, RoutedEventArgs e) => DoneRequested?.Invoke(this, EventArgs.Empty);

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Select ffmpeg.exe",
            Filter = "FFmpeg executable|ffmpeg.exe|Executables|*.exe|All files|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            Vm.Settings.FfmpegPath = dialog.FileName;
            Vm.RefreshEngine();
        }
    }

    private void AutoDetectFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.Settings.FfmpegPath = "";
        Vm.RefreshEngine();
    }

    private void BrowseYtDlp_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Select yt-dlp.exe",
            Filter = "yt-dlp executable|yt-dlp.exe|Executables|*.exe|All files|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            Vm.Settings.InstallerYtDlpPath = dialog.FileName;
            Vm.Installer.RefreshEngine();
        }
    }

    private void AutoDetectYtDlp_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.Settings.InstallerYtDlpPath = "";
        Vm.Installer.RefreshEngine();
    }

    private void UseOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.Settings.DownloadDirectory = Vm.Settings.OutputDirectory;
    }

    private void OpenToolsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(YtDlpLocator.ManagedDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", YtDlpLocator.ManagedDirectory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn("installer.folder", "Could not open the installer tools folder", ("reason", ex.Message));
        }
    }

    private void RefreshLog_Click(object sender, RoutedEventArgs e) => RefreshLog();

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (Tabs.SelectedIndex == 4) Vm?.Installer.RefreshEngine();
        if (Tabs.SelectedIndex == 3) RefreshLog();
    }
}
