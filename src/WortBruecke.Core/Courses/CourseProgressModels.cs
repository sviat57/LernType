namespace WortBruecke.Core.Courses;

/// <summary>Represents the learner-visible completion state of a course node.</summary>
public enum CourseNodeStatus
{
    /// <summary>The node has not been attempted.</summary>
    NotStarted,

    /// <summary>The node has attempts but has not been completed.</summary>
    InProgress,

    /// <summary>The node has satisfied its completion rule.</summary>
    Completed,

    /// <summary>The completed examination node has met its passing threshold.</summary>
    Passed
}

/// <summary>Stores the durable aggregate for one stable node identifier in a course.</summary>
public sealed record CourseNodeProgress(
    string CourseId,
    string NodeId,
    CourseNodeStatus Status,
    double BestScore,
    int AttemptCount,
    DateTimeOffset UpdatedAtUtc);

/// <summary>Stores the last exact lesson step and the in-flight scoring snapshot within a course.</summary>
public sealed record CourseResumeState
{
    /// <summary>Creates an empty in-flight scoring snapshot for a resume location.</summary>
    public CourseResumeState(
        string courseId,
        string unitId,
        string lessonId,
        string stepId,
        DateTimeOffset updatedAtUtc)
        : this(
            courseId,
            unitId,
            lessonId,
            stepId,
            updatedAtUtc,
            new Dictionary<string, double>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal))
    {
    }

    /// <summary>Creates a resume location with a defensive copy of all in-flight scoring evidence.</summary>
    public CourseResumeState(
        string courseId,
        string unitId,
        string lessonId,
        string stepId,
        DateTimeOffset updatedAtUtc,
        IReadOnlyDictionary<string, double> taskScores,
        IReadOnlySet<string> selfReportedTaskKeys)
    {
        CourseId = courseId;
        UnitId = unitId;
        LessonId = lessonId;
        StepId = stepId;
        UpdatedAtUtc = updatedAtUtc;
        ArgumentNullException.ThrowIfNull(taskScores);
        ArgumentNullException.ThrowIfNull(selfReportedTaskKeys);
        TaskScores = new Dictionary<string, double>(taskScores, StringComparer.Ordinal);
        SelfReportedTaskKeys = new HashSet<string>(selfReportedTaskKeys, StringComparer.Ordinal);
    }

    public string CourseId { get; }
    public string UnitId { get; }
    public string LessonId { get; }
    public string StepId { get; }
    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>Latest score for every task already answered in the current flow.</summary>
    public IReadOnlyDictionary<string, double> TaskScores { get; }

    /// <summary>Task keys whose score is self-reported evidence and must not enter deterministic averages.</summary>
    public IReadOnlySet<string> SelfReportedTaskKeys { get; }
}

/// <summary>Requests opening the overview of one stable course.</summary>
public sealed record CourseLaunchRequest(string CourseId);

/// <summary>Requests opening one stable lesson, optionally at a particular step.</summary>
public sealed record CourseLessonLaunch(
    string CourseId,
    string UnitId,
    string LessonId,
    string? StepId = null);

/// <summary>Requests opening the examination embedded in one stable course.</summary>
public sealed record CourseExamLaunch(
    string CourseId,
    string ExamId);
