using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using Nasaq.Core;
using Nasaq.Services;

namespace Nasaq.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly ConversionService _engine;
    private CancellationTokenSource? _cts;
    private bool _isConverting;
    private bool _holdAutoStart;
    private bool _isDragOver;
    private bool _hotkeyRegistered;
    private int _pendingCount;
    private int _doneCount;
    private int _failedCount;
    private int _totalCount;
    private string _engineTitle = "Checking for FFmpeg…";
    private string _engineDetail = "";
    private bool _engineAvailable;

    public MainViewModel(SettingsStore store, ConversionService engine)
    {
        _store = store;
        _engine = engine;
        Settings = store.Settings;
        Installer = new InstallerViewModel(store, engine);
        Editor = new EditorViewModel(store, engine);

        AddFilesCommand = new RelayCommand(BrowseFiles, () => true);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => true);
        OpenOutputCommand = new RelayCommand(OpenOutput, () => true);
        RemoveCommand = new RelayCommand(p => Remove(p as QueueItem), p => p is QueueItem { IsRemovable: true });
        ClearFinishedCommand = new RelayCommand(ClearFinished, () => Items.Any(i => !i.IsQueued && !i.IsConverting));
        ConvertCommand = new RelayCommand(() => _ = StartAsync(), () => !IsConverting && PendingCount > 0);
        CancelCommand = new RelayCommand(Cancel, () => IsConverting);
        OpenLogsCommand = new RelayCommand(Log.OpenLogFolder, () => true);
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics, () => true);
        RefreshEngineCommand = new RelayCommand(RefreshEngine, () => true);
        ShowShelfCommand = new RelayCommand(() => ShelfRequested?.Invoke(this, EventArgs.Empty), () => true);

        Items.CollectionChanged += (_, _) => UpdateCounts();
        Settings.PropertyChanged += OnSettingsChanged;
        ApplyEngineInfo(_engine.Ffmpeg);
        UpdateCounts();
    }

    public AppSettings Settings { get; }
    public InstallerViewModel Installer { get; }
    public EditorViewModel Editor { get; }
    public ObservableCollection<QueueItem> Items { get; } = new();
    public IReadOnlyList<ThemeInfo> Themes => ThemeManager.Themes;
    public IReadOnlyList<OutputFormat> ImageFormats => FormatCatalog.ImageFormats;
    public IReadOnlyList<OutputFormat> AudioFormats => FormatCatalog.AudioFormats;
    public IReadOnlyList<OutputFormat> VideoFormats => FormatCatalog.VideoFormats;
    public IReadOnlyList<OutputFormat> DocumentFormats => FormatCatalog.DocumentFormats;

    public RelayCommand AddFilesCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ClearFinishedCommand { get; }
    public RelayCommand ConvertCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }
    public RelayCommand RefreshEngineCommand { get; }
    public RelayCommand ShowShelfCommand { get; }

    public event EventHandler? ShelfRequested;
    public event EventHandler? BatchStarted;
    public event EventHandler? BatchCompleted;

    public bool IsConverting
    {
        get => _isConverting;
        private set
        {
            if (!Set(ref _isConverting, value)) return;
            OnPropertyChanged(nameof(ConvertButtonText));
            OnPropertyChanged(nameof(CanConvert));
            OnPropertyChanged(nameof(ShelfQueueStatus));
            OnPropertyChanged(nameof(CanAddFiles));
            RaiseCommands();
        }
    }

    public bool IsDragOver
    {
        get => _isDragOver;
        set => Set(ref _isDragOver, value);
    }

    public bool HotkeyRegistered
    {
        get => _hotkeyRegistered;
        set
        {
            if (!Set(ref _hotkeyRegistered, value)) return;
            OnPropertyChanged(nameof(HotkeyHint));
        }
    }

    public string HotkeyHint => !Settings.EnableShelfHotkey
        ? "Hotkey off"
        : HotkeyRegistered
            ? ShelfHotkeys.Find(Settings.ShelfHotkeyId).ShortLabel
            : "Hotkey unavailable";

    public IReadOnlyList<ShelfHotkey> HotkeyChoices => ShelfHotkeys.All;

    public int PendingCount { get => _pendingCount; private set { if (Set(ref _pendingCount, value)) { OnPropertyChanged(nameof(ConvertButtonText)); OnPropertyChanged(nameof(CanConvert)); OnPropertyChanged(nameof(ShelfQueueStatus)); } } }
    public int DoneCount { get => _doneCount; private set => Set(ref _doneCount, value); }
    public int FailedCount { get => _failedCount; private set => Set(ref _failedCount, value); }
    public int TotalCount { get => _totalCount; private set => Set(ref _totalCount, value); }

    public bool HasItems => TotalCount > 0;
    public bool CanAddFiles => true;

    public string QueueSummary
    {
        get
        {
            if (TotalCount == 0) return "No files queued";
            var parts = new List<string> { $"{TotalCount} {(TotalCount == 1 ? "file" : "files")}" };
            if (DoneCount > 0) parts.Add($"{DoneCount} done");
            if (FailedCount > 0) parts.Add($"{FailedCount} failed");
            if (PendingCount > 0 && DoneCount + FailedCount > 0) parts.Add($"{PendingCount} waiting");
            return string.Join("  ·  ", parts);
        }
    }

    public string ConvertButtonText => IsConverting
        ? "Converting…"
        : PendingCount == 0 ? "Convert" : $"Convert {PendingCount}";

    public bool CanConvert => !IsConverting && PendingCount > 0;

    public string ShelfQueueStatus
    {
        get
        {
            if (IsConverting && PendingCount > 0)
                return $"{PendingCount} waiting · converting…";
            if (IsConverting) return "Converting in the background…";
            if (PendingCount == 0) return "Drop files to add them to the queue";
            return PendingCount == 1
                ? "1 file ready · press Enter or Convert"
                : $"{PendingCount} files ready · press Enter or Convert";
        }
    }

    public string OutputDisplay
    {
        get
        {
            var path = string.IsNullOrWhiteSpace(Settings.OutputDirectory)
                ? AppSettings.DefaultOutputDirectory()
                : Settings.OutputDirectory;
            return FileSize.FormatPath(path, 44);
        }
    }

    public string OutputDirectoryFull => Settings.OutputDirectory;
    public string ShelfOutputDisplay => FileSize.FormatPath(Settings.OutputDirectory, 32);

    public string EngineTitle { get => _engineTitle; private set => Set(ref _engineTitle, value); }
    public string EngineDetail { get => _engineDetail; private set => Set(ref _engineDetail, value); }
    public bool EngineAvailable { get => _engineAvailable; private set => Set(ref _engineAvailable, value); }

    public string VersionText => $"Version {AppInfo.Version}";
    public string AuthorName => AppInfo.Author;
    public string Tagline => AppInfo.Tagline;
    public string LogDirectoryPath => Log.DirectoryPath;
    public string RuntimeText => $".NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
    public string OsText => System.Runtime.InteropServices.RuntimeInformation.OSDescription;
    public string FfmpegPathDisplay => string.IsNullOrWhiteSpace(Settings.FfmpegPath)
        ? (EngineAvailable ? _engine.Ffmpeg.FfmpegPath ?? "Auto-detected" : "Not found — install FFmpeg or browse to ffmpeg.exe")
        : Settings.FfmpegPath;
    public string ConcurrencyText => $"{Settings.MaxConcurrentTasks} {(Settings.MaxConcurrentTasks == 1 ? "task" : "tasks")} at a time";
    public string ThemeName => ThemeManager.Themes.FirstOrDefault(t => t.Id == Settings.ThemeId)?.Name ?? "Midnight";
    public IReadOnlyList<int> AudioBitrates { get; } = new[] { 96, 128, 192, 256, 320 };

    public string ShelfFormatsSummary
    {
        get
        {
            var image = FormatCatalog.Find(MediaCategory.Image, Settings.LastImageFormat)?.Label ?? "PNG";
            var audio = FormatCatalog.Find(MediaCategory.Audio, Settings.LastAudioFormat)?.Label ?? "MP3";
            var video = Settings.ExtractAudioFromVideo
                ? audio
                : FormatCatalog.Find(MediaCategory.Video, Settings.LastVideoFormat)?.Label ?? "MP4";
            var doc = FormatCatalog.Find(MediaCategory.Document, Settings.LastDocumentFormat)?.Label ?? "PDF";
            return $"{image}  ·  {audio}  ·  {video}  ·  {doc}";
        }
    }

    // ─────────────────────────── file intake ───────────────────────────

    public void BrowseFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add files to Nasaq",
            Multiselect = true,
            CheckFileExists = true,
            Filter = FormatCatalog.BuildFileDialogFilter(),
        };
        if (dialog.ShowDialog() == true) AddPaths(dialog.FileNames);
    }

    public int AddPaths(IEnumerable<string> inputs, bool startIfIdle = true)
    {
        var files = Expand(inputs);
        var added = 0;
        foreach (var file in files)
        {
            if (Items.Any(i => string.Equals(i.SourcePath, file, StringComparison.OrdinalIgnoreCase))) continue;

            MediaCategory category;
            long size = 0;
            try
            {
                category = FormatCatalog.Detect(file);
                size = new FileInfo(file).Length;
            }
            catch (Exception ex)
            {
                Log.Warn("files.add", "Skipped an unreadable file", ("path", file), ("reason", ex.Message));
                continue;
            }

            var target = ResolveTarget(category);
            var item = new QueueItem(file, category, size, target,
                category == MediaCategory.Unknown ? JobState.Skipped : JobState.Queued);
            if (category == MediaCategory.Unknown)
                item.MarkSkipped($"Unsupported file type ({Path.GetExtension(file)})");

            item.PropertyChanged += OnItemPropertyChanged;
            Items.Add(item);
            added++;
        }

        UpdateCounts();
        if (added > 0)
        {
            Log.Info("files.add", $"Added {added} file(s) to the queue",
                ("files", string.Join(", ", files.Take(8).Select(Path.GetFileName)) + (files.Count > 8 ? "…" : "")));

            if (!startIfIdle)
            {
                // Files dropped on the shelf wait for an explicit Enter / Convert,
                // even when the global AutoStartAfterAdd option is on.
                _holdAutoStart = true;
                Log.Debug("queue.hold", "Shelf items are waiting for an explicit convert",
                    ("queued", PendingCount.ToString()));
            }
            else if (Settings.AutoStartAfterAdd && !IsConverting && Items.Any(i => i.IsQueued))
            {
                _ = StartAsync();
            }
        }
        else if (files.Count == 0)
        {
            Log.Debug("files.add", "No new files were found in the drop.");
        }

        return added;
    }

    public List<string> Expand(IEnumerable<string> inputs)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const int limit = 2000;

        void AddFile(string file)
        {
            if (result.Count >= limit) return;
            try
            {
                var full = Path.GetFullPath(file);
                if (seen.Add(full)) result.Add(full);
            }
            catch { }
        }

        var stack = new Stack<string>(inputs);
        while (stack.Count > 0 && result.Count < limit)
        {
            var path = stack.Pop();
            try
            {
                if (File.Exists(path)) AddFile(path);
                else if (Directory.Exists(path))
                {
                    foreach (var child in Directory.EnumerateDirectories(path))
                        stack.Push(child);
                    foreach (var file in Directory.EnumerateFiles(path))
                        AddFile(file);
                }
            }
            catch (UnauthorizedAccessException)
            {
                Log.Warn("files.expand", "Access denied while scanning a folder", ("path", path));
            }
            catch (Exception ex)
            {
                Log.Warn("files.expand", "Could not read a dropped item", ("path", path), ("reason", ex.Message));
            }
        }

        if (result.Count >= limit)
            Log.Warn("files.expand", $"The drop was capped at {limit} files.");

        return result;
    }

    // ─────────────────────────── conversion ───────────────────────────

    public async Task StartAsync()
    {
        if (IsConverting) return;
        var jobs = Items.Where(i => i.IsQueued).ToList();
        if (jobs.Count == 0) return;

        // A start is explicit (or a normal auto-start): release the shelf hold.
        _holdAutoStart = false;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsConverting = true;
        RaiseCommands();
        BatchStarted?.Invoke(this, EventArgs.Empty);

        var options = new ConversionOptions(
            Settings.OutputDirectory,
            Settings.ImageQuality,
            Settings.AudioBitrate,
            Settings.VideoQuality,
            Settings.OverwriteExisting,
            Settings.PreserveMetadata);

        var concurrency = Math.Clamp(Settings.MaxConcurrentTasks, 1, 8);
        Log.Info("batch.start", $"Starting batch of {jobs.Count} file(s)",
            ("concurrency", concurrency.ToString()),
            ("output", Settings.OutputDirectory),
            ("formats", $"{Settings.LastImageFormat}/{Settings.LastAudioFormat}/{Settings.LastVideoFormat}/{Settings.LastDocumentFormat}"),
            ("extractAudio", Settings.ExtractAudioFromVideo.ToString()));

        using var gate = new SemaphoreSlim(concurrency, concurrency);
        try
        {
            await Task.WhenAll(jobs.Select(job => ProcessAsync(job, gate, options, token)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("batch.fail", "The conversion batch ended unexpectedly", ex);
        }
        finally
        {
            if (token.IsCancellationRequested)
            {
                foreach (var item in jobs.Where(i => i.IsQueued)) item.MarkCanceled();
            }

            IsConverting = false;
            _cts.Dispose();
            _cts = null;
            UpdateCounts();
            RaiseCommands();

            Log.Info("batch.done", "Batch finished",
                ("done", DoneCount.ToString()),
                ("failed", FailedCount.ToString()),
                ("queued", PendingCount.ToString()));

            if (!token.IsCancellationRequested && PendingCount == 0 && DoneCount + FailedCount > 0)
                BatchCompleted?.Invoke(this, EventArgs.Empty);

            if (!_holdAutoStart && Settings.AutoStartAfterAdd && !token.IsCancellationRequested && Items.Any(i => i.IsQueued))
                _ = StartAsync();
        }
    }

    private async Task ProcessAsync(QueueItem item, SemaphoreSlim gate, ConversionOptions options, CancellationToken token)
    {
        try
        {
            await gate.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            item.MarkCanceled();
            return;
        }

        try
        {
            if (token.IsCancellationRequested)
            {
                item.MarkCanceled();
                return;
            }

            item.MarkConverting();
            var progress = new Progress<ConversionProgress>(p => item.UpdateProgress(p.Fraction * 100, p.Stage));
            Log.Info("job.start", $"Processing {item.FileName}", ("job", item.Id), ("target", item.Target.Label));

            var result = await _engine.ConvertAsync(item.SourcePath, item.Target, options, progress, token);
            if (result.Success && result.OutputPath is not null)
            {
                item.MarkCompleted(result.OutputPath, result.Engine, result.Elapsed);
            }
            else if (result.Canceled)
            {
                item.MarkCanceled();
            }
            else
            {
                item.MarkFailed(result.Error ?? "Conversion failed.", result.Detail);
            }
        }
        catch (OperationCanceledException)
        {
            item.MarkCanceled();
        }
        catch (Exception ex)
        {
            item.MarkFailed("Unexpected error — see the log for details.", ex.Message);
            Log.Error("job.fail", "Unhandled error while converting a queued file", ex,
                ("job", item.Id), ("source", item.SourcePath));
        }
        finally
        {
            gate.Release();
            UpdateCounts();
        }
    }

    public void Cancel()
    {
        if (!IsConverting || _cts is null) return;
        Log.Info("batch.cancel", "Cancellation requested by the user");
        _cts.Cancel();
    }

    public void CancelOnShutdown() => _cts?.Cancel();

    // ─────────────────────────── queue helpers ───────────────────────────

    private OutputFormat ResolveTarget(MediaCategory category)
    {
        // When video-to-audio is enabled, videos use the Audio target and
        // ConversionService extracts the soundtrack with FFmpeg (-vn).
        if (category == MediaCategory.Video && Settings.ExtractAudioFromVideo)
        {
            return FormatCatalog.Find(MediaCategory.Audio, Settings.LastAudioFormat)
                   ?? FormatCatalog.AudioFormats[0];
        }

        var preferred = category switch
        {
            MediaCategory.Image => Settings.LastImageFormat,
            MediaCategory.Audio => Settings.LastAudioFormat,
            MediaCategory.Video => Settings.LastVideoFormat,
            MediaCategory.Document => Settings.LastDocumentFormat,
            _ => "png",
        };
        return FormatCatalog.Find(category, preferred)
               ?? FormatCatalog.For(category).FirstOrDefault()
               ?? FormatCatalog.ImageFormats[0];
    }

    private void ApplyTargets(MediaCategory category)
    {
        var target = ResolveTarget(category);
        foreach (var item in Items.Where(i => i.Category == category && i.IsQueued))
            item.Target = target;
    }

    private void Remove(QueueItem? item)
    {
        if (item is null || !item.IsRemovable) return;
        item.PropertyChanged -= OnItemPropertyChanged;
        Items.Remove(item);
        UpdateCounts();
        Log.Debug("queue.remove", "Removed an item from the queue", ("file", item.FileName));
    }

    private void ClearFinished()
    {
        var removable = Items.Where(i => !i.IsQueued && !i.IsConverting).ToList();
        foreach (var item in removable)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
            Items.Remove(item);
        }
        Log.Info("queue.clear", $"Cleared {removable.Count} finished item(s)");
        UpdateCounts();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueueItem.State)) UpdateCounts();
    }

    private void UpdateCounts()
    {
        TotalCount = Items.Count;
        PendingCount = Items.Count(i => i.IsQueued);
        DoneCount = Items.Count(i => i.IsCompleted);
        FailedCount = Items.Count(i => i.IsFailed);
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(ConvertButtonText));
        OnPropertyChanged(nameof(CanConvert));
        OnPropertyChanged(nameof(ShelfQueueStatus));
        OnPropertyChanged(nameof(CanAddFiles));
        ClearFinishedCommand.RaiseCanExecuteChanged();
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        ConvertCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        ClearFinishedCommand.RaiseCanExecuteChanged();
    }

    // ─────────────────────────── settings & shell ───────────────────────────

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.OutputDirectory):
                OnPropertyChanged(nameof(OutputDisplay));
                OnPropertyChanged(nameof(OutputDirectoryFull));
                OnPropertyChanged(nameof(ShelfOutputDisplay));
                break;
            case nameof(AppSettings.LastImageFormat):
                ApplyTargets(MediaCategory.Image);
                OnPropertyChanged(nameof(ShelfFormatsSummary));
                Log.Debug("settings.format", "Image target changed", ("format", Settings.LastImageFormat));
                break;
            case nameof(AppSettings.LastAudioFormat):
                ApplyTargets(MediaCategory.Audio);
                if (Settings.ExtractAudioFromVideo) ApplyTargets(MediaCategory.Video);
                OnPropertyChanged(nameof(ShelfFormatsSummary));
                Log.Debug("settings.format", "Audio target changed", ("format", Settings.LastAudioFormat));
                break;
            case nameof(AppSettings.LastVideoFormat):
                if (!Settings.ExtractAudioFromVideo) ApplyTargets(MediaCategory.Video);
                OnPropertyChanged(nameof(ShelfFormatsSummary));
                Log.Debug("settings.format", "Video target changed", ("format", Settings.LastVideoFormat));
                break;
            case nameof(AppSettings.ExtractAudioFromVideo):
                ApplyTargets(MediaCategory.Video);
                OnPropertyChanged(nameof(ShelfFormatsSummary));
                Log.Debug("settings.videoaudio", "Video audio extraction changed",
                    ("enabled", Settings.ExtractAudioFromVideo.ToString()));
                break;
            case nameof(AppSettings.LastDocumentFormat):
                ApplyTargets(MediaCategory.Document);
                OnPropertyChanged(nameof(ShelfFormatsSummary));
                Log.Debug("settings.format", "Document target changed", ("format", Settings.LastDocumentFormat));
                break;
            case nameof(AppSettings.FfmpegPath):
                RefreshEngine();
                OnPropertyChanged(nameof(FfmpegPathDisplay));
                break;
            case nameof(AppSettings.MaxConcurrentTasks):
                OnPropertyChanged(nameof(ConcurrencyText));
                break;
            case nameof(AppSettings.ThemeId):
                OnPropertyChanged(nameof(ThemeName));
                break;
            case nameof(AppSettings.EnableShelfHotkey):
                OnPropertyChanged(nameof(HotkeyHint));
                break;
            case nameof(AppSettings.ShelfHotkeyId):
                OnPropertyChanged(nameof(HotkeyHint));
                Log.Debug("settings.hotkey", "Drop shelf shortcut changed",
                    ("shortcut", ShelfHotkeys.Find(Settings.ShelfHotkeyId).ShortLabel));
                break;
            case nameof(AppSettings.VerboseLogging):
                Log.Verbose = Settings.VerboseLogging;
                break;
        }
    }

    public void RefreshEngine()
    {
        try
        {
            ApplyEngineInfo(_engine.RefreshFfmpeg(Settings.FfmpegPath));
        }
        catch (Exception ex)
        {
            Log.Warn("engine.refresh", "Could not refresh the engine status", ("reason", ex.Message));
            EngineTitle = "Engine check failed";
            EngineDetail = ex.Message;
        }
    }

    private void ApplyEngineInfo(FfmpegInfo info)
    {
        EngineAvailable = info.IsAvailable;
        EngineTitle = info.IsAvailable ? $"{info.DisplayVersion} ready" : "FFmpeg not found";
        EngineDetail = info.IsAvailable
            ? (info.Source == "settings" ? "Custom engine path" : $"Found via {info.Source.ToLowerInvariant()}")
            : "Audio and video need FFmpeg — set its path in Settings → Advanced";
        OnPropertyChanged(nameof(EngineTitle));
        OnPropertyChanged(nameof(EngineDetail));
        OnPropertyChanged(nameof(FfmpegPathDisplay));
    }

    private void BrowseOutput() => BrowseOutput(null);

    public void BrowseOutput(Window? owner)
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Choose where converted files are saved",
                InitialDirectory = Directory.Exists(Settings.OutputDirectory)
                    ? Settings.OutputDirectory
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            var confirmed = owner is null ? dialog.ShowDialog() == true : dialog.ShowDialog(owner) == true;
            if (confirmed)
            {
                Settings.OutputDirectory = dialog.FolderName;
                Log.Info("settings.output", "Output folder changed", ("path", Settings.OutputDirectory));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("settings.output", "Folder picker failed", ("reason", ex.Message));
        }
    }

    private void OpenOutput()
    {
        try
        {
            Directory.CreateDirectory(Settings.OutputDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", Settings.OutputDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("output.open", "Could not open the output folder", ("reason", ex.Message));
        }
    }

    private void CopyDiagnostics()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("NASAQ DIAGNOSTICS");
            sb.AppendLine($"  version        {AppInfo.Version}");
            sb.AppendLine($"  generated      {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"  os             {Environment.OSVersion}");
            sb.AppendLine($"  runtime        .NET {Environment.Version}");
            sb.AppendLine($"  theme          {Settings.ThemeId}");
            sb.AppendLine($"  output         {Settings.OutputDirectory}");
            sb.AppendLine($"  downloads      {Settings.DownloadDirectory}");
            sb.AppendLine($"  concurrency    {Settings.MaxConcurrentTasks}");
            sb.AppendLine($"  ffmpeg         {(_engine.Ffmpeg.IsAvailable ? _engine.Ffmpeg.FfmpegPath : "not found")}");
            sb.AppendLine($"  ffmpeg ver     {_engine.Ffmpeg.Version ?? "—"}");
            sb.AppendLine($"  yt-dlp         {(Installer.EngineReady ? Installer.EngineVersion : "not set up")}");
            sb.AppendLine($"  yt-dlp source  {Installer.EngineSource}");
            sb.AppendLine($"  cookies        {Settings.InstallerCookiesBrowser}");
            sb.AppendLine($"  log folder     {Log.DirectoryPath}");
            sb.AppendLine($"  queue          {TotalCount} total / {DoneCount} done / {FailedCount} failed");
            sb.AppendLine();
            sb.AppendLine("RECENT LOG");
            foreach (var line in Log.ReadTail(80).Split('\n'))
                sb.Append("  ").AppendLine(line.TrimEnd('\r'));
            Clipboard.SetText(sb.ToString());
            Log.Info("diagnostics.copy", "Diagnostics copied to the clipboard");
        }
        catch (Exception ex)
        {
            Log.Warn("diagnostics.copy", "Could not copy diagnostics", ("reason", ex.Message));
            ToastService.Error("Couldn't copy diagnostics", ex.Message);
        }
    }

    public void FlushSettings() => _store.Flush();
}
