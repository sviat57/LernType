using System.Windows.Controls;
using WortBruecke.App.ViewModels;

namespace WortBruecke.App.Views;

public partial class AudioPracticeView : UserControl
{
    public AudioPracticeView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is AudioPracticeViewModel viewModel)
            {
                await viewModel.InitializeAsync();
                viewModel.Activate();
            }
        };
    }
}
