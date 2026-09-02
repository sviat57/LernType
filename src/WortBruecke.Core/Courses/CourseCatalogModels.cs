using WortBruecke.Core.Learning;

namespace WortBruecke.Core.Courses;

/// <summary>Describes the versioned, offline course catalog distributed with the application.</summary>
public sealed record CourseCatalog(
    int Revision,
    CourseTrackDefinition Track);

/// <summary>Describes one ordered learning track and every course offered by it.</summary>
public sealed record CourseTrackDefinition(
    string Id,
    string Title,
    IReadOnlyList<CourseDefinition> Courses);

/// <summary>Describes a level course, its units and its course-local final examination.</summary>
public sealed record CourseDefinition(
    string Id,
    int Order,
    GermanLevel Level,
    string Title,
    string Subtitle,
    string Outcome,
    CourseAvailability Availability,
    IReadOnlyList<CourseUnitDefinition> Units,
    CourseExamDefinition? Exam);

/// <summary>Describes an ordered thematic unit inside a course.</summary>
public sealed record CourseUnitDefinition(
    string Id,
    int Order,
    string Title,
    string Outcome,
    IReadOnlyList<CourseLessonDefinition> Lessons);

/// <summary>Describes an ordered lesson and its fixed instructional sequence.</summary>
public sealed record CourseLessonDefinition(
    string Id,
    int Order,
    string Title,
    string Outcome,
    int EstimatedMinutes,
    IReadOnlyList<CourseStepDefinition> Steps);

/// <summary>Describes one instructional or assessed step of a course lesson.</summary>
public sealed record CourseStepDefinition(
    string Id,
    int Order,
    CourseStepKind Kind,
    string Title,
    string Instruction,
    string? RussianText,
    string? GermanText,
    string? Hint,
    CourseTableDefinition? Table,
    CourseTaskDefinition? Task);

/// <summary>Describes one machine-checked or learner-reviewed task.</summary>
public sealed record CourseTaskDefinition(
    string Id,
    CourseTaskKind Kind,
    string Prompt,
    string? Answer,
    IReadOnlyList<string> AcceptedAnswers,
    IReadOnlyList<string> Options,
    string? ModelAnswer,
    LanguageSkill Skill,
    ExerciseType ExerciseType);

/// <summary>Describes a final examination embedded in a published course.</summary>
public sealed record CourseExamDefinition(
    string Id,
    string Title,
    int PassPercent,
    IReadOnlyList<CourseExamQuestionDefinition> Questions);

/// <summary>Describes one ordered question in a course examination.</summary>
public sealed record CourseExamQuestionDefinition(
    string Id,
    int Order,
    string Title,
    CourseTaskKind Kind,
    string Prompt,
    string? Answer,
    IReadOnlyList<string> AcceptedAnswers,
    IReadOnlyList<string> Options,
    string? ModelAnswer,
    LanguageSkill Skill,
    ExerciseType ExerciseType,
    string? AudioText = null);

/// <summary>Describes a rectangular table rendered as part of a lesson explanation.</summary>
public sealed record CourseTableDefinition(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>Indicates whether a course contains launchable offline material.</summary>
public enum CourseAvailability
{
    /// <summary>The course is complete enough to be launched.</summary>
    Published,

    /// <summary>The course is represented in the path but has no launchable material yet.</summary>
    Planned
}

/// <summary>Defines the fixed six-part instructional rhythm of every published lesson.</summary>
public enum CourseStepKind
{
    /// <summary>A short explanation that introduces the lesson's first concept.</summary>
    Briefing,

    /// <summary>A guided written application of the introduced concept.</summary>
    Writing,

    /// <summary>A reading task that uses the concept in context.</summary>
    Reading,

    /// <summary>A listening or spoken-production task.</summary>
    ListeningSpeaking,

    /// <summary>A second rule, contrast or exception explained after practice.</summary>
    Rule,

    /// <summary>A checked synthesis task that closes the lesson.</summary>
    Checkpoint
}

/// <summary>Defines the interaction contract used to evaluate a course task.</summary>
public enum CourseTaskKind
{
    /// <summary>A short free-text answer.</summary>
    ShortAnswer,

    /// <summary>A free-text answer that completes a gap.</summary>
    GapFill,

    /// <summary>One answer selected from a finite list.</summary>
    SingleChoice,

    /// <summary>A spoken answer recorded and compared with a model by the learner.</summary>
    SelfRecordedSpeech
}
