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
    public const int CurrentOnlineAnalysisDisclosureVersion = 1;

    public string SourceCulture { get; set; } = LanguagePair.RussianToGerman.Source.CultureCode;
    public string TargetCulture { get; set; } = LanguagePair.RussianToGerman.Target.CultureCode;
    public int PassageFrequency { get; set; } = 8;
    public PassagePracticeMode PassageMode { get; set; } = PassagePracticeMode.Translation;
    public string ApiModel { get; set; } = "gpt-5-mini";
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>The disclosure version accepted by the user; zero means online analysis is disabled.</summary>
    public int OnlineAnalysisConsentVersion { get; set; }
    public bool AllowOnlineLanguageAnalysis
    {
        get => OnlineAnalysisConsentVersion >= CurrentOnlineAnalysisDisclosureVersion;
        set => OnlineAnalysisConsentVersion = value ? CurrentOnlineAnalysisDisclosureVersion : 0;
    }
    public bool UseDarkTheme { get; set; }
}
