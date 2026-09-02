using System.Collections.ObjectModel;
using System.Windows.Input;
using WortBruecke.App.Infrastructure;

namespace WortBruecke.App.ViewModels;

public sealed record InteractiveExerciseCardViewModel(
    string Title,
    string Description,
    string Meta,
    string IconKey,
    ICommand OpenCommand);

/// <summary>
/// A calm entry point for optional drills. These exercises deliberately stay outside the
/// sequential course percentage and never replace the next lesson.
/// </summary>
public sealed class InteractiveExercisesViewModel
{
    public InteractiveExercisesViewModel(Action<string> navigate)
    {
        ArgumentNullException.ThrowIfNull(navigate);
        Exercises =
        [
            Create(
                "Слова и предложения",
                "Переводите в обе стороны и выбирайте точный уровень сложности.",
                "DE ↔ RU · слова · предложения",
                "Icon.Cards",
                "trainer"),
            Create(
                "Набор текстов",
                "Печатайте встроенные тексты без смешивания со словарной практикой.",
                "35 текстов · Pre-A1–C2",
                "Icon.Document",
                "texts"),
            Create(
                "Слушать и говорить",
                "Слушайте немецкую модель, записывайте ответ и сравнивайте себя.",
                "Локальный голос Windows · микрофон",
                "Icon.Audio",
                "audio"),
            Create(
                "Двусторонний словарный тест",
                "Проверьте активный и пассивный словарь короткой отдельной сессией.",
                "RU → DE · DE → RU",
                "Icon.Progress",
                "test")
        ];

        InteractiveExerciseCardViewModel Create(
            string title,
            string description,
            string meta,
            string iconKey,
            string route) =>
            new(title, description, meta, iconKey, new RelayCommand(() => navigate(route)));
    }

    public ObservableCollection<InteractiveExerciseCardViewModel> Exercises { get; }
}
