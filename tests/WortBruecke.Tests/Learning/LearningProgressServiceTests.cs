using WortBruecke.Core.Learning;

namespace WortBruecke.Tests.Learning;

public sealed class LearningProgressServiceTests
{
    private readonly LearningProgressService _service = new();
    private readonly DateTimeOffset _now = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EmptyEvidence_UnlocksOnlyA0()
    {
        var progress = _service.EvaluatePath(GermanCurriculum.CreateDefault(), []);

        Assert.Equal(GermanLevel.A0, progress.CurrentLevel);
        Assert.Null(progress.HighestCompletedLevel);
        Assert.Equal(GermanLevel.A1, progress.NextLockedLevel);
        Assert.True(progress.Levels.Single(level => level.Definition.Level == GermanLevel.A0).IsUnlocked);
        Assert.All(progress.Levels.Where(level => level.Definition.Level != GermanLevel.A0), level => Assert.False(level.IsUnlocked));
        Assert.Equal(0, progress.OverallCompletion);
    }

    [Fact]
    public void MasteringA0_UnlocksA1ButNotA2()
    {
        var path = GermanCurriculum.CreateDefault();
        var a0 = path.Levels.Single(level => level.Level == GermanLevel.A0);
        var progress = _service.EvaluatePath(path, Master(a0));

        Assert.True(Level(progress, GermanLevel.A0).IsCompleted);
        Assert.True(Level(progress, GermanLevel.A1).IsUnlocked);
        Assert.False(Level(progress, GermanLevel.A1).IsCompleted);
        Assert.False(Level(progress, GermanLevel.A2).IsUnlocked);
        Assert.Equal(GermanLevel.A1, progress.CurrentLevel);
        Assert.Equal(GermanLevel.A0, progress.HighestCompletedLevel);
        Assert.Equal(GermanLevel.A2, progress.NextLockedLevel);
    }

    [Fact]
    public void FutureEvidence_DoesNotBypassAnIncompletePrerequisite()
    {
        var path = GermanCurriculum.CreateDefault();
        var a1 = path.Levels.Single(level => level.Level == GermanLevel.A1);
        var progress = _service.EvaluatePath(path, Master(a1));

        Assert.False(Level(progress, GermanLevel.A1).IsUnlocked);
        Assert.False(Level(progress, GermanLevel.A1).IsCompleted);
        Assert.False(Level(progress, GermanLevel.A2).IsUnlocked);
        Assert.Equal(GermanLevel.A0, progress.CurrentLevel);
    }

    [Fact]
    public void Placement_UnlocksPlacedLevelAndSatisfiesLowerStages()
    {
        var progress = _service.EvaluatePath(
            GermanCurriculum.CreateDefault(),
            [],
            placementLevel: GermanLevel.B1);

        Assert.All(
            progress.Levels.Where(level => level.Definition.Level < GermanLevel.B1),
            level =>
            {
                Assert.True(level.IsUnlocked);
                Assert.True(level.IsCompleted);
                Assert.True(level.IsSatisfiedByPlacement);
            });
        Assert.True(Level(progress, GermanLevel.B1).IsUnlocked);
        Assert.False(Level(progress, GermanLevel.B1).IsCompleted);
        Assert.False(Level(progress, GermanLevel.B2).IsUnlocked);
        Assert.Equal(GermanLevel.B1, progress.CurrentLevel);
    }

    [Fact]
    public void Mastery_UsesRecentWindowInsteadOfLifetimeAverage()
    {
        var path = GermanCurriculum.CreateDefault();
        var objective = path.Levels[0].Objectives[0];
        var evidence = Attempts(objective, [1, 1, 1, 0, 0, 0, 0, 0]);

        var firstEvaluation = _service.EvaluatePath(path, evidence);
        var firstProgress = Objective(firstEvaluation, objective.Id);

        Assert.Equal(8, firstProgress.AttemptCount);
        Assert.Equal(0, firstProgress.RecentScore);
        Assert.False(firstProgress.IsMastered);

        evidence.AddRange(Attempts(objective, [1, 1, 1], dayOffset: 20));
        var secondEvaluation = _service.EvaluatePath(path, evidence);
        var secondProgress = Objective(secondEvaluation, objective.Id);

        Assert.Equal(0.6, secondProgress.RecentScore, precision: 10);
        Assert.False(secondProgress.IsMastered);

        evidence.AddRange(Attempts(objective, [1, 1], dayOffset: 30));
        var finalProgress = Objective(_service.EvaluatePath(path, evidence), objective.Id);

        Assert.Equal(1, finalProgress.RecentScore);
        Assert.True(finalProgress.IsMastered);
    }

    [Fact]
    public void EvidenceWithMismatchedDimensions_IsNotCreditedToObjective()
    {
        var path = GermanCurriculum.CreateDefault();
        var objective = path.Levels[0].Objectives[0];
        var attempts = Enumerable.Range(0, 3).Select(index => new LearningAttempt(
            objective.Level,
            LanguageSkill.Grammar,
            objective.ExerciseType,
            1,
            _now.AddMinutes(index),
            objectiveId: objective.Id));

        var result = Objective(_service.EvaluatePath(path, attempts), objective.Id);

        Assert.Equal(0, result.AttemptCount);
        Assert.False(result.IsMastered);
    }

    [Fact]
    public void ExamReadiness_RequiresRecentCheckpointEvidenceTimingAndCompleteMocks()
    {
        var exam = GermanCurriculum.CreateGenericFourSkillExam("b1-sample", "B1 sample", GermanLevel.B1);
        var attempts = new List<LearningAttempt>();

        for (var mock = 0; mock < 2; mock++)
        {
            var sessionId = Guid.NewGuid();
            foreach (var section in exam.Sections)
            {
                attempts.Add(ExamAttempt(exam, section, 0.8, AssessmentMode.MockExam, true, sessionId, mock));
            }
        }
        foreach (var section in exam.Sections)
        {
            attempts.Add(ExamAttempt(exam, section, 0.8, AssessmentMode.Checkpoint, false, null, 10));
        }

        var readiness = _service.EvaluateExamReadiness(exam, attempts);

        Assert.True(readiness.IsReady);
        Assert.Equal(0.8, readiness.OverallScore, precision: 10);
        Assert.Equal(2, readiness.CompleteMockExamCount);
        Assert.Empty(readiness.MissingRequirements);
        Assert.All(readiness.Sections, section =>
        {
            Assert.Equal(3, section.EvidenceCount);
            Assert.Equal(2, section.TimedEvidenceCount);
            Assert.True(section.IsReady);
        });
    }

    [Fact]
    public void ExamReadiness_DoesNotCountPracticeOrAnotherLevel()
    {
        var exam = GermanCurriculum.CreateGenericFourSkillExam("b2-sample", "B2 sample", GermanLevel.B2);
        var reading = exam.Sections.Single(section => section.Id == "reading");
        var attempts = new[]
        {
            ExamAttempt(exam, reading, 1, AssessmentMode.Practice, true, null, 0),
            new LearningAttempt(
                GermanLevel.C1,
                LanguageSkill.Reading,
                ExerciseType.ReadingComprehension,
                1,
                _now,
                AssessmentMode.Checkpoint,
                wasTimed: true)
        };

        var readiness = _service.EvaluateExamReadiness(exam, attempts);
        var readingResult = readiness.Sections.Single(section => section.Definition.Id == "reading");

        Assert.Equal(0, readingResult.EvidenceCount);
        Assert.False(readiness.IsReady);
        Assert.Contains(readiness.MissingRequirements, item => item.StartsWith("Чтение: evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void PartialMockSession_IsNotCountedAsCompleteExam()
    {
        var exam = GermanCurriculum.CreateGenericFourSkillExam("c1-sample", "C1 sample", GermanLevel.C1);
        var sessionId = Guid.NewGuid();
        var attempts = exam.Sections
            .Where(section => section.Id != "speaking")
            .Select(section => ExamAttempt(exam, section, 0.9, AssessmentMode.MockExam, true, sessionId, 0));

        var readiness = _service.EvaluateExamReadiness(exam, attempts);

        Assert.Equal(0, readiness.CompleteMockExamCount);
        Assert.False(readiness.MeetsMockExamRequirement);
        Assert.Contains("Complete mock exams 0/2.", readiness.MissingRequirements);
    }

    [Fact]
    public void MockExamEvidenceWithoutSession_IsRejected()
    {
        var error = Assert.Throws<ArgumentException>(() => new LearningAttempt(
            GermanLevel.B1,
            LanguageSkill.Reading,
            ExerciseType.ExamModuleSimulation,
            0.8,
            _now,
            AssessmentMode.MockExam));

        Assert.Equal("sessionId", error.ParamName);
    }

    private IEnumerable<LearningAttempt> Master(LevelDefinition level) =>
        level.Objectives.SelectMany(objective => Attempts(
            objective,
            Enumerable.Repeat(1d, objective.MinimumAttempts).ToArray()));

    private List<LearningAttempt> Attempts(LearningObjective objective, double[] scores, int dayOffset = 0) => scores
        .Select((score, index) => new LearningAttempt(
            objective.Level,
            objective.Skill,
            objective.ExerciseType,
            score,
            _now.AddDays(dayOffset).AddMinutes(index),
            objectiveId: objective.Id))
        .ToList();

    private LearningAttempt ExamAttempt(
        ExamDefinition exam,
        ExamSectionDefinition section,
        double score,
        AssessmentMode mode,
        bool timed,
        Guid? sessionId,
        int minuteOffset) => new(
            exam.Level,
            section.Skills[0],
            section.ExerciseTypes[0],
            score,
            _now.AddMinutes(minuteOffset),
            mode,
            timed,
            sessionId: sessionId);

    private static LevelProgress Level(LearningPathProgress progress, GermanLevel level) =>
        progress.Levels.Single(item => item.Definition.Level == level);

    private static ObjectiveProgress Objective(LearningPathProgress progress, string objectiveId) =>
        progress.Levels.SelectMany(level => level.Objectives).Single(item => item.Objective.Id == objectiveId);
}
