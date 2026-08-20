using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WortBruecke.App.ViewModels;

namespace WortBruecke.App.Views;

public partial class TextPracticeView : UserControl
{
    private TextPracticeViewModel? _viewModel;

    public TextPracticeView()
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
        _viewModel = e.NewValue as TextPracticeViewModel;
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
        if (e.PropertyName is nameof(TextPracticeViewModel.SourceText) or nameof(TextPracticeViewModel.IsPractising))
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
            TextAnswerBox.Focus();
            Keyboard.Focus(TextAnswerBox);
            TextAnswerBox.CaretIndex = TextAnswerBox.Text.Length;
        }, DispatcherPriority.Input);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control || _viewModel is null)
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
