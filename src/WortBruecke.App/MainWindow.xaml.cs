using System.ComponentModel;
using System.Windows;
using WortBruecke.App.Infrastructure;
using WortBruecke.App.ViewModels;

namespace WortBruecke.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyBackdrop();
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        base.OnClosed(e);
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            ApplyBackdrop();
        }
    }

    private void ApplyBackdrop()
    {
        WindowBackdropService.Apply(this, ThemeManager.IsDarkTheme);
        if (SystemParameters.HighContrast)
        {
            RootSurface.Background = SystemColors.WindowBrush;
            BackdropDecorations.Visibility = Visibility.Collapsed;
            return;
        }

        RootSurface.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "Brush.AppBackdrop");
        BackdropDecorations.Visibility = Visibility.Visible;
    }
}
