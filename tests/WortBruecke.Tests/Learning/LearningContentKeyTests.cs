using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;

namespace WortBruecke.Tests.Learning;

public sealed class LearningContentKeyTests
{
    [Fact]
    public void WordKey_UsesSemanticLemmaInsteadOfReusedNumericId()
    {
        var word = new WordEntry(
            203,
            2,
            "familie",
            "ignored.png",
            "A1",
            "noun",
            Text(("de-DE", "die Schwester"), ("ru-RU", "сестра")),
            Text(("de-DE", "Meine Schwester."), ("ru-RU", "Моя сестра.")));

        Assert.Equal("core.word.familie.schwester", LearningContentKey.ForWord(word));
    }

    [Fact]
    public void SentenceKey_IsStableAcrossNumericIdChanges()
    {
        var translations = Text(("de-DE", "  Ich   lerne Deutsch. "), ("ru-RU", "Я учу немецкий."));
        var first = new SentenceEntry(1, 5, "alltag", "A1", translations);
        var second = new SentenceEntry(999, 5, "alltag", "A1", translations);

        Assert.Equal(LearningContentKey.ForSentence(first), LearningContentKey.ForSentence(second));
        Assert.StartsWith("core.sentence.alltag.", LearningContentKey.ForSentence(first));
    }

    [Fact]
    public void BookWordKey_IsStableWithinBookAndPrivacyPurgeNamespace()
    {
        Assert.Equal(
            "user.book.42.word.strasse",
            LearningContentKey.ForBookWord(42, "Straße"));
    }

    private static LocalizedText Text(params (string Culture, string Value)[] values)
    {
        var result = new LocalizedText();
        foreach (var (culture, value) in values)
        {
            result[culture] = value;
        }
        return result;
    }
}
