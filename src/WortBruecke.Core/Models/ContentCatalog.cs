namespace WortBruecke.Core.Models;

public sealed class ContentCatalog
{
    public int Revision { get; init; }
    public List<ThemeSeed> Themes { get; init; } = [];
    public List<WordSeed> Words { get; init; } = [];
    public List<SentenceSeed> Sentences { get; init; } = [];
    public List<PassageSeed> Passages { get; init; } = [];
    public List<GrammarTaskSeed> GrammarTasks { get; init; } = [];
}

public sealed class SentenceSeed
{
    public int Id { get; init; }
    public int ThemeId { get; init; }
    public string Level { get; init; } = "A1";
    public LocalizedText Translations { get; init; } = [];
}

public sealed class ThemeSeed
{
    public int Id { get; init; }
    public string Key { get; init; } = string.Empty;
    public string IconKey { get; init; } = string.Empty;
    public LocalizedText Names { get; init; } = [];
}

public sealed class WordSeed
{
    public int Id { get; init; }
    public int ThemeId { get; init; }
    public string ImagePath { get; init; } = string.Empty;
    public string Level { get; init; } = "A1";
    public string PartOfSpeech { get; init; } = string.Empty;
    public LocalizedText Translations { get; init; } = [];
    public LocalizedText Examples { get; init; } = [];
    public LocalizedAnswerSet AcceptedAnswers { get; init; } = [];
}

public sealed class PassageSeed
{
    public int Id { get; init; }
    public string Key { get; init; } = string.Empty;
    public LocalizedText Titles { get; init; } = [];
    public PassageKind Kind { get; init; }
    public string Level { get; init; } = "A2";
    public string Topic { get; init; } = string.Empty;
    public List<PassageSegmentSeed> Segments { get; init; } = [];
}

public sealed class PassageSegmentSeed
{
    public int Id { get; init; }
    public int Order { get; init; }
    public LocalizedText Translations { get; init; } = [];
}

public sealed class GrammarTaskSeed
{
    public int Id { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Level { get; init; } = "B1";
    public string SourceText { get; init; } = string.Empty;
    public LocalizedText Instructions { get; init; } = [];
    public string MarkerRule { get; init; } = string.Empty;
}
