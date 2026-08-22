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
        Assert.Equal(AnswerMatchKind.AcceptedVariant, result.MatchKind);
        Assert.Equal("Wohnhaus", result.MatchedAnswer);
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

    [Fact]
    public void Evaluate_RussianVocabularyLenient_AcceptsCuratedVariantBeforeFuzzyMatching()
    {
        var result = AnswerEvaluator.Evaluate(
            "речка",
            "река",
            ["речка"],
            "ru-RU",
            AnswerEvaluationMode.RussianVocabularyLenient);

        Assert.True(result.IsCorrect);
        Assert.Equal(AnswerMatchKind.AcceptedVariant, result.MatchKind);
        Assert.Equal("речка", result.MatchedAnswer);
    }

    [Theory]
    [InlineData("професия", "профессия")]
    [InlineData("професссия", "профессия")]
    [InlineData("профессио", "профессия")]
    [InlineData("прфоессия", "профессия")]
    public void Evaluate_RussianVocabularyLenient_AcceptsOneEditIncludingTransposition(
        string actual,
        string expected)
    {
        var result = AnswerEvaluator.Evaluate(
            actual,
            expected,
            "ru-RU",
            AnswerEvaluationMode.RussianVocabularyLenient);

        Assert.True(result.IsCorrect);
        Assert.Equal(AnswerMatchKind.RussianTypo, result.MatchKind);
        Assert.Equal(expected, result.MatchedAnswer);
    }

    [Fact]
    public void Evaluate_RussianVocabularyLenient_TreatsYoAsEquivalent()
    {
        var result = AnswerEvaluator.Evaluate(
            "елочка",
            "ёлочка",
            "ru-RU",
            AnswerEvaluationMode.RussianVocabularyLenient);

        Assert.True(result.IsCorrect);
        Assert.Equal(AnswerMatchKind.Exact, result.MatchKind);
        Assert.False(AnswerEvaluator.Evaluate("елочка", "ёлочка", "ru-RU").IsCorrect);
    }

    [Theory]
    [InlineData("рука", "река")]
    [InlineData("прафесия", "профессия")]
    [InlineData("моя профессия", "профессия")]
    [InlineData("профеccия", "профессия")]
    public void Evaluate_RussianVocabularyLenient_RejectsUnsafeFuzzyMatches(string actual, string expected)
    {
        var result = AnswerEvaluator.Evaluate(
            actual,
            expected,
            "ru-RU",
            AnswerEvaluationMode.RussianVocabularyLenient);

        Assert.False(result.IsCorrect);
        Assert.Equal(AnswerMatchKind.Incorrect, result.MatchKind);
        Assert.Null(result.MatchedAnswer);
    }

    [Fact]
    public void Evaluate_StrictModeAndGermanNeverApplyRussianTypoTolerance()
    {
        var russianStrict = AnswerEvaluator.Evaluate("професия", "профессия", "ru-RU");
        var germanWithLenientMode = AnswerEvaluator.Evaluate(
            "der Berf",
            "der Beruf",
            "de-DE",
            AnswerEvaluationMode.RussianVocabularyLenient);

        Assert.False(russianStrict.IsCorrect);
        Assert.False(germanWithLenientMode.IsCorrect);
        Assert.Equal(AnswerMatchKind.Incorrect, germanWithLenientMode.MatchKind);
    }
}
