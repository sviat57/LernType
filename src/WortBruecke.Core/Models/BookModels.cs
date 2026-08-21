namespace WortBruecke.Core.Models;

public sealed record DictionaryEntry(
    string SourceCulture,
    string TargetCulture,
    string Headword,
    IReadOnlyList<string> Translations,
    string PartOfSpeech);

public sealed record ExtractedVocabularyItem(
    string Source,
    IReadOnlyList<string> Translations,
    int Frequency,
    string Context,
    string PartOfSpeech,
    long Id = 0);

public sealed record VocabularyExtractionResult(
    IReadOnlyList<ExtractedVocabularyItem> Items,
    int UniqueWordCount,
    int UnresolvedWordCount);

public sealed record UserBook(
    long Id,
    string Title,
    string SourceCulture,
    string RawText,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<ExtractedVocabularyItem> Vocabulary);

/// <summary>A privacy-preserving list item: it never materializes the imported text or word contexts.</summary>
public sealed record UserBookSummary(
    long Id,
    string Title,
    string SourceCulture,
    DateTimeOffset CreatedUtc,
    int CharacterCount,
    int VocabularyCount);
