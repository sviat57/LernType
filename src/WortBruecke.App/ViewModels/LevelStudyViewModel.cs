using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;

namespace WortBruecke.App.ViewModels;

public sealed class LevelStudyModuleViewModel
{
    public LevelStudyModuleViewModel(
        LevelModuleKind kind,
        string title,
        string description,
        string contentText,
        string statusText,
        string progressText,
        double progressValue,
        bool isAvailable,
        bool isPublished,
        bool isMastered,
        ICommand launchCommand,
        string automationName)
    {
        Kind = kind;
        Title = title;
        Description = description;
        ContentText = contentText;
        StatusText = statusText;
        ProgressText = progressText;
        ProgressValue = progressValue;
        IsAvailable = isAvailable;
        IsPublished = isPublished;
        IsMastered = isMastered;
        LaunchCommand = launchCommand;
        AutomationName = automationName;
    }

    public LevelModuleKind Kind { get; }
    public string Title { get; }
    public string Description { get; }
    public string ContentText { get; }
    public string StatusText { get; }
    public string ProgressText { get; }
    public double ProgressValue { get; }
    public bool IsAvailable { get; }
    public bool IsPublished { get; }
    public bool IsOptional => !IsPublished;
    public bool IsMastered { get; }
    public ICommand LaunchCommand { get; }
    public string AutomationName { get; }
}

/// <summary>
/// A level-scoped launch surface. It never substitutes content from another level and keeps every
/// practice unit independent until the learner explicitly chooses another module.
/// </summary>
public sealed class LevelStudyViewModel : ObservableObject
{
    private const int BundledAudioPromptCountPerLevel = 3;
    private readonly IContentRepository _contentRepository;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AttemptEvent>>> _loadAttempts;
    private readonly Action<LevelModuleLaunch> _launchModule;
    private readonly bool _audioPracticeAvailable;
    private readonly LearningPathDefinition _definition = GermanCurriculum.CreateDefault();
    private LevelStudyRequest? _request;
    private LevelStudyModuleViewModel? _continueModule;
    private string _levelLabel = "Pre-A1";
    private string _title = "Уровень Pre-A1";
    private string _outcome = string.Empty;
    private string _progressText = "0 из 0 обязательных модулей освоено";
    private double _progressValue;
    private string _continueButtonText = "Продолжить уровень";
    private string _continueHint = "Выберите первый доступный модуль.";
    private string _availabilityText = string.Empty;
    private bool _hasUnavailableModules;
    private long _loadGeneration;

    public LevelStudyViewModel(
        IContentRepository contentRepository,
        IAttemptRepository attemptRepository,
        Action<LevelModuleLaunch> launchModule,
        Action returnToPath,
        bool audioPracticeAvailable = true)
        : this(
            contentRepository,
            cancellationToken => attemptRepository.GetAsync(cancellationToken: cancellationToken),
            launchModule,
            returnToPath,
            audioPracticeAvailable)
    {
        ArgumentNullException.ThrowIfNull(attemptRepository);
    }

    public LevelStudyViewModel(
        IContentRepository contentRepository,
        ILearningProgressRepository learningProgressRepository,
        Action<LevelModuleLaunch> launchModule,
        Action returnToPath,
        bool audioPracticeAvailable = true)
        : this(
            contentRepository,
            cancellationToken => LoadCompatibilityAttemptsAsync(learningProgressRepository, cancellationToken),
            launchModule,
            returnToPath,
            audioPracticeAvailable)
    {
        ArgumentNullException.ThrowIfNull(learningProgressRepository);
    }

    private LevelStudyViewModel(
        IContentRepository contentRepository,
        Func<CancellationToken, Task<IReadOnlyList<AttemptEvent>>> loadAttempts,
        Action<LevelModuleLaunch> launchModule,
        Action returnToPath,
        bool audioPracticeAvailable)
    {
        _contentRepository = contentRepository ?? throw new ArgumentNullException(nameof(contentRepository));
        _loadAttempts = loadAttempts ?? throw new ArgumentNullException(nameof(loadAttempts));
        _launchModule = launchModule ?? throw new ArgumentNullException(nameof(launchModule));
        ArgumentNullException.ThrowIfNull(returnToPath);
        _audioPracticeAvailable = audioPracticeAvailable;
        ContinueCommand = new RelayCommand(Continue, () => _continueModule is not null);
        BackCommand = new RelayCommand(returnToPath);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _request is not null);
    }

    public ObservableCollection<LevelStudyModuleViewModel> Modules { get; } = [];
    public RelayCommand ContinueCommand { get; }
    public RelayCommand BackCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public GermanLevel? Level => _request?.Level;
    public string LevelLabel { get => _levelLabel; private set => SetProperty(ref _levelLabel, value); }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Outcome { get => _outcome; private set => SetProperty(ref _outcome, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }
    public string ContinueButtonText { get => _continueButtonText; private set => SetProperty(ref _continueButtonText, value); }
    public string ContinueHint { get => _continueHint; private set => SetProperty(ref _continueHint, value); }
    public string AvailabilityText { get => _availabilityText; private set => SetProperty(ref _availabilityText, value); }
    public bool HasUnavailableModules { get => _hasUnavailableModules; private set => SetProperty(ref _hasUnavailableModules, value); }

    public async Task PrepareAsync(LevelStudyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Level))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Level, "Unknown German learning level.");
        }

        _request = request;
        OnPropertyChanged(nameof(Level));
        RefreshCommand.RaiseCanExecuteChanged();
        var load = BeginLoad();
        await LoadAsync(request.Level, load, cancellationToken);
    }

    public Task ActivateAsync(CancellationToken cancellationToken = default) => RefreshAsync(cancellationToken);

    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_request is not null)
        {
            var level = _request.Level;
            var load = BeginLoad();
            await LoadAsync(level, load, cancellationToken);
        }
    }

    private async Task LoadAsync(GermanLevel level, long load, CancellationToken cancellationToken)
    {
        var wordsTask = _contentRepository.GetWordsAsync(cancellationToken: cancellationToken);
        var sentencesTask = _contentRepository.GetSentencesAsync(cancellationToken: cancellationToken);
        var passagesTask = _contentRepository.GetPassagesAsync(cancellationToken);
        var grammarTask = _contentRepository.GetGrammarTasksAsync(cancellationToken);
        var attemptsTask = _loadAttempts(cancellationToken);
        await Task.WhenAll(wordsTask, sentencesTask, passagesTask, grammarTask, attemptsTask);
        if (!IsCurrentLoad(load, level))
        {
            return;
        }

        var definition = _definition.Levels.Single(item => item.Level == level);
        var attempts = await attemptsTask;
        var wordCount = (await wordsTask).Count(item => MatchesLevel(item.Level, level));
        var sentenceCount = (await sentencesTask).Count(item => MatchesLevel(item.Level, level));
        var passageCount = (await passagesTask).Count(item => MatchesLevel(item.Level, level));
        var grammarCount = (await grammarTask).Count(item => MatchesLevel(item.Level, level));
        var audioCount = _audioPracticeAvailable ? BundledAudioPromptCountPerLevel : 0;

        LevelLabel = Label(level);
        Title = definition.Title;
        Outcome = definition.Outcome;
        Modules.Clear();

        AddModule(level, definition, attempts, LevelModuleKind.WordGermanToRussian,
            "Слова · DE → RU", "Узнавайте немецкое слово и вводите русский перевод.", wordCount, "слов");
        AddModule(level, definition, attempts, LevelModuleKind.WordRussianToGerman,
            "Слова · RU → DE", "Воспроизводите немецкое слово по русскому значению.", wordCount, "слов");
        AddModule(level, definition, attempts, LevelModuleKind.SentenceGermanToRussian,
            "Предложения · DE → RU", "Читайте предложение и передавайте его смысл по-русски.", sentenceCount, "предложений");
        AddModule(level, definition, attempts, LevelModuleKind.SentenceRussianToGerman,
            "Предложения · RU → DE", "Собирайте немецкую фразу по русскому предложению.", sentenceCount, "предложений");
        AddModule(level, definition, attempts, LevelModuleKind.Text,
            "Тексты", "Читайте полный текст и отдельно отрабатывайте его фрагменты.", passageCount, "текстов");
        AddModule(level, definition, attempts, LevelModuleKind.Grammar,
            "Грамматика", "Закрепляйте конструкции этого уровня с локальной проверкой.", grammarCount, "заданий");
        AddModule(level, definition, attempts, LevelModuleKind.Audio,
            "Аудирование и речь", "Слушайте образцы, пишите диктант и тренируйте ответ вслух.", audioCount, "заданий");

        var published = Modules.Where(item => item.IsAvailable && item.IsPublished).ToArray();
        var mastered = published.Count(item => item.IsMastered);
        ProgressValue = published.Length == 0 ? 0 : mastered * 100d / published.Length;
        ProgressText = $"{mastered} из {published.Length} обязательных модулей освоено";

        _continueModule = published.FirstOrDefault(item => !item.IsMastered);
        if (_continueModule is null)
        {
            ContinueButtonText = published.Length == 0 ? "Нет обязательных модулей" : "Обязательные модули освоены";
            ContinueHint = published.Length == 0
                ? "Для этого уровня ещё нет опубликованной практики. Дополнительные модули можно открыть отдельно."
                : "Вы можете повторить любой доступный модуль вручную.";
        }
        else
        {
            ContinueButtonText = $"Продолжить: {_continueModule.Title}";
            ContinueHint = "Откроется первый ещё не освоенный опубликованный модуль этого уровня.";
        }
        ContinueCommand.RaiseCanExecuteChanged();

        var unavailable = Modules.Where(item => !item.IsAvailable).Select(item => item.Title).ToArray();
        HasUnavailableModules = unavailable.Length > 0;
        AvailabilityText = unavailable.Length == 0
            ? "Все модули этого уровня доступны."
            : $"Пока без материалов: {string.Join(", ", unavailable)}. Материалы другого уровня не подставляются.";
    }

    private void AddModule(
        GermanLevel level,
        LevelDefinition definition,
        IReadOnlyCollection<AttemptEvent> attempts,
        LevelModuleKind kind,
        string title,
        string description,
        int contentCount,
        string contentNoun)
    {
        var objectives = ObjectivesFor(kind, definition).ToArray();
        var publishedObjectives = objectives
            .Where(item => item.Availability == ObjectiveAvailability.Published)
            .ToArray();
        var isPublished = publishedObjectives.Length > 0;
        var isAvailable = contentCount > 0;
        var evaluation = Evaluate(kind, attempts, publishedObjectives);
        var status = !isAvailable
            ? "Нет материалов"
            : !isPublished
                ? "Дополнительно"
                : evaluation.IsMastered ? "Освоено" : evaluation.AttemptCount > 0 ? "В процессе" : "Не начато";
        var progressText = !isAvailable
            ? "Отключено для этого уровня"
            : !isPublished
                ? OptionalProgressText(kind, attempts, level)
                : evaluation.IsMastered
                    ? "Требования учебной цели выполнены"
                    : evaluation.AttemptCount == 0
                        ? "Практика ещё не начата"
                        : $"Попытки: {evaluation.AttemptCount}/{evaluation.RequiredAttempts} · точность: {evaluation.RecentScore:P0}";
        var request = new LevelModuleLaunch(level, kind);
        var command = new RelayCommand(() => _launchModule(request), () => isAvailable);
        Modules.Add(new LevelStudyModuleViewModel(
            kind,
            title,
            description,
            $"{contentCount} {contentNoun}",
            status,
            progressText,
            evaluation.Progress * 100,
            isAvailable,
            isPublished,
            evaluation.IsMastered,
            command,
            isAvailable ? $"Открыть {title}, уровень {Label(level)}" : $"{title}, уровень {Label(level)}: нет материалов"));
    }

    private void Continue()
    {
        if (_continueModule is not null && _request is not null)
        {
            _launchModule(new LevelModuleLaunch(_request.Level, _continueModule.Kind));
        }
    }

    private static ModuleEvaluation Evaluate(
        LevelModuleKind kind,
        IReadOnlyCollection<AttemptEvent> attempts,
        IReadOnlyCollection<LearningObjective> objectives)
    {
        if (objectives.Count == 0)
        {
            return ModuleEvaluation.Empty;
        }

        var objectiveEvaluations = objectives.Select(objective =>
        {
            var matching = attempts
                .Where(attempt => MatchesAttempt(kind, objective, attempt))
                .OrderByDescending(attempt => attempt.CompletedAtUtc)
                .ToArray();
            var recent = matching.Take(5).ToArray();
            var score = recent.Length == 0 ? 0 : recent.Average(item => item.Score);
            var distinctItems = matching.Select(item => item.ContentKey).Distinct(StringComparer.Ordinal).Count();
            var distinctDays = matching
                .Select(item => DateOnly.FromDateTime(item.CompletedAtUtc.UtcDateTime))
                .Distinct()
                .Count();
            var mastered = matching.Length >= objective.MinimumAttempts
                && distinctItems >= objective.MinimumDistinctItems
                && distinctDays >= objective.MinimumDistinctDays
                && score >= objective.MasteryThreshold;
            var progress = new[]
            {
                Ratio(matching.Length, objective.MinimumAttempts),
                Ratio(distinctItems, objective.MinimumDistinctItems),
                Ratio(distinctDays, objective.MinimumDistinctDays),
                objective.MasteryThreshold == 0 ? 1 : Math.Clamp(score / objective.MasteryThreshold, 0, 1)
            }.Min();
            return new ModuleEvaluation(
                matching.Length,
                objective.MinimumAttempts,
                score,
                progress,
                mastered);
        }).ToArray();

        return new ModuleEvaluation(
            objectiveEvaluations.Sum(item => item.AttemptCount),
            objectiveEvaluations.Sum(item => item.RequiredAttempts),
            objectiveEvaluations.Average(item => item.RecentScore),
            objectiveEvaluations.Average(item => item.Progress),
            objectiveEvaluations.All(item => item.IsMastered));
    }

    private static bool MatchesAttempt(LevelModuleKind kind, LearningObjective objective, AttemptEvent attempt)
    {
        if (attempt.Level != objective.Level
            || !string.Equals(attempt.ObjectiveId, objective.Id, StringComparison.OrdinalIgnoreCase)
            || attempt.Skill != objective.Skill
            || !objective.AcceptedExerciseTypes.Contains(attempt.ExerciseFamily)
            || attempt.EvidenceQuality < objective.MinimumEvidenceQuality)
        {
            return false;
        }

        return kind switch
        {
            LevelModuleKind.WordGermanToRussian =>
                attempt.ContentKey.StartsWith("core.word.", StringComparison.Ordinal)
                && attempt.Direction is AttemptDirection.GermanToRussian or AttemptDirection.Bidirectional,
            LevelModuleKind.WordRussianToGerman =>
                attempt.ContentKey.StartsWith("core.word.", StringComparison.Ordinal)
                && attempt.Direction is AttemptDirection.RussianToGerman or AttemptDirection.Bidirectional,
            LevelModuleKind.SentenceGermanToRussian =>
                attempt.ContentKey.StartsWith("core.sentence.", StringComparison.Ordinal)
                && attempt.Direction is AttemptDirection.GermanToRussian or AttemptDirection.Bidirectional,
            LevelModuleKind.SentenceRussianToGerman =>
                attempt.ContentKey.StartsWith("core.sentence.", StringComparison.Ordinal)
                && attempt.Direction is AttemptDirection.RussianToGerman or AttemptDirection.Bidirectional,
            LevelModuleKind.Text => attempt.ContentKey.StartsWith("core.passage.", StringComparison.Ordinal),
            LevelModuleKind.Grammar => attempt.ContentKey.StartsWith("core.grammar.", StringComparison.Ordinal),
            LevelModuleKind.Audio => attempt.ContentKey.StartsWith("audio.", StringComparison.Ordinal),
            _ => false
        };
    }

    private static IEnumerable<LearningObjective> ObjectivesFor(LevelModuleKind kind, LevelDefinition definition)
    {
        var skills = kind switch
        {
            LevelModuleKind.WordGermanToRussian or LevelModuleKind.WordRussianToGerman => [LanguageSkill.Vocabulary],
            LevelModuleKind.SentenceGermanToRussian => [LanguageSkill.Reading],
            LevelModuleKind.SentenceRussianToGerman => [LanguageSkill.Writing],
            LevelModuleKind.Text => [LanguageSkill.Mediation],
            LevelModuleKind.Grammar => [LanguageSkill.Grammar],
            LevelModuleKind.Audio => [LanguageSkill.Listening, LanguageSkill.Speaking],
            _ => Array.Empty<LanguageSkill>()
        };
        return definition.Objectives.Where(item => skills.Contains(item.Skill));
    }

    private static string OptionalProgressText(
        LevelModuleKind kind,
        IEnumerable<AttemptEvent> attempts,
        GermanLevel level)
    {
        var count = attempts.Count(attempt => attempt.Level == level && kind switch
        {
            LevelModuleKind.Grammar => attempt.ContentKey.StartsWith("core.grammar.", StringComparison.Ordinal),
            LevelModuleKind.Audio => attempt.ContentKey.StartsWith("audio.", StringComparison.Ordinal),
            _ => false
        });
        return count == 0 ? "Дополнительная практика не влияет на обязательный прогресс" : $"{count} попыток · дополнительная практика";
    }

    private static async Task<IReadOnlyList<AttemptEvent>> LoadCompatibilityAttemptsAsync(
        ILearningProgressRepository repository,
        CancellationToken cancellationToken)
    {
        var attempts = await repository.GetAllAsync(cancellationToken);
        return attempts.Select((attempt, index) => new AttemptEvent(
            DeterministicGuid($"level-study|{attempt.CompletedAtUtc:O}|{attempt.ObjectiveId}|{index}"),
            $"legacy.level-study.{attempt.Level.ToString().ToLowerInvariant()}.{index}",
            1,
            attempt.Level,
            attempt.Skill,
            attempt.ExerciseType,
            LegacyDirection(attempt.Skill),
            attempt.Score,
            attempt.Mode,
            attempt.CompletedAtUtc,
            attempt.CompletedAtUtc,
            attempt.SessionId ?? DeterministicGuid($"level-study-session|{attempt.CompletedAtUtc:O}|{index}"),
            "legacy-learning-v1",
            EvidenceQuality.HistoricalAggregate,
            attempt.ObjectiveId,
            attempt.WasTimed,
            attempt.Mode == AssessmentMode.MockExam ? "legacy-unknown-exam" : null)).ToArray();
    }

    private static AttemptDirection LegacyDirection(LanguageSkill skill) => skill switch
    {
        LanguageSkill.Vocabulary => AttemptDirection.Bidirectional,
        LanguageSkill.Reading => AttemptDirection.GermanToRussian,
        LanguageSkill.Writing or LanguageSkill.Mediation => AttemptDirection.RussianToGerman,
        LanguageSkill.Listening => AttemptDirection.GermanComprehension,
        LanguageSkill.Speaking or LanguageSkill.Grammar => AttemptDirection.GermanProduction,
        _ => AttemptDirection.NotApplicable
    };

    private static Guid DeterministicGuid(string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    private static bool MatchesLevel(string value, GermanLevel level) =>
        GermanLevelExtensions.TryParse(value, out var parsed) && parsed == level;

    private static double Ratio(int value, int required) => Math.Clamp((double)value / required, 0, 1);

    private long BeginLoad() => Interlocked.Increment(ref _loadGeneration);

    private bool IsCurrentLoad(long load, GermanLevel level) =>
        Volatile.Read(ref _loadGeneration) == load && _request?.Level == level;

    private static string Label(GermanLevel level) => level == GermanLevel.A0 ? "Pre-A1" : level.ToString();

    private sealed record ModuleEvaluation(
        int AttemptCount,
        int RequiredAttempts,
        double RecentScore,
        double Progress,
        bool IsMastered)
    {
        public static ModuleEvaluation Empty { get; } = new(0, 0, 0, 0, false);
    }
}
