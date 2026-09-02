using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;
using WortBruecke.App.ViewModels;

namespace WortBruecke.App.Views;

public partial class CourseLessonView : UserControl
{
    private INotifyPropertyChanged? _subscribedViewModel;

    public CourseLessonView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => Subscribe(DataContext as INotifyPropertyChanged);
        Unloaded += (_, _) => Subscribe(null);
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e) =>
        Subscribe(IsLoaded ? e.NewValue as INotifyPropertyChanged : null);

    private void Subscribe(INotifyPropertyChanged? viewModel)
    {
        if (ReferenceEquals(_subscribedViewModel, viewModel)) return;
        if (_subscribedViewModel is not null) _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribedViewModel = viewModel;
        if (_subscribedViewModel is not null) _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(CourseLessonViewModel.PositionText) or nameof(CourseLessonViewModel.IsFlowComplete))) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, LessonScroll.ScrollToTop);
    }
}
