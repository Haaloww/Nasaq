using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Nasaq.Core;
using Nasaq.Services;

namespace Nasaq.ViewModels;

public sealed class InstallerHistoryItem
{
    public InstallerHistoryItem(string path, InstallerMediaMode requestedMode, DateTime time)
    {
        Path = path;
        Time = time;

        var kind = InstallerService.KindFromExtension(path);
        if (kind is InstallerMediaKind.Unknown or InstallerMediaKind.Stream or InstallerMediaKind.Page)
            kind = requestedMode switch
            {
                InstallerMediaMode.Video => InstallerMediaKind.Video,
                InstallerMediaMode.Audio => InstallerMediaKind.Audio,
                InstallerMediaMode.Photo => InstallerMediaKind.Image,
                _ => InstallerMediaKind.Unknown,
            };

        Glyph = kind switch
        {
            InstallerMediaKind.Image => "\uEB9F",
            InstallerMediaKind.Audio => "\uE8D6",
            InstallerMediaKind.Video => "\uE714",
            _ => "\uE896",
        };
        KindLabel = kind switch
        {
            InstallerMediaKind.Image => "Photo",
            InstallerMediaKind.Audio => "Audio",
            InstallerMediaKind.Video => "Video",
            _ => "File",
        };

        long size = 0;
        try { size = new FileInfo(path).Length; }
        catch { }
        Meta = $"{KindLabel} · {FileSize.Format(size)} · {time:HH:mm}";
    }

    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string Meta { get; }
    public string Glyph { get; }
    public string KindLabel { get; }
    public DateTime Time { get; }
}

/// <summary>
/// Drives the Installer page: link intake, download progress, history and the
/// one-time yt-dlp setup. All network work lives in <see cref="InstallerService"/>.
/// </summary>
public sealed class InstallerViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _store;
    private readonly ConversionService _engine;
    private readonly InstallerService _installer;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _engineCts;

    private string _url = "";
    private string _selectedMode = "auto";
    private string _selectedQuality = "best";
    private bool _isBusy;
    private double _progressValue;
    private bool _isIndeterminate;
    private string _statusTitle = "";
    private string _statusDetail = "";
    private string _progressText = "";
    private bool _hasResult;
    private string _resultTitle = "";
    private string _resultFileName = "";
    private string _resultMeta = "";
    private string _resultBadge = "FILE";
    private string _resultGlyph = "\uE896";
    private string _resultPath = "";
    private bool _hasError;
    private string _errorTitle = "";
    private string _errorDetail = "";
    private string _errorHint = "";
    private string _lastNotice = "";
    private bool _engineReady;
    private bool _engineManaged;
    private string _engineTitle = "Checking the installer engine…";
    private string _engineDetail = "";
    private string _engineVersion = "";
    private string _engineSource = "";
    private bool _isEngineBusy;
    private double _engineProgress;
    private bool _engineIndeterminate;
    private string _engineProgressText = "";
    private string _engineError = "";
    private bool _disposed;

    public InstallerViewModel(SettingsStore store, ConversionService engine)
    {
        _store = store;
        _engine = engine;
        _installer = new InstallerService(YtDlpLocator.Locate(store.Settings.InstallerYtDlpPath));
        Settings = store.Settings;

        _selectedMode = ResolveMode(Settings.InstallerLastMode);
        _selectedQuality = ResolveQuality(Settings.InstallerLastQuality);

        PasteCommand = new RelayCommand(Paste, () => !IsBusy);
        ClearUrlCommand = new RelayCommand(ClearUrl, () => !IsBusy);
        InstallCommand = new RelayCommand(() => _ = InstallAsync(), () => CanInstall);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        SetupEngineCommand = new RelayCommand(() => _ = InstallEngineAsync(), () => CanSetupEngine);
        UpdateEngineCommand = new RelayCommand(() => _ = InstallEngineAsync(), () => CanUpdateEngine);
        RefreshEngineCommand = new RelayCommand(RefreshEngine, () => !IsBusy && !IsEngineBusy);
        CancelEngineCommand = new RelayCommand(CancelEngine, () => IsEngineBusy);
        BrowseDownloadFolderCommand = new RelayCommand(BrowseDownloadFolder, () => true);
        OpenDownloadFolderCommand = new RelayCommand(OpenDownloadFolder, () => true);
        OpenResultCommand = new RelayCommand(OpenResult, () => HasResult && File.Exists(ResultPath));
        RevealResultCommand = new RelayCommand(RevealResult, () => HasResult && File.Exists(ResultPath));
        OpenHistoryItemCommand = new RelayCommand(p => OpenPath((p as InstallerHistoryItem)?.Path),
            p => p is InstallerHistoryItem item && File.Exists(item.Path));
        RevealHistoryItemCommand = new RelayCommand(p => RevealPath((p as InstallerHistoryItem)?.Path),
            p => p is InstallerHistoryItem item && File.Exists(item.Path));
        ClearHistoryCommand = new RelayCommand(ClearHistory, () => History.Count > 0);
        ClearErrorCommand = new RelayCommand(ClearError, () => HasError);
        ClearResultCommand = new RelayCommand(ClearResult, () => HasResult);

        History.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasHistory));
            ClearHistoryCommand.RaiseCanExecuteChanged();
        };

        Settings.PropertyChanged += OnSettingsChanged;
        ApplyEngineInfo();
        Log.Info("installer.ready", "Installer view model ready",
            ("engine", _installer.YtDlp.IsAvailable ? _installer.YtDlp.DisplayVersion : "not set up"),
            ("downloads", Settings.DownloadDirectory));
    }

    public AppSettings Settings { get; }
    public ObservableCollection<InstallerHistoryItem> History { get; } = new();

    public IReadOnlyList<InstallerOption> Modes => InstallerCatalog.Modes;
    public IReadOnlyList<InstallerOption> Qualities => InstallerCatalog.Qualities;
    public IReadOnlyList<InstallerOption> Browsers => InstallerCatalog.Browsers;

    public RelayCommand PasteCommand { get; }
    public RelayCommand ClearUrlCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SetupEngineCommand { get; }
    public RelayCommand UpdateEngineCommand { get; }
    public RelayCommand RefreshEngineCommand { get; }
    public RelayCommand CancelEngineCommand { get; }
    public RelayCommand BrowseDownloadFolderCommand { get; }
    public RelayCommand OpenDownloadFolderCommand { get; }
    public RelayCommand OpenResultCommand { get; }
    public RelayCommand RevealResultCommand { get; }
    public RelayCommand OpenHistoryItemCommand { get; }
    public RelayCommand RevealHistoryItemCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }
    public RelayCommand ClearErrorCommand { get; }
    public RelayCommand ClearResultCommand { get; }

    public event Action<InstallerHistoryItem>? DownloadCompleted;

    // ─────────────────────────── link & choices ───────────────────────────

    public string Url
    {
        get => _url;
        set
        {
            if (!Set(ref _url, value ?? "")) return;
            OnPropertyChanged(nameof(LinkHint));
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(IsUrlValid));
            RaiseCommands();
        }
    }

    public bool IsUrlValid => InstallerService.ParseUrl(Url) is not null;

    public string LinkHint
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Url))
                return "Paste a share link, a page link, or a direct media URL.";
            var uri = InstallerService.ParseUrl(Url);
            return uri is null
                ? "That doesn't look like a web link yet."
                : $"Ready to install from {uri.Host}";
        }
    }

    public string SelectedMode
    {
        get => _selectedMode;
        set
        {
            var mode = ResolveMode(value);
            if (!Set(ref _selectedMode, mode)) return;
            if (!string.Equals(Settings.InstallerLastMode, mode, StringComparison.OrdinalIgnoreCase))
                Settings.InstallerLastMode = mode;
            OnPropertyChanged(nameof(CanChooseQuality));
            OnPropertyChanged(nameof(ModeHint));
        }
    }

    public string SelectedQuality
    {
        get => _selectedQuality;
        set
        {
            var quality = ResolveQuality(value);
            if (!Set(ref _selectedQuality, quality)) return;
            if (!string.Equals(Settings.InstallerLastQuality, quality, StringComparison.OrdinalIgnoreCase))
                Settings.InstallerLastQuality = quality;
        }
    }

    public bool CanChooseQuality => InstallerCatalog.ParseMode(SelectedMode) is
        InstallerMediaMode.Auto or InstallerMediaMode.Video;

    public string ModeHint => InstallerCatalog.ParseMode(SelectedMode) switch
    {
        InstallerMediaMode.Video => "Best picture and sound available for the link.",
        InstallerMediaMode.Audio => "Keeps only the soundtrack; MP3 when FFmpeg is available.",
        InstallerMediaMode.Photo => "Finds the main image on the page.",
        _ => "Follows the link: video posts become videos, audio stays audio, photos stay photos.",
    };

    public string InstallButtonText => IsBusy ? "Installing…" : "Install";

    public bool CanInstall => !IsBusy && !IsEngineBusy && IsUrlValid;

    // ─────────────────────────── progress state ───────────────────────────

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(InstallButtonText));
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(CanSetupEngine));
            OnPropertyChanged(nameof(CanUpdateEngine));
            OnPropertyChanged(nameof(CanRecheckEngine));
            RaiseCommands();
        }
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => Set(ref _progressValue, Math.Clamp(value, 0, 100));
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => Set(ref _isIndeterminate, value);
    }

    public string StatusTitle
    {
        get => _statusTitle;
        private set => Set(ref _statusTitle, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => Set(ref _statusDetail, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    public string LastNotice
    {
        get => _lastNotice;
        private set => Set(ref _lastNotice, value);
    }

    public bool IsIdle => !IsBusy && !HasResult && !HasError;

    // ─────────────────────────── result state ───────────────────────────

    public bool HasResult
    {
        get => _hasResult;
        private set
        {
            if (!Set(ref _hasResult, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OpenResultCommand?.RaiseCanExecuteChanged();
            RevealResultCommand?.RaiseCanExecuteChanged();
            ClearResultCommand?.RaiseCanExecuteChanged();
        }
    }

    public string ResultTitle
    {
        get => _resultTitle;
        private set => Set(ref _resultTitle, value);
    }

    public string ResultFileName
    {
        get => _resultFileName;
        private set => Set(ref _resultFileName, value);
    }

    public string ResultMeta
    {
        get => _resultMeta;
        private set => Set(ref _resultMeta, value);
    }

    public string ResultBadge
    {
        get => _resultBadge;
        private set => Set(ref _resultBadge, value);
    }

    public string ResultGlyph
    {
        get => _resultGlyph;
        private set => Set(ref _resultGlyph, value);
    }

    public string ResultPath
    {
        get => _resultPath;
        private set
        {
            if (!Set(ref _resultPath, value)) return;
            OpenResultCommand?.RaiseCanExecuteChanged();
            RevealResultCommand?.RaiseCanExecuteChanged();
        }
    }

    // ─────────────────────────── error state ───────────────────────────

    public bool HasError
    {
        get => _hasError;
        private set
        {
            if (!Set(ref _hasError, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            ClearErrorCommand?.RaiseCanExecuteChanged();
        }
    }

    public string ErrorTitle
    {
        get => _errorTitle;
        private set => Set(ref _errorTitle, value);
    }

    public string ErrorDetail
    {
        get => _errorDetail;
        private set => Set(ref _errorDetail, value);
    }

    public string ErrorHint
    {
        get => _errorHint;
        private set => Set(ref _errorHint, value);
    }

    // ─────────────────────────── engine state ───────────────────────────

    public bool EngineReady
    {
        get => _engineReady;
        private set
        {
            if (!Set(ref _engineReady, value)) return;
            OnPropertyChanged(nameof(CanSetupEngine));
            OnPropertyChanged(nameof(CanUpdateEngine));
            OnPropertyChanged(nameof(EngineBadge));
        }
    }

    public bool EngineManaged
    {
        get => _engineManaged;
        private set
        {
            if (!Set(ref _engineManaged, value)) return;
            OnPropertyChanged(nameof(CanUpdateEngine));
        }
    }

    public string EngineTitle
    {
        get => _engineTitle;
        private set => Set(ref _engineTitle, value);
    }

    public string EngineDetail
    {
        get => _engineDetail;
        private set => Set(ref _engineDetail, value);
    }

    public string EngineVersion
    {
        get => _engineVersion;
        private set => Set(ref _engineVersion, value);
    }

    public string EngineSource
    {
        get => _engineSource;
        private set => Set(ref _engineSource, value);
    }

    public string EngineBadge => EngineReady ? "READY" : "SETUP NEEDED";

    public bool IsEngineBusy
    {
        get => _isEngineBusy;
        private set
        {
            if (!Set(ref _isEngineBusy, value)) return;
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(CanSetupEngine));
            OnPropertyChanged(nameof(CanUpdateEngine));
            OnPropertyChanged(nameof(CanRecheckEngine));
            RaiseCommands();
        }
    }

    public double EngineProgress
    {
        get => _engineProgress;
        private set => Set(ref _engineProgress, Math.Clamp(value, 0, 100));
    }

    public bool EngineIndeterminate
    {
        get => _engineIndeterminate;
        private set => Set(ref _engineIndeterminate, value);
    }

    public string EngineProgressText
    {
        get => _engineProgressText;
        private set => Set(ref _engineProgressText, value);
    }

    public string EngineError
    {
        get => _engineError;
        private set => Set(ref _engineError, value);
    }

    public bool CanSetupEngine => !IsBusy && !IsEngineBusy && !EngineReady;
    public bool CanUpdateEngine => !IsBusy && !IsEngineBusy && EngineReady && EngineManaged;
    public bool CanRecheckEngine => !IsBusy && !IsEngineBusy;
    public bool HasHistory => History.Count > 0;

    public string DownloadDirectory => Settings.DownloadDirectory;
    public string DownloadDirectoryDisplay => FileSize.FormatPath(Settings.DownloadDirectory, 40);

    // ─────────────────────────── actions ───────────────────────────

    private void Paste()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            var uri = InstallerService.ParseUrl(text);
            Url = uri?.AbsoluteUri ?? text.Trim();
            Log.Debug("installer.paste", "Link pasted into the Installer");
        }
        catch (Exception ex)
        {
            Log.Warn("installer.paste", "Could not read the clipboard", ("reason", ex.Message));
        }
    }

    private void ClearUrl()
    {
        Url = "";
        HasResult = false;
        HasError = false;
        LastNotice = "";
        StatusTitle = "";
        StatusDetail = "";
        ProgressText = "";
        ProgressValue = 0;
        IsIndeterminate = false;
    }

    private async Task InstallAsync()
    {
        if (IsBusy || IsEngineBusy) return;
        var uri = InstallerService.ParseUrl(Url);
        if (uri is null)
        {
            ShowError("That doesn't look like a web link.",
                "Paste a link that starts with http:// or https://.",
                "Copy the link from the app or browser and try again.");
            return;
        }

        _cts = new CancellationTokenSource();
        var cts = _cts;
        var token = cts.Token;

        HasResult = false;
        HasError = false;
        LastNotice = "";
        ProgressValue = 0;
        IsIndeterminate = true;
        StatusTitle = "Checking link";
        StatusDetail = uri.Host;
        ProgressText = "";
        IsBusy = true;

        var mode = InstallerCatalog.ParseMode(SelectedMode);
        var request = new InstallerDownloadRequest(
            uri.AbsoluteUri,
            mode,
            SelectedQuality,
            Settings.DownloadDirectory,
            Settings.InstallerCookiesBrowser,
            _engine.Ffmpeg.FfmpegPath,
            Settings.OverwriteExisting,
            Settings.PreserveMetadata,
            Settings.AudioBitrate);

        var progress = new Progress<InstallerProgress>(ApplyProgress);
        try
        {
            var result = await _installer.DownloadAsync(request, progress, token);
            if (result.Canceled)
            {
                IsBusy = false;
                ProgressValue = 0;
                IsIndeterminate = false;
                StatusTitle = "Cancelled";
                StatusDetail = "Nothing was saved.";
                LastNotice = "Download cancelled — nothing was saved.";
                return;
            }

            if (!result.Success)
            {
                ShowError(result.Error ?? "The link couldn't be installed.",
                    result.Detail ?? "",
                    ErrorHintFor(result.Error));
                return;
            }

            ShowResult(result, mode);
        }
        catch (Exception ex)
        {
            Log.Error("installer.fail", "Unexpected Installer error", ex, ("url", uri.Host));
            ShowError("Something went wrong while installing that link.",
                ex.Message,
                "The full details are in Settings → Advanced → Debug log.");
        }
        finally
        {
            IsBusy = false;
            cts.Dispose();
            if (ReferenceEquals(_cts, cts)) _cts = null;
        }
    }

    private void ApplyProgress(InstallerProgress progress)
    {
        if (!IsBusy) return;

        StatusTitle = progress.Stage;
        StatusDetail = progress.Title ?? progress.FileName ?? StatusDetail;
        ProgressValue = progress.Fraction < 0 ? 0 : progress.Fraction * 100;
        IsIndeterminate = progress.Fraction < 0;
        ProgressText = BuildProgressText(progress);
    }

    private static string BuildProgressText(InstallerProgress progress)
    {
        if (progress.Fraction < 0) return "";
        var text = $"{progress.Fraction * 100:0}%";
        if (!string.IsNullOrWhiteSpace(progress.Speed)) text += $" · {progress.Speed}";
        if (!string.IsNullOrWhiteSpace(progress.Eta)) text += $" · {progress.Eta} left";
        return text;
    }

    private void ShowResult(InstallerDownloadResult result, InstallerMediaMode requestedMode)
    {
        var paths = result.OutputPaths.Where(File.Exists).ToList();
        if (paths.Count == 0)
        {
            ShowError("The download finished, but no file was found.",
                "The output folder may have been moved or cleaned while the download was running.",
                "Try the link again.");
            return;
        }

        var first = paths[0];
        var kind = InstallerService.KindFromExtension(first);
        if (kind is InstallerMediaKind.Unknown or InstallerMediaKind.Stream or InstallerMediaKind.Page)
            kind = requestedMode switch
            {
                InstallerMediaMode.Video => InstallerMediaKind.Video,
                InstallerMediaMode.Audio => InstallerMediaKind.Audio,
                InstallerMediaMode.Photo => InstallerMediaKind.Image,
                _ => InstallerMediaKind.Unknown,
            };

        long size = 0;
        foreach (var path in paths)
        {
            try { size += new FileInfo(path).Length; }
            catch { }
        }

        HasError = false;
        HasResult = true;
        ResultPath = first;
        ResultTitle = string.IsNullOrWhiteSpace(result.Title)
            ? System.IO.Path.GetFileNameWithoutExtension(first)
            : result.Title!;
        ResultFileName = paths.Count == 1 ? System.IO.Path.GetFileName(first) : $"{paths.Count} files saved";
        ResultMeta = $"{InstallerCatalog.ModeLabel(requestedMode)} · {FileSize.Format(size)} · {DownloadDirectoryDisplay}";
        ResultBadge = kind switch
        {
            InstallerMediaKind.Image => "PHOTO",
            InstallerMediaKind.Audio => "AUDIO",
            InstallerMediaKind.Video => "VIDEO",
            _ => "FILE",
        };
        ResultGlyph = kind switch
        {
            InstallerMediaKind.Image => "\uEB9F",
            InstallerMediaKind.Audio => "\uE8D6",
            InstallerMediaKind.Video => "\uE714",
            _ => "\uE896",
        };

        ProgressValue = 100;
        IsIndeterminate = false;
        StatusTitle = "Saved";
        StatusDetail = ResultFileName;
        ProgressText = "";

        var now = DateTime.Now;
        foreach (var path in paths)
            AddHistory(path, requestedMode, now);

        var firstItem = History.FirstOrDefault(i =>
            string.Equals(i.Path, first, StringComparison.OrdinalIgnoreCase));
        if (firstItem is not null) DownloadCompleted?.Invoke(firstItem);

        Log.Info("installer.ui", "Installer result shown",
            ("files", paths.Count.ToString()), ("title", ResultTitle));
    }

    private void ShowError(string title, string detail, string? hint = null)
    {
        IsBusy = false;
        HasResult = false;
        HasError = true;
        ErrorTitle = title;
        ErrorDetail = detail;
        ErrorHint = hint ?? "Check that the link opens in a browser, then try again.";
        StatusTitle = "Couldn't install that link";
        StatusDetail = title;
        ProgressValue = 0;
        IsIndeterminate = false;
        ProgressText = "";
    }

    private string ErrorHintFor(string? error)
    {
        if (!EngineReady) return "Website links need the installer engine. Set it up above, then try again.";
        if (error?.Contains("signed-in", StringComparison.OrdinalIgnoreCase) == true)
            return "Choose a browser under Sign-in cookies in Settings → Installer.";
        if (error?.Contains("format", StringComparison.OrdinalIgnoreCase) == true)
            return "Try Best quality, or choose a different media type.";
        return "Check that the link opens in a browser, then try again.";
    }

    private void Cancel()
    {
        if (!IsBusy || _cts is null) return;
        Log.Info("installer.cancel", "Cancellation requested by the user");
        _cts.Cancel();
    }

    private void AddHistory(string path, InstallerMediaMode mode, DateTime time)
    {
        var existing = History.FirstOrDefault(i =>
            string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) History.Remove(existing);

        History.Insert(0, new InstallerHistoryItem(path, mode, time));
        while (History.Count > 8) History.RemoveAt(History.Count - 1);
    }

    private void ClearHistory()
    {
        History.Clear();
        Log.Debug("installer.history", "Installer history cleared");
    }

    private void ClearResult()
    {
        HasResult = false;
        ResultPath = "";
        ResultTitle = "";
        ResultFileName = "";
        ResultMeta = "";
        ProgressValue = 0;
        StatusTitle = "";
        StatusDetail = "";
        ProgressText = "";
    }

    private void ClearError()
    {
        HasError = false;
        ErrorTitle = "";
        ErrorDetail = "";
        ErrorHint = "";
        StatusTitle = "";
        StatusDetail = "";
    }

    // ─────────────────────────── engine ───────────────────────────

    private async Task InstallEngineAsync()
    {
        if (IsEngineBusy || IsBusy) return;

        if (EngineReady && !EngineManaged)
        {
            EngineError = "The active engine is an external yt-dlp copy. Update it with its own installer, or clear the path in Settings → Installer.";
            return;
        }

        _engineCts = new CancellationTokenSource();
        var cts = _engineCts;
        var token = cts.Token;
        EngineError = "";
        EngineProgress = 0;
        EngineIndeterminate = true;
        EngineProgressText = "Contacting GitHub…";
        IsEngineBusy = true;

        var progress = new Progress<InstallerProgress>(p =>
        {
            EngineProgress = p.Fraction < 0 ? 0 : p.Fraction * 100;
            EngineIndeterminate = p.Fraction < 0;
            var text = p.Fraction < 0 ? p.Stage : $"{p.Fraction * 100:0}% · {p.Stage}";
            if (!string.IsNullOrWhiteSpace(p.Speed)) text += $" · {p.Speed}";
            EngineProgressText = text;
        });

        try
        {
            var result = await _installer.InstallEngineAsync(progress, token);
            if (result.Canceled)
            {
                EngineProgressText = "Engine setup cancelled.";
                return;
            }

            if (!result.Success)
            {
                EngineError = result.Error ?? "The installer engine couldn't be downloaded.";
                EngineProgressText = result.Detail ?? EngineError;
                return;
            }

            // The managed copy is now the best choice. Clearing any custom path
            // lets the locator pick it up on the next launch too.
            Settings.InstallerYtDlpPath = "";

            ApplyEngineInfo();
            EngineProgress = 100;
            EngineIndeterminate = false;
            EngineProgressText = "Installer engine ready.";
            Log.Info("installer.engine", "Installer engine setup completed from the UI",
                ("version", EngineVersion));
        }
        catch (Exception ex)
        {
            Log.Error("installer.engine", "Unexpected engine setup error", ex);
            EngineError = "The installer engine couldn't be downloaded.";
            EngineProgressText = ex.Message;
        }
        finally
        {
            IsEngineBusy = false;
            cts.Dispose();
            if (ReferenceEquals(_engineCts, cts)) _engineCts = null;
        }
    }

    private void CancelEngine()
    {
        if (!IsEngineBusy || _engineCts is null) return;
        Log.Info("installer.engine", "Engine setup cancellation requested");
        _engineCts.Cancel();
    }

    public void RefreshEngine()
    {
        try
        {
            _installer.RefreshYtDlp(Settings.InstallerYtDlpPath);
            ApplyEngineInfo();
            Log.Debug("installer.engine", "Installer engine rechecked",
                ("available", EngineReady.ToString()), ("version", EngineVersion));
        }
        catch (Exception ex)
        {
            EngineError = ex.Message;
            Log.Warn("installer.engine", "Could not recheck the installer engine", ("reason", ex.Message));
        }
    }

    private void ApplyEngineInfo()
    {
        var info = _installer.YtDlp;
        EngineReady = info.IsAvailable;
        EngineManaged = info.IsManaged;
        EngineVersion = info.Version ?? "";
        EngineSource = info.Source;
        EngineTitle = info.IsAvailable
            ? $"{info.DisplayVersion} ready"
            : "Installer engine not set up";
        EngineDetail = info.IsAvailable
            ? (info.IsManaged
                ? "Managed by Nasaq. Downloads photos, audio and video from 1,000+ sites."
                : $"External copy found via {info.Source.ToLowerInvariant()}.")
            : "One-time setup (~18 MB). Nasaq uses yt-dlp, an open-source downloader.";
        if (info.IsAvailable) EngineError = "";
        OnPropertyChanged(nameof(CanSetupEngine));
        OnPropertyChanged(nameof(CanUpdateEngine));
        OnPropertyChanged(nameof(CanRecheckEngine));
        OnPropertyChanged(nameof(EngineBadge));
        RaiseCommands();
    }

    // ─────────────────────────── folders & files ───────────────────────────

    private void BrowseDownloadFolder()
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Choose where Nasaq saves installed media",
                InitialDirectory = Directory.Exists(Settings.DownloadDirectory)
                    ? Settings.DownloadDirectory
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            if (dialog.ShowDialog() == true)
            {
                Settings.DownloadDirectory = dialog.FolderName;
                Log.Info("installer.folder", "Download folder changed", ("path", Settings.DownloadDirectory));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("installer.folder", "Folder picker failed", ("reason", ex.Message));
        }
    }

    private void OpenDownloadFolder()
    {
        try
        {
            Directory.CreateDirectory(Settings.DownloadDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", Settings.DownloadDirectory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn("installer.folder", "Could not open the download folder", ("reason", ex.Message));
        }
    }

    private void OpenResult() => OpenPath(ResultPath);

    private void RevealResult() => RevealPath(ResultPath);

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("installer.open", "Could not open a downloaded file",
                ("path", path), ("reason", ex.Message));
        }
    }

    private static void RevealPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn("installer.reveal", "Could not reveal a downloaded file",
                ("path", path), ("reason", ex.Message));
        }
    }

    // ─────────────────────────── settings sync ───────────────────────────

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.DownloadDirectory):
                OnPropertyChanged(nameof(DownloadDirectory));
                OnPropertyChanged(nameof(DownloadDirectoryDisplay));
                break;
            case nameof(AppSettings.InstallerYtDlpPath):
                RefreshEngine();
                break;
            case nameof(AppSettings.InstallerLastMode):
                if (!string.Equals(_selectedMode, Settings.InstallerLastMode, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedMode = ResolveMode(Settings.InstallerLastMode);
                    OnPropertyChanged(nameof(SelectedMode));
                    OnPropertyChanged(nameof(CanChooseQuality));
                    OnPropertyChanged(nameof(ModeHint));
                }
                break;
            case nameof(AppSettings.InstallerLastQuality):
                if (!string.Equals(_selectedQuality, Settings.InstallerLastQuality, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedQuality = ResolveQuality(Settings.InstallerLastQuality);
                    OnPropertyChanged(nameof(SelectedQuality));
                }
                break;
        }
    }

    private static string ResolveMode(string? value) =>
        InstallerCatalog.Modes.Any(m => m.Id.Equals(value, StringComparison.OrdinalIgnoreCase))
            ? value!.ToLowerInvariant()
            : "auto";

    private static string ResolveQuality(string? value) =>
        InstallerCatalog.IsKnownQuality(value) ? value!.ToLowerInvariant() : "best";

    private void RaiseCommands()
    {
        InstallCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        PasteCommand.RaiseCanExecuteChanged();
        ClearUrlCommand.RaiseCanExecuteChanged();
        SetupEngineCommand.RaiseCanExecuteChanged();
        UpdateEngineCommand.RaiseCanExecuteChanged();
        RefreshEngineCommand.RaiseCanExecuteChanged();
        CancelEngineCommand.RaiseCanExecuteChanged();
        OpenResultCommand.RaiseCanExecuteChanged();
        RevealResultCommand.RaiseCanExecuteChanged();
        ClearResultCommand.RaiseCanExecuteChanged();
    }

    public void CancelOnShutdown()
    {
        try { _cts?.Cancel(); } catch { }
        try { _engineCts?.Cancel(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Settings.PropertyChanged -= OnSettingsChanged;
        try { _cts?.Cancel(); } catch { }
        try { _engineCts?.Cancel(); } catch { }
        _cts = null;
        _engineCts = null;
    }
}
