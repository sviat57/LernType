using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;

namespace WortBruecke.App.ViewModels;

public sealed class GrammarViewModel : ObservableObject
{
    private readonly IContentRepository _contentRepository;
    private readonly IProgressRepository _progressRepository;
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

    public GrammarViewModel(
        IContentRepository contentRepository,
        IProgressRepository progressRepository,
        IKeyboardLayoutService keyboardLayoutService,
        IGrammarHeuristicService heuristicService,
        ILanguageAnalysisService analysisService)
    {
        _contentRepository = contentRepository;
        _progressRepository = progressRepository;
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
        await _progressRepository.RecordAttemptAsync(ContentType.Grammar, SelectedTask.Id, feedback.HasExpectedMarkers);
    }

    private async Task CheckOnlineAsync()
    {
        if (SelectedTask is null)
        {
            return;
        }
        try
        {
            OnlineFeedback = await _analysisService.AnalyzeGrammarAsync(SourceText, Instruction, Answer);
            OnlineError = string.Empty;
        }
        catch (LanguageAnalysisUnavailableException exception)
        {
            OnlineFeedback = string.Empty;
            OnlineError = exception.Message;
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
