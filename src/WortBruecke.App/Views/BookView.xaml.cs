using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WortBruecke.App.ViewModels;

namespace WortBruecke.App.Views;

public partial class BookView : UserControl
{
    private BookViewModel? _viewModel;

    public BookView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _viewModel = e.NewValue as BookViewModel;
        if (_viewModel is not null && IsLoaded)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
        FocusAnswerBox();
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BookViewModel.Prompt) or nameof(BookViewModel.IsPractising))
        {
            FocusAnswerBox();
        }
    }

    private void OnGermanCharacterClicked(object sender, System.Windows.RoutedEventArgs e) => FocusAnswerBox();

    private void FocusAnswerBox()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_viewModel?.IsPractising != true)
            {
                return;
            }
            BookAnswerBox.Focus();
            Keyboard.Focus(BookAnswerBox);
            BookAnswerBox.CaretIndex = BookAnswerBox.Text.Length;
        }, DispatcherPriority.Input);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel?.IsPractising != true)
        {
            return;
        }
        if (_viewModel.ShowFeedback && _viewModel.NextCommand.CanExecute(null))
        {
            _viewModel.NextCommand.Execute(null);
            e.Handled = true;
        }
        else if (_viewModel.CheckCommand.CanExecute(null))
        {
            _viewModel.CheckCommand.Execute(null);
            e.Handled = true;
        }
    }
}
