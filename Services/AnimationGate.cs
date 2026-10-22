using System.Windows;

namespace Nasaq.Services;

/// <summary>
/// One switch for every transition and looping animation in the app.
/// Code-driven animations check <see cref="IsEnabled"/> before they start, while
/// XAML animations bind to the inherited <see cref="EnabledProperty"/> attached
/// property so template storyboards stop immediately when motion is turned off.
/// </summary>
public static class AnimationGate
{
    public static bool IsEnabled { get; private set; } = true;

    public static event EventHandler? Changed;

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(AnimationGate),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

    public static void SetEnabled(DependencyObject element, bool value) =>
        element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) =>
        (bool)element.GetValue(EnabledProperty);

    /// <summary>
    /// Applies the value to every open window (the attached property inherits
    /// into their whole visual tree) and notifies listeners such as the toast host.
    /// </summary>
    public static void Apply(bool enabled)
    {
        IsEnabled = enabled;

        if (Application.Current is not null)
        {
            foreach (Window window in Application.Current.Windows)
                window.SetValue(EnabledProperty, enabled);
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Pushes the current gate value onto a newly created window.</summary>
    public static void Adopt(Window window) => window.SetValue(EnabledProperty, IsEnabled);
}
