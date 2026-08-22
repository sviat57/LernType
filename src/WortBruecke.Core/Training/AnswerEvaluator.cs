using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WortBruecke.Core.Training;

public enum AnswerEvaluationMode
{
    Strict,
    RussianVocabularyLenient
}

public enum AnswerMatchKind
{
    Incorrect,
    Exact,
    AcceptedVariant,
    RussianTypo
}

public sealed record AnswerEvaluation(
    bool IsCorrect,
    string Expected,
    string NormalizedActual,
    AnswerMatchKind MatchKind,
    string? MatchedAnswer)
{
    public AnswerEvaluation(bool isCorrect, string expected, string normalizedActual)
        : this(
            isCorrect,
            expected,
            normalizedActual,
            isCorrect ? AnswerMatchKind.Exact : AnswerMatchKind.Incorrect,
            isCorrect ? expected : null)
    {
    }

    public void Deconstruct(out bool isCorrect, out string expected, out string normalizedActual)
    {
        isCorrect = IsCorrect;
        expected = Expected;
        normalizedActual = NormalizedActual;
    }
}

public static partial class AnswerEvaluator
{
    public static AnswerEvaluation Evaluate(string? actual, string expected, string cultureCode)
        => Evaluate(actual, expected, cultureCode, AnswerEvaluationMode.Strict);

    public static AnswerEvaluation Evaluate(
        string? actual,
        string expected,
        string cultureCode,
        AnswerEvaluationMode mode)
        => EvaluateCore(actual, [expected], expected, cultureCode, mode);

    public static AnswerEvaluation Evaluate(string? actual, IReadOnlyCollection<string> acceptedAnswers, string cultureCode)
        => Evaluate(actual, acceptedAnswers, cultureCode, AnswerEvaluationMode.Strict);

    public static AnswerEvaluation Evaluate(
        string? actual,
        IReadOnlyCollection<string> acceptedAnswers,
        string cultureCode,
        AnswerEvaluationMode mode)
    {
        var answers = CleanAnswers(acceptedAnswers);
        return EvaluateCore(actual, answers, string.Join(" / ", answers), cultureCode, mode);
    }

    public static AnswerEvaluation Evaluate(
        string? actual,
        string expected,
        IReadOnlyCollection<string> acceptedAnswers,
        string cultureCode,
        AnswerEvaluationMode mode = AnswerEvaluationMode.Strict)
    {
        var answers = CleanAnswers([expected, .. acceptedAnswers]);
        return EvaluateCore(actual, answers, expected, cultureCode, mode);
    }

    private static AnswerEvaluation EvaluateCore(
        string? actual,
        IReadOnlyList<string> acceptedAnswers,
        string expectedDisplay,
        string cultureCode,
        AnswerEvaluationMode mode)
    {
        var normalizedActual = Normalize(actual ?? string.Empty, cultureCode);
        if (acceptedAnswers.Count == 0)
        {
            return Incorrect(expectedDisplay, normalizedActual);
        }

        var useRussianLeniency = mode == AnswerEvaluationMode.RussianVocabularyLenient &&
            cultureCode.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
        var comparableActual = useRussianLeniency
            ? NormalizeRussianYo(normalizedActual)
            : normalizedActual;

        for (var index = 0; index < acceptedAnswers.Count; index++)
        {
            var answer = acceptedAnswers[index];
            var comparableAnswer = Normalize(answer, cultureCode);
            if (useRussianLeniency)
            {
                comparableAnswer = NormalizeRussianYo(comparableAnswer);
            }

            if (string.Equals(comparableActual, comparableAnswer, StringComparison.Ordinal))
            {
                return new AnswerEvaluation(
                    true,
                    expectedDisplay,
                    normalizedActual,
                    index == 0 ? AnswerMatchKind.Exact : AnswerMatchKind.AcceptedVariant,
                    answer);
            }
        }

        if (useRussianLeniency)
        {
            foreach (var answer in acceptedAnswers)
            {
                var normalizedAnswer = NormalizeRussianYo(Normalize(answer, cultureCode));
                if (IsSingleRussianVocabularyTypo(comparableActual, normalizedAnswer))
                {
                    return new AnswerEvaluation(
                        true,
                        expectedDisplay,
                        normalizedActual,
                        AnswerMatchKind.RussianTypo,
                        answer);
                }
            }
        }

        return Incorrect(expectedDisplay, normalizedActual);
    }

    public static string Normalize(string value, string cultureCode)
    {
        var normalized = value
            .Normalize(NormalizationForm.FormKC)
            .Trim()
            .ToLower(CultureInfo.GetCultureInfo(cultureCode));
        normalized = IgnorablePunctuation().Replace(normalized, " ");
        normalized = JoinableDashes().Replace(normalized, string.Empty);
        normalized = CollapseWhitespace().Replace(normalized, " ").Trim();
        if (cultureCode.StartsWith("de", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized
                .Replace("ä", "ae", StringComparison.Ordinal)
                .Replace("ö", "oe", StringComparison.Ordinal)
                .Replace("ü", "ue", StringComparison.Ordinal)
                .Replace("ß", "ss", StringComparison.Ordinal);
        }

        return normalized.Trim().Normalize(NormalizationForm.FormC);
    }

    private static string[] CleanAnswers(IEnumerable<string> answers) =>
        answers
            .Where(answer => !string.IsNullOrWhiteSpace(answer))
            .Select(answer => answer.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static AnswerEvaluation Incorrect(string expected, string normalizedActual) =>
        new(false, expected, normalizedActual, AnswerMatchKind.Incorrect, null);

    private static string NormalizeRussianYo(string value) =>
        value.Replace('ё', 'е');

    private static bool IsSingleRussianVocabularyTypo(string actual, string expected)
    {
        var actualTokens = actual.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var expectedTokens = expected.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (actualTokens.Length == 0 || actualTokens.Length != expectedTokens.Length ||
            actualTokens.Any(token => !CyrillicToken().IsMatch(token)) ||
            expectedTokens.Any(token => !CyrillicToken().IsMatch(token)))
        {
            return false;
        }

        var totalDistance = 0;
        for (var index = 0; index < expectedTokens.Length; index++)
        {
            if (string.Equals(actualTokens[index], expectedTokens[index], StringComparison.Ordinal))
            {
                continue;
            }

            if (expectedTokens[index].Length < 6)
            {
                return false;
            }

            totalDistance += OptimalStringAlignmentDistance(actualTokens[index], expectedTokens[index]);
            if (totalDistance > 1)
            {
                return false;
            }
        }

        return totalDistance == 1;
    }

    private static int OptimalStringAlignmentDistance(string source, string target)
    {
        if (Math.Abs(source.Length - target.Length) > 1)
        {
            return 2;
        }

        var previousPrevious = new int[target.Length + 1];
        var previous = new int[target.Length + 1];
        var current = new int[target.Length + 1];
        for (var targetIndex = 0; targetIndex <= target.Length; targetIndex++)
        {
            previous[targetIndex] = targetIndex;
        }

        for (var sourceIndex = 1; sourceIndex <= source.Length; sourceIndex++)
        {
            current[0] = sourceIndex;
            for (var targetIndex = 1; targetIndex <= target.Length; targetIndex++)
            {
                var substitutionCost = source[sourceIndex - 1] == target[targetIndex - 1] ? 0 : 1;
                current[targetIndex] = Math.Min(
                    Math.Min(
                        previous[targetIndex] + 1,
                        current[targetIndex - 1] + 1),
                    previous[targetIndex - 1] + substitutionCost);

                if (sourceIndex > 1 && targetIndex > 1 &&
                    source[sourceIndex - 1] == target[targetIndex - 2] &&
                    source[sourceIndex - 2] == target[targetIndex - 1])
                {
                    current[targetIndex] = Math.Min(
                        current[targetIndex],
                        previousPrevious[targetIndex - 2] + 1);
                }
            }

            (previousPrevious, previous, current) = (previous, current, previousPrevious);
        }

        return previous[target.Length];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespace();

    [GeneratedRegex("""[\.,!?;:"„“‚‘’'«»()\[\]{}…/\\]+""")]
    private static partial Regex IgnorablePunctuation();

    [GeneratedRegex(@"[-‐‑‒–—―]")]
    private static partial Regex JoinableDashes();

    [GeneratedRegex(@"^\p{IsCyrillic}+$", RegexOptions.CultureInvariant)]
    private static partial Regex CyrillicToken();
}
