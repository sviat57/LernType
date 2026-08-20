using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WortBruecke.App.ViewModels;

namespace WortBruecke.App.Views;

public partial class TrainerView : UserControl
{
    private TrainerViewModel? _viewModel;

    public TrainerView()
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
        _viewModel = e.NewValue as TrainerViewModel;
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
        FocusCurrentInput();
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
        if (e.PropertyName is nameof(TrainerViewModel.Prompt) or nameof(TrainerViewModel.IsSessionActive) or nameof(TrainerViewModel.CurrentInputTarget))
        {
            FocusCurrentInput();
        }
    }

    private void OnGermanCharacterClicked(object sender, System.Windows.RoutedEventArgs e) => FocusCurrentInput();

    private void FocusCurrentInput()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_viewModel?.IsSessionActive != true)
            {
                return;
            }
            var input = _viewModel.CurrentInputTarget switch
            {
                "source" => SourceAnswerBox,
                "target" => TargetAnswerBox,
                _ => AnswerBox
            };
            input.Focus();
            Keyboard.Focus(input);
            input.CaretIndex = input.Text.Length;
        }, DispatcherPriority.Input);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel is null || !_viewModel.IsSessionActive)
        {
            return;
        }

        if (_viewModel.ShowFeedback && _viewModel.NextCommand.CanExecute(null))
        {
            _viewModel.NextCommand.Execute(null);
            e.Handled = true;
        }
        else if (_viewModel.IsSourceStep && _viewModel.AdvanceLanguageCommand.CanExecute(null))
        {
            _viewModel.AdvanceLanguageCommand.Execute(null);
            e.Handled = true;
        }
        else if (_viewModel.CheckAnswerCommand.CanExecute(null))
        {
            _viewModel.CheckAnswerCommand.Execute(null);
            e.Handled = true;
        }
    }
}
