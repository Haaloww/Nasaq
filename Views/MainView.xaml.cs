using System.Windows;
using System.Windows.Controls;
using Nasaq.ViewModels;

namespace Nasaq.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        ImageDropZone.ImagesAccepted += paths => Vm?.AddPaths(paths);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>
    /// True when the pointer is over the image preview zone. The window-level
    /// drop handlers use this to step aside and let the zone validate the drop.
    /// </summary>
    public bool IsPointOverImageZone(DragEventArgs e)
    {
        if (!IsVisible || ImageDropZone.ActualWidth <= 0 || ImageDropZone.ActualHeight <= 0)
            return false;

        var point = e.GetPosition(ImageDropZone);
        return point.X >= 0 && point.Y >= 0
            && point.X <= ImageDropZone.ActualWidth
            && point.Y <= ImageDropZone.ActualHeight;
    }
}
