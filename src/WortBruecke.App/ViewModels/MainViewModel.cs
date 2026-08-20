using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;
using WortBruecke.Core.Training;

namespace WortBruecke.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly TrainerViewModel _trainer;
    private readonly LearningPathViewModel _learningPath;
    private readonly ExamCenterViewModel _examCenter;
    private readonly TextPracticeViewModel _texts;
    private readonly BookViewModel _books;
    private readonly VocabularyTestViewModel _vocabularyTest;
    private readonly GrammarViewModel _grammar;
    private readonly TelcViewModel _telc;
    private readonly SettingsViewModel _settings;
    private readonly Dictionary<string, object> _screens;
    private object _currentViewModel;
    private string _currentTitle = "Обзор";

    public MainViewModel(
        IContentRepository contentRepository,
        IProgressRepository progressRepository,
        IKeyboardLayoutService keyboardLayoutService,
        IImageProvider imageProvider,
        ILanguageAnalysisService languageAnalysisService,
        ISettingsStore settingsStore,
        IBookRepository bookRepository,
        IBookVocabularyExtractor bookVocabularyExtractor,
        IOfflineDictionaryService offlineDictionaryService,
        ILearningProgressRepository learningProgressRepository,
        IExamBlueprintRepository examBlueprintRepository)
    {
        var home = new HomeViewModel(Navigate);
        _learningPath = new LearningPathViewModel(
            contentRepository,
            progressRepository,
            learningProgressRepository,
            examBlueprintRepository,
            Navigate);
        _examCenter = new ExamCenterViewModel(examBlueprintRepository, learningProgressRepository, Navigate);
        _trainer = new TrainerViewModel(contentRepository, progressRepository, keyboardLayoutService, imageProvider);
        _texts = new TextPracticeViewModel(contentRepository, progressRepository, keyboardLayoutService);
        _books = new BookViewModel(bookRepository, bookVocabularyExtractor, progressRepository, keyboardLayoutService, offlineDictionaryService);
        _vocabularyTest = new VocabularyTestViewModel(contentRepository, progressRepository, keyboardLayoutService);
        _grammar = new GrammarViewModel(contentRepository, progressRepository, keyboardLayoutService, new GrammarHeuristicService(), languageAnalysisService);
        _telc = new TelcViewModel(languageAnalysisService, keyboardLayoutService);
        _settings = new SettingsViewModel(settingsStore, keyboardLayoutService, ApplySettings);
        _trainer.PassageExerciseRequested += (_, _) =>
        {
            _texts.StartSuggested();
            NavigateCore("texts", preserveTextFilter: true);
        };
        _trainer.TextPracticeRequested += level =>
        {
            _texts.ApplyLevelFilter(level);
            NavigateCore("texts", preserveTextFilter: true);
        };
        _texts.SuggestedPracticeCompleted += (_, _) => Navigate("trainer");
        _screens = new Dictionary<string, object>
        {
            ["home"] = home,
            ["path"] = _learningPath,
            ["exams"] = _examCenter,
            ["trainer"] = _trainer,
            ["texts"] = _texts,
            ["books"] = _books,
            ["test"] = _vocabularyTest,
            ["grammar"] = _grammar,
            ["telc"] = _telc,
            ["settings"] = _settings
        };
        _currentViewModel = home;

        NavigationItems =
        [
            CreateNav("home", "Обзор", "Icon.Home"),
            CreateNav("path", "Путь A0–C2", "Icon.Route"),
            CreateNav("exams", "Экзамены", "Icon.Certificate"),
            CreateNav("trainer", "Тренажёр", "Icon.Cards"),
            CreateNav("texts", "Тексты", "Icon.Document"),
            CreateNav("books", "Моя книга", "Icon.Document"),
            CreateNav("test", "Тест", "Icon.Chart"),
            CreateNav("grammar", "Грамматика", "Icon.Edit"),
            CreateNav("telc", "TELC-чекер", "Icon.Chart"),
            CreateNav("settings", "Настройки", "Icon.Settings")
        ];
        SetSelection("home");
    }

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    public object CurrentViewModel
    {
        get => _currentViewModel;
        private set => SetProperty(ref _currentViewModel, value);
    }

    public string CurrentTitle
    {
        get => _currentTitle;
        private set => SetProperty(ref _currentTitle, value);
    }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(
            _learningPath.InitializeAsync(),
            _examCenter.InitializeAsync(),
            _trainer.InitializeAsync(),
            _texts.InitializeAsync(),
            _books.InitializeAsync(),
            _vocabularyTest.InitializeAsync(),
            _grammar.InitializeAsync(),
            _settings.InitializeAsync());
    }

    private void ApplySettings(AppSettings settings)
    {
        _trainer.ApplySettings(settings);
        _texts.ApplySettings(settings);
        ThemeManager.Apply(settings.UseDarkTheme);
    }

    public void Navigate(string key) => NavigateCore(key, preserveTextFilter: false);

    private void NavigateCore(string key, bool preserveTextFilter)
    {
        if (!_screens.TryGetValue(key, out var screen))
        {
            return;
        }
        if (key == "texts" && !preserveTextFilter)
        {
            _texts.ApplyLevelFilter(null);
        }
        CurrentViewModel = screen;
        if (screen is GrammarViewModel grammar)
        {
            grammar.Activate();
        }
        if (screen is TelcViewModel telc)
        {
            telc.Activate();
        }
        if (screen is VocabularyTestViewModel vocabularyTest)
        {
            vocabularyTest.Activate();
        }
        SetSelection(key);
        CurrentTitle = NavigationItems.First(x => x.Key == key).Title;
    }

    private NavigationItemViewModel CreateNav(string key, string title, string iconResource)
    {
        var icon = Application.Current.TryFindResource(iconResource) as Geometry ?? Geometry.Empty;
        return new NavigationItemViewModel(key, title, icon, new RelayCommand(() => Navigate(key)));
    }

    private void SetSelection(string key)
    {
        foreach (var item in NavigationItems)
        {
            item.IsSelected = item.Key == key;
        }
    }
}
