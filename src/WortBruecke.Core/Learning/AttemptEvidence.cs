namespace WortBruecke.Core.Learning;

/// <summary>
/// Direction of the observable learner action. It is deliberately independent from the UI
/// language pair so historic evidence keeps its meaning after localization changes.
/// </summary>
public enum AttemptDirection
{
    NotApplicable,
    RussianToGerman,
    GermanToRussian,
    Bidirectional,
    GermanComprehension,
    GermanProduction
}

/// <summary>
/// Describes how trustworthy an attempt is for progression decisions.
/// Values are ordered; a higher value may satisfy a requirement for a lower value.
/// </summary>
public enum EvidenceQuality
{
    HistoricalAggregate = 0,
    SelfReported = 1,
    Heuristic = 2,
    Deterministic = 3,
    HumanReviewed = 4,
    CalibratedAssessment = 5
}

/// <summary>
/// The canonical, append-only learning evidence event. It carries stable content identity and
/// evaluator/scheduler versions so projections can be rebuilt deterministically.
/// </summary>
public sealed record AttemptEvent
{
    public AttemptEvent(
        Guid eventId,
        string contentKey,
        int contentRevision,
        GermanLevel level,
        LanguageSkill skill,
        ExerciseType exerciseFamily,
        AttemptDirection direction,
        double score,
        AssessmentMode mode,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        Guid sessionId,
        string rubricVersion,
        EvidenceQuality evidenceQuality,
        string? objectiveId = null,
        bool wasTimed = false,
        string? examId = null,
        string? moduleId = null,
        string schedulerVersion = "fsrs-like-v1")
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("An event identifier is required.", nameof(eventId));
        }
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A session identifier is required.", nameof(sessionId));
        }
        ContentKey = RequireKey(contentKey, nameof(contentKey));
        if (contentRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentRevision), contentRevision, "Content revision must be positive.");
        }
        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown German learning level.");
        }
        if (!Enum.IsDefined(skill))
        {
            throw new ArgumentOutOfRangeException(nameof(skill), skill, "Unknown language skill.");
        }
        if (!Enum.IsDefined(exerciseFamily))
        {
            throw new ArgumentOutOfRangeException(nameof(exerciseFamily), exerciseFamily, "Unknown exercise family.");
        }
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown attempt direction.");
        }
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown assessment mode.");
        }
        if (!Enum.IsDefined(evidenceQuality))
        {
            throw new ArgumentOutOfRangeException(nameof(evidenceQuality), evidenceQuality, "Unknown evidence quality.");
        }
        if (score is < 0 or > 1 || double.IsNaN(score))
        {
            throw new ArgumentOutOfRangeException(nameof(score), score, "Score must be between 0 and 1.");
        }

        var started = startedAtUtc.ToUniversalTime();
        var completed = completedAtUtc.ToUniversalTime();
        if (completed < started)
        {
            throw new ArgumentException("Completion cannot precede the start of an attempt.", nameof(completedAtUtc));
        }
        if (mode == AssessmentMode.MockExam && string.IsNullOrWhiteSpace(examId))
        {
            throw new ArgumentException("Mock-exam evidence must identify its exam.", nameof(examId));
        }

        EventId = eventId;
        ContentRevision = contentRevision;
        Level = level;
        Skill = skill;
        ExerciseFamily = exerciseFamily;
        Direction = direction;
        Score = score;
        Mode = mode;
        StartedAtUtc = started;
        CompletedAtUtc = completed;
        SessionId = sessionId;
        RubricVersion = RequireKey(rubricVersion, nameof(rubricVersion));
        EvidenceQuality = evidenceQuality;
        ObjectiveId = NormalizeOptionalKey(objectiveId);
        WasTimed = wasTimed;
        ExamId = NormalizeOptionalKey(examId);
        ModuleId = NormalizeOptionalKey(moduleId);
        SchedulerVersion = RequireKey(schedulerVersion, nameof(schedulerVersion));
    }

    public Guid EventId { get; }
    public string ContentKey { get; }
    public int ContentRevision { get; }
    public string? ObjectiveId { get; }
    public GermanLevel Level { get; }
    public LanguageSkill Skill { get; }
    public ExerciseType ExerciseFamily { get; }
    public AttemptDirection Direction { get; }
    public double Score { get; }
    public AssessmentMode Mode { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public TimeSpan Duration => CompletedAtUtc - StartedAtUtc;
    public Guid SessionId { get; }
    public string RubricVersion { get; }
    public EvidenceQuality EvidenceQuality { get; }
    public bool WasTimed { get; }
    public string? ExamId { get; }
    public string? ModuleId { get; }
    public string SchedulerVersion { get; }

    private static string RequireKey(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }
        return value.Trim();
    }

    private static string? NormalizeOptionalKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record AttemptQuery(
    GermanLevel? Level = null,
    string? ObjectiveId = null,
    string? ExamId = null,
    string? ModuleId = null,
    Guid? SessionId = null,
    DateTimeOffset? CompletedSinceUtc = null);
