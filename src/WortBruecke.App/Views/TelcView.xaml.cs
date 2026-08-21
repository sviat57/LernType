using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WortBruecke.App.ViewModels;

namespace WortBruecke.App.Views;

public partial class TelcView : UserControl
{
    public TelcView()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is TelcViewModel viewModel)
        {
            viewModel.Activate();
        }
        Dispatcher.BeginInvoke(() =>
        {
            TelcInputBox.Focus();
            Keyboard.Focus(TelcInputBox);
        }, DispatcherPriority.Input);
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is TelcViewModel viewModel)
        {
            viewModel.CancelOnlineAnalysis();
        }
    }
}
