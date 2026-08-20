using System.Windows;
using System.Windows.Controls;
using WortBruecke.App.ViewModels;

namespace WortBruecke.App.Views;

public partial class SettingsView : UserControl
{
    private bool _synchronizing;

    public SettingsView() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel && ApiKeyBox.Password != viewModel.ApiKey)
        {
            _synchronizing = true;
            ApiKeyBox.Password = viewModel.ApiKey;
            _synchronizing = false;
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_synchronizing && DataContext is SettingsViewModel viewModel)
        {
            viewModel.ApiKey = ApiKeyBox.Password;
        }
    }
}
