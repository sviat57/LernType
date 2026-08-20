namespace WortBruecke.Core.Models;

public sealed record VocabularyTestQuestion(
    int Number,
    int WordId,
    TranslationDirection Direction,
    string Prompt,
    string ExpectedAnswer,
    string PromptCultureCode,
    string AnswerCultureCode,
    string Level)
{
    public PracticeUnit Unit => PracticeUnit.Word;
}

public sealed record VocabularyTestQuestionResult(
    VocabularyTestQuestion Question,
    string? Answer,
    bool IsCorrect)
{
    public bool IsAnswered => Answer is not null;
}

public sealed record VocabularyTestResult(
    int RequestedQuestionCount,
    int TotalQuestionCount,
    int AnsweredQuestionCount,
    int CorrectAnswerCount,
    int SourceToTargetQuestionCount,
    int SourceToTargetCorrectCount,
    int TargetToSourceQuestionCount,
    int TargetToSourceCorrectCount,
    IReadOnlyList<VocabularyTestQuestionResult> QuestionResults)
{
    public bool IsComplete => AnsweredQuestionCount == TotalQuestionCount;

    public double Accuracy => TotalQuestionCount == 0
        ? 0
        : (double)CorrectAnswerCount / TotalQuestionCount;

    public double SourceToTargetAccuracy => SourceToTargetQuestionCount == 0
        ? 0
        : (double)SourceToTargetCorrectCount / SourceToTargetQuestionCount;

    public double TargetToSourceAccuracy => TargetToSourceQuestionCount == 0
        ? 0
        : (double)TargetToSourceCorrectCount / TargetToSourceQuestionCount;
}
