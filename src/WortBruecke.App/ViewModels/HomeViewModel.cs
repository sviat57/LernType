using System.Windows.Input;
using WortBruecke.App.Infrastructure;

namespace WortBruecke.App.ViewModels;

public sealed class HomeViewModel
{
    public HomeViewModel(Action<string> navigate)
    {
        ArgumentNullException.ThrowIfNull(navigate);
        OpenCoursesCommand = new RelayCommand(() => navigate("path"));
        OpenInteractiveExercisesCommand = new RelayCommand(() => navigate("interactive"));
        OpenProgressCommand = new RelayCommand(() => navigate("progress"));
    }

    public string Greeting => DateTime.Now.Hour switch
    {
        < 12 => "Guten Morgen",
        < 18 => "Guten Tag",
        _ => "Guten Abend"
    };

    public ICommand OpenCoursesCommand { get; }
    public ICommand OpenInteractiveExercisesCommand { get; }
    public ICommand OpenProgressCommand { get; }
}
