using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WortBruecke.App.ViewModels;

namespace WortBruecke.App.Views;

public partial class VocabularyTestView : UserControl
{
    private VocabularyTestViewModel? _viewModel;

    public VocabularyTestView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as VocabularyTestViewModel;
        if (_viewModel is not null && IsLoaded)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.Activate();
        }
        FocusAnswerBox();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VocabularyTestViewModel.CurrentQuestion) or
            nameof(VocabularyTestViewModel.IsTestActive))
        {
            FocusAnswerBox();
        }
    }

    private void OnGermanCharacterClicked(object sender, RoutedEventArgs e) => FocusAnswerBox();

    private void FocusAnswerBox()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_viewModel?.IsTestActive != true)
            {
                return;
            }
            AnswerBox.Focus();
            Keyboard.Focus(AnswerBox);
            AnswerBox.CaretIndex = AnswerBox.Text.Length;
        }, DispatcherPriority.Input);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel?.IsTestActive != true)
        {
            return;
        }

        if (_viewModel.SubmitCommand.CanExecute(null))
        {
            _viewModel.SubmitCommand.Execute(null);
            e.Handled = true;
        }
    }
}
