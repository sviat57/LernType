using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;

namespace WortBruecke.Core.Training;

public sealed partial class BookVocabularyExtractor(IOfflineDictionaryService dictionary) : IBookVocabularyExtractor
{
    public const int MaximumTextLength = 500_000;

    private static readonly HashSet<string> RussianStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "а", "без", "бы", "был", "была", "были", "было", "в", "во", "вы", "да", "для", "до", "его", "ее", "её",
        "если", "есть", "же", "за", "и", "из", "или", "им", "их", "к", "как", "ко", "когда", "ли", "мне", "мы",
        "на", "не", "но", "о", "об", "он", "она", "они", "от", "по", "при", "с", "со", "так", "то", "у", "что", "это", "я"
    };

    private static readonly HashSet<string> GermanStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "aber", "als", "am", "an", "auch", "auf", "aus", "bei", "bin", "bis", "da", "das", "dass", "dem", "den", "der",
        "des", "die", "doch", "du", "ein", "eine", "einem", "einen", "einer", "er", "es", "für", "hat", "ich", "im", "in",
        "ist", "mit", "nach", "nicht", "noch", "oder", "sich", "sie", "so", "und", "vom", "von", "war", "was", "wenn", "wie", "wir", "zu", "zum", "zur"
    };

    public async Task<VocabularyExtractionResult> ExtractAsync(
        string text,
        string sourceCulture,
        int maximumItems = 40,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new VocabularyExtractionResult([], 0, 0);
        }
        if (text.Length > MaximumTextLength)
        {
            throw new ArgumentException($"Текст длиннее допустимых {MaximumTextLength:N0} символов.", nameof(text));
        }

        maximumItems = Math.Clamp(maximumItems, 5, 100);
        var culture = CultureInfo.GetCultureInfo(sourceCulture);
        var preserveCase = sourceCulture.StartsWith("de", StringComparison.OrdinalIgnoreCase);
        var stopWords = preserveCase ? GermanStopWords : RussianStopWords;
        var occurrences = new Dictionary<string, Occurrence>(StringComparer.Ordinal);

        foreach (var sentence in SentencePattern().Split(text).Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compactContext = CollapseWhitespace().Replace(sentence.Trim(), " ");
            if (compactContext.Length > 180)
            {
                compactContext = compactContext[..177] + "…";
            }

            var matchIndex = 0;
            foreach (Match match in WordPattern().Matches(sentence))
            {
                if ((matchIndex++ & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var surface = match.Value.Trim('—', '–', '-', '\'', '’').Normalize(NormalizationForm.FormC);
                var occurrenceKey = preserveCase ? surface : surface.ToLower(culture);
                if (occurrenceKey.Length < 2 || stopWords.Contains(occurrenceKey))
                {
                    continue;
                }

                if (occurrences.TryGetValue(occurrenceKey, out var existing))
                {
                    occurrences[occurrenceKey] = existing with { Frequency = existing.Frequency + 1 };
                }
                else
                {
                    occurrences[occurrenceKey] = new Occurrence(surface, 1, compactContext);
                }
            }
        }

        var candidates = occurrences
            .OrderByDescending(pair => pair.Value.Frequency)
            .ThenBy(pair => pair.Key, StringComparer.Create(culture, true))
            .Take(maximumItems * 4)
            .ToArray();
        var targetCulture = sourceCulture.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? "ru-RU" : "de-DE";
        var found = new Dictionary<string, DictionaryEntry>(StringComparer.Ordinal);
        foreach (var batch in candidates.Select(pair => pair.Value.Surface).Chunk(200))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchResult = await dictionary.LookupBatchAsync(batch, sourceCulture, targetCulture, cancellationToken);
            foreach (var pair in batchResult)
            {
                found[pair.Key] = pair.Value;
            }
        }

        var items = candidates
            .Where(pair => found.ContainsKey(pair.Value.Surface))
            .Take(maximumItems)
            .Select(pair =>
            {
                var entry = found[pair.Value.Surface];
                return new ExtractedVocabularyItem(
                    entry.Headword,
                    entry.Translations,
                    pair.Value.Frequency,
                    pair.Value.Context,
                    entry.PartOfSpeech);
            })
            .ToList();

        return new VocabularyExtractionResult(items, occurrences.Count, Math.Max(0, occurrences.Count - items.Count));
    }

    private sealed record Occurrence(string Surface, int Frequency, string Context);

    [GeneratedRegex(@"[\p{L}][\p{L}\p{M}’'\-]{1,48}", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"(?<=[.!?…])\s+|[\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex SentencePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespace();
}
