using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WortBruecke.App.ViewModels;

namespace WortBruecke.App.Views;

public partial class GrammarView : UserControl
{
    public GrammarView() => InitializeComponent();

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is GrammarViewModel viewModel)
        {
            viewModel.Activate();
        }
        Dispatcher.BeginInvoke(() =>
        {
            GrammarAnswerBox.Focus();
            Keyboard.Focus(GrammarAnswerBox);
        }, DispatcherPriority.Input);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control &&
            DataContext is GrammarViewModel viewModel && viewModel.CheckCommand.CanExecute(null))
        {
            viewModel.CheckCommand.Execute(null);
            e.Handled = true;
        }
    }
}
