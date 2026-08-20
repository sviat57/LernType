namespace WortBruecke.Core.Models;

public enum ContentType
{
    Word,
    Passage,
    Grammar,
    Sentence,
    BookWord,
    AssessmentWord
}

public sealed record ProgressRecord(
    ContentType ContentType,
    long ContentId,
    int AttemptCount,
    int CorrectCount,
    DateTimeOffset? LastAttemptUtc)
{
    public double Accuracy => AttemptCount == 0 ? 0 : (double)CorrectCount / AttemptCount;
}

public enum PassagePracticeMode
{
    Translation,
    GermanTyping
}

public sealed class AppSettings
{
    public string SourceCulture { get; set; } = LanguagePair.RussianToGerman.Source.CultureCode;
    public string TargetCulture { get; set; } = LanguagePair.RussianToGerman.Target.CultureCode;
    public int PassageFrequency { get; set; } = 8;
    public PassagePracticeMode PassageMode { get; set; } = PassagePracticeMode.Translation;
    public string ApiModel { get; set; } = "gpt-5-mini";
    public string ApiKey { get; set; } = string.Empty;
    public bool UseDarkTheme { get; set; }
}
