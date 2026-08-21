using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;

namespace WortBruecke.App.ViewModels;

public sealed record LearningLevelCardViewModel(
    string Level,
    string Title,
    string Outcome,
    string StateText,
    string ProgressText,
    double ProgressValue,
    string ContentText,
    string ExamText,
    string MissingSkillsText,
    bool HasMissingSkills,
    bool IsUnlocked,
    bool IsCurrent,
    bool IsCompleted,
    ICommand OpenPracticeCommand,
    ICommand OpenExamCommand);

public sealed class LearningPathViewModel : ObservableObject
{
    private readonly IContentRepository _contentRepository;
    private readonly IAttemptRepository? _attemptRepository;
    private readonly ILearningProgressRepository? _compatibilityRepository;
    private readonly IExamBlueprintRepository _examBlueprintRepository;
    private readonly IPlacementResultProvider? _placementProvider;
    private readonly Action<string> _navigate;
    private readonly LearningPathDefinition _definition = GermanCurriculum.CreateDefault();
    private readonly LearningProgressService _progressService = new();
    private string _currentLevelText = "A0";
    private string _overallProgressText = "0%";
    private double _overallProgressValue;
    private string _catalogStatusText = "Проверяем учебный каталог…";

    public LearningPathViewModel(
        IContentRepository contentRepository,
        IProgressRepository legacyProgressRepository,
        ILearningProgressRepository learningProgressRepository,
        IExamBlueprintRepository examBlueprintRepository,
        Action<string> navigate)
    {
        _contentRepository = contentRepository;
        _ = legacyProgressRepository;
        _compatibilityRepository = learningProgressRepository;
        _examBlueprintRepository = examBlueprintRepository;
        _navigate = navigate;
        RefreshCommand = new AsyncRelayCommand(InitializeAsync);
    }

    public LearningPathViewModel(
        IContentRepository contentRepository,
        IAttemptRepository attemptRepository,
        IExamBlueprintRepository examBlueprintRepository,
        Action<string> navigate,
        IPlacementResultProvider? placementProvider = null)
    {
        _contentRepository = contentRepository;
        _attemptRepository = attemptRepository;
        _examBlueprintRepository = examBlueprintRepository;
        _placementProvider = placementProvider;
        _navigate = navigate;
        RefreshCommand = new AsyncRelayCommand(InitializeAsync);
    }

    public ObservableCollection<LearningLevelCardViewModel> Levels { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public string CurrentLevelText { get => _currentLevelText; private set => SetProperty(ref _currentLevelText, value); }
    public string OverallProgressText { get => _overallProgressText; private set => SetProperty(ref _overallProgressText, value); }
    public double OverallProgressValue { get => _overallProgressValue; private set => SetProperty(ref _overallProgressValue, value); }
    public string CatalogStatusText { get => _catalogStatusText; private set => SetProperty(ref _catalogStatusText, value); }

    public async Task InitializeAsync()
    {
        var wordsTask = _contentRepository.GetWordsAsync();
        var sentencesTask = _contentRepository.GetSentencesAsync();
        var passagesTask = _contentRepository.GetPassagesAsync();
        var grammarTask = _contentRepository.GetGrammarTasksAsync();
        var attemptsTask = LoadAttemptsAsync();
        var placementTask = _placementProvider?.GetLatestAsync() ?? Task.FromResult<PlacementResult?>(null);
        var examCatalogTask = _examBlueprintRepository.LoadAsync();
        await Task.WhenAll(wordsTask, sentencesTask, passagesTask, grammarTask, attemptsTask, placementTask, examCatalogTask);

        var words = await wordsTask;
        var sentences = await sentencesTask;
        var passages = await passagesTask;
        var grammar = await grammarTask;
        var attempts = await attemptsTask;
        var placement = await placementTask;
        var path = _progressService.EvaluatePathFromEvents(_definition, attempts, placement?.RecommendedLevel);
        var examCatalog = await examCatalogTask;

        Levels.Clear();
        foreach (var levelProgress in path.Levels)
        {
            var level = levelProgress.Definition.Level;
            var levelCode = level.ToString();
            var levelWords = words.Count(item => MatchesLevel(item.Level, level));
            var levelSentences = sentences.Count(item => MatchesLevel(item.Level, level));
            var levelPassages = passages.Count(item => MatchesLevel(item.Level, level));
            var levelGrammar = grammar.Count(item => MatchesLevel(item.Level, level));
            var contentCount = levelWords + levelSentences + levelPassages + levelGrammar;
            var coveredSkills = CoveredSkills(levelWords, levelSentences, levelPassages, levelGrammar);
            var releasedObjectives = levelProgress.Definition.Objectives
                .Where(item => item.Availability == ObjectiveAvailability.Published)
                .ToArray();
            var plannedObjectives = levelProgress.Definition.Objectives
                .Where(item => item.Availability != ObjectiveAvailability.Published)
                .ToArray();
            var requiredSkills = releasedObjectives.Select(item => item.Skill).Distinct().ToArray();
            var missingSkills = requiredSkills.Except(coveredSkills).ToArray();
            var examCount = examCatalog.Exams.Count(exam => exam.Levels.Contains(levelCode, StringComparer.OrdinalIgnoreCase));
            var state = levelProgress.IsCompleted
                ? "Завершён"
                : levelProgress.Definition.Level == path.CurrentLevel
                    ? "Текущий этап"
                    : levelProgress.IsUnlocked ? "Доступен" : "Следующий этап";

            Levels.Add(new LearningLevelCardViewModel(
                LevelLabel(level),
                levelProgress.Definition.Title,
                levelProgress.Definition.Outcome,
                state,
                $"Освоено целей: {levelProgress.MasteredRequiredObjectiveCount} из {levelProgress.RequiredObjectiveCount}",
                levelProgress.Completion * 100,
                $"{contentCount} упражнений · опубликовано целей: {releasedObjectives.Length}",
                examCount == 0 ? "Внутренний этап Pre-A1" : $"Официальных форматов в каталоге: {examCount}",
                BuildAvailabilityText(missingSkills, plannedObjectives),
                missingSkills.Length > 0 || plannedObjectives.Length > 0,
                levelProgress.IsUnlocked,
                levelProgress.Definition.Level == path.CurrentLevel,
                levelProgress.IsCompleted,
                new RelayCommand(() => _navigate("trainer"), () => contentCount > 0),
                new RelayCommand(() => _navigate("exams"), () => examCount > 0)));
        }

        CurrentLevelText = LevelLabel(path.CurrentLevel);
        OverallProgressValue = path.OverallCompletion * 100;
        OverallProgressText = $"{OverallProgressValue:0}% пути подтверждено попытками";
        CatalogStatusText = $"Экзаменационные форматы проверены {examCatalog.LastVerified:dd.MM.yyyy}. " +
            "Pre-A1 — внутренний этап; невыпущенные цели не снижают прогресс и не блокируют A1.";
    }

    public Task ActivateAsync() => InitializeAsync();

    private async Task<IReadOnlyList<AttemptEvent>> LoadAttemptsAsync()
    {
        if (_attemptRepository is not null)
        {
            return await _attemptRepository.GetAsync();
        }
        if (_compatibilityRepository is null)
        {
            return [];
        }
        var legacy = await _compatibilityRepository.GetAllAsync();
        return legacy.Select((item, index) => new AttemptEvent(
            DeterministicGuid($"{item.CompletedAtUtc:O}|{item.ObjectiveId}|{index}"),
            $"legacy.learning.{item.Level.ToString().ToLowerInvariant()}.{item.ObjectiveId ?? item.Skill.ToString().ToLowerInvariant()}",
            1,
            item.Level,
            item.Skill,
            item.ExerciseType,
            AttemptDirection.NotApplicable,
            item.Score,
            item.Mode,
            item.CompletedAtUtc,
            item.CompletedAtUtc,
            item.SessionId ?? DeterministicGuid($"session|{item.CompletedAtUtc:O}|{index}"),
            "legacy-learning-v1",
            EvidenceQuality.HistoricalAggregate,
            item.ObjectiveId,
            item.WasTimed,
            item.Mode == AssessmentMode.MockExam ? "legacy-unknown-exam" : null)).ToArray();
    }

    private static Guid DeterministicGuid(string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    private static bool MatchesLevel(string value, GermanLevel level) =>
        GermanLevelExtensions.TryParse(value, out var parsed) && parsed == level;

    private static IReadOnlySet<LanguageSkill> CoveredSkills(int words, int sentences, int passages, int grammar)
    {
        var result = new HashSet<LanguageSkill>();
        if (words > 0)
        {
            result.Add(LanguageSkill.Vocabulary);
        }
        if (sentences > 0)
        {
            result.Add(LanguageSkill.Writing);
        }
        if (passages > 0)
        {
            result.Add(LanguageSkill.Reading);
            result.Add(LanguageSkill.Mediation);
        }
        if (grammar > 0)
        {
            result.Add(LanguageSkill.Grammar);
        }
        return result;
    }

    private static string SkillLabel(LanguageSkill skill) => skill switch
    {
        LanguageSkill.Vocabulary => "лексика",
        LanguageSkill.Grammar => "грамматика",
        LanguageSkill.Reading => "чтение",
        LanguageSkill.Listening => "аудирование",
        LanguageSkill.Writing => "письмо",
        LanguageSkill.Speaking => "говорение",
        LanguageSkill.Mediation => "медиация",
        _ => skill.ToString()
    };

    private static string LevelLabel(GermanLevel level) => level == GermanLevel.A0 ? "Pre-A1" : level.ToString();

    private static string BuildAvailabilityText(
        IReadOnlyCollection<LanguageSkill> missingPublishedSkills,
        IReadOnlyCollection<LearningObjective> plannedObjectives)
    {
        var parts = new List<string>();
        if (missingPublishedSkills.Count > 0)
        {
            parts.Add($"Нет заданий: {string.Join(", ", missingPublishedSkills.Select(SkillLabel))}");
        }
        if (plannedObjectives.Count > 0)
        {
            parts.Add($"В разработке: {string.Join(", ", plannedObjectives.Select(item => SkillLabel(item.Skill)).Distinct())}");
        }
        return string.Join(" · ", parts);
    }
}
