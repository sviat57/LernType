using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;
using WortBruecke.Infrastructure.Content;

namespace WortBruecke.Tests.Learning;

public sealed class ExamPolicyServiceTests
{
    private readonly ExamPolicyService _service = new();
    private readonly DateTimeOffset _now = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GoetheModule_ReadinessUsesSelectedModuleAndIndependentThreshold()
    {
        var exam = await Exam("goethe-b1");
        var events = TwoMocks(exam, GermanLevel.B1, 0.72, ["reading"]);

        var result = _service.Evaluate(exam, new ExamReadinessTarget(GermanLevel.B1, "reading"), events);

        Assert.True(result.PolicySatisfied);
        Assert.True(result.IsReady);
        Assert.Equal(2, result.BufferedPassingMockCount);
        Assert.Single(result.Modules);
    }

    [Fact]
    public async Task Telc_RequiresWrittenAndOralComponentsSeparately()
    {
        var exam = await Exam("telc-b2-general");
        var strongWrittenWeakOral = TwoMocks(exam, GermanLevel.B2, 0.75)
            .Select(item => item.ModuleId == "speaking" ? WithScore(item, 0.40) : item)
            .ToArray();

        var result = _service.Evaluate(exam, new ExamReadinessTarget(GermanLevel.B2), strongWrittenWeakOral);

        Assert.False(result.PolicySatisfied);
        Assert.False(result.IsReady);
        Assert.Equal(0.40, result.Modules.Single(item => item.ModuleId == "speaking").Score, precision: 6);
    }

    [Fact]
    public async Task TestDaf_ReportsPerSkillBandForExplicitTarget()
    {
        var exam = await Exam("testdaf-digital");
        var tdn5 = TwoMocks(exam, GermanLevel.C1, 0.82, ["reading"]);

        var result = _service.Evaluate(
            exam,
            new ExamReadinessTarget(GermanLevel.C1, "reading", "TDN4"),
            tdn5);

        Assert.Equal("TDN5", Assert.Single(result.Modules).Band);
        Assert.True(result.IsReady);
        Assert.False(exam.Scoring.UniversalPass);
    }

    [Fact]
    public async Task Dtz_A2AndB1UseDifferentProfilesAndB1RequiresSpeaking()
    {
        var exam = await Exam("dtz-a2-b1");
        var middle = TwoMocks(exam, GermanLevel.B1, 0.50);

        var b1 = _service.Evaluate(exam, new ExamReadinessTarget(GermanLevel.B1), middle);
        var a2Events = middle.Select(item => WithLevel(item, GermanLevel.A2)).ToArray();
        var a2 = _service.Evaluate(exam, new ExamReadinessTarget(GermanLevel.A2), a2Events);

        Assert.False(b1.PolicySatisfied);
        Assert.True(a2.PolicySatisfied);
    }

    [Fact]
    public async Task EvidenceForAnotherExam_IsNeverCredited()
    {
        var exam = await Exam("goethe-b2");
        var evidence = TwoMocks(exam, GermanLevel.B2, 1, ["reading"])
            .Select(item => WithExam(item, "telc-b2-general"));

        var result = _service.Evaluate(exam, new ExamReadinessTarget(GermanLevel.B2, "reading"), evidence);

        Assert.False(result.PolicySatisfied);
        Assert.Equal(0, Assert.Single(result.Modules).EvidenceCount);
    }

    private IEnumerable<AttemptEvent> TwoMocks(
        ExamBlueprint exam,
        GermanLevel level,
        double score,
        IReadOnlyCollection<string>? moduleIds = null)
    {
        var selected = moduleIds is null
            ? exam.Segments
            : exam.Segments.Where(item => moduleIds.Contains(item.Id)).ToArray();
        for (var mock = 0; mock < 2; mock++)
        {
            var session = Guid.NewGuid();
            foreach (var segment in selected)
            {
                yield return new AttemptEvent(
                    Guid.NewGuid(),
                    $"exam.{exam.Id}.{segment.Id}.mock-{mock}",
                    1,
                    level,
                    Skill(segment),
                    ExerciseType.ExamModuleSimulation,
                    AttemptDirection.NotApplicable,
                    score,
                    AssessmentMode.MockExam,
                    _now.AddDays(mock).AddMinutes(-1),
                    _now.AddDays(mock),
                    session,
                    $"{exam.Id}-rubric-v1",
                    EvidenceQuality.CalibratedAssessment,
                    wasTimed: true,
                    examId: exam.Id,
                    moduleId: segment.Id);
            }
        }
    }

    private static LanguageSkill Skill(ExamBlueprintSegment segment) => segment.Skills.FirstOrDefault() switch
    {
        "listening" => LanguageSkill.Listening,
        "writing" => LanguageSkill.Writing,
        "speaking" => LanguageSkill.Speaking,
        "mediation" => LanguageSkill.Mediation,
        "language-elements" => LanguageSkill.Grammar,
        _ => LanguageSkill.Reading
    };

    private static AttemptEvent WithScore(AttemptEvent item, double score) => Copy(item, score: score);
    private static AttemptEvent WithLevel(AttemptEvent item, GermanLevel level) => Copy(item, level: level);
    private static AttemptEvent WithExam(AttemptEvent item, string examId) => Copy(item, examId: examId);

    private static AttemptEvent Copy(
        AttemptEvent item,
        double? score = null,
        GermanLevel? level = null,
        string? examId = null) => new(
        item.EventId,
        item.ContentKey,
        item.ContentRevision,
        level ?? item.Level,
        item.Skill,
        item.ExerciseFamily,
        item.Direction,
        score ?? item.Score,
        item.Mode,
        item.StartedAtUtc,
        item.CompletedAtUtc,
        item.SessionId,
        item.RubricVersion,
        item.EvidenceQuality,
        item.ObjectiveId,
        item.WasTimed,
        examId ?? item.ExamId,
        item.ModuleId,
        item.SchedulerVersion);

    private static async Task<ExamBlueprint> Exam(string id)
    {
        var root = FindSolutionRoot();
        var catalog = await new JsonExamBlueprintRepository(Path.Combine(root, "src", "WortBruecke.App", "Content")).LoadAsync();
        return catalog.Exams.Single(item => item.Id == id);
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LernType.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
