using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using WortBruecke.Core.Models;

namespace WortBruecke.Core.Learning;

public static class LearningContentKey
{
    public static string ForWord(WordEntry word)
    {
        ArgumentNullException.ThrowIfNull(word);
        var lemma = word.Translations.For("de-DE").Trim();
        foreach (var article in new[] { "der ", "die ", "das ", "ein ", "eine " })
        {
            if (lemma.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                lemma = lemma[article.Length..];
                break;
            }
        }
        return $"core.word.{Slug(word.ThemeKey)}.{Slug(lemma)}";
    }

    public static string ForSentence(SentenceEntry sentence)
    {
        ArgumentNullException.ThrowIfNull(sentence);
        return $"core.sentence.{Slug(sentence.ThemeKey)}.{Digest(Normalize(sentence.Translations.For("de-DE")))[..12]}";
    }

    public static string ForPassageSegment(Passage passage, PassageSegment segment)
    {
        ArgumentNullException.ThrowIfNull(passage);
        ArgumentNullException.ThrowIfNull(segment);
        return $"core.passage.{Slug(passage.Key)}.segment-{segment.Order:D2}";
    }

    public static string ForGrammar(GrammarTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return $"core.grammar.{Slug(task.Key)}";
    }

    public static string ForBookWord(long bookId, string lemma)
    {
        if (bookId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bookId), bookId, "A persisted book identifier is required.");
        }
        if (string.IsNullOrWhiteSpace(lemma))
        {
            throw new ArgumentException("A vocabulary lemma is required.", nameof(lemma));
        }
        return $"user.book.{bookId}.word.{Slug(lemma)}";
    }

    private static string Slug(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD)
            .Replace("ß", "ss", StringComparison.OrdinalIgnoreCase);
        var result = new StringBuilder(normalized.Length);
        var pendingSeparator = false;
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && result.Length > 0)
                {
                    result.Append('-');
                }
                result.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }
        return result.ToString().Trim('-') is { Length: > 0 } key ? key : "item";
    }

    private static string Digest(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    private static string Normalize(string value) =>
        string.Join(' ', value.Normalize(NormalizationForm.FormKC)
            .Trim()
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public static class LearningEvidenceFactory
{
    public const int BundledCatalogRevision = 5;
    public const string ExactAnswerRubric = "exact-answer-v2";
    public const string RussianVocabularyLeniencyRubric = "russian-vocabulary-leniency-v1";
    public const string HeuristicGrammarRubric = "grammar-markers-v1";
    private static readonly IReadOnlyDictionary<(GermanLevel Level, LanguageSkill Skill), LearningObjective> Objectives =
        GermanCurriculum.CreateDefault().Levels
            .SelectMany(item => item.Objectives)
            .ToDictionary(item => (item.Level, item.Skill));

    public static AttemptEvent Create(
        string contentKey,
        string levelText,
        LanguageSkill skill,
        ExerciseType family,
        AttemptDirection direction,
        bool correct,
        Guid sessionId,
        DateTimeOffset startedAtUtc,
        EvidenceQuality quality = EvidenceQuality.Deterministic,
        AssessmentMode mode = AssessmentMode.Practice,
        string rubricVersion = ExactAnswerRubric,
        string? examId = null,
        string? moduleId = null,
        bool wasTimed = false,
        DateTimeOffset? completedAtUtc = null,
        Guid? eventId = null,
        int contentRevision = BundledCatalogRevision)
    {
        if (!GermanLevelExtensions.TryParse(levelText, out var level))
        {
            throw new ArgumentException($"Unknown content level: {levelText}.", nameof(levelText));
        }
        if (!Enum.IsDefined(skill))
        {
            throw new ArgumentOutOfRangeException(nameof(skill), skill, "Unknown language skill.");
        }
        var objective = Objectives[(level, skill)];

        return new AttemptEvent(
            eventId ?? Guid.NewGuid(),
            contentKey,
            contentRevision,
            level,
            skill,
            family,
            direction,
            correct ? 1 : 0,
            mode,
            startedAtUtc,
            completedAtUtc ?? DateTimeOffset.UtcNow,
            sessionId,
            rubricVersion,
            quality,
            objective.Id,
            wasTimed,
            examId,
            moduleId,
            DeterministicSpacedRepetitionScheduler.CurrentVersion);
    }
}

public sealed record PlacementResult(
    GermanLevel RecommendedLevel,
    IReadOnlyDictionary<LanguageSkill, double> SkillScores,
    double Confidence,
    DateTimeOffset CompletedAtUtc,
    string AssessmentVersion);

public interface IPlacementResultProvider
{
    Task<PlacementResult?> GetLatestAsync(CancellationToken cancellationToken = default);
}
