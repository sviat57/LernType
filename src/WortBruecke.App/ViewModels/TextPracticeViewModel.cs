using System.Collections.ObjectModel;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;
using WortBruecke.Core.Training;

namespace WortBruecke.App.ViewModels;

public sealed record PassageModeOption(PassagePracticeMode Mode, string Title, string Description);

public sealed class TextPracticeViewModel : ObservableObject
{
    private readonly IContentRepository _contentRepository;
    private readonly IProgressRepository _progressRepository;
    private readonly IKeyboardLayoutService _keyboardLayoutService;
    private readonly LanguagePair _pair = LanguagePair.RussianToGerman;
    private readonly List<Passage> _allPassages = [];
    private readonly Dictionary<int, bool> _segmentResults = [];
    private Passage? _selectedPassage;
    private PassageModeOption? _selectedMode;
    private int _segmentIndex;
    private string _answer = string.Empty;
    private bool _isPractising;
    private bool _showFeedback;
    private bool _isCorrect;
    private bool _isComplete;
    private int _correctCount;
    private bool _isSuggestedPractice;
    private string? _activeLevel;

    public TextPracticeViewModel(
        IContentRepository contentRepository,
        IProgressRepository progressRepository,
        IKeyboardLayoutService keyboardLayoutService)
    {
        _contentRepository = contentRepository;
        _progressRepository = progressRepository;
        _keyboardLayoutService = keyboardLayoutService;
        Modes =
        [
            new PassageModeOption(PassagePracticeMode.Translation, "Перевод по предложениям", "Показываем русский оригинал, вы пишете по-немецки"),
            new PassageModeOption(PassagePracticeMode.GermanTyping, "Чистый набор немецкого", "Перепечатайте немецкий текст точно и без спешки")
        ];
        SelectedMode = Modes[0];
        StartCommand = new RelayCommand(Start, () => SelectedPassage is not null && SelectedMode is not null);
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => IsPractising && !ShowFeedback && !string.IsNullOrWhiteSpace(Answer));
        NextCommand = new RelayCommand(Next, () => ShowFeedback);
        PreviousCommand = new RelayCommand(Previous, () => IsPractising && _segmentIndex > 0 && !ShowFeedback);
        RestartCommand = new RelayCommand(Reset);
        InsertGermanCharacterCommand = new ParameterizedRelayCommand(InsertGermanCharacter, parameter => parameter is string);
    }

    public ObservableCollection<Passage> Passages { get; } = [];
    public ObservableCollection<PassageModeOption> Modes { get; }
    public RelayCommand StartCommand { get; }
    public AsyncRelayCommand CheckCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand RestartCommand { get; }
    public ParameterizedRelayCommand InsertGermanCharacterCommand { get; }
    public event EventHandler? SuggestedPracticeCompleted;

    public Passage? SelectedPassage
    {
        get => _selectedPassage;
        set
        {
            if (SetProperty(ref _selectedPassage, value))
            {
                StartCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(SelectedPassageTitle));
                OnPropertyChanged(nameof(SelectedPassageMeta));
            }
        }
    }

    public PassageModeOption? SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (SetProperty(ref _selectedMode, value))
            {
                StartCommand?.RaiseCanExecuteChanged();
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
                CheckCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsPractising
    {
        get => _isPractising;
        private set
        {
            if (SetProperty(ref _isPractising, value))
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
                CheckCommand.RaiseCanExecuteChanged();
                NextCommand.RaiseCanExecuteChanged();
                PreviousCommand.RaiseCanExecuteChanged();
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

    public bool IsSelectionVisible => !IsPractising && !IsComplete;
    public bool HasPassages => Passages.Count > 0;
    public bool HasNoPassages => !HasPassages;
    public string FilterLabel => string.IsNullOrWhiteSpace(_activeLevel) ? "Все уровни" : $"Уровень {_activeLevel}";
    public string SelectedPassageTitle => SelectedPassage?.Titles.For(_pair.Source.CultureCode) ?? string.Empty;
    public string SelectedPassageMeta => SelectedPassage is null ? string.Empty : $"{SelectedPassage.Level} · {KindLabel(SelectedPassage.Kind)} · {SelectedPassage.Segments.Count} фрагм.";
    public string SourceLabel => SelectedMode?.Mode == PassagePracticeMode.Translation ? "РУССКИЙ ОРИГИНАЛ" : "НЕМЕЦКИЙ ОРИГИНАЛ";
    public string SourceText
    {
        get
        {
            var segment = CurrentSegment;
            if (segment is null)
            {
                return string.Empty;
            }
            var culture = SelectedMode?.Mode == PassagePracticeMode.Translation ? _pair.Source.CultureCode : _pair.Target.CultureCode;
            return segment.Translations.For(culture);
        }
    }
    public string InputLabel => SelectedMode?.Mode == PassagePracticeMode.Translation ? "Ваш перевод по-немецки" : "Перепечатайте текст";
    public string ProgressText => SelectedPassage is null ? "0 / 0" : $"{Math.Min(_segmentIndex + 1, SelectedPassage.Segments.Count)} / {SelectedPassage.Segments.Count}";
    public double ProgressValue => SelectedPassage?.Segments.Count > 0 ? (double)(_segmentIndex + 1) / SelectedPassage.Segments.Count * 100 : 0;
    public string FeedbackTitle => IsCorrect ? "Совпадает" : "Есть различия";
    public string FeedbackDetail => IsCorrect ? "Фрагмент набран точно." : $"Ожидаемый вариант: {ExpectedText}";
    public string CompletionTitle => $"{_correctCount} из {SelectedPassage?.Segments.Count ?? 0} точно";

    private PassageSegment? CurrentSegment => SelectedPassage?.Segments.ElementAtOrDefault(_segmentIndex);
    private string ExpectedText => CurrentSegment?.Translations.For(_pair.Target.CultureCode) ?? string.Empty;

    public async Task InitializeAsync()
    {
        _allPassages.Clear();
        _allPassages.AddRange(await _contentRepository.GetPassagesAsync());
        ApplyLevelFilter(null);
    }

    public void ApplyLevelFilter(string? level)
    {
        Reset();
        _activeLevel = level;
        Passages.Clear();
        foreach (var passage in _allPassages.Where(passage => string.IsNullOrWhiteSpace(level) ||
                     string.Equals(passage.Level, level, StringComparison.OrdinalIgnoreCase)))
        {
            Passages.Add(passage);
        }
        SelectedPassage = Passages.FirstOrDefault();
        OnPropertyChanged(nameof(HasPassages));
        OnPropertyChanged(nameof(HasNoPassages));
        OnPropertyChanged(nameof(FilterLabel));
    }

    public void ApplySettings(AppSettings settings)
    {
        SelectedMode = Modes.First(mode => mode.Mode == settings.PassageMode);
    }

    public void StartSuggested()
    {
        ApplyLevelFilter(null);
        if (Passages.Count == 0)
        {
            return;
        }
        SelectedPassage = Passages[Random.Shared.Next(Passages.Count)];
        StartCore(true);
    }

    private void Start()
    {
        StartCore(false);
    }

    private void StartCore(bool isSuggestedPractice)
    {
        if (SelectedPassage is null)
        {
            return;
        }
        _isSuggestedPractice = isSuggestedPractice;
        _segmentIndex = 0;
        _correctCount = 0;
        _segmentResults.Clear();
        IsComplete = false;
        IsPractising = true;
        LoadSegment();
    }

    private async Task CheckAsync()
    {
        if (CurrentSegment is null || SelectedPassage is null)
        {
            return;
        }
        IsCorrect = AnswerEvaluator.Evaluate(Answer, ExpectedText, _pair.Target.CultureCode).IsCorrect;
        // A learner may return to a previous segment and submit it again. Keep one
        // current result per segment so navigation cannot inflate the final score.
        _segmentResults[_segmentIndex] = IsCorrect;
        _correctCount = _segmentResults.Values.Count(result => result);
        ShowFeedback = true;
        await _progressRepository.RecordAttemptAsync(ContentType.Passage, SelectedPassage.Id, IsCorrect);
        OnPropertyChanged(nameof(FeedbackTitle));
        OnPropertyChanged(nameof(FeedbackDetail));
    }

    private void Next()
    {
        if (SelectedPassage is null)
        {
            return;
        }
        _segmentIndex++;
        if (_segmentIndex >= SelectedPassage.Segments.Count)
        {
            IsPractising = false;
            IsComplete = true;
            ShowFeedback = false;
            OnPropertyChanged(nameof(CompletionTitle));
            if (_isSuggestedPractice)
            {
                _isSuggestedPractice = false;
                SuggestedPracticeCompleted?.Invoke(this, EventArgs.Empty);
            }
            return;
        }
        LoadSegment();
    }

    private void Previous()
    {
        if (_segmentIndex <= 0)
        {
            return;
        }
        _segmentIndex--;
        LoadSegment();
    }

    private void LoadSegment()
    {
        _keyboardLayoutService.SwitchTo(_pair.Target.CultureCode);
        Answer = string.Empty;
        ShowFeedback = false;
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(InputLabel));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressValue));
        PreviousCommand.RaiseCanExecuteChanged();
    }

    private void Reset()
    {
        _isSuggestedPractice = false;
        IsComplete = false;
        IsPractising = false;
        ShowFeedback = false;
        Answer = string.Empty;
        _segmentResults.Clear();
        _correctCount = 0;
    }

    private void InsertGermanCharacter(object? parameter)
    {
        if (parameter is string character)
        {
            Answer += character;
            OnPropertyChanged(nameof(Answer));
        }
    }

    private static string KindLabel(PassageKind kind) => kind switch
    {
        PassageKind.FairyTale => "Сказка",
        PassageKind.Classic => "Классика",
        _ => "Бытовой текст"
    };
}
