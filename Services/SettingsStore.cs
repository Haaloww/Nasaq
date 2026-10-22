using System.IO;
using System.Text.Json;
using Nasaq.Core;

namespace Nasaq.Services;

public sealed class AppSettings : ObservableObject
{
    private string _themeId = "midnight";
    private string _outputDirectory = DefaultOutputDirectory();
    private int _maxConcurrentTasks = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    private bool _autoStartAfterAdd = true;
    private bool _launchAtStartup;
    private bool _overwriteExisting;
    private bool _preserveMetadata = true;
    private int _imageQuality = 82;
    private int _audioBitrate = 192;
    private int _videoQuality = 23;
    private bool _enableShelfHotkey = true;
    private string _shelfHotkeyId = ShelfHotkeys.Default.Id;
    private bool _showShelfOnCtrlDrag = true;
    private bool _shelfOnTrayMiddleClick;
    private bool _keepRunningInBackground = true;
    private bool _startMinimized;
    private bool _extractAudioFromVideo;
    private bool _animationsEnabled = true;
    private bool _animatedBackground = true;
    private string _ffmpegPath = "";
    private bool _verboseLogging;
    private string _lastImageFormat = "webp";
    private string _lastAudioFormat = "mp3";
    private string _lastVideoFormat = "mp4";
    private string _lastDocumentFormat = "pdf";
    private string _downloadDirectory = DefaultDownloadDirectory();
    private string _installerYtDlpPath = "";
    private string _installerCookiesBrowser = "none";
    private string _installerLastMode = "auto";
    private string _installerLastQuality = "best";

    public string ThemeId { get => _themeId; set => Set(ref _themeId, value); }
    public string OutputDirectory { get => _outputDirectory; set => Set(ref _outputDirectory, value); }
    public int MaxConcurrentTasks { get => _maxConcurrentTasks; set => Set(ref _maxConcurrentTasks, value); }
    public bool AutoStartAfterAdd { get => _autoStartAfterAdd; set => Set(ref _autoStartAfterAdd, value); }
    public bool LaunchAtStartup { get => _launchAtStartup; set => Set(ref _launchAtStartup, value); }
    public bool OverwriteExisting { get => _overwriteExisting; set => Set(ref _overwriteExisting, value); }
    public bool PreserveMetadata { get => _preserveMetadata; set => Set(ref _preserveMetadata, value); }
    public int ImageQuality { get => _imageQuality; set => Set(ref _imageQuality, value); }
    public int AudioBitrate { get => _audioBitrate; set => Set(ref _audioBitrate, value); }
    public int VideoQuality { get => _videoQuality; set => Set(ref _videoQuality, value); }
    public bool EnableShelfHotkey { get => _enableShelfHotkey; set => Set(ref _enableShelfHotkey, value); }
    public string ShelfHotkeyId { get => _shelfHotkeyId; set => Set(ref _shelfHotkeyId, value); }
    public bool ShowShelfOnCtrlDrag { get => _showShelfOnCtrlDrag; set => Set(ref _showShelfOnCtrlDrag, value); }
    public bool ShelfOnTrayMiddleClick { get => _shelfOnTrayMiddleClick; set => Set(ref _shelfOnTrayMiddleClick, value); }
    public bool KeepRunningInBackground { get => _keepRunningInBackground; set => Set(ref _keepRunningInBackground, value); }
    public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }
    public bool ExtractAudioFromVideo { get => _extractAudioFromVideo; set => Set(ref _extractAudioFromVideo, value); }
    public bool AnimationsEnabled { get => _animationsEnabled; set => Set(ref _animationsEnabled, value); }
    public bool AnimatedBackground { get => _animatedBackground; set => Set(ref _animatedBackground, value); }
    public string FfmpegPath { get => _ffmpegPath; set => Set(ref _ffmpegPath, value); }
    public bool VerboseLogging { get => _verboseLogging; set => Set(ref _verboseLogging, value); }
    public string LastImageFormat { get => _lastImageFormat; set => Set(ref _lastImageFormat, value); }
    public string LastAudioFormat { get => _lastAudioFormat; set => Set(ref _lastAudioFormat, value); }
    public string LastVideoFormat { get => _lastVideoFormat; set => Set(ref _lastVideoFormat, value); }
    public string LastDocumentFormat { get => _lastDocumentFormat; set => Set(ref _lastDocumentFormat, value); }
    public string DownloadDirectory { get => _downloadDirectory; set => Set(ref _downloadDirectory, value); }
    public string InstallerYtDlpPath { get => _installerYtDlpPath; set => Set(ref _installerYtDlpPath, value); }
    public string InstallerCookiesBrowser { get => _installerCookiesBrowser; set => Set(ref _installerCookiesBrowser, value); }
    public string InstallerLastMode { get => _installerLastMode; set => Set(ref _installerLastMode, value); }
    public string InstallerLastQuality { get => _installerLastQuality; set => Set(ref _installerLastQuality, value); }

    public static string DefaultOutputDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");
        if (!Directory.Exists(downloads))
            downloads = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(downloads, "Nasaq");
    }

    public static string DefaultDownloadDirectory() => DefaultOutputDirectory();
}

public sealed class SettingsStore
{
    private readonly object _gate = new();
    private System.Threading.Timer? _saveTimer;

    public AppSettings Settings { get; private set; } = new();
    public string FilePath { get; }
    public bool RecoveredFromCorruption { get; private set; }

    public SettingsStore()
    {
        FilePath = Path.Combine(AppInfo.AppDataDirectory, "settings.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Settings = new AppSettings();
                Log.Info("settings.load", "No settings file yet; defaults created",
                    ("path", FilePath));
            }
            else
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions());
                if (loaded is null) throw new InvalidDataException("Settings file contained no data.");
                Settings = loaded;
                Log.Info("settings.load", "Settings loaded", ("path", FilePath));
            }
        }
        catch (Exception ex)
        {
            RecoveredFromCorruption = true;
            Settings = new AppSettings();
            Log.Warn("settings.load", "Settings could not be read; using defaults.",
                ("path", FilePath), ("reason", ex.Message));
            try
            {
                if (File.Exists(FilePath))
                {
                    var backup = FilePath + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}";
                    File.Copy(FilePath, backup, overwrite: true);
                    Log.Info("settings.load", "Previous settings preserved", ("backup", backup));
                }
            }
            catch { }
        }

        Normalise(Settings);
        Settings.PropertyChanged += OnSettingChanged;
        Save();
    }

    private static void Normalise(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.OutputDirectory)) s.OutputDirectory = AppSettings.DefaultOutputDirectory();
        s.OutputDirectory = SafeExpand(s.OutputDirectory);
        if (string.IsNullOrWhiteSpace(s.DownloadDirectory)) s.DownloadDirectory = AppSettings.DefaultDownloadDirectory();
        s.DownloadDirectory = SafeExpand(s.DownloadDirectory);
        s.MaxConcurrentTasks = Math.Clamp(s.MaxConcurrentTasks, 1, 8);
        s.ImageQuality = Math.Clamp(s.ImageQuality, 30, 100);
        s.VideoQuality = Math.Clamp(s.VideoQuality, 14, 35);
        s.AudioBitrate = s.AudioBitrate is 96 or 128 or 192 or 256 or 320 ? s.AudioBitrate : 192;
        if (FormatCatalog.Find(MediaCategory.Image, s.LastImageFormat) is null) s.LastImageFormat = "webp";
        if (FormatCatalog.Find(MediaCategory.Audio, s.LastAudioFormat) is null) s.LastAudioFormat = "mp3";
        if (FormatCatalog.Find(MediaCategory.Video, s.LastVideoFormat) is null) s.LastVideoFormat = "mp4";
        if (FormatCatalog.Find(MediaCategory.Document, s.LastDocumentFormat) is null) s.LastDocumentFormat = "pdf";
        s.ThemeId = ThemeManager.IsKnown(s.ThemeId) ? s.ThemeId : "midnight";
        s.FfmpegPath ??= "";
        s.InstallerYtDlpPath ??= "";
        if (!InstallerCatalog.IsKnownBrowser(s.InstallerCookiesBrowser)) s.InstallerCookiesBrowser = "none";
        else s.InstallerCookiesBrowser = s.InstallerCookiesBrowser.ToLowerInvariant();
        if (!InstallerCatalog.Modes.Any(m => m.Id.Equals(s.InstallerLastMode, StringComparison.OrdinalIgnoreCase)))
            s.InstallerLastMode = "auto";
        else s.InstallerLastMode = s.InstallerLastMode.ToLowerInvariant();
        if (!InstallerCatalog.IsKnownQuality(s.InstallerLastQuality)) s.InstallerLastQuality = "best";
        else s.InstallerLastQuality = s.InstallerLastQuality.ToLowerInvariant();
        s.ShelfHotkeyId = ShelfHotkeys.IsKnown(s.ShelfHotkeyId)
            ? s.ShelfHotkeyId.ToLowerInvariant()
            : ShelfHotkeys.Default.Id;
    }

    private static string SafeExpand(string path)
    {
        try { return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')); }
        catch { return path; }
    }

    private void OnSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.VerboseLogging))
            Log.Verbose = Settings.VerboseLogging;

        if (e.PropertyName == nameof(AppSettings.LaunchAtStartup))
            StartupManager.Apply(Settings.LaunchAtStartup);

        ScheduleSave();
    }

    public void ScheduleSave()
    {
        lock (_gate)
        {
            _saveTimer?.Dispose();
            _saveTimer = new System.Threading.Timer(_ => Save(), null, 450, Timeout.Infinite);
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(AppInfo.AppDataDirectory);
                var json = JsonSerializer.Serialize(Settings, JsonOptions());
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warn("settings.save", "Unable to save settings", ("reason", ex.Message));
            }
        }
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public void Attach()
    {
        Log.Verbose = Settings.VerboseLogging;
        StartupManager.Apply(Settings.LaunchAtStartup);
    }

    public void Flush()
    {
        lock (_gate)
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
        }
        Save();
    }
}
