using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Nasaq.Core;
using Nasaq.Services;

namespace Nasaq.ViewModels;

/// <summary>
/// Drives the Editor page. Photo crop, video/audio timelines, audio clip
/// assembly and the PDF workspace all render their controls inside the live
/// preview, and every change schedules a fresh result.
/// </summary>
public sealed class EditorViewModel : ObservableObject, IDisposable
{
    private const string ImageFilter =
        "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic;*.avif|All files|*.*";
    private const string VideoFilter =
        "Videos|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v;*.wmv;*.flv;*.ts|All files|*.*";
    private const string AudioFilter =
        "Audio|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma;*.aiff|All files|*.*";
    private const string PdfFilter = "PDF documents|*.pdf|All files|*.*";

    private readonly AppSettings _settings;
    private readonly PhotoEditor _photo;
    private readonly MediaEditor _media;
    private readonly PdfEditor _pdf;
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _exportCts;
    private int _loadVersion;
    private int _pdfThumbVersion;
    private bool _disposed;

    private EditorTool _tool = EditorTool.Photo;
    private string _toolId = "Photo";
    private bool _isBusy;
    private bool _isPreviewBusy;
    private double _progress;
    private string _progressText = "";
    private string _statusTitle = "Ready to edit";
    private string _statusDetail = "Choose a photo from the left to begin.";
    private string _notice = "";
    private string _previewMessage = "";
    private string _previewBadge = "LIVE PREVIEW";
    private string? _lastOutput;

    // playback
    private bool _isMediaPlaying;
    private bool _loopMedia = true;
    private bool _videoPlaybackFailed;
    private string _videoPlaybackNotice = "";

    // photo
    private string? _photoPath;
    private int _photoSourceWidth;
    private int _photoSourceHeight;
    private long _photoBytes;
    private BitmapSource? _photoPreview;
    private double _cropLeft;
    private double _cropTop;
    private double _cropRight = 100;
    private double _cropBottom = 100;
    private string _cropAspectId = "Free";
    private string _photoFormatId = "jpg";
    private double _photoQuality = 92;

    // video
    private string? _videoPath;
    private double _videoDuration;
    private double _videoStart;
    private double _videoEnd;
    private double _videoPosition;
    private int _videoWidth;
    private int _videoHeight;
    private long _videoBytes;
    private BitmapSource? _videoPreview;
    private int _videoFrameWidth;
    private int _videoFrameHeight;
    private double _videoQuality = 23;
    private int _videoAudioBitrate = 192;

    // audio
    private string _audioModeId = "Trim";
    private AudioEditMode _audioMode = AudioEditMode.Trim;
    private string? _audioPath;
    private double _audioDuration;
    private double _audioStart;
    private double _audioEnd;
    private double _audioPosition;
    private double _audioMergePosition;
    private double _audioMergeDuration;
    private long _audioBytes;
    private BitmapSource? _audioWaveform;
    private AudioTrackItem? _audioSelectedTrack;
    private string _audioFormatId = "mp3";
    private int _audioBitrate = 192;

    // pdf
    private PdfEditMode _pdfMode = PdfEditMode.Merge;
    private PdfQueueItem? _selectedPdf;
    private int _pdfPageCount;
    private int _pdfPage = 1;
    private string _pdfPageText = "1-";
    private string _pdfSelectionLabel = "All pages";
    private string _pdfSelectionError = "";
    private BitmapSource? _pdfPreview;
    private string _pdfThumbnailNote = "";
    private string _pdfImageFormatId = "png";
    private double _pdfImageQuality = 92;
    private int _pdfImageEdge = 2000;
    private bool _pdfSeparatePages = true;

    public EditorViewModel(SettingsStore store, ConversionService engine)
    {
        _settings = store.Settings;
        _photo = new PhotoEditor(engine);
        _media = new MediaEditor(engine);
        _pdf = new PdfEditor();

        Tools = new[]
        {
            new EditorOption("Photo", "Photo", "\uE91B", "Crop photo"),
            new EditorOption("Video", "Video", "\uE714", "Play & cut"),
            new EditorOption("Audio", "Audio", "\uE8D6", "Trim / merge"),
            new EditorOption("Pdf", "PDF", "\uE8A5", "Pages & merge"),
        };
        AudioModes = new[]
        {
            new EditorOption("Trim", "Trim", "\uE8C6", "Cut one track"),
            new EditorOption("Merge", "Merge", "\uE8F1", "Join clips"),
        };
        CropAspects = new[]
        {
            new EditorOption("Free", "Free", "", "Freeform crop"),
            new EditorOption("Original", "Original", "", "Full frame"),
            new EditorOption("Square", "1:1", "", "Square crop"),
            new EditorOption("4:3", "4:3", "", "Classic photo"),
            new EditorOption("16:9", "16:9", "", "Wide crop"),
            new EditorOption("9:16", "9:16", "", "Vertical crop"),
        };
        PhotoFormats = new[]
        {
            new EditorOption("jpg", "JPG", "", "Smaller, photo-friendly"),
            new EditorOption("png", "PNG", "", "Lossless"),
        };
        AudioFormats = new[]
        {
            new EditorOption("mp3", "MP3"),
            new EditorOption("wav", "WAV"),
            new EditorOption("m4a", "M4A"),
            new EditorOption("flac", "FLAC"),
            new EditorOption("ogg", "OGG"),
            new EditorOption("opus", "OPUS"),
        };
        PdfImageFormats = new[]
        {
            new EditorOption("png", "PNG"),
            new EditorOption("jpg", "JPG"),
        };
        AudioBitrates = new[] { 96, 128, 192, 256, 320 };
        PdfImageEdges = new[] { 1200, 1600, 2000, 3000 };

        PickCurrentCommand = new RelayCommand(PickCurrent, () => !IsBusy);
        PickPhotoCommand = new RelayCommand(PickPhoto, () => !IsBusy);
        PickVideoCommand = new RelayCommand(PickVideo, () => !IsBusy);
        PickAudioCommand = new RelayCommand(PickAudio, () => !IsBusy);
        PickPdfCommand = new RelayCommand(PickPdf, () => !IsBusy);
        ResetCropCommand = new RelayCommand(ResetCrop, () => !IsBusy && PhotoPath is not null);
        CenterCropCommand = new RelayCommand(CenterCrop, () => !IsBusy && PhotoPath is not null);
        CropAspectCommand = new RelayCommand(p => ApplyCropAspect(p as string),
            p => !IsBusy && PhotoPath is not null && p is string);
        UseWholeVideoCommand = new RelayCommand(UseWholeVideo, () => !IsBusy && VideoPath is not null);
        SetVideoStartCommand = new RelayCommand(SetVideoStartAtPlayhead, () => !IsBusy && VideoPath is not null);
        SetVideoEndCommand = new RelayCommand(SetVideoEndAtPlayhead, () => !IsBusy && VideoPath is not null);
        UseWholeAudioCommand = new RelayCommand(UseWholeAudio, () => !IsBusy && AudioPath is not null);
        SetAudioStartCommand = new RelayCommand(SetAudioStartAtPlayhead, () => !IsBusy && AudioPath is not null);
        SetAudioEndCommand = new RelayCommand(SetAudioEndAtPlayhead, () => !IsBusy && AudioPath is not null);
        RemoveAudioTrackCommand = new RelayCommand(p => RemoveAudioTrack(p as AudioTrackItem));
        MoveAudioTrackUpCommand = new RelayCommand(() => MoveAudioTrack(-1), () => AudioSelectedTrack is not null);
        MoveAudioTrackDownCommand = new RelayCommand(() => MoveAudioTrack(1), () => AudioSelectedTrack is not null);
        SplitAudioAtPlayheadCommand = new RelayCommand(SplitAudioAtPlayhead,
            () => !IsBusy && IsAudioMerge && AudioTracks.Count > 0);
        ClearAudioTracksCommand = new RelayCommand(ClearAudioTracks, () => !IsBusy && AudioTracks.Count > 0);
        RemovePdfCommand = new RelayCommand(p => RemovePdf(p as PdfQueueItem));
        MovePdfUpCommand = new RelayCommand(() => MovePdf(-1), () => SelectedPdf is not null);
        MovePdfDownCommand = new RelayCommand(() => MovePdf(1), () => SelectedPdf is not null);
        ClearPdfsCommand = new RelayCommand(ClearPdfs, () => !IsBusy && PdfFiles.Count > 0);
        PdfFirstCommand = new RelayCommand(() => PdfPage = 1, () => PdfPage > 1);
        PdfPreviousCommand = new RelayCommand(() => PdfPage--, () => PdfPage > 1);
        PdfNextCommand = new RelayCommand(() => PdfPage++, () => PdfPage < PdfPageCount);
        PdfLastCommand = new RelayCommand(() => PdfPage = PdfPageCount,
            () => PdfPageCount > 0 && PdfPage < PdfPageCount);
        ExportPdfMergeCommand = new RelayCommand(() => RunPdfAction(PdfEditMode.Merge),
            () => !IsBusy && PdfFiles.Count >= 2);
        ExportPdfSplitCommand = new RelayCommand(() => RunPdfAction(PdfEditMode.Split),
            () => !IsBusy && SelectedPdf is not null);
        ExportPdfImagesCommand = new RelayCommand(() => RunPdfAction(PdfEditMode.Images),
            () => !IsBusy && SelectedPdf is not null);
        ChangeOutputCommand = new RelayCommand(ChangeOutput, () => !IsBusy);
        OpenOutputCommand = new RelayCommand(OpenOutput);
        RevealResultCommand = new RelayCommand(RevealResult, () => CanRevealResult);
        ExportCommand = new RelayCommand(() => _ = ExportAsync(), () => CanExport);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        CancelEditCommand = new RelayCommand(CancelEdit, () => HasActiveEdit && !IsBusy);
        CancelPreviewCommand = new RelayCommand(CancelPreview, () => IsPreviewBusy && !IsBusy);

        AudioTracks.CollectionChanged += (_, _) => OnAudioTracksChanged();
        PdfFiles.CollectionChanged += (_, _) => OnPdfFilesChanged();

        _settings.PropertyChanged += OnSettingsChanged;
        Log.Debug("editor.ready", "Editor view model created",
            ("ffmpeg", _media.Available.ToString()), ("output", OutputDirectory));
    }

    // ───────────────────────── options ─────────────────────────

    public IReadOnlyList<EditorOption> Tools { get; }
    public IReadOnlyList<EditorOption> AudioModes { get; }
    public IReadOnlyList<EditorOption> CropAspects { get; }
    public IReadOnlyList<EditorOption> PhotoFormats { get; }
    public IReadOnlyList<EditorOption> AudioFormats { get; }
    public IReadOnlyList<EditorOption> PdfImageFormats { get; }
    public IReadOnlyList<int> AudioBitrates { get; }
    public IReadOnlyList<int> PdfImageEdges { get; }

    public ObservableCollection<AudioTrackItem> AudioTracks { get; } = new();
    public ObservableCollection<PdfQueueItem> PdfFiles { get; } = new();
    public ObservableCollection<PdfThumbnailItem> PdfThumbnails { get; } = new();

    // ───────────────────────── commands ─────────────────────────

    public RelayCommand PickCurrentCommand { get; }
    public RelayCommand PickPhotoCommand { get; }
    public RelayCommand PickVideoCommand { get; }
    public RelayCommand PickAudioCommand { get; }
    public RelayCommand PickPdfCommand { get; }
    public RelayCommand ResetCropCommand { get; }
    public RelayCommand CenterCropCommand { get; }
    public RelayCommand CropAspectCommand { get; }
    public RelayCommand UseWholeVideoCommand { get; }
    public RelayCommand SetVideoStartCommand { get; }
    public RelayCommand SetVideoEndCommand { get; }
    public RelayCommand UseWholeAudioCommand { get; }
    public RelayCommand SetAudioStartCommand { get; }
    public RelayCommand SetAudioEndCommand { get; }
    public RelayCommand RemoveAudioTrackCommand { get; }
    public RelayCommand MoveAudioTrackUpCommand { get; }
    public RelayCommand MoveAudioTrackDownCommand { get; }
    public RelayCommand SplitAudioAtPlayheadCommand { get; }
    public RelayCommand ClearAudioTracksCommand { get; }
    public RelayCommand RemovePdfCommand { get; }
    public RelayCommand MovePdfUpCommand { get; }
    public RelayCommand MovePdfDownCommand { get; }
    public RelayCommand ClearPdfsCommand { get; }
    public RelayCommand PdfFirstCommand { get; }
    public RelayCommand PdfPreviousCommand { get; }
    public RelayCommand PdfNextCommand { get; }
    public RelayCommand PdfLastCommand { get; }
    public RelayCommand ExportPdfMergeCommand { get; }
    public RelayCommand ExportPdfSplitCommand { get; }
    public RelayCommand ExportPdfImagesCommand { get; }
    public RelayCommand ChangeOutputCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public RelayCommand RevealResultCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand CancelEditCommand { get; }
    public RelayCommand CancelPreviewCommand { get; }

    // ───────────────────────── tool state ─────────────────────────

    public string ToolId
    {
        get => _toolId;
        set
        {
            if (!Set(ref _toolId, value)) return;
            _tool = value switch
            {
                "Video" => EditorTool.Video,
                "Audio" => EditorTool.Audio,
                "Pdf" => EditorTool.Pdf,
                _ => EditorTool.Photo,
            };
            OnPropertyChanged(nameof(IsPhotoTool));
            OnPropertyChanged(nameof(IsVideoTool));
            OnPropertyChanged(nameof(IsAudioTool));
            OnPropertyChanged(nameof(IsPdfTool));
            OnPropertyChanged(nameof(ShowPdfPageNav));
            OnPropertyChanged(nameof(PreviewTitle));
            OnPropertyChanged(nameof(PreviewSubtitle));
            OnPropertyChanged(nameof(PreviewBadge));
            OnPropertyChanged(nameof(PickButtonText));
            OnPropertyChanged(nameof(ToolGlyph));
            OnPropertyChanged(nameof(HasActiveEdit));
            StatusTitle = ToolReadyText;
            StatusDetail = ToolHint;
            Notice = "";
            SchedulePreview(immediate: true);
            RefreshCommands();
            Log.Debug("editor.tool", "Editor tool changed", ("tool", value));
        }
    }

    public EditorTool Tool => _tool;
    public bool IsPhotoTool => _tool == EditorTool.Photo;
    public bool IsVideoTool => _tool == EditorTool.Video;
    public bool IsAudioTool => _tool == EditorTool.Audio;
    public bool IsPdfTool => _tool == EditorTool.Pdf;
    public bool ShowPdfPageNav => IsPdfTool && SelectedPdf is not null;

    private string ToolReadyText => _tool switch
    {
        EditorTool.Photo => "Photo ready",
        EditorTool.Video => "Video ready",
        EditorTool.Audio => IsAudioMerge ? "Audio mix" : "Audio ready",
        _ => "PDF ready",
    };

    private string ToolHint => _tool switch
    {
        EditorTool.Photo => "Choose a photo, then drag the crop box directly in the preview.",
        EditorTool.Video => "Choose a clip, play it, and set the cut points on the timeline.",
        EditorTool.Audio => IsAudioMerge
            ? "Add clips, arrange them, then split a clip at the playhead if needed."
            : "Choose audio and drag the trim handles on the waveform.",
        _ => "Add PDFs, browse their pages, then merge, split or render pages as images.",
    };

    public string PickButtonText => _tool switch
    {
        EditorTool.Photo => "Choose photo",
        EditorTool.Video => "Choose video",
        EditorTool.Audio => IsAudioMerge ? "Add audio clips" : "Choose audio",
        _ => "Add PDFs",
    };

    public string ToolGlyph => _tool switch
    {
        EditorTool.Video => "\uE714",
        EditorTool.Audio => "\uE8D6",
        EditorTool.Pdf => "\uE8A5",
        _ => "\uE91B",
    };

    // ───────────────────────── shared state ─────────────────────────

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(ExportButtonText));
            OnPropertyChanged(nameof(ProgressVisible));
            OnPropertyChanged(nameof(CanExport));
            RefreshCommands();
        }
    }

    public bool IsPreviewBusy
    {
        get => _isPreviewBusy;
        private set
        {
            if (!Set(ref _isPreviewBusy, value)) return;
            OnPropertyChanged(nameof(PreviewLiveText));
            CancelPreviewCommand.RaiseCanExecuteChanged();
        }
    }

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, Math.Clamp(value, 0, 1));
    }

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    public bool ProgressVisible => IsBusy;

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

    public string Notice
    {
        get => _notice;
        private set
        {
            if (!Set(ref _notice, value)) return;
            OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);

    public string PreviewMessage
    {
        get => _previewMessage;
        private set => Set(ref _previewMessage, value);
    }

    public string PreviewBadge
    {
        get => _previewBadge;
        private set => Set(ref _previewBadge, value);
    }

    public string PreviewLiveText => IsPreviewBusy ? "Updating preview…" : "Live preview";

    public string? LastOutput
    {
        get => _lastOutput;
        private set
        {
            if (!Set(ref _lastOutput, value)) return;
            OnPropertyChanged(nameof(CanRevealResult));
            RevealResultCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanRevealResult =>
        !string.IsNullOrWhiteSpace(LastOutput) && File.Exists(LastOutput);

    public bool EngineAvailable => _media.Available;
    public string EngineHint => EngineAvailable ? "FFmpeg ready" : "FFmpeg not found";

    public string OutputDirectory => string.IsNullOrWhiteSpace(_settings.OutputDirectory)
        ? AppSettings.DefaultOutputDirectory()
        : _settings.OutputDirectory;

    public string OutputDisplay => FileSize.FormatPath(OutputDirectory, 40);

    public string ExportButtonText
    {
        get
        {
            if (IsBusy) return "Working…";
            return _tool switch
            {
                EditorTool.Photo => "Save photo",
                EditorTool.Video => "Export video",
                EditorTool.Audio => IsAudioMerge ? "Merge clips" : "Trim audio",
                _ => _pdfMode switch
                {
                    PdfEditMode.Split => "Split PDF",
                    PdfEditMode.Images => "Export pages",
                    _ => "Merge PDFs",
                },
            };
        }
    }

    public bool CanExport
    {
        get
        {
            if (IsBusy) return false;
            return _tool switch
            {
                EditorTool.Photo => !string.IsNullOrWhiteSpace(PhotoPath),
                EditorTool.Video => !string.IsNullOrWhiteSpace(VideoPath) && VideoEnd > VideoStart,
                EditorTool.Audio => IsAudioMerge ? AudioTracks.Count >= 2 : !string.IsNullOrWhiteSpace(AudioPath),
                _ => _pdfMode == PdfEditMode.Merge
                    ? PdfFiles.Count >= 2
                    : SelectedPdf is not null,
            };
        }
    }

    public bool CanPdfMerge => !IsBusy && PdfFiles.Count >= 2;
    public bool CanPdfSplit => !IsBusy && SelectedPdf is not null;

    public bool HasActiveEdit => _tool switch
    {
        EditorTool.Photo => PhotoPath is not null,
        EditorTool.Video => VideoPath is not null,
        EditorTool.Audio => IsAudioMerge ? AudioTracks.Count > 0 : AudioPath is not null,
        _ => PdfFiles.Count > 0,
    };

    public string PreviewTitle => _tool switch
    {
        EditorTool.Photo => PhotoPath is null ? "No photo yet" : PhotoName,
        EditorTool.Video => VideoPath is null ? "No video yet" : VideoName,
        EditorTool.Audio => IsAudioMerge
            ? AudioTracks.Count == 0 ? "No clips yet" : "Audio mix"
            : AudioPath is null ? "No audio yet" : AudioName,
        _ => SelectedPdf is null ? "No PDF yet" : SelectedPdf.FileName,
    };

    public string PreviewSubtitle => _tool switch
    {
        EditorTool.Photo => PhotoPath is null ? "Choose a photo to start" : PhotoMeta,
        EditorTool.Video => VideoPath is null ? "Choose a video to start" : VideoMeta,
        EditorTool.Audio => IsAudioMerge
            ? AudioTracks.Count == 0 ? "Add two clips to preview the mix" : AudioMergeTotalText
            : AudioPath is null ? "Choose audio to start" : AudioMeta,
        _ => SelectedPdf is null
            ? "Add PDFs to open the workspace"
            : PdfFiles.Count > 1 ? $"{SelectedPdf.Meta} · {PdfFilesSummary}" : SelectedPdf.Meta,
    };

    // ───────────────────────── playback ─────────────────────────

    public bool IsMediaPlaying
    {
        get => _isMediaPlaying;
        set
        {
            if (!Set(ref _isMediaPlaying, value)) return;
            OnPropertyChanged(nameof(PlaybackGlyph));
            OnPropertyChanged(nameof(PlaybackLabel));
            OnPropertyChanged(nameof(ShowVideoPlayOverlay));
        }
    }

    public string PlaybackGlyph => IsMediaPlaying ? "\uE769" : "\uE768";
    public string PlaybackLabel => IsMediaPlaying ? "Pause" : "Play";

    public bool LoopMedia
    {
        get => _loopMedia;
        set => Set(ref _loopMedia, value);
    }

    public bool VideoPlaybackFailed
    {
        get => _videoPlaybackFailed;
        private set
        {
            if (!Set(ref _videoPlaybackFailed, value)) return;
            OnPropertyChanged(nameof(ShowVideoFallback));
            OnPropertyChanged(nameof(CanPlayVideo));
            OnPropertyChanged(nameof(ShowVideoPlayOverlay));
        }
    }

    public bool ShowVideoFallback => VideoPlaybackFailed;
    public string VideoPlaybackNotice => _videoPlaybackNotice;

    public bool CanPlayVideo => VideoPath is not null && !VideoPlaybackFailed;
    public bool ShowVideoPlayOverlay => CanPlayVideo && !IsMediaPlaying;

    public void ReportVideoPlaybackIssue(string message)
    {
        VideoPlaybackFailed = true;
        _videoPlaybackNotice = message;
        OnPropertyChanged(nameof(VideoPlaybackNotice));
        OnPropertyChanged(nameof(ShowVideoFallback));
        IsMediaPlaying = false;
        if (VideoPath is not null) ScheduleVideoFrame(immediate: true);
    }

    public void ResetVideoPlaybackIssue()
    {
        VideoPlaybackFailed = false;
        _videoPlaybackNotice = "";
        OnPropertyChanged(nameof(VideoPlaybackNotice));
    }

    public void SetPlaybackPosition(double seconds)
    {
        if (_tool == EditorTool.Video)
            VideoPosition = seconds;
        else if (_tool == EditorTool.Audio && !IsAudioMerge)
            AudioPosition = seconds;
        else if (_tool == EditorTool.Audio)
            AudioMergePosition = seconds;
    }

    // ───────────────────────── photo ─────────────────────────

    public string? PhotoPath
    {
        get => _photoPath;
        private set
        {
            if (!Set(ref _photoPath, value)) return;
            OnPropertyChanged(nameof(PhotoName));
            OnPropertyChanged(nameof(PhotoReady));
            OnPropertyChanged(nameof(PreviewTitle));
            OnPropertyChanged(nameof(PreviewSubtitle));
            NotifyEditState();
        }
    }

    public bool PhotoReady => !string.IsNullOrWhiteSpace(PhotoPath);
    public string PhotoName => PhotoPath is null ? "No photo" : Path.GetFileName(PhotoPath);
    public string PhotoMeta => PhotoPath is null
        ? "Choose a photo to start"
        : $"{PhotoSourceWidth} × {PhotoSourceHeight} · {FileSize.Format(_photoBytes)}";

    public BitmapSource? PhotoPreview
    {
        get => _photoPreview;
        private set => Set(ref _photoPreview, value);
    }

    public int PhotoSourceWidth
    {
        get => _photoSourceWidth;
        private set
        {
            if (!Set(ref _photoSourceWidth, value)) return;
            OnPropertyChanged(nameof(PhotoMeta));
            OnPropertyChanged(nameof(CropSummary));
        }
    }

    public int PhotoSourceHeight
    {
        get => _photoSourceHeight;
        private set
        {
            if (!Set(ref _photoSourceHeight, value)) return;
            OnPropertyChanged(nameof(PhotoMeta));
            OnPropertyChanged(nameof(CropSummary));
        }
    }

    public double CropLeft
    {
        get => _cropLeft;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, _cropRight - 0.8));
            if (!Set(ref _cropLeft, clamped)) return;
            RaiseCrop();
        }
    }

    public double CropTop
    {
        get => _cropTop;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, _cropBottom - 0.8));
            if (!Set(ref _cropTop, clamped)) return;
            RaiseCrop();
        }
    }

    public double CropRight
    {
        get => _cropRight;
        set
        {
            var clamped = Math.Clamp(value, Math.Min(100, _cropLeft + 0.8), 100);
            if (!Set(ref _cropRight, clamped)) return;
            RaiseCrop();
        }
    }

    public double CropBottom
    {
        get => _cropBottom;
        set
        {
            var clamped = Math.Clamp(value, Math.Min(100, _cropTop + 0.8), 100);
            if (!Set(ref _cropBottom, clamped)) return;
            RaiseCrop();
        }
    }

    public Rect PhotoCropRect
    {
        get => new(CropLeft, CropTop, Math.Max(0.8, CropRight - CropLeft),
            Math.Max(0.8, CropBottom - CropTop));
        set
        {
            var left = Math.Clamp(value.X, 0, 99.2);
            var top = Math.Clamp(value.Y, 0, 99.2);
            var right = Math.Clamp(value.X + Math.Max(0.8, value.Width), left + 0.8, 100);
            var bottom = Math.Clamp(value.Y + Math.Max(0.8, value.Height), top + 0.8, 100);

            if (Math.Abs(left - _cropLeft) > 0.0001) { _cropLeft = left; OnPropertyChanged(nameof(CropLeft)); }
            if (Math.Abs(top - _cropTop) > 0.0001) { _cropTop = top; OnPropertyChanged(nameof(CropTop)); }
            if (Math.Abs(right - _cropRight) > 0.0001) { _cropRight = right; OnPropertyChanged(nameof(CropRight)); }
            if (Math.Abs(bottom - _cropBottom) > 0.0001) { _cropBottom = bottom; OnPropertyChanged(nameof(CropBottom)); }
            RaiseCrop();
        }
    }

    public string CropAspectId
    {
        get => _cropAspectId;
        private set => Set(ref _cropAspectId, value);
    }

    public string CropSummary
    {
        get
        {
            if (PhotoSourceWidth <= 0 || PhotoSourceHeight <= 0) return "Full frame";
            var width = Math.Max(1, (int)Math.Round(PhotoSourceWidth * (CropRight - CropLeft) / 100.0));
            var height = Math.Max(1, (int)Math.Round(PhotoSourceHeight * (CropBottom - CropTop) / 100.0));
            return $"{width} × {height} px";
        }
    }

    public string PhotoFormatId
    {
        get => _photoFormatId;
        set => Set(ref _photoFormatId, value);
    }

    public double PhotoQuality
    {
        get => _photoQuality;
        set
        {
            if (!Set(ref _photoQuality, Math.Clamp(value, 30, 100))) return;
            OnPropertyChanged(nameof(PhotoQualityText));
        }
    }

    public string PhotoQualityText => $"{PhotoQuality:0}";

    private void RaiseCrop()
    {
        OnPropertyChanged(nameof(PhotoCropRect));
        OnPropertyChanged(nameof(CropSummary));
    }

    private void ResetCrop()
    {
        SetCropRect(0, 0, 100, 100);
        CropAspectId = "Original";
    }

    private void CenterCrop()
    {
        var width = Math.Max(0.8, CropRight - CropLeft);
        var height = Math.Max(0.8, CropBottom - CropTop);
        SetCropRect((100 - width) / 2, (100 - height) / 2, width, height);
        CropAspectId = "Free";
    }

    private void ApplyCropAspect(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        CropAspectId = id;
        if (id == "Free") return;
        if (id == "Original")
        {
            SetCropRect(0, 0, 100, 100);
            return;
        }
        if (PhotoSourceWidth <= 0 || PhotoSourceHeight <= 0) return;

        var ratio = id switch
        {
            "Square" => 1.0,
            "4:3" => 4.0 / 3.0,
            "16:9" => 16.0 / 9.0,
            "9:16" => 9.0 / 16.0,
            _ => 1.0,
        };
        var sourceAspect = (double)PhotoSourceWidth / PhotoSourceHeight;
        double widthPercent;
        double heightPercent;
        if (sourceAspect >= ratio)
        {
            heightPercent = 100;
            widthPercent = 100 * ratio / sourceAspect;
        }
        else
        {
            widthPercent = 100;
            heightPercent = 100 * sourceAspect / ratio;
        }

        SetCropRect((100 - widthPercent) / 2, (100 - heightPercent) / 2, widthPercent, heightPercent);
    }

    public void SetCropAspectFree()
    {
        if (CropAspectId != "Free") CropAspectId = "Free";
    }

    private void SetCropRect(double left, double top, double width, double height)
    {
        _cropLeft = Math.Clamp(left, 0, 99.2);
        _cropTop = Math.Clamp(top, 0, 99.2);
        _cropRight = Math.Clamp(left + width, _cropLeft + 0.8, 100);
        _cropBottom = Math.Clamp(top + height, _cropTop + 0.8, 100);
        OnPropertyChanged(nameof(CropLeft));
        OnPropertyChanged(nameof(CropTop));
        OnPropertyChanged(nameof(CropRight));
        OnPropertyChanged(nameof(CropBottom));
        RaiseCrop();
    }

    // ───────────────────────── video ─────────────────────────

    public string? VideoPath
    {
        get => _videoPath;
        private set
        {
            if (!Set(ref _videoPath, value)) return;
            OnPropertyChanged(nameof(VideoName));
            OnPropertyChanged(nameof(VideoReady));
            OnPropertyChanged(nameof(PreviewTitle));
            OnPropertyChanged(nameof(PreviewSubtitle));
            OnPropertyChanged(nameof(CanPlayVideo));
            OnPropertyChanged(nameof(ShowVideoPlayOverlay));
            NotifyEditState();
        }
    }

    public bool VideoReady => !string.IsNullOrWhiteSpace(VideoPath);
    public string VideoName => VideoPath is null ? "No video" : Path.GetFileName(VideoPath);
    public string VideoMeta => VideoPath is null
        ? "Choose a video to start"
        : $"{_videoWidth} × {_videoHeight} · {EditorTime.Format(_videoDuration)} · {FileSize.Format(_videoBytes)}";

    public double VideoDuration
    {
        get => _videoDuration;
        private set
        {
            if (!Set(ref _videoDuration, Math.Max(0, value))) return;
            OnPropertyChanged(nameof(VideoDurationMax));
            OnPropertyChanged(nameof(VideoMeta));
            OnPropertyChanged(nameof(VideoClipLengthText));
            OnPropertyChanged(nameof(VideoStartText));
            OnPropertyChanged(nameof(VideoEndText));
            OnPropertyChanged(nameof(VideoPositionText));
        }
    }

    public double VideoDurationMax => Math.Max(0.1, VideoDuration);

    public double VideoStart
    {
        get => _videoStart;
        set
        {
            var max = Math.Max(0, VideoEnd - 0.05);
            var clamped = EditorTime.Clamp(value, 0, max);
            if (!Set(ref _videoStart, clamped)) return;
            if (VideoPosition < clamped) VideoPosition = clamped;
            OnPropertyChanged(nameof(VideoStartText));
            OnPropertyChanged(nameof(VideoClipLengthText));
            RefreshCommands();
        }
    }

    public double VideoEnd
    {
        get => _videoEnd;
        set
        {
            var clamped = EditorTime.Clamp(value, Math.Min(VideoStart + 0.05, VideoDuration), Math.Max(0.05, VideoDuration));
            if (!Set(ref _videoEnd, clamped)) return;
            if (VideoPosition > clamped) VideoPosition = clamped;
            OnPropertyChanged(nameof(VideoEndText));
            OnPropertyChanged(nameof(VideoClipLengthText));
            RefreshCommands();
        }
    }

    public double VideoPosition
    {
        get => _videoPosition;
        set
        {
            var clamped = EditorTime.Clamp(value, 0, VideoDurationMax);
            if (!Set(ref _videoPosition, clamped)) return;
            OnPropertyChanged(nameof(VideoPositionText));
        }
    }

    public string VideoStartText => EditorTime.Format(VideoStart);
    public string VideoEndText => EditorTime.Format(VideoEnd);
    public string VideoPositionText => EditorTime.Format(VideoPosition);
    public string VideoClipLengthText => EditorTime.Format(Math.Max(0, VideoEnd - VideoStart));

    public BitmapSource? VideoPreview
    {
        get => _videoPreview;
        private set => Set(ref _videoPreview, value);
    }

    public int VideoFrameWidth
    {
        get => _videoFrameWidth;
        private set => Set(ref _videoFrameWidth, value);
    }

    public int VideoFrameHeight
    {
        get => _videoFrameHeight;
        private set => Set(ref _videoFrameHeight, value);
    }

    public double VideoQuality
    {
        get => _videoQuality;
        set
        {
            if (!Set(ref _videoQuality, Math.Clamp(value, 14, 32))) return;
            OnPropertyChanged(nameof(VideoQualityText));
        }
    }

    public string VideoQualityText => $"CRF {VideoQuality:0}";

    public int VideoAudioBitrate
    {
        get => _videoAudioBitrate;
        set => Set(ref _videoAudioBitrate, value);
    }

    private void SetVideoStartAtPlayhead()
    {
        if (VideoPosition < VideoEnd - 0.05) VideoStart = VideoPosition;
    }

    private void SetVideoEndAtPlayhead()
    {
        if (VideoPosition > VideoStart + 0.05) VideoEnd = VideoPosition;
    }

    private void UseWholeVideo()
    {
        _videoStart = 0;
        _videoEnd = VideoDuration;
        OnPropertyChanged(nameof(VideoStart));
        OnPropertyChanged(nameof(VideoEnd));
        OnPropertyChanged(nameof(VideoStartText));
        OnPropertyChanged(nameof(VideoEndText));
        OnPropertyChanged(nameof(VideoClipLengthText));
        RefreshCommands();
    }

    // ───────────────────────── audio ─────────────────────────

    public string AudioModeId
    {
        get => _audioModeId;
        set
        {
            if (!Set(ref _audioModeId, value)) return;
            _audioMode = value == "Merge" ? AudioEditMode.Merge : AudioEditMode.Trim;
            OnPropertyChanged(nameof(IsAudioTrim));
            OnPropertyChanged(nameof(IsAudioMerge));
            OnPropertyChanged(nameof(PreviewTitle));
            OnPropertyChanged(nameof(PreviewSubtitle));
            OnPropertyChanged(nameof(PreviewBadge));
            OnPropertyChanged(nameof(PickButtonText));
            OnPropertyChanged(nameof(ExportButtonText));
            OnPropertyChanged(nameof(HasActiveEdit));
            StatusTitle = ToolReadyText;
            StatusDetail = ToolHint;
            Notice = "";
            if (IsAudioMerge) UpdateAudioTimeline();
            SchedulePreview(immediate: true);
            RefreshCommands();
        }
    }

    public AudioEditMode AudioMode => _audioMode;
    public bool IsAudioTrim => _audioMode == AudioEditMode.Trim;
    public bool IsAudioMerge => _audioMode == AudioEditMode.Merge;

    public string? AudioPath
    {
        get => _audioPath;
        private set
        {
            if (!Set(ref _audioPath, value)) return;
            OnPropertyChanged(nameof(AudioName));
            OnPropertyChanged(nameof(PreviewTitle));
            OnPropertyChanged(nameof(PreviewSubtitle));
            NotifyEditState();
        }
    }

    public string AudioName => AudioPath is null ? "No audio" : Path.GetFileName(AudioPath);
    public string AudioMeta => AudioPath is null
        ? "Choose audio to start"
        : $"{EditorTime.Format(_audioDuration)} · {FileSize.Format(_audioBytes)}";

    public double AudioDuration
    {
        get => _audioDuration;
        private set
        {
            if (!Set(ref _audioDuration, Math.Max(0, value))) return;
            OnPropertyChanged(nameof(AudioDurationMax));
            OnPropertyChanged(nameof(AudioMeta));
            OnPropertyChanged(nameof(AudioStartText));
            OnPropertyChanged(nameof(AudioEndText));
            OnPropertyChanged(nameof(AudioClipLengthText));
            OnPropertyChanged(nameof(AudioStartPercent));
            OnPropertyChanged(nameof(AudioEndPercent));
            OnPropertyChanged(nameof(AudioPositionText));
        }
    }

    public double AudioDurationMax => Math.Max(0.1, AudioDuration);

    public double AudioStart
    {
        get => _audioStart;
        set
        {
            var max = Math.Max(0, AudioEnd - 0.05);
            var clamped = EditorTime.Clamp(value, 0, max);
            if (!Set(ref _audioStart, clamped)) return;
            if (AudioPosition < clamped) AudioPosition = clamped;
            OnPropertyChanged(nameof(AudioStartText));
            OnPropertyChanged(nameof(AudioClipLengthText));
            OnPropertyChanged(nameof(AudioStartPercent));
            OnPropertyChanged(nameof(PreviewBadge));
            RefreshCommands();
        }
    }

    public double AudioEnd
    {
        get => _audioEnd;
        set
        {
            var clamped = EditorTime.Clamp(value, Math.Min(AudioStart + 0.05, AudioDuration), Math.Max(0.05, AudioDuration));
            if (!Set(ref _audioEnd, clamped)) return;
            if (AudioPosition > clamped) AudioPosition = clamped;
            OnPropertyChanged(nameof(AudioEndText));
            OnPropertyChanged(nameof(AudioClipLengthText));
            OnPropertyChanged(nameof(AudioEndPercent));
            OnPropertyChanged(nameof(PreviewBadge));
            RefreshCommands();
        }
    }

    public double AudioPosition
    {
        get => _audioPosition;
        set
        {
            var clamped = EditorTime.Clamp(value, 0, AudioDurationMax);
            if (!Set(ref _audioPosition, clamped)) return;
            OnPropertyChanged(nameof(AudioPositionText));
        }
    }

    public double AudioMergePosition
    {
        get => _audioMergePosition;
        set
        {
            var clamped = EditorTime.Clamp(value, 0, Math.Max(0.1, AudioMergeDuration));
            if (!Set(ref _audioMergePosition, clamped)) return;
            OnPropertyChanged(nameof(AudioMergePositionText));
        }
    }

    public string AudioStartText => EditorTime.Format(AudioStart);
    public string AudioEndText => EditorTime.Format(AudioEnd);
    public string AudioPositionText => EditorTime.Format(AudioPosition);
    public string AudioMergePositionText => EditorTime.Format(AudioMergePosition);
    public string AudioClipLengthText => EditorTime.Format(Math.Max(0, AudioEnd - AudioStart));
    public double AudioStartPercent => AudioDuration > 0 ? Math.Clamp(AudioStart / AudioDuration, 0, 1) : 0;
    public double AudioEndPercent => AudioDuration > 0 ? Math.Clamp(AudioEnd / AudioDuration, 0, 1) : 1;

    public BitmapSource? AudioWaveform
    {
        get => _audioWaveform;
        private set => Set(ref _audioWaveform, value);
    }

    public AudioTrackItem? AudioSelectedTrack
    {
        get => _audioSelectedTrack;
        set
        {
            if (!Set(ref _audioSelectedTrack, value)) return;
            MoveAudioTrackUpCommand.RaiseCanExecuteChanged();
            MoveAudioTrackDownCommand.RaiseCanExecuteChanged();
        }
    }

    public double AudioMergeDuration
    {
        get => _audioMergeDuration;
        private set
        {
            if (!Set(ref _audioMergeDuration, Math.Max(0, value))) return;
            OnPropertyChanged(nameof(AudioMergeTotalText));
            OnPropertyChanged(nameof(AudioMergePositionText));
            OnPropertyChanged(nameof(PreviewSubtitle));
        }
    }

    public string AudioFormatId
    {
        get => _audioFormatId;
        set => Set(ref _audioFormatId, value);
    }

    public int AudioBitrate
    {
        get => _audioBitrate;
        set => Set(ref _audioBitrate, value);
    }

    public string AudioTrackCountText => AudioTracks.Count == 0
        ? "No clips added"
        : $"{AudioTracks.Count} {(AudioTracks.Count == 1 ? "clip" : "clips")}";

    public string AudioMergeTotalText
    {
        get
        {
            if (AudioTracks.Count == 0) return "Add two or more clips";
            return $"{AudioTrackCountText} · {EditorTime.Format(AudioMergeDuration)}";
        }
    }

    private void SetAudioStartAtPlayhead()
    {
        if (AudioPosition < AudioEnd - 0.05) AudioStart = AudioPosition;
    }

    private void SetAudioEndAtPlayhead()
    {
        if (AudioPosition > AudioStart + 0.05) AudioEnd = AudioPosition;
    }

    private void UseWholeAudio()
    {
        _audioStart = 0;
        _audioEnd = AudioDuration;
        OnPropertyChanged(nameof(AudioStart));
        OnPropertyChanged(nameof(AudioEnd));
        OnPropertyChanged(nameof(AudioStartText));
        OnPropertyChanged(nameof(AudioEndText));
        OnPropertyChanged(nameof(AudioClipLengthText));
        OnPropertyChanged(nameof(AudioStartPercent));
        OnPropertyChanged(nameof(AudioEndPercent));
        RefreshCommands();
    }

    // ───────────────────────── pdf ─────────────────────────

    public PdfEditMode PdfMode => _pdfMode;
    public bool IsPdfMerge => _pdfMode == PdfEditMode.Merge;
    public bool IsPdfSplit => _pdfMode == PdfEditMode.Split;
    public bool IsPdfImages => _pdfMode == PdfEditMode.Images;

    public string PdfModeId
    {
        get => _pdfMode switch
        {
            PdfEditMode.Split => "Split",
            PdfEditMode.Images => "Images",
            _ => "Merge",
        };
        set
        {
            var mode = value switch
            {
                "Split" => PdfEditMode.Split,
                "Images" => PdfEditMode.Images,
                _ => PdfEditMode.Merge,
            };
            if (_pdfMode == mode) return;
            _pdfMode = mode;
            OnPropertyChanged(nameof(PdfModeId));
            OnPropertyChanged(nameof(PdfMode));
            OnPropertyChanged(nameof(IsPdfMerge));
            OnPropertyChanged(nameof(IsPdfSplit));
            OnPropertyChanged(nameof(IsPdfImages));
            OnPropertyChanged(nameof(ExportButtonText));
            OnPropertyChanged(nameof(CanExport));
            RefreshCommands();
        }
    }

    public PdfQueueItem? SelectedPdf
    {
        get => _selectedPdf;
        set
        {
            if (!Set(ref _selectedPdf, value)) return;
            if (value is null)
            {
                _pdfPageCount = 0;
                _pdfPage = 1;
                PdfPreview = null;
                PdfThumbnails.Clear();
                PdfThumbnailNote = "";
                OnPropertyChanged(nameof(PdfPath));
                OnPropertyChanged(nameof(PdfName));
                OnPropertyChanged(nameof(PdfMeta));
                OnPropertyChanged(nameof(PdfPageCount));
                OnPropertyChanged(nameof(PdfPage));
                OnPropertyChanged(nameof(PdfPageLabel));
                OnPropertyChanged(nameof(PdfFilesSummary));
                OnPropertyChanged(nameof(PreviewTitle));
                OnPropertyChanged(nameof(PreviewSubtitle));
                NotifyEditState();
                RefreshCommands();
                return;
            }

            _pdfPageCount = value.PageCount;
            _pdfPage = 1;
            _pdfPageText = "1-";
            OnPropertyChanged(nameof(PdfPath));
            OnPropertyChanged(nameof(PdfName));
            OnPropertyChanged(nameof(PdfMeta));
            OnPropertyChanged(nameof(PdfPageCount));
            OnPropertyChanged(nameof(PdfPage));
            OnPropertyChanged(nameof(PdfPageLabel));
            OnPropertyChanged(nameof(PdfPageText));
            OnPropertyChanged(nameof(PdfFilesSummary));
            OnPropertyChanged(nameof(PreviewTitle));
            OnPropertyChanged(nameof(PreviewSubtitle));
            OnPropertyChanged(nameof(ShowPdfPageNav));
            ParsePdfSelection(updatePreview: false);
            SchedulePdfThumbnails(value);
            SchedulePreview(immediate: true);
            RefreshCommands();
        }
    }

    public string? PdfPath => SelectedPdf?.Path;
    public string PdfName => SelectedPdf?.FileName ?? "No PDF";
    public string PdfMeta => SelectedPdf is null
        ? "Choose a PDF to start"
        : $"{PdfPageCount} {(PdfPageCount == 1 ? "page" : "pages")} · {FileSize.Format(SelectedPdf.Bytes)}";

    public int PdfPageCount
    {
        get => _pdfPageCount;
        private set
        {
            if (!Set(ref _pdfPageCount, Math.Max(0, value))) return;
            OnPropertyChanged(nameof(PdfPageLabel));
            OnPropertyChanged(nameof(PdfMeta));
            OnPropertyChanged(nameof(PreviewBadge));
            OnPropertyChanged(nameof(ShowPdfPageNav));
            PdfFirstCommand.RaiseCanExecuteChanged();
            PdfPreviousCommand.RaiseCanExecuteChanged();
            PdfNextCommand.RaiseCanExecuteChanged();
            PdfLastCommand.RaiseCanExecuteChanged();
        }
    }

    public int PdfPage
    {
        get => _pdfPage;
        set
        {
            var clamped = Math.Clamp(value, 1, Math.Max(1, PdfPageCount));
            if (!Set(ref _pdfPage, clamped)) return;
            OnPropertyChanged(nameof(PdfPageLabel));
            OnPropertyChanged(nameof(PreviewBadge));
            PdfFirstCommand.RaiseCanExecuteChanged();
            PdfPreviousCommand.RaiseCanExecuteChanged();
            PdfNextCommand.RaiseCanExecuteChanged();
            PdfLastCommand.RaiseCanExecuteChanged();
            SchedulePreview();
        }
    }

    public string PdfPageLabel => PdfPageCount <= 0 ? "No pages" : $"Page {PdfPage} of {PdfPageCount}";

    public string PdfPageText
    {
        get => _pdfPageText;
        set
        {
            if (!Set(ref _pdfPageText, value)) return;
            ParsePdfSelection(updatePreview: true);
        }
    }

    public string PdfSelectionLabel
    {
        get => _pdfSelectionLabel;
        private set => Set(ref _pdfSelectionLabel, value);
    }

    public string PdfSelectionError
    {
        get => _pdfSelectionError;
        private set
        {
            if (!Set(ref _pdfSelectionError, value)) return;
            OnPropertyChanged(nameof(HasPdfSelectionError));
        }
    }

    public bool HasPdfSelectionError => !string.IsNullOrWhiteSpace(PdfSelectionError);

    public BitmapSource? PdfPreview
    {
        get => _pdfPreview;
        private set => Set(ref _pdfPreview, value);
    }

    public string PdfThumbnailNote
    {
        get => _pdfThumbnailNote;
        private set
        {
            if (!Set(ref _pdfThumbnailNote, value)) return;
            OnPropertyChanged(nameof(HasPdfThumbnailNote));
        }
    }

    public bool HasPdfThumbnailNote => !string.IsNullOrWhiteSpace(PdfThumbnailNote);

    public string PdfImageFormatId
    {
        get => _pdfImageFormatId;
        set => Set(ref _pdfImageFormatId, value);
    }

    public double PdfImageQuality
    {
        get => _pdfImageQuality;
        set
        {
            if (!Set(ref _pdfImageQuality, Math.Clamp(value, 30, 100))) return;
            OnPropertyChanged(nameof(PdfImageQualityText));
        }
    }

    public string PdfImageQualityText => $"{PdfImageQuality:0}";

    public int PdfImageEdge
    {
        get => _pdfImageEdge;
        set => Set(ref _pdfImageEdge, value);
    }

    public bool PdfSeparatePages
    {
        get => _pdfSeparatePages;
        set => Set(ref _pdfSeparatePages, value);
    }

    public string PdfFilesSummary => PdfFiles.Count == 0
        ? "No PDFs added"
        : $"{(PdfFiles.Count == 1 ? "1 PDF" : $"{PdfFiles.Count} PDFs")} · {PdfTotalPagesText}";

    public int PdfTotalPages => PdfFiles.Sum(f => f.PageCount);

    public string PdfTotalPagesText => PdfTotalPages == 1 ? "1 page" : $"{PdfTotalPages} pages";

    // ───────────────────────── file intake ─────────────────────────

    public void PickCurrent()
    {
        switch (_tool)
        {
            case EditorTool.Photo: PickPhoto(); break;
            case EditorTool.Video: PickVideo(); break;
            case EditorTool.Audio: PickAudio(); break;
            default: PickPdf(); break;
        }
    }

    private void PickPhoto()
    {
        var path = PickFile("Choose a photo", ImageFilter);
        if (path is not null) _ = LoadPhotoAsync(path);
    }

    private void PickVideo()
    {
        var path = PickFile("Choose a video", VideoFilter);
        if (path is not null) _ = LoadVideoAsync(path);
    }

    private void PickAudio()
    {
        if (IsAudioMerge)
        {
            var files = PickFiles("Add audio clips", AudioFilter);
            if (files.Count > 0) _ = AddAudioTracksAsync(files);
            return;
        }

        var path = PickFile("Choose audio", AudioFilter);
        if (path is not null) _ = LoadAudioAsync(path);
    }

    private void PickPdf()
    {
        var files = PickFiles("Add PDFs", PdfFilter);
        if (files.Count > 0) _ = AddPdfFilesAsync(files);
    }

    private static string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            CheckFileExists = true,
            Multiselect = false,
            Filter = filter,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static List<string> PickFiles(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            CheckFileExists = true,
            Multiselect = true,
            Filter = filter,
        };
        return dialog.ShowDialog() == true ? dialog.FileNames.ToList() : new List<string>();
    }

    private CancellationToken BeginLoad()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        return _loadCts.Token;
    }

    private async Task LoadPhotoAsync(string path)
    {
        var version = ++_loadVersion;
        var token = BeginLoad();
        try
        {
            IsPreviewBusy = true;
            Notice = "";
            PreviewMessage = "";
            StatusTitle = "Reading photo…";
            StatusDetail = Path.GetFileName(path);
            var info = await _photo.LoadInfoAsync(path, token);
            if (version != _loadVersion || _disposed) return;

            _photoBytes = info.Bytes;
            PhotoSourceWidth = info.Width;
            PhotoSourceHeight = info.Height;
            PhotoPath = path;
            PhotoPreview = null;
            SetCropRect(0, 0, 100, 100);
            CropAspectId = "Original";
            OnPropertyChanged(nameof(PhotoMeta));
            StatusTitle = "Photo ready";
            StatusDetail = "Drag the crop box in the preview. The dimmed area is what will be removed.";
            SchedulePhotoPreview(immediate: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _loadVersion)
            {
                StatusTitle = "Could not read the photo";
                Notice = ex.Message;
                Log.Warn("editor.photo", "Photo load failed", ("path", path), ("reason", ex.Message));
            }
        }
        finally
        {
            if (version == _loadVersion) IsPreviewBusy = false;
        }
    }

    private async Task LoadVideoAsync(string path)
    {
        var version = ++_loadVersion;
        var token = BeginLoad();
        try
        {
            IsPreviewBusy = true;
            Notice = "";
            PreviewMessage = "";
            ResetVideoPlaybackIssue();
            StatusTitle = "Reading video…";
            StatusDetail = Path.GetFileName(path);
            var info = await _media.ProbeAsync(path, token);
            if (version != _loadVersion || _disposed) return;
            if (!info.HasVideo)
                throw new InvalidOperationException("That file does not contain a video track.");

            _videoBytes = info.Bytes;
            _videoWidth = info.Width;
            _videoHeight = info.Height;
            _videoDuration = Math.Max(0, info.Duration);
            _videoStart = 0;
            _videoEnd = _videoDuration;
            _videoPosition = 0;
            VideoPreview = null;
            VideoFrameWidth = info.Width;
            VideoFrameHeight = info.Height;
            VideoPath = path;
            OnPropertyChanged(nameof(VideoDuration));
            OnPropertyChanged(nameof(VideoDurationMax));
            OnPropertyChanged(nameof(VideoStart));
            OnPropertyChanged(nameof(VideoEnd));
            OnPropertyChanged(nameof(VideoPosition));
            OnPropertyChanged(nameof(VideoStartText));
            OnPropertyChanged(nameof(VideoEndText));
            OnPropertyChanged(nameof(VideoPositionText));
            OnPropertyChanged(nameof(VideoClipLengthText));
            OnPropertyChanged(nameof(VideoMeta));
            StatusTitle = "Video ready";
            StatusDetail = "Press play, then drag the timeline handles or use the cut buttons.";
            RefreshCommands();
            ScheduleVideoFrame(immediate: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _loadVersion)
            {
                StatusTitle = "Could not read the video";
                Notice = ex.Message;
                Log.Warn("editor.video", "Video load failed", ("path", path), ("reason", ex.Message));
            }
        }
        finally
        {
            if (version == _loadVersion) IsPreviewBusy = false;
        }
    }

    private async Task LoadAudioAsync(string path)
    {
        var version = ++_loadVersion;
        var token = BeginLoad();
        try
        {
            IsPreviewBusy = true;
            Notice = "";
            PreviewMessage = "";
            StatusTitle = "Reading audio…";
            StatusDetail = Path.GetFileName(path);
            var info = await _media.ProbeAsync(path, token);
            if (version != _loadVersion || _disposed) return;
            if (!info.HasAudio)
                throw new InvalidOperationException("That file does not contain an audio track.");

            _audioBytes = info.Bytes;
            _audioDuration = Math.Max(0, info.Duration);
            _audioStart = 0;
            _audioEnd = _audioDuration;
            _audioPosition = 0;
            AudioPath = path;
            AudioWaveform = null;
            OnPropertyChanged(nameof(AudioDuration));
            OnPropertyChanged(nameof(AudioDurationMax));
            OnPropertyChanged(nameof(AudioStart));
            OnPropertyChanged(nameof(AudioEnd));
            OnPropertyChanged(nameof(AudioPosition));
            OnPropertyChanged(nameof(AudioStartText));
            OnPropertyChanged(nameof(AudioEndText));
            OnPropertyChanged(nameof(AudioPositionText));
            OnPropertyChanged(nameof(AudioClipLengthText));
            OnPropertyChanged(nameof(AudioStartPercent));
            OnPropertyChanged(nameof(AudioEndPercent));
            OnPropertyChanged(nameof(AudioMeta));
            StatusTitle = "Audio ready";
            StatusDetail = "Press play, then drag the trim handles on the waveform.";
            PreviewBadge = "WAVEFORM";
            RefreshCommands();
            AudioWaveform = await _media.WaveformAsync(path, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _loadVersion)
            {
                StatusTitle = "Could not read the audio";
                Notice = ex.Message;
                Log.Warn("editor.audio", "Audio load failed", ("path", path), ("reason", ex.Message));
            }
        }
        finally
        {
            if (version == _loadVersion) IsPreviewBusy = false;
        }
    }

    private async Task AddAudioTracksAsync(IEnumerable<string> paths)
    {
        var version = ++_loadVersion;
        var token = BeginLoad();
        try
        {
            IsPreviewBusy = true;
            Notice = "";
            AudioTrackItem? lastAdded = null;
            foreach (var path in paths)
            {
                if (AudioTracks.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                var info = await _media.ProbeAsync(path, token);
                if (version != _loadVersion || _disposed) return;
                if (!info.HasAudio)
                {
                    Notice = $"{Path.GetFileName(path)} has no audio track.";
                    continue;
                }
                lastAdded = new AudioTrackItem(path, info.Duration, info.Bytes);
                AudioTracks.Add(lastAdded);
            }

            if (lastAdded is not null) AudioSelectedTrack = lastAdded;
            if (AudioTracks.Count > 0)
            {
                StatusTitle = "Clips ready";
                StatusDetail = AudioMergeTotalText;
                PreviewMessage = "";
                PreviewBadge = AudioTracks.Count == 1 ? "1 CLIP" : $"{AudioTracks.Count} CLIPS";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _loadVersion)
            {
                StatusTitle = "Could not add a clip";
                Notice = ex.Message;
                Log.Warn("editor.audio", "Audio track add failed", ("reason", ex.Message));
            }
        }
        finally
        {
            if (version == _loadVersion) IsPreviewBusy = false;
        }
    }

    private void RemoveAudioTrack(AudioTrackItem? item)
    {
        if (item is null) return;
        var index = AudioTracks.IndexOf(item);
        AudioTracks.Remove(item);
        if (ReferenceEquals(AudioSelectedTrack, item))
            AudioSelectedTrack = AudioTracks.Count == 0 ? null : AudioTracks[Math.Clamp(index, 0, AudioTracks.Count - 1)];
        StatusDetail = AudioMergeTotalText;
    }

    private void MoveAudioTrack(int direction)
    {
        var item = AudioSelectedTrack;
        if (item is null) return;
        var index = AudioTracks.IndexOf(item);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= AudioTracks.Count) return;
        AudioTracks.Move(index, target);
        AudioSelectedTrack = item;
        StatusDetail = AudioMergeTotalText;
    }

    private void SplitAudioAtPlayhead()
    {
        if (!IsAudioMerge || AudioTracks.Count == 0) return;
        var position = AudioMergePosition;
        var track = AudioTracks.FirstOrDefault(t =>
            position > t.StartOffset + 0.04 && position < t.EndOffset - 0.04);
        if (track is null)
        {
            Notice = "Move the playhead inside a clip to split it.";
            return;
        }

        var local = position - track.StartOffset;
        var index = AudioTracks.IndexOf(track);
        var left = track.Slice(0, local);
        var right = track.Slice(local, track.Duration);
        AudioTracks[index] = left;
        AudioTracks.Insert(index + 1, right);
        AudioSelectedTrack = right;
        AudioMergePosition = right.StartOffset;
        StatusTitle = "Clip split";
        StatusDetail = $"Split {track.FileName} at {EditorTime.Format(position)}.";
        Notice = "";
    }

    private void ClearAudioTracks()
    {
        AudioTracks.Clear();
        AudioSelectedTrack = null;
        AudioMergePosition = 0;
        StatusTitle = "Clips cleared";
        StatusDetail = "Add two or more clips to build a new mix.";
    }

    private async Task AddPdfFilesAsync(IEnumerable<string> paths)
    {
        var version = ++_loadVersion;
        var token = BeginLoad();
        try
        {
            IsPreviewBusy = true;
            Notice = "";
            PdfQueueItem? lastAdded = null;
            foreach (var path in paths)
            {
                if (PdfFiles.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                var info = await _pdf.ProbeAsync(path, token);
                if (version != _loadVersion || _disposed) return;
                var item = new PdfQueueItem(info.Path, info.PageCount, info.Bytes);
                PdfFiles.Add(item);
                lastAdded = item;
            }

            if (lastAdded is not null) SelectedPdf = lastAdded;
            else if (SelectedPdf is null && PdfFiles.Count > 0) SelectedPdf = PdfFiles[0];

            if (PdfFiles.Count > 0)
            {
                StatusTitle = "PDF workspace ready";
                StatusDetail = "Browse pages, then merge all PDFs or split the selected document.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _loadVersion)
            {
                StatusTitle = "Could not add a PDF";
                Notice = ex.Message;
                Log.Warn("editor.pdf", "PDF add failed", ("reason", ex.Message));
            }
        }
        finally
        {
            if (version == _loadVersion) IsPreviewBusy = false;
        }
    }

    private void RemovePdf(PdfQueueItem? item)
    {
        if (item is null) return;
        var index = PdfFiles.IndexOf(item);
        var wasSelected = ReferenceEquals(SelectedPdf, item);
        PdfFiles.Remove(item);
        if (PdfFiles.Count == 0)
        {
            SelectedPdf = null;
            StatusDetail = "Add PDFs to start a new workspace.";
        }
        else if (wasSelected)
        {
            SelectedPdf = PdfFiles[Math.Clamp(index, 0, PdfFiles.Count - 1)];
            StatusDetail = PdfFilesSummary;
        }
        else
        {
            StatusDetail = PdfFilesSummary;
        }
    }

    private void MovePdf(int direction)
    {
        var item = SelectedPdf;
        if (item is null) return;
        var index = PdfFiles.IndexOf(item);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= PdfFiles.Count) return;
        PdfFiles.Move(index, target);
        SelectedPdf = item;
        StatusDetail = "Merge order updated.";
    }

    private void ClearPdfs()
    {
        PdfFiles.Clear();
        SelectedPdf = null;
        PdfThumbnails.Clear();
        PdfThumbnailNote = "";
        StatusTitle = "PDF workspace cleared";
        StatusDetail = "Add PDFs to merge, split or render pages.";
    }

    private void OnAudioTracksChanged()
    {
        UpdateAudioTimeline();
        OnPropertyChanged(nameof(AudioTrackCountText));
        OnPropertyChanged(nameof(AudioMergeTotalText));
        OnPropertyChanged(nameof(PreviewTitle));
        OnPropertyChanged(nameof(PreviewSubtitle));
        OnPropertyChanged(nameof(PreviewBadge));
        OnPropertyChanged(nameof(HasActiveEdit));
        OnPropertyChanged(nameof(CanExport));
        if (AudioSelectedTrack is not null && !AudioTracks.Contains(AudioSelectedTrack))
            AudioSelectedTrack = null;
        if (IsAudioMerge)
        {
            PreviewMessage = AudioTracks.Count == 0
                ? "Add two or more clips to preview the mix."
                : "";
            PreviewBadge = AudioTracks.Count switch
            {
                0 => "LIVE PREVIEW",
                1 => "1 CLIP",
                _ => $"{AudioTracks.Count} CLIPS",
            };
        }
        NotifyEditState();
    }

    private void OnPdfFilesChanged()
    {
        for (var i = 0; i < PdfFiles.Count; i++) PdfFiles[i].Order = i + 1;
        OnPropertyChanged(nameof(PdfFilesSummary));
        OnPropertyChanged(nameof(PdfTotalPages));
        OnPropertyChanged(nameof(PdfTotalPagesText));
        OnPropertyChanged(nameof(PreviewSubtitle));
        OnPropertyChanged(nameof(HasActiveEdit));
        OnPropertyChanged(nameof(CanExport));
        if (SelectedPdf is not null && !PdfFiles.Contains(SelectedPdf))
            SelectedPdf = PdfFiles.Count > 0 ? PdfFiles[0] : null;
        else if (SelectedPdf is null && PdfFiles.Count > 0)
            SelectedPdf = PdfFiles[0];
        NotifyEditState();
    }

    private void UpdateAudioTimeline()
    {
        var cursor = 0.0;
        var total = AudioTracks.Sum(t => Math.Max(0.01, t.Duration));
        for (var i = 0; i < AudioTracks.Count; i++)
        {
            var track = AudioTracks[i];
            track.Order = i + 1;
            track.StartOffset = cursor;
            track.BarWidth = total <= 0 ? 120 : Math.Max(18, 520 * Math.Max(0.01, track.Duration) / total);
            track.BarColor = i % 2 == 0 ? "#7C9CFF" : "#A98BFF";
            cursor += Math.Max(0.01, track.Duration);
        }
        AudioMergeDuration = cursor;
        if (AudioMergePosition > cursor) AudioMergePosition = cursor;
        OnPropertyChanged(nameof(AudioMergeTotalText));
        OnPropertyChanged(nameof(AudioTrackCountText));
        OnPropertyChanged(nameof(AudioMergeDuration));
        OnPropertyChanged(nameof(PreviewSubtitle));
        ExportCommand.RaiseCanExecuteChanged();
    }

    // ───────────────────────── preview ─────────────────────────

    private void SchedulePhotoPreview(bool immediate = false)
    {
        if (_tool != EditorTool.Photo) return;
        SchedulePreview(immediate);
    }

    private void ScheduleVideoFrame(bool immediate = false)
    {
        if (_tool != EditorTool.Video) return;
        SchedulePreview(immediate);
    }

    private void SchedulePreview(bool immediate = false)
    {
        if (_disposed) return;
        _previewCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _previewCts = cts;
        _ = PreviewAfterDelayAsync(cts, immediate);
    }

    private async Task PreviewAfterDelayAsync(CancellationTokenSource cts, bool immediate)
    {
        var token = cts.Token;
        try
        {
            IsPreviewBusy = true;
            if (!immediate) await Task.Delay(140, token);
            await RefreshPreviewAsync(token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && !_disposed)
            {
                PreviewMessage = "Preview unavailable: " + ex.Message;
                IsPreviewBusy = false;
            }
        }
        finally
        {
            if (ReferenceEquals(_previewCts, cts))
            {
                _previewCts = null;
                IsPreviewBusy = false;
            }
            cts.Dispose();
        }
    }

    private async Task RefreshPreviewAsync(CancellationToken token)
    {
        switch (_tool)
        {
            case EditorTool.Photo:
            {
                if (string.IsNullOrWhiteSpace(PhotoPath))
                {
                    PhotoPreview = null;
                    PreviewMessage = "Choose a photo to see the full frame and crop box here.";
                    PreviewBadge = "LIVE PREVIEW";
                    return;
                }

                IsPreviewBusy = true;
                var request = new PhotoEditRequest(PhotoPath, 0, 0, 100, 100,
                    EditorCaption.None, OutputDirectory, PhotoFormatId, (int)PhotoQuality, false);
                var preview = await _photo.RenderPreviewAsync(request, 1600, 1000, token);
                if (token.IsCancellationRequested) return;
                PhotoPreview = preview;
                PreviewMessage = "";
                PreviewBadge = $"{preview.PixelWidth} × {preview.PixelHeight}";
                return;
            }
            case EditorTool.Video:
            {
                if (string.IsNullOrWhiteSpace(VideoPath))
                {
                    VideoPreview = null;
                    PreviewMessage = "Choose a video to play it and set the cut points here.";
                    PreviewBadge = "LIVE PREVIEW";
                    return;
                }

                if (VideoPreview is null)
                {
                    IsPreviewBusy = true;
                    var frame = await _media.ExtractFrameAsync(VideoPath, VideoStart, 1000, token);
                    if (token.IsCancellationRequested) return;
                    VideoPreview = frame;
                    VideoFrameWidth = frame.PixelWidth;
                    VideoFrameHeight = frame.PixelHeight;
                }
                PreviewMessage = "";
                PreviewBadge = $"CUT {EditorTime.Format(VideoStart)} – {EditorTime.Format(VideoEnd)}";
                return;
            }
            case EditorTool.Audio:
            {
                if (IsAudioMerge)
                {
                    PreviewMessage = AudioTracks.Count == 0
                        ? "Add two or more clips to preview the mix."
                        : "";
                    PreviewBadge = AudioTracks.Count switch
                    {
                        0 => "LIVE PREVIEW",
                        1 => "1 CLIP",
                        _ => $"{AudioTracks.Count} CLIPS",
                    };
                }
                else
                {
                    PreviewMessage = AudioPath is null ? "Choose audio to see its waveform." : "";
                    PreviewBadge = AudioPath is null ? "LIVE PREVIEW" : "WAVEFORM";
                }
                return;
            }
            default:
            {
                if (string.IsNullOrWhiteSpace(PdfPath))
                {
                    PdfPreview = null;
                    PreviewMessage = "Add PDFs to browse their pages here.";
                    PreviewBadge = "LIVE PREVIEW";
                    return;
                }

                IsPreviewBusy = true;
                var preview = await _pdf.RenderPreviewAsync(PdfPath!, PdfPage, 1800, token);
                if (token.IsCancellationRequested) return;
                PdfPreview = preview;
                PreviewMessage = "";
                PreviewBadge = $"PAGE {PdfPage} / {Math.Max(1, PdfPageCount)}";
                return;
            }
        }
    }

    private async void SchedulePdfThumbnails(PdfQueueItem item)
    {
        var version = ++_pdfThumbVersion;
        var token = _lifetime.Token;
        try
        {
            var thumbnails = await _pdf.RenderThumbnailsAsync(item.Path, 150, 60, token);
            if (version != _pdfThumbVersion || _disposed || !ReferenceEquals(SelectedPdf, item)) return;

            PdfThumbnails.Clear();
            for (var i = 0; i < thumbnails.Count; i++)
                PdfThumbnails.Add(new PdfThumbnailItem(i + 1, thumbnails[i]));
            PdfThumbnailNote = item.PageCount > thumbnails.Count
                ? $"Showing the first {thumbnails.Count} pages. Use the page arrows for the rest."
                : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug("editor.pdf", "PDF thumbnails could not be rendered",
                ("path", item.Path), ("reason", ex.Message));
        }
    }

    // ───────────────────────── pdf selection ─────────────────────────

    private void ParsePdfSelection(bool updatePreview)
    {
        if (PdfPageCount <= 0)
        {
            PdfSelectionLabel = "Choose a PDF first";
            PdfSelectionError = "";
            return;
        }

        try
        {
            var pages = EditorPageRange.Parse(PdfPageText, PdfPageCount);
            PdfSelectionError = "";
            PdfSelectionLabel = EditorPageRange.Describe(pages);
            if (updatePreview && pages.Count > 0)
                PdfPage = pages[0];
        }
        catch (FormatException ex)
        {
            PdfSelectionLabel = "Check the page range";
            PdfSelectionError = ex.Message;
        }
    }

    private IReadOnlyList<int>? ParsePagesOrNotice()
    {
        if (PdfPageCount <= 0)
        {
            Notice = "Choose a PDF first.";
            return null;
        }

        try
        {
            var pages = EditorPageRange.Parse(PdfPageText, PdfPageCount);
            PdfSelectionError = "";
            PdfSelectionLabel = EditorPageRange.Describe(pages);
            return pages;
        }
        catch (FormatException ex)
        {
            PdfSelectionError = ex.Message;
            Notice = ex.Message;
            return null;
        }
    }

    // ───────────────────────── export ─────────────────────────

    private void RunPdfAction(PdfEditMode mode)
    {
        if (IsBusy) return;
        _pdfMode = mode;
        OnPropertyChanged(nameof(PdfModeId));
        OnPropertyChanged(nameof(PdfMode));
        OnPropertyChanged(nameof(IsPdfMerge));
        OnPropertyChanged(nameof(IsPdfSplit));
        OnPropertyChanged(nameof(IsPdfImages));
        OnPropertyChanged(nameof(ExportButtonText));
        OnPropertyChanged(nameof(CanExport));
        _ = ExportAsync(mode);
    }

    private async Task ExportAsync(PdfEditMode? pdfMode = null)
    {
        if (IsBusy || !CanExport) return;
        if (pdfMode is not null && _tool == EditorTool.Pdf) _pdfMode = pdfMode.Value;

        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
        var token = _exportCts.Token;
        IsBusy = true;
        Progress = 0;
        ProgressText = "Preparing";
        Notice = "";
        StatusTitle = "Working…";
        StatusDetail = ToolHint;
        LastOutput = null;

        var progress = new Progress<EditorProgress>(value =>
        {
            Progress = value.Fraction;
            ProgressText = value.Stage;
        });

        try
        {
            EditorResult result;
            switch (_tool)
            {
                case EditorTool.Photo:
                    result = await _photo.ExportAsync(new PhotoEditRequest(
                        PhotoPath!, CropLeft, CropTop, CropRight, CropBottom, EditorCaption.None,
                        OutputDirectory, PhotoFormatId, (int)PhotoQuality, false), progress, token);
                    break;
                case EditorTool.Video:
                    result = await _media.ExportVideoAsync(new VideoEditRequest(
                        VideoPath!, VideoStart, VideoEnd, EditorCaption.None, OutputDirectory,
                        (int)VideoQuality, VideoAudioBitrate, false, VideoFrameWidth, VideoFrameHeight),
                        progress, token);
                    break;
                case EditorTool.Audio:
                    result = IsAudioMerge
                        ? await _media.MergeAudioAsync(new AudioMergeRequest(
                            AudioTracks.Select(t => t.Path).ToList(), OutputDirectory,
                            AudioFormatId, AudioBitrate, false,
                            AudioTracks.Select(t => new AudioMergeClip(t.Path, t.StartSeconds, t.EndSeconds)).ToList()),
                            progress, token)
                        : await _media.ExportAudioTrimAsync(new AudioTrimRequest(
                            AudioPath!, AudioStart, AudioEnd, OutputDirectory,
                            AudioFormatId, AudioBitrate, false), progress, token);
                    break;
                default:
                    result = await ExportPdfAsync(progress, token);
                    break;
            }

            if (result.Success)
            {
                Progress = 1;
                ProgressText = "Saved";
                StatusTitle = result.Outputs.Count == 1 ? "Saved" : $"Saved {result.Outputs.Count} files";
                StatusDetail = result.Outputs.Count == 1
                    ? Path.GetFileName(result.Outputs[0])
                    : $"In {FileSize.FormatPath(OutputDirectory, 42)}";
                LastOutput = result.FirstOutput;
                Notice = "";
                Log.Info("editor.export", "Editor export finished",
                    ("tool", _tool.ToString()), ("outputs", result.Outputs.Count.ToString()),
                    ("elapsed", $"{result.Elapsed.TotalSeconds:0.0}s"));
            }
            else if (result.Canceled)
            {
                ProgressText = "Canceled";
                StatusTitle = "Edit canceled";
                StatusDetail = "Nothing was saved.";
            }
            else
            {
                ProgressText = "Not saved";
                StatusTitle = result.Error ?? "The edit could not be saved.";
                StatusDetail = result.Detail ?? "Try a different file or output folder.";
                Notice = result.Detail ?? result.Error ?? "";
                Log.Warn("editor.export", "Editor export failed",
                    ("tool", _tool.ToString()), ("reason", result.Error), ("detail", result.Detail));
            }
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Canceled";
            StatusTitle = "Edit canceled";
            StatusDetail = "Nothing was saved.";
        }
        catch (Exception ex)
        {
            ProgressText = "Not saved";
            StatusTitle = "The edit could not be saved.";
            StatusDetail = ex.Message;
            Notice = ex.Message;
            Log.Warn("editor.export", "Editor export threw", ("tool", _tool.ToString()), ("reason", ex.Message));
        }
        finally
        {
            IsBusy = false;
            _exportCts?.Dispose();
            _exportCts = null;
            RefreshCommands();
        }
    }

    private async Task<EditorResult> ExportPdfAsync(IProgress<EditorProgress> progress, CancellationToken token)
    {
        if (_pdfMode == PdfEditMode.Merge)
        {
            return await _pdf.MergeAsync(new PdfMergeRequest(
                PdfFiles.Select(p => p.Path).ToList(), OutputDirectory, false), progress, token);
        }

        if (string.IsNullOrWhiteSpace(PdfPath))
            return EditorResult.Fail("Choose a PDF first.", null, TimeSpan.Zero);

        var pages = ParsePagesOrNotice();
        if (pages is null)
            return EditorResult.Fail(PdfSelectionError, null, TimeSpan.Zero);

        if (_pdfMode == PdfEditMode.Split)
        {
            return await _pdf.SplitAsync(new PdfSplitRequest(
                PdfPath, pages, PdfSeparatePages, OutputDirectory, false), progress, token);
        }

        return await _pdf.ExportImagesAsync(new PdfImageRequest(
            PdfPath, pages, OutputDirectory, PdfImageFormatId,
            PdfImageEdge, (int)PdfImageQuality, false), progress, token);
    }

    private void Cancel()
    {
        _exportCts?.Cancel();
        ProgressText = "Stopping…";
    }

    private void CancelPreview()
    {
        _loadCts?.Cancel();
        _previewCts?.Cancel();
        IsPreviewBusy = false;
        ProgressText = "Preview stopped";
        StatusTitle = "Preview stopped";
        StatusDetail = "Adjust the file or choose another one.";
        Notice = "";
    }

    private void CancelEdit()
    {
        if (IsBusy) return;
        _loadCts?.Cancel();
        _previewCts?.Cancel();
        switch (_tool)
        {
            case EditorTool.Photo:
                PhotoPath = null;
                PhotoPreview = null;
                PhotoSourceWidth = 0;
                PhotoSourceHeight = 0;
                SetCropRect(0, 0, 100, 100);
                StatusDetail = "Choose a photo to start again.";
                break;
            case EditorTool.Video:
                VideoPath = null;
                VideoPreview = null;
                VideoFrameWidth = 0;
                VideoFrameHeight = 0;
                _videoStart = 0;
                _videoEnd = 0;
                _videoPosition = 0;
                OnPropertyChanged(nameof(VideoStart));
                OnPropertyChanged(nameof(VideoEnd));
                OnPropertyChanged(nameof(VideoPosition));
                OnPropertyChanged(nameof(VideoStartText));
                OnPropertyChanged(nameof(VideoEndText));
                OnPropertyChanged(nameof(VideoPositionText));
                OnPropertyChanged(nameof(VideoClipLengthText));
                ResetVideoPlaybackIssue();
                StatusDetail = "Choose a video to start again.";
                break;
            case EditorTool.Audio:
                if (IsAudioMerge)
                {
                    AudioTracks.Clear();
                    AudioSelectedTrack = null;
                    AudioMergePosition = 0;
                    StatusDetail = "Add clips to start a new mix.";
                }
                else
                {
                    AudioPath = null;
                    AudioWaveform = null;
                    _audioStart = 0;
                    _audioEnd = 0;
                    _audioPosition = 0;
                    OnPropertyChanged(nameof(AudioStart));
                    OnPropertyChanged(nameof(AudioEnd));
                    OnPropertyChanged(nameof(AudioPosition));
                    OnPropertyChanged(nameof(AudioStartText));
                    OnPropertyChanged(nameof(AudioEndText));
                    OnPropertyChanged(nameof(AudioPositionText));
                    OnPropertyChanged(nameof(AudioClipLengthText));
                    StatusDetail = "Choose audio to start again.";
                }
                break;
            default:
                PdfFiles.Clear();
                SelectedPdf = null;
                PdfPreview = null;
                PdfThumbnails.Clear();
                PdfThumbnailNote = "";
                StatusDetail = "Add PDFs to start again.";
                break;
        }

        IsMediaPlaying = false;
        PreviewMessage = "";
        PreviewBadge = "LIVE PREVIEW";
        Notice = "";
        StatusTitle = "Edit canceled";
        NotifyEditState();
        RefreshCommands();
    }

    public void CancelOnShutdown()
    {
        _loadCts?.Cancel();
        _previewCts?.Cancel();
        _exportCts?.Cancel();
        _lifetime.Cancel();
    }

    // ───────────────────────── output & shell ─────────────────────────

    private void ChangeOutput()
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Choose where edited files are saved",
                InitialDirectory = Directory.Exists(OutputDirectory)
                    ? OutputDirectory
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            if (dialog.ShowDialog() != true) return;
            _settings.OutputDirectory = dialog.FolderName;
            Log.Info("editor.output", "Editor output folder changed", ("path", dialog.FolderName));
        }
        catch (Exception ex)
        {
            Notice = "The folder picker could not be opened.";
            Log.Warn("editor.output", "Folder picker failed", ("reason", ex.Message));
        }
    }

    private void OpenOutput()
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", OutputDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notice = "The output folder could not be opened.";
            Log.Warn("editor.output", "Open output folder failed", ("reason", ex.Message));
        }
    }

    private void RevealResult()
    {
        if (!CanRevealResult) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LastOutput}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notice = "The saved file could not be revealed.";
            Log.Warn("editor.output", "Reveal result failed", ("reason", ex.Message));
        }
    }

    public void Activate()
    {
        OnPropertyChanged(nameof(OutputDisplay));
        OnPropertyChanged(nameof(EngineAvailable));
        OnPropertyChanged(nameof(EngineHint));
        if (string.IsNullOrWhiteSpace(StatusTitle) || StatusTitle == "Ready to edit")
        {
            StatusTitle = ToolReadyText;
            StatusDetail = ToolHint;
        }
        SchedulePreview(immediate: true);
    }

    private void NotifyEditState()
    {
        OnPropertyChanged(nameof(HasActiveEdit));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        PickCurrentCommand.RaiseCanExecuteChanged();
        PickPhotoCommand.RaiseCanExecuteChanged();
        PickVideoCommand.RaiseCanExecuteChanged();
        PickAudioCommand.RaiseCanExecuteChanged();
        PickPdfCommand.RaiseCanExecuteChanged();
        ResetCropCommand.RaiseCanExecuteChanged();
        CenterCropCommand.RaiseCanExecuteChanged();
        CropAspectCommand.RaiseCanExecuteChanged();
        UseWholeVideoCommand.RaiseCanExecuteChanged();
        SetVideoStartCommand.RaiseCanExecuteChanged();
        SetVideoEndCommand.RaiseCanExecuteChanged();
        UseWholeAudioCommand.RaiseCanExecuteChanged();
        SetAudioStartCommand.RaiseCanExecuteChanged();
        SetAudioEndCommand.RaiseCanExecuteChanged();
        RemoveAudioTrackCommand.RaiseCanExecuteChanged();
        MoveAudioTrackUpCommand.RaiseCanExecuteChanged();
        MoveAudioTrackDownCommand.RaiseCanExecuteChanged();
        SplitAudioAtPlayheadCommand.RaiseCanExecuteChanged();
        ClearAudioTracksCommand.RaiseCanExecuteChanged();
        RemovePdfCommand.RaiseCanExecuteChanged();
        MovePdfUpCommand.RaiseCanExecuteChanged();
        MovePdfDownCommand.RaiseCanExecuteChanged();
        ClearPdfsCommand.RaiseCanExecuteChanged();
        ExportPdfMergeCommand.RaiseCanExecuteChanged();
        ExportPdfSplitCommand.RaiseCanExecuteChanged();
        ExportPdfImagesCommand.RaiseCanExecuteChanged();
        ChangeOutputCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        CancelEditCommand.RaiseCanExecuteChanged();
        CancelPreviewCommand.RaiseCanExecuteChanged();
        RevealResultCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanPdfMerge));
        OnPropertyChanged(nameof(CanPdfSplit));
        OnPropertyChanged(nameof(ExportButtonText));
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.OutputDirectory))
        {
            OnPropertyChanged(nameof(OutputDirectory));
            OnPropertyChanged(nameof(OutputDisplay));
        }
        else if (e.PropertyName is nameof(AppSettings.FfmpegPath))
        {
            OnPropertyChanged(nameof(EngineAvailable));
            OnPropertyChanged(nameof(EngineHint));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.PropertyChanged -= OnSettingsChanged;
        try { _loadCts?.Cancel(); } catch { }
        try { _previewCts?.Cancel(); } catch { }
        try { _exportCts?.Cancel(); } catch { }
        try { _lifetime.Cancel(); } catch { }
        _lifetime.Dispose();
        _loadCts?.Dispose();
        _previewCts?.Dispose();
        _exportCts?.Dispose();
    }
}
