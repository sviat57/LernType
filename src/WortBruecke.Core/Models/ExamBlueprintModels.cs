namespace WortBruecke.Core.Models;

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
    string ScoringSummary,
    IReadOnlyList<string> TrainingRequirements,
    IReadOnlyList<ExamSourceLink> Sources)
{
    public int TotalWorkingMinutes => Segments.Sum(segment => segment.DurationMinutes);
}

public sealed record ExamBlueprintSegment(
    string Id,
    IReadOnlyList<string> Skills,
    int Parts,
    int DurationMinutes,
    bool IsApproximate,
    IReadOnlyList<string> TaskFamilies);

public sealed record ExamSourceLink(string Title, string Url);
