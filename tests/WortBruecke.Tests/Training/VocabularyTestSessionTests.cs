using WortBruecke.Core.Models;
using WortBruecke.Core.Training;

namespace WortBruecke.Tests.Training;

public sealed class VocabularyTestSessionTests
{
    [Fact]
    public void Create_WithSameSeed_ReproducesQuestionsAndDirections()
    {
        var words = CreateWords(30);

        var first = VocabularyTestSession.Create(words, requestedQuestionCount: 20, seed: 1701);
        var second = VocabularyTestSession.Create(words, requestedQuestionCount: 20, seed: 1701);

        Assert.Equal(1701, first.Seed);
        Assert.Equal(
            first.Questions.Select(question => (question.WordId, question.Direction)),
            second.Questions.Select(question => (question.WordId, question.Direction)));
    }

    [Fact]
    public void Create_UsesUniqueWordsAndRequestedSize()
    {
        var session = VocabularyTestSession.Create(CreateWords(30), requestedQuestionCount: 20, seed: 42);

        Assert.Equal(20, session.Questions.Count);
        Assert.Equal(20, session.Questions.Select(question => question.WordId).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 20), session.Questions.Select(question => question.Number));
        Assert.All(session.Questions, question => Assert.Equal(PracticeUnit.Word, question.Unit));
        Assert.Equal(PracticeUnit.Word, session.Unit);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(19)]
    [InlineData(20)]
    public void Create_BalancesDirections(int requestedQuestionCount)
    {
        var session = VocabularyTestSession.Create(
            CreateWords(30),
            requestedQuestionCount,
            seed: 91);

        var sourceToTarget = session.Questions.Count(
            question => question.Direction == TranslationDirection.SourceToTarget);
        var targetToSource = session.Questions.Count - sourceToTarget;

        Assert.InRange(Math.Abs(sourceToTarget - targetToSource), 0, 1);
    }

    [Fact]
    public void Create_ReducesSizeForSmallPoolAndIgnoresDuplicateOrIncompleteEntries()
    {
        var words = new[]
        {
            CreateWord(1),
            CreateWord(1),
            CreateWord(2),
            CreateWord(3, includeTargetTranslation: false)
        };

        var session = VocabularyTestSession.Create(words, requestedQuestionCount: 20, seed: 7);

        Assert.Equal(20, session.RequestedQuestionCount);
        Assert.Equal(2, session.Questions.Count);
        Assert.Equal(2, session.Questions.Select(question => question.WordId).Distinct().Count());
        Assert.Single(session.Questions, question => question.Direction == TranslationDirection.SourceToTarget);
        Assert.Single(session.Questions, question => question.Direction == TranslationDirection.TargetToSource);
    }

    [Fact]
    public void Create_DeduplicatesNormalizedTranslationPairsAcrossDifferentIds()
    {
        var words = CreateWords(18).Concat(
        [
            CreateWordWithTranslations(714, "  выходные ", " DAS   WOCHENENDE "),
            CreateWordWithTranslations(1015, "ВЫХОДНЫЕ", "das Wochenende")
        ]);

        var session = VocabularyTestSession.Create(
            words,
            requestedQuestionCount: 20,
            seed: 51);

        Assert.Equal(19, session.Questions.Count);
        Assert.Single(session.Questions, question => question.WordId is 714 or 1015);
        Assert.Equal(
            19,
            session.Questions
                .Select(question => question.WordId)
                .Distinct()
                .Count());
    }

    [Fact]
    public void Create_MapsPromptAnswerAndCulturesForBothDirections()
    {
        var session = VocabularyTestSession.Create(CreateWords(8), requestedQuestionCount: 8, seed: 123);

        Assert.Contains(session.Questions, question => question.Direction == TranslationDirection.SourceToTarget);
        Assert.Contains(session.Questions, question => question.Direction == TranslationDirection.TargetToSource);

        foreach (var question in session.Questions)
        {
            var word = CreateWord(question.WordId);
            if (question.Direction == TranslationDirection.SourceToTarget)
            {
                Assert.Equal("ru-RU", question.PromptCultureCode);
                Assert.Equal("de-DE", question.AnswerCultureCode);
                Assert.Equal(word.Translations["ru-RU"], question.Prompt);
                Assert.Equal(word.Translations["de-DE"], question.ExpectedAnswer);
            }
            else
            {
                Assert.Equal("de-DE", question.PromptCultureCode);
                Assert.Equal("ru-RU", question.AnswerCultureCode);
                Assert.Equal(word.Translations["de-DE"], question.Prompt);
                Assert.Equal(word.Translations["ru-RU"], question.ExpectedAnswer);
            }
        }
    }

    [Fact]
    public void SubmitAnswer_ProducesOverallAndPerDirectionResult()
    {
        var session = VocabularyTestSession.Create(CreateWords(6), requestedQuestionCount: 6, seed: 31);
        var intentionallyWrong = session.Questions[0];

        foreach (var question in session.Questions)
        {
            session.SubmitAnswer(
                question.Number,
                question == intentionallyWrong ? "намеренно неверно" : question.ExpectedAnswer);
        }

        var result = session.GetResult();

        Assert.True(session.IsComplete);
        Assert.True(result.IsComplete);
        Assert.Equal(0, session.RemainingQuestionCount);
        Assert.Equal(6, result.TotalQuestionCount);
        Assert.Equal(6, result.AnsweredQuestionCount);
        Assert.Equal(5, result.CorrectAnswerCount);
        Assert.Equal(5d / 6d, result.Accuracy, precision: 10);
        Assert.Equal(3, result.SourceToTargetQuestionCount);
        Assert.Equal(3, result.TargetToSourceQuestionCount);
        Assert.Equal(
            result.CorrectAnswerCount,
            result.SourceToTargetCorrectCount + result.TargetToSourceCorrectCount);
        Assert.Single(result.QuestionResults, answer => !answer.IsCorrect);
    }

    [Fact]
    public void SubmitAnswer_RussianTargetAcceptsTypoAndCuratedAliasWithMatchKinds()
    {
        var profession = CreateWordWithTranslations(402, "профессия", "der Beruf");
        var professionSession = CreateSingleDirectionSession(profession, "ru-RU");
        var typo = professionSession.SubmitAnswer(1, "професия");

        var river = CreateWordWithTranslations(604, "река", "der Fluss") with
        {
            AcceptedAnswers = new LocalizedAnswerSet { ["ru-RU"] = ["речка"] }
        };
        var riverSession = CreateSingleDirectionSession(river, "ru-RU");
        var alias = riverSession.SubmitAnswer(1, "речка");

        Assert.True(typo.IsCorrect);
        Assert.Equal(AnswerMatchKind.RussianTypo, typo.MatchKind);
        Assert.Equal("профессия", typo.MatchedAnswer);
        Assert.True(alias.IsCorrect);
        Assert.Equal(AnswerMatchKind.AcceptedVariant, alias.MatchKind);
        Assert.Equal("речка", alias.MatchedAnswer);
    }

    [Fact]
    public void SubmitAnswer_GermanTargetRejectsTypoAndMissingArticle()
    {
        var word = CreateWordWithTranslations(402, "профессия", "der Beruf");

        var missingArticle = CreateSingleDirectionSession(word, "de-DE").SubmitAnswer(1, "Beruf");
        var typo = CreateSingleDirectionSession(word, "de-DE").SubmitAnswer(1, "der Beruff");

        Assert.False(missingArticle.IsCorrect);
        Assert.Equal(AnswerMatchKind.Incorrect, missingArticle.MatchKind);
        Assert.False(typo.IsCorrect);
        Assert.Equal(AnswerMatchKind.Incorrect, typo.MatchKind);
    }

    [Fact]
    public void GetResult_RepresentsUnansweredQuestionsWithoutMarkingThemAnswered()
    {
        var session = VocabularyTestSession.Create(CreateWords(4), requestedQuestionCount: 4, seed: 5);
        var answered = session.Questions[0];

        session.SubmitAnswer(answered.Number, answered.ExpectedAnswer);
        var result = session.GetResult();

        Assert.False(result.IsComplete);
        Assert.Equal(1, result.AnsweredQuestionCount);
        Assert.Equal(1, result.CorrectAnswerCount);
        Assert.Equal(4, result.QuestionResults.Count);
        Assert.Equal(3, result.QuestionResults.Count(question => !question.IsAnswered));
    }

    [Fact]
    public void SubmitAnswer_RejectsUnknownOrRepeatedQuestion()
    {
        var session = VocabularyTestSession.Create(CreateWords(2), requestedQuestionCount: 2, seed: 10);
        var question = session.Questions[0];

        session.SubmitAnswer(question.Number, question.ExpectedAnswer);

        Assert.Throws<InvalidOperationException>(() => session.SubmitAnswer(question.Number, question.ExpectedAnswer));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SubmitAnswer(999, "answer"));
    }

    [Fact]
    public void Create_RejectsNonPositiveRequestedSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VocabularyTestSession.Create(CreateWords(2), requestedQuestionCount: 0, seed: 1));
    }

    private static IReadOnlyList<WordEntry> CreateWords(int count) =>
        Enumerable.Range(1, count).Select(id => CreateWord(id)).ToList();

    private static VocabularyTestSession CreateSingleDirectionSession(WordEntry word, string answerCultureCode) =>
        Enumerable.Range(0, 100)
            .Select(seed => VocabularyTestSession.Create([word], requestedQuestionCount: 1, seed: seed))
            .First(session => session.Questions[0].AnswerCultureCode == answerCultureCode);

    private static WordEntry CreateWord(int id, bool includeTargetTranslation = true)
    {
        var translations = new LocalizedText
        {
            ["ru-RU"] = $"слово {id}"
        };
        if (includeTargetTranslation)
        {
            translations["de-DE"] = $"das Wort {id}";
        }

        return new WordEntry(
            id,
            ThemeId: 1,
            ThemeKey: "test",
            ImagePath: string.Empty,
            Level: "A1",
            PartOfSpeech: "noun",
            Translations: translations,
            Examples: []);
    }

    private static WordEntry CreateWordWithTranslations(int id, string source, string target) =>
        new(
            id,
            ThemeId: 1,
            ThemeKey: "test",
            ImagePath: string.Empty,
            Level: "A1",
            PartOfSpeech: "noun",
            Translations: new LocalizedText
            {
                ["ru-RU"] = source,
                ["de-DE"] = target
            },
            Examples: []);
}
