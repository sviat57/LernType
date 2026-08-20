using System.Windows.Input;
using WortBruecke.App.Infrastructure;

namespace WortBruecke.App.ViewModels;

public sealed class HomeViewModel(Action<string> navigate)
{
    public string Greeting => DateTime.Now.Hour switch
    {
        < 12 => "Guten Morgen",
        < 18 => "Guten Tag",
        _ => "Guten Abend"
    };

    public ICommand OpenTrainerCommand { get; } = new RelayCommand(() => navigate("trainer"));
    public ICommand OpenLearningPathCommand { get; } = new RelayCommand(() => navigate("path"));
    public ICommand OpenTextsCommand { get; } = new RelayCommand(() => navigate("texts"));
    public ICommand OpenBooksCommand { get; } = new RelayCommand(() => navigate("books"));
    public ICommand OpenVocabularyTestCommand { get; } = new RelayCommand(() => navigate("test"));
    public ICommand OpenGrammarCommand { get; } = new RelayCommand(() => navigate("grammar"));
    public ICommand OpenTelcCommand { get; } = new RelayCommand(() => navigate("telc"));
}
