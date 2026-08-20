namespace WortBruecke.Core.Learning;

public sealed class ExamSectionDefinition
{
    public ExamSectionDefinition(
        string id,
        string title,
        IEnumerable<LanguageSkill> skills,
        IEnumerable<ExerciseType> exerciseTypes,
        double weight,
        double minimumScore,
        int minimumEvidenceCount,
        int minimumTimedEvidenceCount = 0,
        IEnumerable<AssessmentMode>? acceptedModes = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("A section identifier is required.", nameof(id));
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("A section title is required.", nameof(title));
        }
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(exerciseTypes);
        if (weight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "Section weight must be positive.");
        }
        if (minimumScore is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumScore), minimumScore, "Minimum score must be between 0 and 1.");
        }
        if (minimumEvidenceCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumEvidenceCount), minimumEvidenceCount, "Evidence count must be positive.");
        }
        if (minimumTimedEvidenceCount < 0 || minimumTimedEvidenceCount > minimumEvidenceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumTimedEvidenceCount), minimumTimedEvidenceCount, "Timed evidence must fit inside the required evidence count.");
        }

        var materializedSkills = skills.Distinct().ToArray();
        var materializedTypes = exerciseTypes.Distinct().ToArray();
        var materializedModes = (acceptedModes ?? [AssessmentMode.Checkpoint, AssessmentMode.MockExam]).Distinct().ToArray();
        if (materializedSkills.Length == 0 || materializedSkills.Any(skill => !Enum.IsDefined(skill)))
        {
            throw new ArgumentException("At least one known skill is required.", nameof(skills));
        }
        if (materializedTypes.Length == 0 || materializedTypes.Any(type => !Enum.IsDefined(type)))
        {
            throw new ArgumentException("At least one known exercise type is required.", nameof(exerciseTypes));
        }
        if (materializedModes.Length == 0 || materializedModes.Any(mode => !Enum.IsDefined(mode)))
        {
            throw new ArgumentException("At least one known assessment mode is required.", nameof(acceptedModes));
        }

        Id = id.Trim();
        Title = title.Trim();
        Skills = Array.AsReadOnly(materializedSkills);
        ExerciseTypes = Array.AsReadOnly(materializedTypes);
        Weight = weight;
        MinimumScore = minimumScore;
        MinimumEvidenceCount = minimumEvidenceCount;
        MinimumTimedEvidenceCount = minimumTimedEvidenceCount;
        AcceptedModes = Array.AsReadOnly(materializedModes);
    }

    public string Id { get; }
    public string Title { get; }
    public IReadOnlyList<LanguageSkill> Skills { get; }
    public IReadOnlyList<ExerciseType> ExerciseTypes { get; }
    public double Weight { get; }
    public double MinimumScore { get; }
    public int MinimumEvidenceCount { get; }
    public int MinimumTimedEvidenceCount { get; }
    public IReadOnlyList<AssessmentMode> AcceptedModes { get; }
}

public sealed class ExamDefinition
{
    public ExamDefinition(
        string id,
        string title,
        GermanLevel level,
        IEnumerable<ExamSectionDefinition> sections,
        double overallMinimumScore = 0.6,
        int minimumCompleteMockExams = 1,
        int recentEvidenceWindowPerSection = 5)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("An exam identifier is required.", nameof(id));
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("An exam title is required.", nameof(title));
        }
        if (!level.IsCefrLevel())
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "Official exam readiness starts at A1; A0 is a pre-CEFR stage.");
        }
        ArgumentNullException.ThrowIfNull(sections);
        if (overallMinimumScore is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(overallMinimumScore), overallMinimumScore, "Overall score must be between 0 and 1.");
        }
        if (minimumCompleteMockExams < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumCompleteMockExams), minimumCompleteMockExams, "Mock-exam count cannot be negative.");
        }
        if (recentEvidenceWindowPerSection <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(recentEvidenceWindowPerSection), recentEvidenceWindowPerSection, "Evidence window must be positive.");
        }

        var materialized = sections.ToArray();
        if (materialized.Length == 0)
        {
            throw new ArgumentException("An exam must contain at least one section.", nameof(sections));
        }
        if (materialized.Select(section => section.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != materialized.Length)
        {
            throw new ArgumentException("Exam section identifiers must be unique.", nameof(sections));
        }
        if (materialized.Any(section => section.MinimumEvidenceCount > recentEvidenceWindowPerSection))
        {
            throw new ArgumentException(
                "The recent evidence window must be large enough for every section's minimum evidence count.",
                nameof(recentEvidenceWindowPerSection));
        }

        Id = id.Trim();
        Title = title.Trim();
        Level = level;
        Sections = Array.AsReadOnly(materialized);
        OverallMinimumScore = overallMinimumScore;
        MinimumCompleteMockExams = minimumCompleteMockExams;
        RecentEvidenceWindowPerSection = recentEvidenceWindowPerSection;
    }

    public string Id { get; }
    public string Title { get; }
    public GermanLevel Level { get; }
    public IReadOnlyList<ExamSectionDefinition> Sections { get; }
    public double OverallMinimumScore { get; }
    public int MinimumCompleteMockExams { get; }
    public int RecentEvidenceWindowPerSection { get; }
}

public sealed record ExamSectionReadiness(
    ExamSectionDefinition Definition,
    int EvidenceCount,
    int TimedEvidenceCount,
    double RecentScore,
    bool HasEnoughEvidence,
    bool MeetsScore,
    bool MeetsTiming,
    bool IsReady);

public sealed record ExamReadiness(
    ExamDefinition Exam,
    double OverallScore,
    int CompleteMockExamCount,
    bool MeetsOverallScore,
    bool MeetsMockExamRequirement,
    bool IsReady,
    IReadOnlyList<ExamSectionReadiness> Sections,
    IReadOnlyList<string> MissingRequirements);
