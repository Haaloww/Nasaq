using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Nasaq.Services;

namespace Nasaq.Controls;

public partial class ToastHost : UserControl
{
    private const int MaxToasts = 4;

    private readonly ObservableCollection<ToastMessage> _toasts = new();
    private readonly Dictionary<ToastMessage, FrameworkElement> _cards = new();
    private readonly Dictionary<ToastMessage, DispatcherTimer> _timers = new();

    public ToastHost()
    {
        InitializeComponent();
        Items.ItemsSource = _toasts;
        Loaded += ToastHost_Loaded;
        Unloaded += ToastHost_Unloaded;
    }

    private void ToastHost_Loaded(object sender, RoutedEventArgs e)
    {
        ToastService.Raised -= OnToastRaised;
        ToastService.Raised += OnToastRaised;
        AnimationGate.Changed -= AnimationGate_Changed;
        AnimationGate.Changed += AnimationGate_Changed;
    }

    private void ToastHost_Unloaded(object sender, RoutedEventArgs e)
    {
        ToastService.Raised -= OnToastRaised;
        AnimationGate.Changed -= AnimationGate_Changed;
        foreach (var timer in _timers.Values) timer.Stop();
        _timers.Clear();
        _cards.Clear();
        _toasts.Clear();
    }

    private void OnToastRaised(object? sender, ToastMessage toast)
    {
        _toasts.Add(toast);
        while (_toasts.Count > MaxToasts)
            RemoveNow(_toasts[0]);
    }

    private void ToastCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement card || card.DataContext is not ToastMessage toast) return;

        _cards[toast] = card;
        card.Opacity = 1;
        var transform = EnsureSlide(card);
        transform.X = 0;

        if (AnimationGate.IsEnabled)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            card.Opacity = 0;
            card.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease });

            transform.X = 26;
            transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(26, 0, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease });
        }

        StartTimer(toast);
    }

    private void StartTimer(ToastMessage toast)
    {
        StopTimer(toast);
        var timer = new DispatcherTimer { Interval = toast.Duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Dismiss(toast);
        };
        _timers[toast] = timer;
        timer.Start();
    }

    private void StopTimer(ToastMessage toast)
    {
        if (!_timers.TryGetValue(toast, out var timer)) return;
        timer.Stop();
        _timers.Remove(toast);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ToastMessage toast)
            Dismiss(toast);
    }

    private void Dismiss(ToastMessage toast)
    {
        if (!_toasts.Contains(toast)) return;
        StopTimer(toast);

        if (!_cards.TryGetValue(toast, out var card) || !AnimationGate.IsEnabled)
        {
            RemoveNow(toast);
            return;
        }

        if (card.Tag as string == "closing") return;
        card.Tag = "closing";

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(card.Opacity, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = ease,
        };
        fade.Completed += (_, _) => RemoveNow(toast);
        card.BeginAnimation(OpacityProperty, fade);

        var transform = EnsureSlide(card);
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(transform.X, 24, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
    }

    private void RemoveNow(ToastMessage toast)
    {
        StopTimer(toast);
        _cards.Remove(toast);
        _toasts.Remove(toast);
    }

    private void AnimationGate_Changed(object? sender, EventArgs e)
    {
        foreach (var toast in _toasts.ToList())
        {
            if (!_cards.TryGetValue(toast, out var card)) continue;

            if (card.Tag as string == "closing")
            {
                RemoveNow(toast);
                continue;
            }

            card.BeginAnimation(OpacityProperty, null);
            card.Opacity = 1;
            var transform = EnsureSlide(card);
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
        }
    }

    /// <summary>
    /// Template transforms can be frozen by WPF. Replace a frozen one before
    /// animating so the toast never throws a read-only Freezable exception.
    /// </summary>
    private static TranslateTransform EnsureSlide(FrameworkElement card)
    {
        if (card.RenderTransform is TranslateTransform transform && !transform.IsFrozen)
            return transform;

        var fresh = new TranslateTransform();
        card.RenderTransform = fresh;
        return fresh;
    }
}
