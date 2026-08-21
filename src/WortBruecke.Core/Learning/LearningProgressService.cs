namespace WortBruecke.Core.Learning;

/// <summary>
/// Calculates deterministic curriculum progress and conservative exam readiness from evidence.
/// The service is stateless so persistence and UI concerns can evolve independently.
/// </summary>
public sealed class LearningProgressService
{
    public LearningPathProgress EvaluatePathFromEvents(
        LearningPathDefinition definition,
        IEnumerable<AttemptEvent> attempts,
        GermanLevel? placementLevel = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(attempts);
        if (placementLevel is { } placement && !Enum.IsDefined(placement))
        {
            throw new ArgumentOutOfRangeException(nameof(placementLevel), placement, "Unknown placement level.");
        }

        var evidence = attempts.ToArray();
        return EvaluatePathCore(
            definition,
            placementLevel,
            objective => EvaluateObjective(objective, evidence, definition.RecentAttemptWindow));
    }

    public LearningPathProgress EvaluatePath(
        LearningPathDefinition definition,
        IEnumerable<LearningAttempt> attempts,
        GermanLevel? placementLevel = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(attempts);
        if (placementLevel is { } placement && !Enum.IsDefined(placement))
        {
            throw new ArgumentOutOfRangeException(nameof(placementLevel), placement, "Unknown placement level.");
        }

        var evidence = attempts.ToArray();
        return EvaluatePathCore(
            definition,
            placementLevel,
            objective => EvaluateObjective(objective, evidence, definition.RecentAttemptWindow));
    }

    private static LearningPathProgress EvaluatePathCore(
        LearningPathDefinition definition,
        GermanLevel? placementLevel,
        Func<LearningObjective, ObjectiveProgress> evaluateObjective)
    {
        var results = new List<LevelProgress>(definition.Levels.Count);
        var previousCompleted = false;

        foreach (var level in definition.Levels)
        {
            var objectiveProgress = level.Objectives
                .Select(evaluateObjective)
                .ToArray();
            var required = objectiveProgress.Where(progress => progress.Objective.ContributesToProgress).ToArray();
            var masteredRequiredCount = required.Count(progress => progress.IsMastered);
            var satisfiedByPlacement = placementLevel is { } placed && level.Level < placed;
            var unlocked = level.Level == GermanLevel.A0
                || (placementLevel is { } placementValue && level.Level <= placementValue)
                || previousCompleted;
            var completedFromEvidence = masteredRequiredCount == required.Length;
            var completed = unlocked && (satisfiedByPlacement || completedFromEvidence);

            var skillProgress = objectiveProgress
                .GroupBy(progress => progress.Objective.Skill)
                .OrderBy(group => group.Key)
                .Select(group =>
                {
                    var requiredForSkill = group.Where(progress => progress.Objective.ContributesToProgress).ToArray();
                    return new SkillProgress(
                        group.Key,
                        requiredForSkill.Length,
                        requiredForSkill.Count(progress => progress.IsMastered),
                        Ratio(requiredForSkill.Count(progress => progress.IsMastered), requiredForSkill.Length),
                        Average(group.Select(progress => progress.RecentScore)));
                })
                .ToArray();

            results.Add(new LevelProgress(
                level,
                unlocked,
                completed,
                satisfiedByPlacement,
                required.Length,
                masteredRequiredCount,
                completed ? 1 : Ratio(masteredRequiredCount, required.Length),
                Average(required.Select(progress => progress.RecentScore)),
                Array.AsReadOnly(objectiveProgress),
                Array.AsReadOnly(skillProgress)));
            previousCompleted = completed;
        }

        var current = results.FirstOrDefault(level => level.IsUnlocked && !level.IsCompleted)?.Definition.Level
            ?? GermanLevel.C2;
        var highestCompleted = results.LastOrDefault(level => level.IsCompleted)?.Definition.Level;
        var nextLocked = results.FirstOrDefault(level => !level.IsUnlocked)?.Definition.Level;

        return new LearningPathProgress(
            current,
            highestCompleted,
            nextLocked,
            Average(results.Select(level => level.Completion)),
            results.AsReadOnly());
    }

    public ExamReadiness EvaluateExamReadiness(
        ExamDefinition exam,
        IEnumerable<LearningAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(exam);
        ArgumentNullException.ThrowIfNull(attempts);

        var levelEvidence = attempts.Where(attempt => attempt.Level == exam.Level).ToArray();
        var sections = exam.Sections.Select(section =>
        {
            var matches = levelEvidence
                .Where(attempt => section.Skills.Contains(attempt.Skill)
                    && section.ExerciseTypes.Contains(attempt.ExerciseType)
                    && section.AcceptedModes.Contains(attempt.Mode))
                .OrderByDescending(attempt => attempt.CompletedAtUtc)
                .Take(exam.RecentEvidenceWindowPerSection)
                .ToArray();
            var evidenceCount = matches.Length;
            var timedCount = matches.Count(attempt => attempt.WasTimed);
            var score = Average(matches.Select(attempt => attempt.Score));
            var hasEvidence = evidenceCount >= section.MinimumEvidenceCount;
            var meetsScore = score >= section.MinimumScore;
            var meetsTiming = timedCount >= section.MinimumTimedEvidenceCount;

            return new ExamSectionReadiness(
                section,
                evidenceCount,
                timedCount,
                score,
                hasEvidence,
                meetsScore,
                meetsTiming,
                hasEvidence && meetsScore && meetsTiming);
        }).ToArray();

        var weightSum = sections.Sum(section => section.Definition.Weight);
        var overallScore = sections.Sum(section => section.RecentScore * section.Definition.Weight) / weightSum;
        var completeMocks = CountCompleteMockExams(exam, levelEvidence);
        var meetsOverall = overallScore >= exam.OverallMinimumScore;
        var meetsMocks = completeMocks >= exam.MinimumCompleteMockExams;
        var missing = DescribeMissingRequirements(exam, sections, completeMocks, meetsOverall).ToArray();

        return new ExamReadiness(
            exam,
            overallScore,
            completeMocks,
            meetsOverall,
            meetsMocks,
            sections.All(section => section.IsReady) && meetsOverall && meetsMocks,
            Array.AsReadOnly(sections),
            Array.AsReadOnly(missing));
    }

    private static ObjectiveProgress EvaluateObjective(
        LearningObjective objective,
        IReadOnlyCollection<LearningAttempt> attempts,
        int recentAttemptWindow)
    {
        var matching = attempts
            .Where(attempt => string.Equals(attempt.ObjectiveId, objective.Id, StringComparison.OrdinalIgnoreCase)
                && attempt.Level == objective.Level
                && attempt.Skill == objective.Skill
                && attempt.ExerciseType == objective.ExerciseType)
            .OrderByDescending(attempt => attempt.CompletedAtUtc)
            .ToArray();
        var recentScore = Average(matching.Take(recentAttemptWindow).Select(attempt => attempt.Score));
        return new ObjectiveProgress(
            objective,
            matching.Length,
            recentScore,
            matching.Length >= objective.MinimumAttempts && recentScore >= objective.MasteryThreshold);
    }

    private static ObjectiveProgress EvaluateObjective(
        LearningObjective objective,
        IReadOnlyCollection<AttemptEvent> attempts,
        int recentAttemptWindow)
    {
        if (objective.Availability != ObjectiveAvailability.Published)
        {
            return new ObjectiveProgress(objective, 0, 0, false);
        }

        var matching = attempts
            .Where(attempt => string.Equals(attempt.ObjectiveId, objective.Id, StringComparison.OrdinalIgnoreCase)
                && attempt.Level == objective.Level
                && attempt.Skill == objective.Skill
                && objective.AcceptedExerciseTypes.Contains(attempt.ExerciseFamily)
                && attempt.EvidenceQuality >= objective.MinimumEvidenceQuality)
            .OrderByDescending(attempt => attempt.CompletedAtUtc)
            .ToArray();
        var recent = matching.Take(recentAttemptWindow).ToArray();
        var recentScore = Average(recent.Select(attempt => attempt.Score));
        var distinctItems = matching.Select(attempt => attempt.ContentKey).Distinct(StringComparer.Ordinal).Count();
        var distinctDays = matching.Select(attempt => DateOnly.FromDateTime(attempt.CompletedAtUtc.UtcDateTime)).Distinct().Count();
        return new ObjectiveProgress(
            objective,
            matching.Length,
            recentScore,
            matching.Length >= objective.MinimumAttempts
                && distinctItems >= objective.MinimumDistinctItems
                && distinctDays >= objective.MinimumDistinctDays
                && recentScore >= objective.MasteryThreshold)
        {
            DistinctItemCount = distinctItems,
            DistinctDayCount = distinctDays
        };
    }

    private static int CountCompleteMockExams(ExamDefinition exam, IEnumerable<LearningAttempt> levelEvidence) =>
        levelEvidence
            .Where(attempt => attempt.Mode == AssessmentMode.MockExam && attempt.SessionId is not null)
            .GroupBy(attempt => attempt.SessionId!.Value)
            .Count(session => exam.Sections.All(section => session.Any(attempt =>
                section.Skills.Contains(attempt.Skill) && section.ExerciseTypes.Contains(attempt.ExerciseType))));

    private static IEnumerable<string> DescribeMissingRequirements(
        ExamDefinition exam,
        IEnumerable<ExamSectionReadiness> sections,
        int completeMocks,
        bool meetsOverall)
    {
        foreach (var section in sections)
        {
            if (!section.HasEnoughEvidence)
            {
                yield return $"{section.Definition.Title}: evidence {section.EvidenceCount}/{section.Definition.MinimumEvidenceCount}.";
            }
            if (!section.MeetsScore)
            {
                yield return $"{section.Definition.Title}: score {section.RecentScore:P0}, required {section.Definition.MinimumScore:P0}.";
            }
            if (!section.MeetsTiming)
            {
                yield return $"{section.Definition.Title}: timed evidence {section.TimedEvidenceCount}/{section.Definition.MinimumTimedEvidenceCount}.";
            }
        }
        if (!meetsOverall)
        {
            yield return $"Overall score is below the required {exam.OverallMinimumScore:P0}.";
        }
        if (completeMocks < exam.MinimumCompleteMockExams)
        {
            yield return $"Complete mock exams {completeMocks}/{exam.MinimumCompleteMockExams}.";
        }
    }

    private static double Ratio(int numerator, int denominator) => denominator == 0 ? 0 : (double)numerator / denominator;

    private static double Average(IEnumerable<double> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0 ? 0 : materialized.Average();
    }
}
