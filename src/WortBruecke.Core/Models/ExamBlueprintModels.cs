namespace WortBruecke.Core.Models;

public enum ExamScoringKind
{
    WholeExamThreshold,
    WholeExamWithComponentThresholds,
    IndependentModules,
    WrittenAndOralThresholds,
    BandPerSkill,
    DualLevelProfile
}

public sealed record ExamScoreBand(string Name, int MinimumPoints, int MaximumPoints);

public sealed record ExamScoringDefinition(
    ExamScoringKind Kind,
    double? OverallPassRatio = null,
    double? WrittenPassRatio = null,
    double? OralPassRatio = null,
    double? ModulePassRatio = null,
    int? SkillMaximumPoints = null,
    IReadOnlyList<ExamScoreBand>? Bands = null,
    bool UniversalPass = true,
    int? ReceptiveMaximumItems = null,
    int? ReceptiveA2MinimumCorrect = null,
    int? ReceptiveB1MinimumCorrect = null)
{
    public IReadOnlyList<ExamScoreBand> ScoreBands { get; } = Bands ?? [];
}

public sealed record ExamBlueprintCatalog(
    DateOnly LastVerified,
    string CoverageNote,
    string ReadinessDisclaimer,
    IReadOnlyList<ExamBlueprint> Exams);

public sealed record ExamBlueprint(
    string Id,
    string ProviderId,
    string ProviderName,
    string Name,
    IReadOnlyList<string> Levels,
    IReadOnlyList<ExamBlueprintSegment> Segments,
    string ScoringType,
    ExamScoringDefinition Scoring,
    string ScoringSummary,
    IReadOnlyList<string> TrainingRequirements,
    IReadOnlyList<ExamSourceLink> Sources,
    bool PartialExamAvailable,
    int BreakMinutes,
    string? NavigationPolicy,
    IReadOnlyList<string> Delivery)
{
    public int TotalWorkingMinutes => Segments.Sum(segment => segment.DurationMinutes);
    public int TotalSessionMinutes => TotalWorkingMinutes + BreakMinutes + Segments.Sum(segment => segment.PreparationMinutes);
}

public sealed record ExamBlueprintSegment(
    string Id,
    IReadOnlyList<string> Skills,
    int Parts,
    int DurationMinutes,
    bool IsApproximate,
    IReadOnlyList<string> TaskFamilies,
    int? Items = null,
    int PreparationMinutes = 0,
    int BreakMinutes = 0,
    bool IsPairFormat = false,
    bool IsGroupFormat = false,
    bool IsIndividualFormat = false,
    bool IsRecordedComputerFormat = false,
    bool IsDurationPerParticipant = false);

public sealed record ExamSourceLink(string Title, string Url);
