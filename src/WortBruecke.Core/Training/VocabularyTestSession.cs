using WortBruecke.Core.Models;

namespace WortBruecke.Core.Training;

/// <summary>
/// Builds and scores a reproducible, bidirectional word-only assessment.
/// Every content word can appear at most once in a session.
/// </summary>
public sealed class VocabularyTestSession
{
    private readonly Dictionary<int, VocabularyTestQuestionResult> _answers = [];
    private readonly Dictionary<int, VocabularyTestQuestion> _questionsByNumber;

    private VocabularyTestSession(
        int requestedQuestionCount,
        int seed,
        LanguagePair languagePair,
        IReadOnlyList<VocabularyTestQuestion> questions)
    {
        RequestedQuestionCount = requestedQuestionCount;
        Seed = seed;
        LanguagePair = languagePair;
        Questions = questions;
        _questionsByNumber = questions.ToDictionary(question => question.Number);
    }

    public int RequestedQuestionCount { get; }
    public int Seed { get; }
    public LanguagePair LanguagePair { get; }
    public PracticeUnit Unit => PracticeUnit.Word;
    public IReadOnlyList<VocabularyTestQuestion> Questions { get; }
    public int AnsweredQuestionCount => _answers.Count;
    public int RemainingQuestionCount => Questions.Count - AnsweredQuestionCount;
    public bool IsComplete => AnsweredQuestionCount == Questions.Count;

    public static VocabularyTestSession Create(
        IEnumerable<WordEntry> words,
        int requestedQuestionCount = 20,
        int? seed = null,
        LanguagePair? languagePair = null)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (requestedQuestionCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedQuestionCount),
                requestedQuestionCount,
                "Question count must be greater than zero.");
        }

        var pair = languagePair ?? LanguagePair.RussianToGerman;
        var effectiveSeed = seed ?? Random.Shared.Next();
        var random = new Random(effectiveSeed);

        var candidates = words
            .Where(word => HasTranslation(word, pair.Source.CultureCode) && HasTranslation(word, pair.Target.CultureCode))
            .GroupBy(word => word.Id)
            .Select(group => group.First())
            .GroupBy(word => (
                Source: DictionaryKeyNormalizer.Normalize(
                    word.Translations[pair.Source.CultureCode],
                    pair.Source.CultureCode),
                Target: DictionaryKeyNormalizer.Normalize(
                    word.Translations[pair.Target.CultureCode],
                    pair.Target.CultureCode)))
            .Select(group => group.First())
            .ToList();

        Shuffle(candidates, random);
        var questionCount = Math.Min(requestedQuestionCount, candidates.Count);
        var selectedWords = candidates.Take(questionCount).ToList();
        var directions = CreateBalancedDirections(questionCount, random);

        var questions = new List<VocabularyTestQuestion>(questionCount);
        for (var index = 0; index < questionCount; index++)
        {
            var word = selectedWords[index];
            var direction = directions[index];
            var promptCulture = direction == TranslationDirection.SourceToTarget
                ? pair.Source.CultureCode
                : pair.Target.CultureCode;
            var answerCulture = direction == TranslationDirection.SourceToTarget
                ? pair.Target.CultureCode
                : pair.Source.CultureCode;

            questions.Add(new VocabularyTestQuestion(
                index + 1,
                word.Id,
                direction,
                word.Translations[promptCulture],
                word.Translations[answerCulture],
                promptCulture,
                answerCulture,
                word.Level)
            {
                AcceptedAnswers = word.AcceptedAnswers.For(answerCulture).ToArray()
            });
        }

        return new VocabularyTestSession(
            requestedQuestionCount,
            effectiveSeed,
            pair,
            questions.AsReadOnly());
    }

    public VocabularyTestQuestionResult SubmitAnswer(int questionNumber, string? answer)
    {
        if (!_questionsByNumber.TryGetValue(questionNumber, out var question))
        {
            throw new ArgumentOutOfRangeException(
                nameof(questionNumber),
                questionNumber,
                "The question does not belong to this vocabulary test session.");
        }
        if (_answers.ContainsKey(questionNumber))
        {
            throw new InvalidOperationException($"Question {questionNumber} has already been answered.");
        }

        var submittedAnswer = answer ?? string.Empty;
        var russianAnswer = question.AnswerCultureCode.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
        var evaluation = russianAnswer
            ? AnswerEvaluator.Evaluate(
                submittedAnswer,
                question.ExpectedAnswer,
                question.AcceptedAnswers,
                question.AnswerCultureCode,
                AnswerEvaluationMode.RussianVocabularyLenient)
            : AnswerEvaluator.Evaluate(submittedAnswer, question.ExpectedAnswer, question.AnswerCultureCode);
        var result = new VocabularyTestQuestionResult(question, submittedAnswer, evaluation.IsCorrect)
        {
            MatchKind = evaluation.MatchKind,
            MatchedAnswer = evaluation.MatchedAnswer
        };
        _answers.Add(questionNumber, result);
        return result;
    }

    public VocabularyTestResult GetResult()
    {
        var questionResults = Questions
            .Select(question => _answers.TryGetValue(question.Number, out var answer)
                ? answer
                : new VocabularyTestQuestionResult(question, null, false))
            .ToList()
            .AsReadOnly();

        var sourceToTargetQuestions = Questions.Count(
            question => question.Direction == TranslationDirection.SourceToTarget);
        var targetToSourceQuestions = Questions.Count - sourceToTargetQuestions;
        var sourceToTargetCorrect = _answers.Values.Count(
            answer => answer.IsCorrect && answer.Question.Direction == TranslationDirection.SourceToTarget);
        var targetToSourceCorrect = _answers.Values.Count(
            answer => answer.IsCorrect && answer.Question.Direction == TranslationDirection.TargetToSource);

        return new VocabularyTestResult(
            RequestedQuestionCount,
            Questions.Count,
            _answers.Count,
            _answers.Values.Count(answer => answer.IsCorrect),
            sourceToTargetQuestions,
            sourceToTargetCorrect,
            targetToSourceQuestions,
            targetToSourceCorrect,
            questionResults);
    }

    private static bool HasTranslation(WordEntry word, string cultureCode) =>
        word.Translations.TryGetValue(cultureCode, out var text) && !string.IsNullOrWhiteSpace(text);

    private static List<TranslationDirection> CreateBalancedDirections(int questionCount, Random random)
    {
        var sourceToTargetCount = questionCount / 2;
        if (questionCount % 2 != 0 && random.Next(2) == 0)
        {
            sourceToTargetCount++;
        }

        var targetToSourceCount = questionCount - sourceToTargetCount;
        var directions = Enumerable
            .Repeat(TranslationDirection.SourceToTarget, sourceToTargetCount)
            .Concat(Enumerable.Repeat(TranslationDirection.TargetToSource, targetToSourceCount))
            .ToList();
        Shuffle(directions, random);
        return directions;
    }

    private static void Shuffle<T>(IList<T> values, Random random)
    {
        for (var index = values.Count - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (values[index], values[swapIndex]) = (values[swapIndex], values[index]);
        }
    }
}
