using System.Windows;

namespace Nasaq.Services;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed class ToastMessage
{
    public ToastMessage(ToastKind kind, string title, string message, TimeSpan? duration = null)
    {
        Kind = kind;
        Title = title;
        Message = message;
        Duration = duration ?? (kind == ToastKind.Error ? TimeSpan.FromSeconds(7) : TimeSpan.FromSeconds(4.5));
    }

    public ToastKind Kind { get; }
    public string Title { get; }
    public string Message { get; }
    public TimeSpan Duration { get; }
}

/// <summary>
/// App-wide notification channel. The toast host in the main window listens
/// here, so any feature can surface a friendly error without a native dialog.
/// </summary>
public static class ToastService
{
    public static event EventHandler<ToastMessage>? Raised;

    public static void Show(ToastKind kind, string title, string message, TimeSpan? duration = null)
    {
        var toast = new ToastMessage(kind, title, message, duration);
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => Raised?.Invoke(null, toast)));
            return;
        }

        Raised?.Invoke(null, toast);
    }

    public static void Info(string title, string message) => Show(ToastKind.Info, title, message);
    public static void Success(string title, string message) => Show(ToastKind.Success, title, message);
    public static void Warning(string title, string message) => Show(ToastKind.Warning, title, message);
    public static void Error(string title, string message) => Show(ToastKind.Error, title, message);
}
