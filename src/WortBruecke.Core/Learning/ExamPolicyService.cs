using WortBruecke.Core.Models;

namespace WortBruecke.Core.Learning;

public sealed record ExamReadinessTarget(
    GermanLevel Level,
    string? ModuleId = null,
    string? RequiredBand = null);

public sealed record ExamModuleEvidence(
    string ModuleId,
    string Title,
    int EvidenceCount,
    int TimedEvidenceCount,
    double Score,
    string? Band,
    bool MeetsPolicy,
    string Requirement);

public sealed record ExamPolicyReadiness(
    ExamBlueprint Exam,
    ExamReadinessTarget Target,
    double OverallScore,
    int CompleteMockCount,
    int BufferedPassingMockCount,
    bool PolicySatisfied,
    bool IsReady,
    IReadOnlyList<ExamModuleEvidence> Modules,
    IReadOnlyList<string> MissingRequirements);

/// <summary>
/// Applies the scoring shape published for the selected provider instead of reducing every exam
/// to a generic four-skill average. Readiness additionally requires two buffered passes among the
/// latest three complete mock sessions.
/// </summary>
public sealed class ExamPolicyService
{
    private const double ReadinessBuffer = 0.10;

    public ExamPolicyReadiness Evaluate(
        ExamBlueprint exam,
        ExamReadinessTarget target,
        IEnumerable<AttemptEvent> attempts)
    {
        ArgumentNullException.ThrowIfNull(exam);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(attempts);
        if (!exam.Levels.Contains(target.Level.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The target level is not offered by the selected exam.", nameof(target));
        }
        if (target.ModuleId is not null && !exam.Segments.Any(item => item.Id.Equals(target.ModuleId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The target module does not belong to the selected exam.", nameof(target));
        }
        if (exam.Scoring.Kind == ExamScoringKind.BandPerSkill
            && !exam.Scoring.ScoreBands.Any(item => item.Name.Equals(target.RequiredBand ?? "TDN4", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The requested score band is not defined for the selected exam.", nameof(target));
        }

        var relevant = attempts
            .Where(item => item.Level == target.Level
                && string.Equals(item.ExamId, exam.Id, StringComparison.OrdinalIgnoreCase)
                && item.Mode is AssessmentMode.Checkpoint or AssessmentMode.MockExam)
            .ToArray();
        var selectedSegments = target.ModuleId is null
            ? exam.Segments
            : exam.Segments.Where(item => item.Id.Equals(target.ModuleId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var modules = selectedSegments.Select(segment => EvaluateModule(exam, target, segment, relevant)).ToArray();
        var overall = WeightedAverage(modules.Select(item => (item.Score, 1d)));
        var policySatisfied = EvaluatePolicy(exam, target, modules, overall, buffered: false);

        var completeMocks = relevant
            .Where(item => item.Mode == AssessmentMode.MockExam)
            .GroupBy(item => item.SessionId)
            .Where(session => selectedSegments.All(segment => session.Any(item =>
                string.Equals(item.ModuleId, segment.Id, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(session => session.Max(item => item.CompletedAtUtc))
            .Take(3)
            .ToArray();
        var bufferedPasses = completeMocks.Count(session =>
        {
            var sessionModules = selectedSegments.Select(segment => EvaluateModule(exam, target, segment, session)).ToArray();
            return EvaluatePolicy(
                exam,
                target,
                sessionModules,
                WeightedAverage(sessionModules.Select(item => (item.Score, 1d))),
                buffered: true);
        });

        var missing = new List<string>();
        foreach (var module in modules.Where(item => !item.MeetsPolicy))
        {
            missing.Add($"{module.Title}: {module.Requirement}");
        }
        if (!policySatisfied && modules.All(item => item.MeetsPolicy))
        {
            missing.Add("Не выполнено правило общего результата выбранного экзамена.");
        }
        if (bufferedPasses < 2)
        {
            missing.Add($"Пробные экзамены с запасом: {bufferedPasses}/2 среди последних {Math.Min(3, completeMocks.Length)}.");
        }

        return new ExamPolicyReadiness(
            exam,
            target,
            overall,
            completeMocks.Length,
            bufferedPasses,
            policySatisfied,
            policySatisfied && bufferedPasses >= 2,
            Array.AsReadOnly(modules),
            missing.AsReadOnly());
    }

    private static ExamModuleEvidence EvaluateModule(
        ExamBlueprint exam,
        ExamReadinessTarget target,
        ExamBlueprintSegment segment,
        IEnumerable<AttemptEvent> attempts)
    {
        var moduleAttempts = attempts
            .Where(item => string.Equals(item.ModuleId, segment.Id, StringComparison.OrdinalIgnoreCase))
            .Where(item => RequiredQuality(item.Skill) <= item.EvidenceQuality)
            .OrderByDescending(item => item.CompletedAtUtc)
            .Take(10)
            .ToArray();
        var score = moduleAttempts.Length == 0 ? 0 : moduleAttempts.Average(item => item.Score);
        var band = exam.Scoring.Kind == ExamScoringKind.BandPerSkill ? ResolveBand(exam.Scoring, score)?.Name : null;
        var threshold = ModuleThreshold(exam, target, segment);
        var meets = moduleAttempts.Length > 0 && exam.Scoring.Kind switch
        {
            ExamScoringKind.IndependentModules => score >= threshold,
            ExamScoringKind.BandPerSkill => band is not null
                && BandRank(exam.Scoring, band) >= BandRank(exam.Scoring, target.RequiredBand ?? "TDN4"),
            _ => true
        };
        var requirement = moduleAttempts.Length == 0
            ? "нет подтверждённых результатов"
            : exam.Scoring.Kind == ExamScoringKind.IndependentModules
                ? $"результат {score:P0}, требуется {threshold:P0}"
                : exam.Scoring.Kind == ExamScoringKind.BandPerSkill
                    ? $"уровень {band ?? "ниже TDN3"}, требуется {target.RequiredBand ?? "TDN4"}"
                    : "модуль учитывается в составном правиле экзамена";

        return new ExamModuleEvidence(
            segment.Id,
            SegmentTitle(segment.Id),
            moduleAttempts.Length,
            moduleAttempts.Count(item => item.WasTimed),
            score,
            band,
            meets,
            requirement);
    }

    private static bool EvaluatePolicy(
        ExamBlueprint exam,
        ExamReadinessTarget target,
        IReadOnlyList<ExamModuleEvidence> modules,
        double overall,
        bool buffered)
    {
        var buffer = buffered ? ReadinessBuffer : 0;
        bool Meets(ExamModuleEvidence module, double threshold) =>
            module.EvidenceCount > 0 && module.Score >= Math.Min(1, threshold + buffer);

        return exam.Scoring.Kind switch
        {
            ExamScoringKind.WholeExamThreshold =>
                modules.All(item => item.EvidenceCount > 0)
                && overall >= Math.Min(1, (exam.Scoring.OverallPassRatio ?? 0.6) + buffer),
            ExamScoringKind.WholeExamWithComponentThresholds =>
                MeetsWrittenOrOral(exam, modules, exam.Scoring.WrittenPassRatio ?? 0.6, oral: false, buffer)
                && MeetsWrittenOrOral(exam, modules, exam.Scoring.OralPassRatio ?? 0.6, oral: true, buffer)
                && overall >= Math.Min(1, (exam.Scoring.OverallPassRatio ?? 0.6) + buffer),
            ExamScoringKind.IndependentModules => modules.All(item =>
                Meets(item, exam.Scoring.ModulePassRatio ?? 0.6)),
            ExamScoringKind.WrittenAndOralThresholds =>
                MeetsWrittenOrOral(exam, modules, exam.Scoring.WrittenPassRatio ?? 0.6, oral: false, buffer)
                && MeetsWrittenOrOral(exam, modules, exam.Scoring.OralPassRatio ?? 0.6, oral: true, buffer),
            ExamScoringKind.BandPerSkill => modules.All(item => item.Band is not null
                && BandRank(exam.Scoring, item.Band) >= BandRank(exam.Scoring, target.RequiredBand ?? "TDN4") + (buffered ? 1 : 0)),
            ExamScoringKind.DualLevelProfile => EvaluateDtz(exam, target, modules, buffer),
            _ => false
        };
    }

    private static bool MeetsWrittenOrOral(
        ExamBlueprint exam,
        IReadOnlyList<ExamModuleEvidence> modules,
        double threshold,
        bool oral,
        double buffer)
    {
        var ids = exam.Segments
            .Where(segment => segment.Skills.Contains("speaking", StringComparer.OrdinalIgnoreCase) == oral)
            .Select(segment => segment.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = modules.Where(module => ids.Contains(module.ModuleId)).ToArray();
        return selected.Length > 0
            && selected.All(item => item.EvidenceCount > 0)
            && selected.Average(item => item.Score) >= Math.Min(1, threshold + buffer);
    }

    private static bool EvaluateDtz(
        ExamBlueprint exam,
        ExamReadinessTarget target,
        IReadOnlyList<ExamModuleEvidence> modules,
        double buffer)
    {
        var byId = modules.ToDictionary(item => item.ModuleId, StringComparer.OrdinalIgnoreCase);
        if (!byId.TryGetValue("speaking", out var speaking)
            || !byId.TryGetValue("writing", out var writing)
            || !byId.TryGetValue("listening", out var listening)
            || !byId.TryGetValue("reading", out var reading))
        {
            return false;
        }
        var maximum = exam.Scoring.ReceptiveMaximumItems ?? 45;
        var minimum = target.Level == GermanLevel.B1
            ? exam.Scoring.ReceptiveB1MinimumCorrect ?? 33
            : exam.Scoring.ReceptiveA2MinimumCorrect ?? 20;
        var receptiveThreshold = (double)minimum / maximum;
        var productiveThreshold = target.Level == GermanLevel.B1 ? 0.60 : 0.40;
        var receptive = (listening.Score + reading.Score) / 2 >= Math.Min(1, receptiveThreshold + buffer);
        var writingMeets = writing.Score >= Math.Min(1, productiveThreshold + buffer);
        var speakingMeets = speaking.Score >= Math.Min(1, productiveThreshold + buffer);
        return target.Level == GermanLevel.B1
            ? speakingMeets && (receptive || writingMeets)
            : speakingMeets && (receptive || writingMeets);
    }

    private static double ModuleThreshold(ExamBlueprint exam, ExamReadinessTarget target, ExamBlueprintSegment segment) =>
        exam.Scoring.Kind switch
        {
            ExamScoringKind.IndependentModules => exam.Scoring.ModulePassRatio ?? 0.6,
            ExamScoringKind.WrittenAndOralThresholds or ExamScoringKind.WholeExamWithComponentThresholds
                when segment.Skills.Contains("speaking", StringComparer.OrdinalIgnoreCase) => exam.Scoring.OralPassRatio ?? 0.6,
            ExamScoringKind.WrittenAndOralThresholds or ExamScoringKind.WholeExamWithComponentThresholds => exam.Scoring.WrittenPassRatio ?? 0.6,
            ExamScoringKind.DualLevelProfile when segment.Id is "reading" or "listening" =>
                (double)(target.Level == GermanLevel.B1
                    ? exam.Scoring.ReceptiveB1MinimumCorrect ?? 33
                    : exam.Scoring.ReceptiveA2MinimumCorrect ?? 20) / (exam.Scoring.ReceptiveMaximumItems ?? 45),
            ExamScoringKind.DualLevelProfile => target.Level == GermanLevel.B1 ? 0.60 : 0.40,
            _ => exam.Scoring.OverallPassRatio ?? 0.6
        };

    private static EvidenceQuality RequiredQuality(LanguageSkill skill) => skill switch
    {
        LanguageSkill.Writing or LanguageSkill.Speaking or LanguageSkill.Mediation => EvidenceQuality.HumanReviewed,
        _ => EvidenceQuality.Deterministic
    };

    private static ExamScoreBand? ResolveBand(ExamScoringDefinition scoring, double normalizedScore)
    {
        var points = (int)Math.Round(normalizedScore * (scoring.SkillMaximumPoints ?? 20), MidpointRounding.AwayFromZero);
        return scoring.ScoreBands.SingleOrDefault(item => points >= item.MinimumPoints && points <= item.MaximumPoints);
    }

    private static int BandRank(ExamScoringDefinition scoring, string name) =>
        scoring.ScoreBands.Select((band, index) => (band, index))
            .FirstOrDefault(item => item.band.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).index;

    private static double WeightedAverage(IEnumerable<(double Value, double Weight)> values)
    {
        var materialized = values.ToArray();
        var weight = materialized.Sum(item => item.Weight);
        return weight <= 0 ? 0 : materialized.Sum(item => item.Value * item.Weight) / weight;
    }

    private static string SegmentTitle(string id) => id switch
    {
        "reading" => "Чтение",
        "listening" => "Аудирование",
        "writing" => "Письмо",
        "speaking" => "Говорение",
        "reading-writing" => "Чтение и письмо",
        "reading-language-elements" => "Чтение и языковые элементы",
        "listening-integrated-writing" => "Аудирование и интегрированное письмо",
        _ => id.Replace('-', ' ')
    };
}
