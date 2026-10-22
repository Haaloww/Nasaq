using System;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Nasaq.ViewModels;

namespace Nasaq.Views;

public partial class InstallerView : UserControl
{
    public InstallerView()
    {
        InitializeComponent();
    }

    private InstallerViewModel? Vm => DataContext as InstallerViewModel;

    public void FocusLink()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            LinkBox.Focus();
            LinkBox.CaretIndex = LinkBox.Text.Length;
        }), DispatcherPriority.Input);
    }

    private void LinkBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Vm?.InstallCommand.CanExecute(null) == true)
        {
            Vm.InstallCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void LinkBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (LinkBox.IsKeyboardFocusWithin) return;
        LinkBox.Focus();
        e.Handled = true;
    }
}
