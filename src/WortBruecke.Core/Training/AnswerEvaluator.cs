using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WortBruecke.Core.Training;

public sealed record AnswerEvaluation(bool IsCorrect, string Expected, string NormalizedActual);

public static partial class AnswerEvaluator
{
    public static AnswerEvaluation Evaluate(string? actual, string expected, string cultureCode)
    {
        var normalizedActual = Normalize(actual ?? string.Empty, cultureCode);
        var normalizedExpected = Normalize(expected, cultureCode);
        return new AnswerEvaluation(
            string.Equals(normalizedActual, normalizedExpected, StringComparison.Ordinal),
            expected,
            normalizedActual);
    }

    public static AnswerEvaluation Evaluate(string? actual, IReadOnlyCollection<string> acceptedAnswers, string cultureCode)
    {
        var answers = acceptedAnswers.Where(answer => !string.IsNullOrWhiteSpace(answer)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var normalizedActual = Normalize(actual ?? string.Empty, cultureCode);
        var isCorrect = answers.Any(answer => string.Equals(normalizedActual, Normalize(answer, cultureCode), StringComparison.Ordinal));
        return new AnswerEvaluation(isCorrect, string.Join(" / ", answers), normalizedActual);
    }

    public static string Normalize(string value, string cultureCode)
    {
        var normalized = CollapseWhitespace().Replace(value.Trim(), " ").ToLower(CultureInfo.GetCultureInfo(cultureCode));
        if (cultureCode.StartsWith("de", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized
                .Replace("ä", "ae", StringComparison.Ordinal)
                .Replace("ö", "oe", StringComparison.Ordinal)
                .Replace("ü", "ue", StringComparison.Ordinal)
                .Replace("ß", "ss", StringComparison.Ordinal);
        }

        return normalized.Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespace();
}
