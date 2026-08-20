using System.Collections.ObjectModel;
using System.Windows.Input;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;

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
    private readonly IProgressRepository _legacyProgressRepository;
    private readonly ILearningProgressRepository _learningProgressRepository;
    private readonly IExamBlueprintRepository _examBlueprintRepository;
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
        _legacyProgressRepository = legacyProgressRepository;
        _learningProgressRepository = learningProgressRepository;
        _examBlueprintRepository = examBlueprintRepository;
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
        var legacyProgressTask = _legacyProgressRepository.GetAllAsync();
        var learningAttemptsTask = _learningProgressRepository.GetAllAsync();
        var examCatalogTask = _examBlueprintRepository.LoadAsync();
        await Task.WhenAll(wordsTask, sentencesTask, passagesTask, grammarTask, legacyProgressTask, learningAttemptsTask, examCatalogTask);

        var words = await wordsTask;
        var sentences = await sentencesTask;
        var passages = await passagesTask;
        var grammar = await grammarTask;
        var legacyProgress = await legacyProgressTask;
        var attempts = (await learningAttemptsTask).ToList();
        attempts.AddRange(AdaptLegacyEvidence(legacyProgress, words, sentences, passages, grammar));
        var path = _progressService.EvaluatePath(_definition, attempts);
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
            var requiredSkills = levelProgress.Definition.Objectives.Select(item => item.Skill).Distinct().ToArray();
            var missingSkills = requiredSkills.Except(coveredSkills).ToArray();
            var examCount = examCatalog.Exams.Count(exam => exam.Levels.Contains(levelCode, StringComparer.OrdinalIgnoreCase));
            var state = levelProgress.IsCompleted
                ? "Завершён"
                : levelProgress.Definition.Level == path.CurrentLevel
                    ? "Текущий этап"
                    : levelProgress.IsUnlocked ? "Доступен" : "Следующий этап";

            Levels.Add(new LearningLevelCardViewModel(
                levelCode,
                levelProgress.Definition.Title,
                levelProgress.Definition.Outcome,
                state,
                $"Освоено целей: {levelProgress.MasteredRequiredObjectiveCount} из {levelProgress.RequiredObjectiveCount}",
                levelProgress.Completion * 100,
                $"{contentCount} упражнений · покрыто {coveredSkills.Count} из {requiredSkills.Length} навыков",
                examCount == 0 ? "Внутренний этап Pre-A1" : $"Официальных форматов в каталоге: {examCount}",
                missingSkills.Length == 0 ? string.Empty : $"Нужно добавить: {string.Join(", ", missingSkills.Select(SkillLabel))}",
                missingSkills.Length > 0,
                levelProgress.IsUnlocked,
                levelProgress.Definition.Level == path.CurrentLevel,
                levelProgress.IsCompleted,
                new RelayCommand(() => _navigate("trainer"), () => contentCount > 0),
                new RelayCommand(() => _navigate("exams"), () => examCount > 0)));
        }

        CurrentLevelText = path.CurrentLevel.ToString();
        OverallProgressValue = path.OverallCompletion * 100;
        OverallProgressText = $"{OverallProgressValue:0}% пути подтверждено попытками";
        CatalogStatusText = $"Экзаменационные форматы проверены {examCatalog.LastVerified:dd.MM.yyyy}. " +
            "A0 — внутренний этап; официальные сертификаты начинаются с A1.";
    }

    private IEnumerable<LearningAttempt> AdaptLegacyEvidence(
        IReadOnlyList<ProgressRecord> records,
        IReadOnlyList<WordEntry> words,
        IReadOnlyList<SentenceEntry> sentences,
        IReadOnlyList<Passage> passages,
        IReadOnlyList<GrammarTask> grammar)
    {
        var wordsById = words.ToDictionary(item => (long)item.Id);
        var sentencesById = sentences.ToDictionary(item => (long)item.Id);
        var passagesById = passages.ToDictionary(item => (long)item.Id);
        var grammarById = grammar.ToDictionary(item => (long)item.Id);

        foreach (var record in records.Where(item => item.AttemptCount > 0))
        {
            var mapped = record.ContentType switch
            {
                ContentType.Word or ContentType.AssessmentWord when wordsById.TryGetValue(record.ContentId, out var word) =>
                    Map(word.Level, LanguageSkill.Vocabulary, ExerciseType.BidirectionalTranslation),
                ContentType.Sentence when sentencesById.TryGetValue(record.ContentId, out var sentence) =>
                    Map(sentence.Level, LanguageSkill.Writing, ExerciseType.GuidedWriting),
                ContentType.Passage when passagesById.TryGetValue(record.ContentId, out var passage) =>
                    Map(passage.Level, LanguageSkill.Reading, ExerciseType.ReadingComprehension),
                ContentType.Grammar when grammarById.TryGetValue(record.ContentId, out var grammarTask) =>
                    Map(grammarTask.Level, LanguageSkill.Grammar, ExerciseType.GrammarTransformation),
                _ => null
            };
            if (mapped is null)
            {
                continue;
            }

            var (level, skill, exerciseType, objectiveId) = mapped.Value;
            var timestamp = record.LastAttemptUtc ?? DateTimeOffset.UtcNow;
            var mode = record.ContentType == ContentType.AssessmentWord ? AssessmentMode.Diagnostic : AssessmentMode.Practice;
            for (var index = 0; index < Math.Min(record.AttemptCount, 10); index++)
            {
                yield return new LearningAttempt(
                    level,
                    skill,
                    exerciseType,
                    record.Accuracy,
                    timestamp.AddSeconds(-index),
                    mode,
                    objectiveId: objectiveId);
            }
        }
    }

    private (GermanLevel Level, LanguageSkill Skill, ExerciseType ExerciseType, string ObjectiveId)? Map(
        string levelText,
        LanguageSkill skill,
        ExerciseType preferredType)
    {
        if (!GermanLevelExtensions.TryParse(levelText, out var level))
        {
            return null;
        }

        var definition = _definition.Levels.Single(item => item.Level == level);
        var objective = definition.Objectives.FirstOrDefault(item => item.Skill == skill && item.ExerciseType == preferredType)
            ?? definition.Objectives.FirstOrDefault(item => item.Skill == skill);
        return objective is null ? null : (level, skill, objective.ExerciseType, objective.Id);
    }

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
}
