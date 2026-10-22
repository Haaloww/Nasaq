using System.IO;
using Nasaq.Core;

namespace Nasaq.ViewModels;

public enum JobState
{
    Queued,
    Converting,
    Completed,
    Failed,
    Skipped,
    Canceled
}

public sealed class QueueItem : ObservableObject
{
    private OutputFormat _target;
    private JobState _state = JobState.Queued;
    private double _progress;
    private string _stage = "Waiting";
    private string? _outputPath;
    private string _detail = "";
    private string _statusText = "Waiting";
    private long _outputSize;

    public QueueItem(string sourcePath, MediaCategory category, long size, OutputFormat target, JobState state)
    {
        SourcePath = sourcePath;
        Category = category;
        SizeBytes = size;
        _target = target;
        _state = state;
        _statusText = state == JobState.Skipped ? "Unsupported file type" : "Waiting";
        Id = Guid.NewGuid().ToString("N")[..8];
    }

    public string Id { get; }
    public string SourcePath { get; }
    public MediaCategory Category { get; }
    public long SizeBytes { get; }
    public string FileName => Path.GetFileName(SourcePath);
    public string Extension => Path.GetExtension(SourcePath).TrimStart('.').ToUpperInvariant();
    public string CategoryName => FormatCatalog.CategoryName(Category);
    public string Glyph => FormatCatalog.CategoryGlyph(Category);
    public string SizeText => FileSize.Format(SizeBytes);

    public string Meta => Category == MediaCategory.Unknown
        ? $"{Extension} · unsupported"
        : $"{Extension} · {SizeText}";

    public OutputFormat Target
    {
        get => _target;
        set
        {
            if (Set(ref _target, value))
            {
                OnPropertyChanged(nameof(TargetLabel));
                OnPropertyChanged(nameof(TargetDescription));
            }
        }
    }

    public string TargetLabel => Target.Label;
    public string TargetDescription => Target.Hint;

    public JobState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(IsConverting));
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(IsQueued));
            OnPropertyChanged(nameof(IsCanceled));
            OnPropertyChanged(nameof(IsRemovable));
            OnPropertyChanged(nameof(StateLabel));
        }
    }

    public bool IsConverting => State == JobState.Converting;
    public bool IsCompleted => State == JobState.Completed;
    public bool IsFailed => State == JobState.Failed;
    public bool IsQueued => State == JobState.Queued;
    public bool IsCanceled => State == JobState.Canceled;
    public bool IsRemovable => State != JobState.Converting;

    public string StateLabel => State switch
    {
        JobState.Queued => "Waiting",
        JobState.Converting => "Converting",
        JobState.Completed => "Done",
        JobState.Failed => "Failed",
        JobState.Skipped => "Skipped",
        JobState.Canceled => "Cancelled",
        _ => "—"
    };

    public double Progress
    {
        get => _progress;
        set
        {
            if (!Set(ref _progress, Math.Clamp(value, 0, 100))) return;
            OnPropertyChanged(nameof(ProgressText));
        }
    }

    public string ProgressText => State == JobState.Converting
        ? $"{Progress:0}%"
        : State == JobState.Completed ? "100%" : "";

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value);
    }

    public string? OutputPath
    {
        get => _outputPath;
        private set
        {
            if (Set(ref _outputPath, value)) OnPropertyChanged(nameof(OutputName));
        }
    }

    public string OutputName => OutputPath is null ? "" : Path.GetFileName(OutputPath);
    public long OutputSize => _outputSize;

    public void UpdateProgress(double percent, string stage)
    {
        Progress = percent;
        _stage = stage;
        StatusText = string.IsNullOrWhiteSpace(stage) ? "Converting…" : stage;
        OnPropertyChanged(nameof(ProgressText));
    }

    public void MarkConverting()
    {
        State = JobState.Converting;
        Progress = 0;
        StatusText = "Starting…";
        Detail = "";
        OutputPath = null;
    }

    public void MarkCompleted(string outputPath, string engine, TimeSpan elapsed)
    {
        OutputPath = outputPath;
        try { _outputSize = new FileInfo(outputPath).Length; } catch { _outputSize = 0; }
        Progress = 100;
        State = JobState.Completed;
        StatusText = $"Saved · {FileSize.Format(_outputSize)} · {elapsed.TotalSeconds:0.0}s";
        Detail = $"{engine} · {outputPath}";
    }

    public void MarkFailed(string message, string? detail)
    {
        State = JobState.Failed;
        Progress = 0;
        StatusText = message;
        Detail = string.IsNullOrWhiteSpace(detail) ? message : detail!;
    }

    public void MarkSkipped(string message)
    {
        State = JobState.Skipped;
        StatusText = message;
        Detail = message;
    }

    public void MarkCanceled()
    {
        State = JobState.Canceled;
        Progress = 0;
        StatusText = "Cancelled";
    }

    public void ResetToQueued()
    {
        State = JobState.Queued;
        Progress = 0;
        StatusText = "Waiting";
        Detail = "";
        OutputPath = null;
    }
}
