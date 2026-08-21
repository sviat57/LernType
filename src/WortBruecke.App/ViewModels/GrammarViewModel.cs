using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;

namespace WortBruecke.App.ViewModels;

public sealed class GrammarViewModel : ObservableObject
{
    private readonly IContentRepository _contentRepository;
    private readonly LearningAttemptSink _attemptSink;
    private readonly IKeyboardLayoutService _keyboardLayoutService;
    private readonly IGrammarHeuristicService _heuristicService;
    private readonly ILanguageAnalysisService _analysisService;
    private readonly LanguagePair _pair = LanguagePair.RussianToGerman;
    private GrammarTask? _selectedTask;
    private string _answer = string.Empty;
    private bool _hasFeedback;
    private bool _hasExpectedMarkers;
    private string _feedbackSummary = string.Empty;
    private string _onlineFeedback = string.Empty;
    private string _onlineError = string.Empty;
    private bool _hasOnlineFeedback;
    private readonly Guid _sessionId = Guid.NewGuid();
    private DateTimeOffset _attemptStartedAtUtc = DateTimeOffset.UtcNow;

    public GrammarViewModel(
        IContentRepository contentRepository,
        IProgressRepository progressRepository,
        IKeyboardLayoutService keyboardLayoutService,
        IGrammarHeuristicService heuristicService,
        ILanguageAnalysisService analysisService)
    {
        _contentRepository = contentRepository;
        _attemptSink = new LearningAttemptSink(progressRepository);
        _keyboardLayoutService = keyboardLayoutService;
        _heuristicService = heuristicService;
        _analysisService = analysisService;
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => SelectedTask is not null && !string.IsNullOrWhiteSpace(Answer));
        OnlineCheckCommand = new AsyncRelayCommand(CheckOnlineAsync, () => SelectedTask is not null && !string.IsNullOrWhiteSpace(Answer));
        InsertGermanCharacterCommand = new ParameterizedRelayCommand(InsertGermanCharacter, parameter => parameter is string);
    }

    public GrammarViewModel(
        IContentRepository contentRepository,
        IAttemptRepository attemptRepository,
        IKeyboardLayoutService keyboardLayoutService,
        IGrammarHeuristicService heuristicService,
        ILanguageAnalysisService analysisService)
    {
        _contentRepository = contentRepository;
        _attemptSink = new LearningAttemptSink(attemptRepository);
        _keyboardLayoutService = keyboardLayoutService;
        _heuristicService = heuristicService;
        _analysisService = analysisService;
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => SelectedTask is not null && !string.IsNullOrWhiteSpace(Answer));
        OnlineCheckCommand = new AsyncRelayCommand(CheckOnlineAsync, () => SelectedTask is not null && !string.IsNullOrWhiteSpace(Answer));
        InsertGermanCharacterCommand = new ParameterizedRelayCommand(InsertGermanCharacter, parameter => parameter is string);
    }

    public ObservableCollection<GrammarTask> Tasks { get; } = [];
    public ObservableCollection<string> FoundMarkers { get; } = [];
    public ObservableCollection<string> MissingMarkers { get; } = [];
    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand OnlineCheckCommand { get; }
    public ParameterizedRelayCommand InsertGermanCharacterCommand { get; }

    public GrammarTask? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetProperty(ref _selectedTask, value))
            {
                Answer = string.Empty;
                _attemptStartedAtUtc = DateTimeOffset.UtcNow;
                HasFeedback = false;
                OnPropertyChanged(nameof(Instruction));
                OnPropertyChanged(nameof(SourceText));
                OnPropertyChanged(nameof(LevelLabel));
                CheckCommand.RaiseCanExecuteChanged();
                OnlineCheckCommand.RaiseCanExecuteChanged();
                Activate();
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
                OnlineCheckCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasFeedback
    {
        get => _hasFeedback;
        private set => SetProperty(ref _hasFeedback, value);
    }

    public bool HasExpectedMarkers
    {
        get => _hasExpectedMarkers;
        private set => SetProperty(ref _hasExpectedMarkers, value);
    }

    public string FeedbackSummary
    {
        get => _feedbackSummary;
        private set => SetProperty(ref _feedbackSummary, value);
    }

    public string OnlineFeedback { get => _onlineFeedback; private set => SetProperty(ref _onlineFeedback, value); }
    public string OnlineError { get => _onlineError; private set => SetProperty(ref _onlineError, value); }
    public bool HasOnlineFeedback { get => _hasOnlineFeedback; private set => SetProperty(ref _hasOnlineFeedback, value); }

    public string Instruction => SelectedTask?.Instructions.For(_pair.Source.CultureCode) ?? string.Empty;
    public string SourceText => SelectedTask?.SourceText ?? string.Empty;
    public string LevelLabel => SelectedTask is null ? string.Empty : $"{SelectedTask.Level} · офлайн-проверка";

    public async Task InitializeAsync()
    {
        Tasks.Clear();
        foreach (var task in await _contentRepository.GetGrammarTasksAsync())
        {
            Tasks.Add(task);
        }
        SelectedTask = Tasks.FirstOrDefault();
    }

    public void Activate() => _keyboardLayoutService.SwitchTo(_pair.Target.CultureCode);

    public void CancelOnlineAnalysis() => OnlineCheckCommand.Cancel();

    private async Task CheckAsync()
    {
        if (SelectedTask is null)
        {
            return;
        }
        var feedback = _heuristicService.Analyze(SelectedTask.MarkerRule, Answer);
        HasExpectedMarkers = feedback.HasExpectedMarkers;
        FeedbackSummary = feedback.Summary;
        FoundMarkers.Clear();
        MissingMarkers.Clear();
        foreach (var marker in feedback.FoundMarkers)
        {
            FoundMarkers.Add(marker);
        }
        foreach (var marker in feedback.MissingMarkers)
        {
            MissingMarkers.Add(marker);
        }
        HasFeedback = true;
        var attempt = LearningEvidenceFactory.Create(
            LearningContentKey.ForGrammar(SelectedTask),
            SelectedTask.Level,
            LanguageSkill.Grammar,
            ExerciseType.GrammarTransformation,
            AttemptDirection.GermanProduction,
            feedback.HasExpectedMarkers,
            _sessionId,
            _attemptStartedAtUtc,
            EvidenceQuality.Heuristic,
            rubricVersion: LearningEvidenceFactory.HeuristicGrammarRubric);
        await _attemptSink.RecordAsync(attempt, ContentType.Grammar, SelectedTask.Id);
    }

    private async Task CheckOnlineAsync(CancellationToken cancellationToken)
    {
        if (SelectedTask is null)
        {
            return;
        }
        try
        {
            OnlineFeedback = await _analysisService.AnalyzeGrammarAsync(SourceText, Instruction, Answer, cancellationToken);
            OnlineError = string.Empty;
        }
        catch (LanguageAnalysisUnavailableException exception)
        {
            OnlineFeedback = string.Empty;
            OnlineError = exception.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            OnlineFeedback = string.Empty;
            OnlineError = "Онлайн-разбор отменён.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            OnlineFeedback = string.Empty;
            OnlineError = "Расширенный разбор сейчас недоступен. Офлайн-проверка продолжает работать.";
        }
        HasOnlineFeedback = true;
    }

    private void InsertGermanCharacter(object? parameter)
    {
        if (parameter is string character)
        {
            Answer += character;
        }
    }
}
