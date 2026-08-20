namespace WortBruecke.Core.Learning;

public sealed class LearningObjective
{
    public LearningObjective(
        string id,
        GermanLevel level,
        LanguageSkill skill,
        ExerciseType exerciseType,
        string title,
        string descriptor,
        int minimumAttempts = 3,
        double masteryThreshold = 0.75,
        bool isRequired = true)
    {
        Id = RequireText(id, nameof(id));
        Level = EnsureDefined(level, nameof(level));
        Skill = EnsureDefined(skill, nameof(skill));
        ExerciseType = EnsureDefined(exerciseType, nameof(exerciseType));
        Title = RequireText(title, nameof(title));
        Descriptor = RequireText(descriptor, nameof(descriptor));
        if (minimumAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumAttempts), minimumAttempts, "Minimum attempts must be positive.");
        }
        if (masteryThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(masteryThreshold), masteryThreshold, "Mastery threshold must be between 0 and 1.");
        }

        MinimumAttempts = minimumAttempts;
        MasteryThreshold = masteryThreshold;
        IsRequired = isRequired;
    }

    public string Id { get; }
    public GermanLevel Level { get; }
    public LanguageSkill Skill { get; }
    public ExerciseType ExerciseType { get; }
    public string Title { get; }
    public string Descriptor { get; }
    public int MinimumAttempts { get; }
    public double MasteryThreshold { get; }
    public bool IsRequired { get; }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }
        return value.Trim();
    }

    private static T EnsureDefined<T>(T value, string parameterName) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Unknown enum value.");
        }
        return value;
    }
}

public sealed class LevelDefinition
{
    public LevelDefinition(
        GermanLevel level,
        string title,
        string outcome,
        IEnumerable<LearningObjective> objectives)
    {
        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown German learning level.");
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("A level title is required.", nameof(title));
        }
        if (string.IsNullOrWhiteSpace(outcome))
        {
            throw new ArgumentException("A level outcome is required.", nameof(outcome));
        }
        ArgumentNullException.ThrowIfNull(objectives);

        var materialized = objectives.ToArray();
        if (materialized.Length == 0)
        {
            throw new ArgumentException("A level must contain at least one objective.", nameof(objectives));
        }
        if (materialized.Any(objective => objective.Level != level))
        {
            throw new ArgumentException("Every objective must belong to the containing level.", nameof(objectives));
        }
        if (materialized.Select(objective => objective.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != materialized.Length)
        {
            throw new ArgumentException("Objective identifiers must be unique inside a level.", nameof(objectives));
        }
        if (materialized.All(objective => !objective.IsRequired))
        {
            throw new ArgumentException("A level must contain at least one required objective.", nameof(objectives));
        }

        Level = level;
        Title = title.Trim();
        Outcome = outcome.Trim();
        Objectives = Array.AsReadOnly(materialized);
    }

    public GermanLevel Level { get; }
    public string Title { get; }
    public string Outcome { get; }
    public IReadOnlyList<LearningObjective> Objectives { get; }
}

public sealed class LearningPathDefinition
{
    public LearningPathDefinition(IEnumerable<LevelDefinition> levels, int recentAttemptWindow = 5)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (recentAttemptWindow <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(recentAttemptWindow), recentAttemptWindow, "Evidence window must be positive.");
        }

        var materialized = levels.OrderBy(level => level.Level).ToArray();
        var expectedLevels = Enum.GetValues<GermanLevel>();
        if (!materialized.Select(level => level.Level).SequenceEqual(expectedLevels))
        {
            throw new ArgumentException("A learning path must define every level exactly once from A0 through C2.", nameof(levels));
        }

        var objectiveIds = materialized.SelectMany(level => level.Objectives).Select(objective => objective.Id).ToArray();
        if (objectiveIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != objectiveIds.Length)
        {
            throw new ArgumentException("Objective identifiers must be unique across the complete learning path.", nameof(levels));
        }

        Levels = Array.AsReadOnly(materialized);
        RecentAttemptWindow = recentAttemptWindow;
    }

    public IReadOnlyList<LevelDefinition> Levels { get; }
    public int RecentAttemptWindow { get; }
}

public sealed class LearningAttempt
{
    public LearningAttempt(
        GermanLevel level,
        LanguageSkill skill,
        ExerciseType exerciseType,
        double score,
        DateTimeOffset completedAtUtc,
        AssessmentMode mode = AssessmentMode.Practice,
        bool wasTimed = false,
        string? objectiveId = null,
        Guid? sessionId = null)
    {
        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown German learning level.");
        }
        if (!Enum.IsDefined(skill))
        {
            throw new ArgumentOutOfRangeException(nameof(skill), skill, "Unknown language skill.");
        }
        if (!Enum.IsDefined(exerciseType))
        {
            throw new ArgumentOutOfRangeException(nameof(exerciseType), exerciseType, "Unknown exercise type.");
        }
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown assessment mode.");
        }
        if (score is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(score), score, "Score must be between 0 and 1.");
        }
        if (mode == AssessmentMode.MockExam && sessionId is null)
        {
            throw new ArgumentException("Mock-exam evidence must belong to a session.", nameof(sessionId));
        }

        Level = level;
        Skill = skill;
        ExerciseType = exerciseType;
        Score = score;
        CompletedAtUtc = completedAtUtc.ToUniversalTime();
        Mode = mode;
        WasTimed = wasTimed;
        ObjectiveId = string.IsNullOrWhiteSpace(objectiveId) ? null : objectiveId.Trim();
        SessionId = sessionId;
    }

    public GermanLevel Level { get; }
    public LanguageSkill Skill { get; }
    public ExerciseType ExerciseType { get; }
    public double Score { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public AssessmentMode Mode { get; }
    public bool WasTimed { get; }
    public string? ObjectiveId { get; }
    public Guid? SessionId { get; }
}

public sealed record ObjectiveProgress(
    LearningObjective Objective,
    int AttemptCount,
    double RecentScore,
    bool IsMastered);

public sealed record SkillProgress(
    LanguageSkill Skill,
    int RequiredObjectiveCount,
    int MasteredObjectiveCount,
    double Completion,
    double RecentScore);

public sealed record LevelProgress(
    LevelDefinition Definition,
    bool IsUnlocked,
    bool IsCompleted,
    bool IsSatisfiedByPlacement,
    int RequiredObjectiveCount,
    int MasteredRequiredObjectiveCount,
    double Completion,
    double RecentScore,
    IReadOnlyList<ObjectiveProgress> Objectives,
    IReadOnlyList<SkillProgress> Skills);

public sealed record LearningPathProgress(
    GermanLevel CurrentLevel,
    GermanLevel? HighestCompletedLevel,
    GermanLevel? NextLockedLevel,
    double OverallCompletion,
    IReadOnlyList<LevelProgress> Levels);
