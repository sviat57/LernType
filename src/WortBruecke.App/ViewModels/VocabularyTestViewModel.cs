using System.Collections.ObjectModel;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;
using WortBruecke.Core.Training;

namespace WortBruecke.App.ViewModels;

public sealed record VocabularyTestLevelOption(
    string? Level,
    string Title,
    string Description);

public sealed record VocabularyTestMistake(
    string Direction,
    string Prompt,
    string SubmittedAnswer,
    string ExpectedAnswer);

public sealed record VocabularyTestLenientAcceptance(
    string Direction,
    string Prompt,
    string SubmittedAnswer,
    string ExpectedAnswer,
    string Message);

public sealed class VocabularyTestViewModel : ObservableObject
{
    private const int RequestedQuestionCount = 20;

    private readonly IContentRepository _contentRepository;
    private readonly LearningAttemptSink _attemptSink;
    private readonly IKeyboardLayoutService _keyboardLayoutService;
    private readonly List<WordEntry> _allWords = [];
    private readonly LanguagePair _pair = LanguagePair.RussianToGerman;

    private VocabularyTestLevelOption? _selectedLevel;
    private VocabularyTestSession? _session;
    private VocabularyTestQuestion? _currentQuestion;
    private VocabularyTestResult? _result;
    private int _currentQuestionIndex;
    private string _answer = string.Empty;
    private string _selectionMessage = string.Empty;
    private bool _isTestActive;
    private bool _isComplete;
    private Guid _sessionId;
    private DateTimeOffset _attemptStartedAtUtc;

    public VocabularyTestViewModel(
        IContentRepository contentRepository,
        IProgressRepository progressRepository,
        IKeyboardLayoutService keyboardLayoutService)
    {
        _contentRepository = contentRepository;
        _attemptSink = new LearningAttemptSink(progressRepository);
        _keyboardLayoutService = keyboardLayoutService;

        Levels =
        [
            new VocabularyTestLevelOption(null, "Все уровни", "Смешанный тест Pre-A1–B1"),
            new VocabularyTestLevelOption("A0", "Pre-A1 · Первый шаг", "Самые наглядные слова"),
            new VocabularyTestLevelOption("A1", "A1 · База", "Самые частые слова"),
            new VocabularyTestLevelOption("A2", "A2 · Уверенный старт", "Повседневная лексика"),
            new VocabularyTestLevelOption("B1", "B1 · Средний", "Более точные значения")
        ];

        _selectedLevel = Levels[0];
        StartCommand = new RelayCommand(Start, CanStart);
        SubmitCommand = new AsyncRelayCommand(SubmitAsync, CanSubmit);
        RestartCommand = new RelayCommand(Restart);
        InsertGermanCharacterCommand = new ParameterizedRelayCommand(
            InsertGermanCharacter,
            parameter => parameter is string && ShowGermanCharacterPanel);
    }

    public VocabularyTestViewModel(
        IContentRepository contentRepository,
        IAttemptRepository attemptRepository,
        IKeyboardLayoutService keyboardLayoutService)
    {
        _contentRepository = contentRepository;
        _attemptSink = new LearningAttemptSink(attemptRepository);
        _keyboardLayoutService = keyboardLayoutService;

        Levels =
        [
            new VocabularyTestLevelOption(null, "Все уровни", "Смешанный тест Pre-A1–B1"),
            new VocabularyTestLevelOption("A0", "Pre-A1 · Первый шаг", "Самые наглядные слова"),
            new VocabularyTestLevelOption("A1", "A1 · База", "Самые частые слова"),
            new VocabularyTestLevelOption("A2", "A2 · Уверенный старт", "Повседневная лексика"),
            new VocabularyTestLevelOption("B1", "B1 · Средний", "Более точные значения")
        ];

        _selectedLevel = Levels[0];
        StartCommand = new RelayCommand(Start, CanStart);
        SubmitCommand = new AsyncRelayCommand(SubmitAsync, CanSubmit);
        RestartCommand = new RelayCommand(Restart);
        InsertGermanCharacterCommand = new ParameterizedRelayCommand(
            InsertGermanCharacter,
            parameter => parameter is string && ShowGermanCharacterPanel);
    }

    public ObservableCollection<VocabularyTestLevelOption> Levels { get; }
    public ObservableCollection<VocabularyTestMistake> Mistakes { get; } = [];
    public ObservableCollection<VocabularyTestLenientAcceptance> LenientAcceptances { get; } = [];

    public RelayCommand StartCommand { get; }
    public AsyncRelayCommand SubmitCommand { get; }
    public RelayCommand RestartCommand { get; }
    public ParameterizedRelayCommand InsertGermanCharacterCommand { get; }

    public event EventHandler? TestCompleted;

    public VocabularyTestLevelOption? SelectedLevel
    {
        get => _selectedLevel;
        set
        {
            if (SetProperty(ref _selectedLevel, value))
            {
                SelectionMessage = string.Empty;
                RaiseSelectionProperties();
            }
        }
    }

    public VocabularyTestQuestion? CurrentQuestion
    {
        get => _currentQuestion;
        private set
        {
            if (SetProperty(ref _currentQuestion, value))
            {
                OnPropertyChanged(nameof(Prompt));
                OnPropertyChanged(nameof(DirectionLabel));
                OnPropertyChanged(nameof(DirectionInstruction));
                OnPropertyChanged(nameof(AnswerLanguageLabel));
                OnPropertyChanged(nameof(QuestionNumberText));
                OnPropertyChanged(nameof(ProgressValue));
                OnPropertyChanged(nameof(ShowGermanCharacterPanel));
                InsertGermanCharacterCommand.RaiseCanExecuteChanged();
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
                SubmitCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectionMessage
    {
        get => _selectionMessage;
        private set => SetProperty(ref _selectionMessage, value);
    }

    public bool IsTestActive
    {
        get => _isTestActive;
        private set
        {
            if (SetProperty(ref _isTestActive, value))
            {
                OnPropertyChanged(nameof(IsSelectionVisible));
                SubmitCommand.RaiseCanExecuteChanged();
            }
        }
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

    public bool IsSelectionVisible => !IsTestActive && !IsComplete;
    public int AvailableWordCount => FilteredWords.Count;
    public int EffectiveQuestionCount => Math.Min(RequestedQuestionCount, AvailableWordCount);
    public string AvailableWordsText => AvailableWordCount switch
    {
        0 => "Для этого уровня пока нет слов",
        1 => "Доступно 1 слово",
        _ => $"Доступно слов: {AvailableWordCount}"
    };
    public string StartButtonText => EffectiveQuestionCount == 0
        ? "Нет доступных вопросов"
        : $"Начать тест · {EffectiveQuestionCount} вопросов";

    public string SelectedLevelLabel => SelectedLevel?.Level switch
    {
        "A0" => "Pre-A1",
        { } level => level,
        _ => "Pre-A1–B1"
    };
    public string Prompt => CurrentQuestion?.Prompt ?? string.Empty;
    public string DirectionLabel => CurrentQuestion?.Direction == TranslationDirection.SourceToTarget
        ? "RU → DE"
        : "DE → RU";
    public string DirectionInstruction => CurrentQuestion?.Direction == TranslationDirection.SourceToTarget
        ? "Переведите слово на немецкий"
        : "Переведите слово на русский";
    public string AnswerLanguageLabel => CurrentQuestion?.AnswerCultureCode == _pair.Target.CultureCode
        ? "Ответ по-немецки"
        : "Ответ по-русски";
    public string QuestionNumberText => CurrentQuestion is null || _session is null
        ? "0 / 0"
        : $"{CurrentQuestion.Number} / {_session.Questions.Count}";
    public double ProgressValue => CurrentQuestion is null || _session?.Questions.Count is not > 0
        ? 0
        : (double)CurrentQuestion.Number / _session.Questions.Count * 100;
    public bool ShowGermanCharacterPanel =>
        CurrentQuestion?.AnswerCultureCode == _pair.Target.CultureCode;

    public string CompletionTitle => _result is null
        ? "Тест завершён"
        : _result.Accuracy switch
        {
            >= 0.9 => "Отличный словарный запас",
            >= 0.7 => "Хорошая основа",
            >= 0.5 => "Есть уверенный прогресс",
            _ => "Теперь понятна точка старта"
        };
    public string OverallResultText => _result is null
        ? "0 из 0"
        : $"{_result.CorrectAnswerCount} из {_result.TotalQuestionCount} · {FormatPercent(_result.Accuracy)}";
    public string RussianToGermanResultText => _result is null
        ? "0 из 0"
        : $"{_result.SourceToTargetCorrectCount} из {_result.SourceToTargetQuestionCount} · {FormatPercent(_result.SourceToTargetAccuracy)}";
    public string GermanToRussianResultText => _result is null
        ? "0 из 0"
        : $"{_result.TargetToSourceCorrectCount} из {_result.TargetToSourceQuestionCount} · {FormatPercent(_result.TargetToSourceAccuracy)}";
    public bool HasMistakes => Mistakes.Count > 0;
    public bool HasLenientAcceptances => LenientAcceptances.Count > 0;
    public bool IsPerfectResult => _result is not null && Mistakes.Count == 0;
    public string MistakesTitle => Mistakes.Count switch
    {
        0 => "Без ошибок",
        1 => "1 слово для повторения",
        _ => $"{Mistakes.Count} слов для повторения"
    };

    private IReadOnlyList<WordEntry> FilteredWords => _allWords
        .Where(word => SelectedLevel?.Level is null ||
                       string.Equals(word.Level, SelectedLevel.Level, StringComparison.OrdinalIgnoreCase))
        .ToList();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _allWords.Clear();
        _allWords.AddRange(await _contentRepository.GetWordsAsync(cancellationToken: cancellationToken));
        RaiseSelectionProperties();
    }

    public void Activate()
    {
        if (IsTestActive && CurrentQuestion is not null)
        {
            _keyboardLayoutService.SwitchTo(CurrentQuestion.AnswerCultureCode);
        }
    }

    private bool CanStart() => !IsTestActive && EffectiveQuestionCount > 0;

    private bool CanSubmit() =>
        IsTestActive && CurrentQuestion is not null && !string.IsNullOrWhiteSpace(Answer);

    private void Start()
    {
        var words = FilteredWords;
        if (words.Count == 0)
        {
            SelectionMessage = "Выберите уровень, для которого есть слова.";
            return;
        }

        _session = VocabularyTestSession.Create(words, RequestedQuestionCount);
        if (_session.Questions.Count == 0)
        {
            SelectionMessage = "В подборке нет полных пар перевода RU–DE.";
            return;
        }

        _result = null;
        _currentQuestionIndex = 0;
        _sessionId = Guid.NewGuid();
        Mistakes.Clear();
        LenientAcceptances.Clear();
        IsComplete = false;
        IsTestActive = true;
        LoadCurrentQuestion();
    }

    private async Task SubmitAsync()
    {
        if (_session is null || CurrentQuestion is null)
        {
            return;
        }

        var session = _session;
        var question = CurrentQuestion;
        var questionResult = session.SubmitAnswer(question.Number, Answer);
        var completed = session.IsComplete;

        var word = _allWords.Single(item => item.Id == question.WordId);
        var attempt = LearningEvidenceFactory.Create(
            LearningContentKey.ForWord(word),
            question.Level,
            LanguageSkill.Vocabulary,
            ExerciseType.BidirectionalTranslation,
            question.Direction == TranslationDirection.SourceToTarget
                ? AttemptDirection.RussianToGerman
                : AttemptDirection.GermanToRussian,
            questionResult.IsCorrect,
            _sessionId,
            _attemptStartedAtUtc,
            mode: AssessmentMode.Diagnostic,
            rubricVersion: question.AnswerCultureCode.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
                ? LearningEvidenceFactory.RussianVocabularyLeniencyRubric
                : LearningEvidenceFactory.ExactAnswerRubric);
        await _attemptSink.RecordAsync(attempt, ContentType.AssessmentWord, question.WordId);

        // The progress write yields to the dispatcher. Work only with the captured session
        // afterwards so a reset or navigation action cannot invalidate this continuation.
        if (!ReferenceEquals(_session, session))
        {
            return;
        }

        if (completed)
        {
            CompleteTest(session);
            TestCompleted?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _currentQuestionIndex++;
            LoadCurrentQuestion();
        }
    }

    private void LoadCurrentQuestion()
    {
        if (_session is null)
        {
            return;
        }

        Answer = string.Empty;
        CurrentQuestion = _session.Questions[_currentQuestionIndex];
        _attemptStartedAtUtc = DateTimeOffset.UtcNow;
        _keyboardLayoutService.SwitchTo(CurrentQuestion.AnswerCultureCode);
    }

    private void CompleteTest(VocabularyTestSession session)
    {
        _result = session.GetResult();
        Mistakes.Clear();
        LenientAcceptances.Clear();
        foreach (var mistake in _result.QuestionResults.Where(item => item.IsAnswered && !item.IsCorrect))
        {
            Mistakes.Add(new VocabularyTestMistake(
                DirectionText(mistake.Question.Direction),
                mistake.Question.Prompt,
                string.IsNullOrWhiteSpace(mistake.Answer) ? "—" : mistake.Answer,
                mistake.Question.ExpectedAnswer));
        }
        foreach (var accepted in _result.QuestionResults.Where(item =>
                     item.IsAnswered && item.IsCorrect &&
                     item.MatchKind is AnswerMatchKind.AcceptedVariant or AnswerMatchKind.RussianTypo))
        {
            LenientAcceptances.Add(new VocabularyTestLenientAcceptance(
                DirectionText(accepted.Question.Direction),
                accepted.Question.Prompt,
                accepted.Answer ?? string.Empty,
                accepted.Question.ExpectedAnswer,
                accepted.MatchKind == AnswerMatchKind.RussianTypo
                    ? "Зачтено — проверьте написание"
                    : "Верно — допустимый вариант"));
        }

        CurrentQuestion = null;
        Answer = string.Empty;
        IsTestActive = false;
        IsComplete = true;
        RaiseResultProperties();
    }

    private void Restart()
    {
        _session = null;
        _result = null;
        _currentQuestionIndex = 0;
        CurrentQuestion = null;
        Answer = string.Empty;
        Mistakes.Clear();
        LenientAcceptances.Clear();
        IsComplete = false;
        IsTestActive = false;
        SelectionMessage = string.Empty;
        RaiseSelectionProperties();
    }

    private void InsertGermanCharacter(object? parameter)
    {
        if (parameter is string character && ShowGermanCharacterPanel)
        {
            Answer += character;
        }
    }

    private void RaiseSelectionProperties()
    {
        OnPropertyChanged(nameof(AvailableWordCount));
        OnPropertyChanged(nameof(EffectiveQuestionCount));
        OnPropertyChanged(nameof(AvailableWordsText));
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(SelectedLevelLabel));
        StartCommand.RaiseCanExecuteChanged();
    }

    private void RaiseResultProperties()
    {
        OnPropertyChanged(nameof(CompletionTitle));
        OnPropertyChanged(nameof(OverallResultText));
        OnPropertyChanged(nameof(RussianToGermanResultText));
        OnPropertyChanged(nameof(GermanToRussianResultText));
        OnPropertyChanged(nameof(HasMistakes));
        OnPropertyChanged(nameof(HasLenientAcceptances));
        OnPropertyChanged(nameof(IsPerfectResult));
        OnPropertyChanged(nameof(MistakesTitle));
    }

    private static string DirectionText(TranslationDirection direction) =>
        direction == TranslationDirection.SourceToTarget ? "RU → DE" : "DE → RU";

    private static string FormatPercent(double value) => $"{value:P0}";
}
