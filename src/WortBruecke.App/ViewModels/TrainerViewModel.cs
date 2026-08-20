using System.Collections.ObjectModel;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;
using WortBruecke.Core.Training;

namespace WortBruecke.App.ViewModels;

public sealed record ThemeOption(int? Id, string Label, string SecondaryLabel);
public sealed record DifficultyOption(int Level, string Title, string Description);
public sealed record PracticeUnitOption(PracticeUnit Unit, string Title, string Description);
public sealed record CefrOption(string? Level, string Label);

public sealed class TrainerViewModel : ObservableObject
{
    private readonly IContentRepository _contentRepository;
    private readonly IProgressRepository _progressRepository;
    private readonly IKeyboardLayoutService _keyboardLayoutService;
    private readonly IImageProvider _imageProvider;
    private readonly LanguagePair _pair = LanguagePair.RussianToGerman;
    private readonly List<WordEntry> _wordSession = [];
    private readonly List<SentenceEntry> _sentenceSession = [];
    private ThemeOption? _selectedTheme;
    private DifficultyOption? _selectedDifficulty;
    private PracticeUnitOption? _selectedPracticeUnit;
    private CefrOption? _selectedCefr;
    private WordEntry? _currentWord;
    private SentenceEntry? _currentSentence;
    private string _answer = string.Empty;
    private string _sourceAnswer = string.Empty;
    private string _targetAnswer = string.Empty;
    private int _currentIndex;
    private bool _isSessionActive;
    private bool _showFeedback;
    private bool _isCorrect;
    private bool _isComplete;
    private bool _isSourceStep;
    private int _correctCount;
    private string? _resolvedImagePath;
    private int _passageFrequency = 8;
    private string _selectionMessage = string.Empty;

    public TrainerViewModel(
        IContentRepository contentRepository,
        IProgressRepository progressRepository,
        IKeyboardLayoutService keyboardLayoutService,
        IImageProvider imageProvider)
    {
        _contentRepository = contentRepository;
        _progressRepository = progressRepository;
        _keyboardLayoutService = keyboardLayoutService;
        _imageProvider = imageProvider;

        StartSessionCommand = new AsyncRelayCommand(StartSessionAsync, CanStartSession);
        CheckAnswerCommand = new AsyncRelayCommand(CheckAnswerAsync, CanCheckAnswer);
        AdvanceLanguageCommand = new RelayCommand(AdvanceToTargetLanguage, () => IsLevelThree && IsSourceStep && !string.IsNullOrWhiteSpace(SourceAnswer));
        NextCommand = new RelayCommand(Next, () => ShowFeedback);
        RestartCommand = new RelayCommand(ResetSelection);
        InsertGermanCharacterCommand = new ParameterizedRelayCommand(InsertGermanCharacter, parameter => parameter is string);

        PracticeUnits.Add(new PracticeUnitOption(PracticeUnit.Word, "Ступень 1 · Слова", "Образ и базовая лексика"));
        PracticeUnits.Add(new PracticeUnitOption(PracticeUnit.Sentence, "Ступень 2 · Предложения", "Грамматика в контексте"));
        PracticeUnits.Add(new PracticeUnitOption(PracticeUnit.Text, "Ступень 3 · Тексты", "Связный перевод A0–C2"));
        SelectedPracticeUnit = PracticeUnits[0];
    }

    public ObservableCollection<ThemeOption> Themes { get; } = [];
    public ObservableCollection<DifficultyOption> Difficulties { get; } = [];
    public ObservableCollection<PracticeUnitOption> PracticeUnits { get; } = [];
    public ObservableCollection<CefrOption> CefrLevels { get; } = [];
    public AsyncRelayCommand StartSessionCommand { get; }
    public AsyncRelayCommand CheckAnswerCommand { get; }
    public RelayCommand AdvanceLanguageCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand RestartCommand { get; }
    public ParameterizedRelayCommand InsertGermanCharacterCommand { get; }
    public event EventHandler? PassageExerciseRequested;
    public event Action<string?>? TextPracticeRequested;

    public ThemeOption? SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetProperty(ref _selectedTheme, value))
            {
                StartSessionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public DifficultyOption? SelectedDifficulty
    {
        get => _selectedDifficulty;
        set
        {
            if (SetProperty(ref _selectedDifficulty, value))
            {
                StartSessionCommand.RaiseCanExecuteChanged();
                NotifyModeProperties();
            }
        }
    }

    public PracticeUnitOption? SelectedPracticeUnit
    {
        get => _selectedPracticeUnit;
        set
        {
            if (SetProperty(ref _selectedPracticeUnit, value))
            {
                RefreshSelectionOptions();
                NotifyModeProperties();
            }
        }
    }

    public CefrOption? SelectedCefr
    {
        get => _selectedCefr;
        set
        {
            if (SetProperty(ref _selectedCefr, value))
            {
                StartSessionCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(LevelLabel));
            }
        }
    }

    public string Answer
    {
        get => _answer;
        set
        {
            if (SetProperty(ref _answer, value))
            {
                CheckAnswerCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SourceAnswer
    {
        get => _sourceAnswer;
        set
        {
            if (SetProperty(ref _sourceAnswer, value))
            {
                AdvanceLanguageCommand.RaiseCanExecuteChanged();
                CheckAnswerCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string TargetAnswer
    {
        get => _targetAnswer;
        set
        {
            if (SetProperty(ref _targetAnswer, value))
            {
                CheckAnswerCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSessionActive
    {
        get => _isSessionActive;
        private set
        {
            if (SetProperty(ref _isSessionActive, value))
            {
                OnPropertyChanged(nameof(IsSelectionVisible));
            }
        }
    }

    public bool ShowFeedback
    {
        get => _showFeedback;
        private set
        {
            if (SetProperty(ref _showFeedback, value))
            {
                CheckAnswerCommand.RaiseCanExecuteChanged();
                NextCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCorrect
    {
        get => _isCorrect;
        private set => SetProperty(ref _isCorrect, value);
    }

    public bool IsComplete
    {
        get => _isComplete;
        private set
        {
            if (SetProperty(ref _isComplete, value))
            {
                OnPropertyChanged(nameof(IsSelectionVisible));
            }
        }
    }

    public bool IsSourceStep
    {
        get => _isSourceStep;
        private set
        {
            if (SetProperty(ref _isSourceStep, value))
            {
                OnPropertyChanged(nameof(IsTargetStep));
                OnPropertyChanged(nameof(CurrentInputTarget));
                OnPropertyChanged(nameof(ShowGermanCharacterPanel));
                OnPropertyChanged(nameof(IsCheckStage));
                AdvanceLanguageCommand.RaiseCanExecuteChanged();
                CheckAnswerCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectionMessage
    {
        get => _selectionMessage;
        private set
        {
            if (SetProperty(ref _selectionMessage, value))
            {
                OnPropertyChanged(nameof(HasSelectionMessage));
            }
        }
    }

    public bool HasSelectionMessage => !string.IsNullOrWhiteSpace(SelectionMessage);
    public bool IsWordUnit => SelectedPracticeUnit?.Unit == PracticeUnit.Word;
    public bool IsSentenceUnit => SelectedPracticeUnit?.Unit == PracticeUnit.Sentence;
    public bool IsTextUnit => SelectedPracticeUnit?.Unit == PracticeUnit.Text;
    public bool ShowThemeOptions => !IsTextUnit;
    public bool ShowDirectionOptions => !IsTextUnit;
    public bool IsTargetStep => IsLevelThree && !IsSourceStep;
    public bool IsLevelThree => IsWordUnit && SelectedDifficulty?.Level == 3;
    public bool IsSingleAnswerMode => !IsLevelThree;
    public bool IsSelectionVisible => !IsSessionActive && !IsComplete;
    public bool ShowWordPrompt => !IsLevelThree;
    public bool HasImage => IsWordUnit && !string.IsNullOrWhiteSpace(ResolvedImagePath);
    public bool ShowGermanCharacterPanel => SelectedDifficulty?.Level == 2 || IsTargetStep;
    public bool IsCheckStage => !IsLevelThree || IsTargetStep;
    public string CurrentInputTarget => IsLevelThree ? (IsSourceStep ? "source" : "target") : "single";
    public string? ResolvedImagePath => _resolvedImagePath;
    public double PromptFontSize => IsSentenceUnit ? 22 : 38;
    public string Prompt => CurrentTranslations is null || IsLevelThree || SelectedDifficulty is null
        ? string.Empty
        : CurrentTranslations.For(SelectedDifficulty.Level == 1 ? _pair.Target.CultureCode : _pair.Source.CultureCode);
    public string PromptInstruction => IsSentenceUnit
        ? SelectedDifficulty?.Level == 1 ? "ПЕРЕВЕДИТЕ ПРЕДЛОЖЕНИЕ НА РУССКИЙ" : "ПЕРЕВЕДИТЕ ПРЕДЛОЖЕНИЕ НА НЕМЕЦКИЙ"
        : SelectedDifficulty?.Level switch
        {
            1 => "ПЕРЕВЕДИТЕ НА РУССКИЙ",
            2 => "ПЕРЕВЕДИТЕ НА НЕМЕЦКИЙ",
            _ => "НАЗОВИТЕ ПРЕДМЕТ НА ДВУХ ЯЗЫКАХ"
        };
    public string InputLabel => SelectedDifficulty?.Level == 1 ? "Ответ по-русски" : "Ответ по-немецки";
    public string ThemeLabel => IsTextUnit ? "Связный текст" : SelectedTheme?.Label ?? string.Empty;
    public string LevelLabel => $"{SelectedCefr?.Label ?? "Все уровни"} · {SelectedDifficulty?.Title ?? SelectedPracticeUnit?.Title}";
    public string ProgressText => SessionCount == 0 ? "0 / 0" : $"{Math.Min(_currentIndex + 1, SessionCount)} / {SessionCount}";
    public double ProgressValue => SessionCount == 0 ? 0 : (double)Math.Min(_currentIndex + 1, SessionCount) / SessionCount * 100;
    public string FeedbackTitle => IsCorrect ? "Верно" : "Проверьте ответ";
    public string FeedbackDetail
    {
        get
        {
            if (IsCorrect)
            {
                return "Ответ совпадает с учебным эталоном.";
            }
            if (CurrentTranslations is null)
            {
                return string.Empty;
            }
            if (IsLevelThree)
            {
                return $"Правильно: {CurrentTranslations.For(_pair.Source.CultureCode)} · {CurrentTranslations.For(_pair.Target.CultureCode)}";
            }
            var expectedCulture = SelectedDifficulty?.Level == 1 ? _pair.Source.CultureCode : _pair.Target.CultureCode;
            return $"Правильный ответ: {CurrentTranslations.For(expectedCulture)}";
        }
    }
    public string CompletionTitle => $"{_correctCount} из {SessionCount} без ошибок";
    public string CompletionDetail => _correctCount == SessionCount
        ? "Все ответы точные. Можно перейти к следующей ступени."
        : "Результаты сохранены локально. Повторите эту сложность или попробуйте другое направление.";
    public string SelectionDescription => IsTextUnit
        ? "Связные упражнения развивают понимание контекста: от первого текста A0 до стилистически сложного C2."
        : IsSentenceUnit
            ? "Предложения проверяют не только лексику, но и порядок слов, артикли и грамматическую форму."
            : "Отдельные слова помогают закрепить базовую лексику перед переходом к предложениям.";
    public string StartButtonText => IsTextUnit ? "Выбрать текст" : IsSentenceUnit ? "Начать до 10 предложений" : "Начать до 10 слов";

    private int SessionCount => IsSentenceUnit ? _sentenceSession.Count : _wordSession.Count;
    private LocalizedText? CurrentTranslations => IsSentenceUnit ? _currentSentence?.Translations : _currentWord?.Translations;
    private int CurrentContentId => IsSentenceUnit ? _currentSentence?.Id ?? 0 : _currentWord?.Id ?? 0;

    public async Task InitializeAsync()
    {
        var themes = await _contentRepository.GetThemesAsync();
        Themes.Clear();
        Themes.Add(new ThemeOption(null, "Все темы", "Смешанная подборка"));
        foreach (var theme in themes)
        {
            Themes.Add(new ThemeOption(theme.Id, theme.Names.For(_pair.Source.CultureCode), theme.Names.For(_pair.Target.CultureCode)));
        }
        SelectedTheme = Themes[0];
    }

    public void ApplySettings(AppSettings settings) => _passageFrequency = Math.Clamp(settings.PassageFrequency, 1, 20);

    private void RefreshSelectionOptions()
    {
        SelectionMessage = string.Empty;
        Difficulties.Clear();
        if (!IsTextUnit)
        {
            Difficulties.Add(new DifficultyOption(1, "DE → RU", "Ответ по-русски"));
            Difficulties.Add(new DifficultyOption(2, "RU → DE", "Ответ по-немецки"));
            if (IsWordUnit)
            {
                Difficulties.Add(new DifficultyOption(3, "Изображение → два языка", "Два ответа и смена раскладки"));
            }
        }
        SelectedDifficulty = Difficulties.FirstOrDefault();

        CefrLevels.Clear();
        CefrLevels.Add(new CefrOption(null, "Все доступные"));
        var levels = IsTextUnit || IsSentenceUnit
            ? new[] { "A0", "A1", "A2", "B1", "B2", "C1", "C2" }
            : new[] { "A0", "A1", "A2", "B1" };
        foreach (var level in levels)
        {
            CefrLevels.Add(new CefrOption(level, level));
        }
        SelectedCefr = CefrLevels[0];
        StartSessionCommand.RaiseCanExecuteChanged();
    }

    private bool CanStartSession() => SelectedPracticeUnit is not null && SelectedCefr is not null &&
        (IsTextUnit || SelectedTheme is not null && SelectedDifficulty is not null);

    private async Task StartSessionAsync()
    {
        SelectionMessage = string.Empty;
        if (IsTextUnit)
        {
            TextPracticeRequested?.Invoke(SelectedCefr?.Level);
            return;
        }

        _wordSession.Clear();
        _sentenceSession.Clear();
        if (IsSentenceUnit)
        {
            var sentences = await _contentRepository.GetSentencesAsync(SelectedTheme?.Id);
            _sentenceSession.AddRange(FilterLevel(sentences, item => item.Level).OrderBy(_ => Random.Shared.Next()).Take(10));
        }
        else
        {
            var words = await _contentRepository.GetWordsAsync(SelectedTheme?.Id);
            _wordSession.AddRange(FilterLevel(words, item => item.Level).OrderBy(_ => Random.Shared.Next()).Take(10));
        }

        if (SessionCount == 0)
        {
            SelectionMessage = "Для этой темы и уровня пока нет материала. Выберите «Все доступные» или другую тему.";
            return;
        }

        _currentIndex = 0;
        _correctCount = 0;
        IsComplete = false;
        IsSessionActive = true;
        LoadCurrentItem();
    }

    private IEnumerable<T> FilterLevel<T>(IEnumerable<T> source, Func<T, string> levelSelector) =>
        string.IsNullOrWhiteSpace(SelectedCefr?.Level)
            ? source
            : source.Where(item => string.Equals(levelSelector(item), SelectedCefr.Level, StringComparison.OrdinalIgnoreCase));

    private async Task CheckAnswerAsync()
    {
        if (CurrentTranslations is null)
        {
            return;
        }
        if (IsLevelThree)
        {
            var sourceCorrect = AnswerEvaluator.Evaluate(SourceAnswer, CurrentTranslations.For(_pair.Source.CultureCode), _pair.Source.CultureCode).IsCorrect;
            var targetCorrect = AnswerEvaluator.Evaluate(TargetAnswer, CurrentTranslations.For(_pair.Target.CultureCode), _pair.Target.CultureCode).IsCorrect;
            IsCorrect = sourceCorrect && targetCorrect;
        }
        else
        {
            var expectedCulture = SelectedDifficulty?.Level == 1 ? _pair.Source.CultureCode : _pair.Target.CultureCode;
            IsCorrect = AnswerEvaluator.Evaluate(Answer, CurrentTranslations.For(expectedCulture), expectedCulture).IsCorrect;
        }
        if (IsCorrect)
        {
            _correctCount++;
        }
        ShowFeedback = true;
        await _progressRepository.RecordAttemptAsync(IsSentenceUnit ? ContentType.Sentence : ContentType.Word, CurrentContentId, IsCorrect);
        OnPropertyChanged(nameof(FeedbackTitle));
        OnPropertyChanged(nameof(FeedbackDetail));
    }

    private void AdvanceToTargetLanguage()
    {
        if (!IsLevelThree || !IsSourceStep || string.IsNullOrWhiteSpace(SourceAnswer))
        {
            return;
        }
        _keyboardLayoutService.SwitchTo(_pair.Target.CultureCode);
        IsSourceStep = false;
    }

    private void Next()
    {
        _currentIndex++;
        if (_currentIndex >= SessionCount)
        {
            IsSessionActive = false;
            IsComplete = true;
            ShowFeedback = false;
            OnPropertyChanged(nameof(CompletionTitle));
            OnPropertyChanged(nameof(CompletionDetail));
            return;
        }
        LoadCurrentItem();
        if (IsWordUnit && _currentIndex > 0 && _currentIndex % _passageFrequency == 0)
        {
            PassageExerciseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void LoadCurrentItem()
    {
        _currentWord = IsWordUnit ? _wordSession[_currentIndex] : null;
        _currentSentence = IsSentenceUnit ? _sentenceSession[_currentIndex] : null;
        _resolvedImagePath = _currentWord is null ? null : _imageProvider.Resolve(_currentWord.ImagePath);
        Answer = string.Empty;
        SourceAnswer = string.Empty;
        TargetAnswer = string.Empty;
        ShowFeedback = false;

        var inputCulture = SelectedDifficulty?.Level == 1 || IsLevelThree ? _pair.Source.CultureCode : _pair.Target.CultureCode;
        _keyboardLayoutService.SwitchTo(inputCulture);
        IsSourceStep = IsLevelThree;
        NotifySessionProperties();
    }

    private void ResetSelection()
    {
        _wordSession.Clear();
        _sentenceSession.Clear();
        _currentWord = null;
        _currentSentence = null;
        IsComplete = false;
        IsSessionActive = false;
        ShowFeedback = false;
        Answer = string.Empty;
        SourceAnswer = string.Empty;
        TargetAnswer = string.Empty;
    }

    private bool CanCheckAnswer() => IsSessionActive && !ShowFeedback &&
        (IsLevelThree
            ? IsTargetStep && !string.IsNullOrWhiteSpace(SourceAnswer) && !string.IsNullOrWhiteSpace(TargetAnswer)
            : !string.IsNullOrWhiteSpace(Answer));

    private void InsertGermanCharacter(object? parameter)
    {
        if (parameter is not string character)
        {
            return;
        }
        if (IsLevelThree)
        {
            TargetAnswer += character;
        }
        else
        {
            Answer += character;
        }
        OnPropertyChanged(nameof(CurrentInputTarget));
    }

    private void NotifyModeProperties()
    {
        OnPropertyChanged(nameof(IsWordUnit));
        OnPropertyChanged(nameof(IsSentenceUnit));
        OnPropertyChanged(nameof(IsTextUnit));
        OnPropertyChanged(nameof(ShowThemeOptions));
        OnPropertyChanged(nameof(ShowDirectionOptions));
        OnPropertyChanged(nameof(IsLevelThree));
        OnPropertyChanged(nameof(IsSingleAnswerMode));
        OnPropertyChanged(nameof(SelectionDescription));
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(PromptFontSize));
        StartSessionCommand.RaiseCanExecuteChanged();
    }

    private void NotifySessionProperties()
    {
        OnPropertyChanged(nameof(Prompt));
        OnPropertyChanged(nameof(PromptInstruction));
        OnPropertyChanged(nameof(InputLabel));
        OnPropertyChanged(nameof(IsLevelThree));
        OnPropertyChanged(nameof(IsSingleAnswerMode));
        OnPropertyChanged(nameof(ShowWordPrompt));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(ResolvedImagePath));
        OnPropertyChanged(nameof(ShowGermanCharacterPanel));
        OnPropertyChanged(nameof(IsCheckStage));
        OnPropertyChanged(nameof(CurrentInputTarget));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(LevelLabel));
        OnPropertyChanged(nameof(PromptFontSize));
    }
}
