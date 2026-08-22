namespace WortBruecke.Core.Models;

public sealed record Theme(
    int Id,
    string Key,
    string IconKey,
    LocalizedText Names);

public sealed record WordEntry(
    int Id,
    int ThemeId,
    string ThemeKey,
    string ImagePath,
    string Level,
    string PartOfSpeech,
    LocalizedText Translations,
    LocalizedText Examples)
{
    public LocalizedAnswerSet AcceptedAnswers { get; init; } = [];

    public WordEntry(
        int id,
        int themeId,
        string themeKey,
        string imagePath,
        string level,
        string partOfSpeech,
        LocalizedText translations,
        LocalizedText examples,
        LocalizedAnswerSet acceptedAnswers)
        : this(id, themeId, themeKey, imagePath, level, partOfSpeech, translations, examples)
    {
        AcceptedAnswers = acceptedAnswers;
    }
}

public sealed record SentenceEntry(
    int Id,
    int ThemeId,
    string ThemeKey,
    string Level,
    LocalizedText Translations);

public enum PassageKind
{
    FairyTale,
    Everyday,
    Classic
}

public sealed record PassageSegment(
    int Id,
    int Order,
    LocalizedText Translations);

public sealed record Passage(
    int Id,
    string Key,
    LocalizedText Titles,
    PassageKind Kind,
    string Level,
    string Topic,
    IReadOnlyList<PassageSegment> Segments);

public sealed record GrammarTask(
    int Id,
    string Key,
    string Level,
    string SourceText,
    LocalizedText Instructions,
    string MarkerRule);
