using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Nasaq.Core;
using Nasaq.Services;
using Nasaq.ViewModels;

namespace Nasaq.Views;

public partial class DropShelfWindow : Window
{
    private readonly DispatcherTimer _autoClose = new() { Interval = TimeSpan.FromSeconds(20) };
    private bool _dragging;
    private bool _holdOpen;

    public event Action<IReadOnlyList<string>>? FilesDropped;
    public event EventHandler? OpenAppRequested;

    public DropShelfWindow()
    {
        InitializeComponent();
        AnimationGate.Adopt(this);
        _autoClose.Tick += (_, _) => { if (!_holdOpen) HideShelf(); };
        MouseEnter += (_, _) => _autoClose.Stop();
        MouseLeave += (_, _) => { if (IsVisible && !_holdOpen && !IsAnyTargetOpen()) _autoClose.Start(); };

        foreach (var target in new[] { ImageTarget, AudioTarget, VideoTarget, DocumentTarget })
        {
            target.DropDownOpened += (_, _) => _autoClose.Stop();
            target.DropDownClosed += (_, _) =>
            {
                if (IsVisible && !_holdOpen && !IsAnyTargetOpen()) _autoClose.Start();
            };
        }
    }

    public void ShowShelf()
    {
        PositionNearCursor();

        if (!IsVisible) Show();
        Topmost = true;
        if (AnimationGate.IsEnabled)
        {
            Opacity = 0;
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
            BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        }

        _autoClose.Stop();
        if (!_holdOpen) _autoClose.Start();
        Log.Debug("shelf.show", "Drop shelf summoned",
            ("left", Left.ToString("0")), ("top", Top.ToString("0")));
    }

    public void HideShelf()
    {
        _autoClose.Stop();
        _holdOpen = false;
        Hide();
    }

    public void Toggle()
    {
        if (IsVisible) HideShelf();
        else ShowShelf();
    }

    private bool IsAnyTargetOpen() =>
        FindVisualChildren<ComboBox>(this).Any(c => c.IsDropDownOpen);

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    private void PositionNearCursor()
    {
        try
        {
            if (!GetCursorPos(out var point)) return;

            var dpi = GetDpiForSystem();
            try
            {
                var handle = new WindowInteropHelper(this).EnsureHandle();
                if (handle != IntPtr.Zero) dpi = GetDpiForWindow(handle);
            }
            catch { }

            var scale = dpi / 96.0;
            var work = SystemParameters.WorkArea;
            var cursorX = point.X / scale;
            var cursorY = point.Y / scale;

            // Appear just below the cursor, centred, so a dragged file is only
            // a short move away from the drop surface.
            var x = cursorX - Width / 2;
            var y = cursorY + 26;

            if (x + Width > work.Right - 6) x = work.Right - Width - 6;
            if (x < work.Left + 6) x = work.Left + 6;
            if (y + Height > work.Bottom - 6) y = cursorY - Height - 26;
            if (y < work.Top + 6) y = work.Top + 6;

            Left = x;
            Top = y;
        }
        catch (Exception ex)
        {
            Log.Warn("shelf.position", "Could not position the shelf near the cursor", ("reason", ex.Message));
            var work = SystemParameters.WorkArea;
            Left = work.Right - Width - 40;
            Top = work.Bottom - Height - 40;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => HideShelf();

    private void OpenApp_Click(object sender, RoutedEventArgs e)
    {
        HideShelf();
        OpenAppRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ChangeOutput_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.BrowseOutput(this);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideShelf();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && ConvertButton.IsEnabled && !IsAnyTargetOpen())
        {
            e.Handled = true;
            StartConversion();
        }
    }

    private void Convert_Click(object sender, RoutedEventArgs e) => StartConversion();

    private void StartConversion()
    {
        if (DataContext is not MainViewModel vm || !vm.CanConvert) return;

        Log.Info("shelf.convert", "Conversion requested from the drop shelf",
            ("queued", vm.PendingCount.ToString()));
        HideShelf();
        _ = vm.StartAsync();
    }

    private void Surface_DragEnter(object sender, DragEventArgs e) => SetDragVisual(true, e);

    private void Surface_DragOver(object sender, DragEventArgs e) => SetDragVisual(true, e);

    private void Surface_DragLeave(object sender, DragEventArgs e) => SetDragVisual(false, e);

    private void SetDragVisual(bool active, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        if (_dragging == active) return;
        _dragging = active;

        if (active)
        {
            _autoClose.Stop();
            DropSurface.Background = (System.Windows.Media.Brush)FindResource("Brush.AccentSoft");
            Dash.Stroke = (System.Windows.Media.Brush)FindResource("Brush.Accent");
            Headline.Text = "Release to queue";
            ShelfGlyph.Text = "\uE73E";
        }
        else
        {
            DropSurface.Background = (System.Windows.Media.Brush)FindResource("Brush.Surface");
            Dash.Stroke = (System.Windows.Media.Brush)FindResource("Brush.BorderStrong");
            Headline.Text = "Drop files here";
            ShelfGlyph.Text = "\uE896";
            if (!_holdOpen && !IsAnyTargetOpen()) _autoClose.Start();
        }
    }

    private void Surface_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            SetDragVisual(false, e);
            return;
        }
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            SetDragVisual(false, e);
            return;
        }

        // Keep the shelf visible until the user converts or closes it.
        _holdOpen = true;
        _autoClose.Stop();
        SetDragVisual(false, e);

        Log.Info("shelf.drop", $"Drop shelf received {paths.Length} path(s)",
            ("paths", string.Join(", ", paths.Take(6))));
        FilesDropped?.Invoke(paths);

        // Activate after the drop so Enter can reach the default Convert button.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsVisible) return;
            Activate();
            if (ConvertButton.IsEnabled) ConvertButton.Focus();
            else Focus();
        }), DispatcherPriority.ApplicationIdle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
