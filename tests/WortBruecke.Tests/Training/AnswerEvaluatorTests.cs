using WortBruecke.Core.Training;

namespace WortBruecke.Tests.Training;

public sealed class AnswerEvaluatorTests
{
    [Fact]
    public void Evaluate_AcceptsAnyDictionaryVariant()
    {
        var result = AnswerEvaluator.Evaluate("Wohnhaus", new[] { "Haus", "Wohnhaus", "Gebäude" }, "de-DE");

        Assert.True(result.IsCorrect);
        Assert.Contains("Wohnhaus", result.Expected);
    }

    [Theory]
    [InlineData("  Яблоко ", "яблоко", "ru-RU")]
    [InlineData("GRÜN", "gruen", "de-DE")]
    [InlineData("die Straße", "die Strasse", "de-DE")]
    [InlineData("schön", "schoen", "de-DE")]
    public void Evaluate_AcceptsCaseWhitespaceAndGermanTransliterations(string actual, string expected, string culture)
    {
        Assert.True(AnswerEvaluator.Evaluate(actual, expected, culture).IsCorrect);
    }

    [Fact]
    public void Evaluate_DoesNotIgnoreArticles()
    {
        Assert.False(AnswerEvaluator.Evaluate("Apfel", "der Apfel", "de-DE").IsCorrect);
    }

    [Theory]
    [InlineData("Guten Morgen!", "Guten Morgen.", "de-DE")]
    [InlineData("Wie geht es dir?", "Wie geht es dir", "de-DE")]
    [InlineData("E-Mail", "EMail", "de-DE")]
    [InlineData("«Доброе утро!»", "Доброе утро", "ru-RU")]
    public void Evaluate_IgnoresNonSemanticPunctuation(string actual, string expected, string culture)
    {
        Assert.True(AnswerEvaluator.Evaluate(actual, expected, culture).IsCorrect);
    }

    [Fact]
    public void Evaluate_StillRejectsMissingOrDifferentWords()
    {
        Assert.False(AnswerEvaluator.Evaluate("Ich komme morgen.", "Ich komme heute.", "de-DE").IsCorrect);
    }
}
