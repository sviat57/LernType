using WortBruecke.Core.Learning;

namespace WortBruecke.Tests.Learning;

public sealed class CanonicalEvidenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PublishedA0Objectives_AreReachableAndUnlockA1()
    {
        var definition = GermanCurriculum.CreateDefault();
        var a0 = definition.Levels.Single(item => item.Level == GermanLevel.A0);
        var events = a0.Objectives
            .Where(item => item.ContributesToProgress)
            .SelectMany(Master)
            .ToArray();

        var progress = new LearningProgressService().EvaluatePathFromEvents(definition, events);
        var a0Progress = progress.Levels.Single(item => item.Definition.Level == GermanLevel.A0);
        var a1Progress = progress.Levels.Single(item => item.Definition.Level == GermanLevel.A1);

        Assert.Equal(4, a0Progress.RequiredObjectiveCount);
        Assert.True(a0Progress.IsCompleted);
        Assert.True(a1Progress.IsUnlocked);
        Assert.Equal(GermanLevel.A1, progress.CurrentLevel);
    }

    [Fact]
    public void Mastery_RequiresDistinctItemsAndTwoCalendarDays()
    {
        var definition = GermanCurriculum.CreateDefault();
        var objective = definition.Levels[0].Objectives.Single(item => item.Skill == LanguageSkill.Vocabulary);
        var sameItem = Enumerable.Range(0, objective.MinimumAttempts)
            .Select(index => Event(objective, "same", Start.AddMinutes(index)))
            .ToArray();
        var oneDay = Enumerable.Range(0, objective.MinimumAttempts)
            .Select(index => Event(objective, $"item-{index}", Start.AddMinutes(index)))
            .ToArray();
        var valid = Enumerable.Range(0, objective.MinimumAttempts)
            .Select(index => Event(objective, $"item-{index}", Start.AddDays(index == 0 ? 0 : 1).AddMinutes(index)))
            .ToArray();

        Assert.False(Objective(definition, sameItem, objective).IsMastered);
        Assert.False(Objective(definition, oneDay, objective).IsMastered);
        var mastered = Objective(definition, valid, objective);
        Assert.True(mastered.IsMastered);
        Assert.Equal(3, mastered.DistinctItemCount);
        Assert.Equal(2, mastered.DistinctDayCount);
    }

    [Fact]
    public void HeuristicAndHistoricalEvidence_DoNotMasterPublishedObjectives()
    {
        var definition = GermanCurriculum.CreateDefault();
        var objective = definition.Levels[0].Objectives.Single(item => item.Skill == LanguageSkill.Vocabulary);
        var events = Master(objective)
            .Select(item => new AttemptEvent(
                item.EventId,
                item.ContentKey,
                item.ContentRevision,
                item.Level,
                item.Skill,
                item.ExerciseFamily,
                item.Direction,
                item.Score,
                item.Mode,
                item.StartedAtUtc,
                item.CompletedAtUtc,
                item.SessionId,
                item.RubricVersion,
                EvidenceQuality.Heuristic,
                item.ObjectiveId))
            .ToArray();

        var progress = Objective(definition, events, objective);

        Assert.Equal(0, progress.AttemptCount);
        Assert.False(progress.IsMastered);
    }

    [Fact]
    public void PracticeOnlyObjectives_AreVisibleButDoNotCountAsFailure()
    {
        var definition = GermanCurriculum.CreateDefault();
        var b2 = definition.Levels.Single(item => item.Level == GermanLevel.B2);

        Assert.Equal(ObjectiveAvailability.PracticeOnly, b2.Objectives.Single(item => item.Skill == LanguageSkill.Listening).Availability);
        Assert.Equal(ObjectiveAvailability.PracticeOnly, b2.Objectives.Single(item => item.Skill == LanguageSkill.Speaking).Availability);
        Assert.Equal(ObjectiveAvailability.PracticeOnly, b2.Objectives.Single(item => item.Skill == LanguageSkill.Grammar).Availability);
        Assert.Equal(3, b2.Objectives.Count(item => item.ContributesToProgress));
    }

    [Fact]
    public void PlacementHook_SatisfiesOnlyStagesBelowRecommendation()
    {
        var progress = new LearningProgressService().EvaluatePathFromEvents(
            GermanCurriculum.CreateDefault(),
            [],
            GermanLevel.B1);

        Assert.All(progress.Levels.Where(item => item.Definition.Level < GermanLevel.B1), item => Assert.True(item.IsSatisfiedByPlacement));
        Assert.True(progress.Levels.Single(item => item.Definition.Level == GermanLevel.B1).IsUnlocked);
        Assert.False(progress.Levels.Single(item => item.Definition.Level == GermanLevel.B1).IsCompleted);
    }

    [Fact]
    public void Scheduler_IsVersionedDeterministicAndPenalizesLapses()
    {
        var scheduler = new DeterministicSpacedRepetitionScheduler();
        var objective = GermanCurriculum.CreateDefault().Levels[0].Objectives[0];
        var attempt = Event(objective, "scheduler-item", Start);

        var good1 = scheduler.Schedule(null, attempt, ReviewRating.Good);
        var good2 = scheduler.Schedule(null, attempt, ReviewRating.Good);
        var lapse = scheduler.Schedule(good1, Event(objective, "scheduler-item", Start.AddDays(2)), ReviewRating.Again);

        Assert.Equal(good1, good2);
        Assert.Equal(DeterministicSpacedRepetitionScheduler.CurrentVersion, good1.SchedulerVersion);
        Assert.True(lapse.StabilityDays < good1.StabilityDays);
        Assert.Equal(1, lapse.Lapses);
    }

    private static ObjectiveProgress Objective(
        LearningPathDefinition definition,
        IEnumerable<AttemptEvent> events,
        LearningObjective objective) =>
        new LearningProgressService().EvaluatePathFromEvents(definition, events)
            .Levels.SelectMany(item => item.Objectives)
            .Single(item => item.Objective.Id == objective.Id);

    private static IEnumerable<AttemptEvent> Master(LearningObjective objective) =>
        Enumerable.Range(0, objective.MinimumAttempts)
            .Select(index => Event(
                objective,
                $"{objective.Id}.item-{index}",
                Start.AddDays(index == 0 ? 0 : 1).AddMinutes(index)));

    private static AttemptEvent Event(LearningObjective objective, string contentKey, DateTimeOffset completed) => new(
        Guid.NewGuid(),
        contentKey,
        1,
        objective.Level,
        objective.Skill,
        objective.AcceptedExerciseTypes[0],
        AttemptDirection.NotApplicable,
        1,
        AssessmentMode.Practice,
        completed.AddSeconds(-5),
        completed,
        Guid.NewGuid(),
        "test-rubric-v1",
        EvidenceQuality.Deterministic,
        objective.Id);
}
