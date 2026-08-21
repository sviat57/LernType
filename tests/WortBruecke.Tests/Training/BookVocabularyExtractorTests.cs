using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;
using WortBruecke.Core.Training;

namespace WortBruecke.Tests.Training;

public sealed class BookVocabularyExtractorTests
{
    [Fact]
    public async Task Extract_HandlesUnicodeFrequencyAndUsesSingleBatchLookup()
    {
        var dictionary = new FakeDictionary(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["häuser"] = ["дома"],
            ["straße"] = ["улица"],
            ["apfel"] = ["яблоко"]
        });
        var extractor = new BookVocabularyExtractor(dictionary);

        var result = await extractor.ExtractAsync("Häuser, Häuser! Die Straße führt zum Apfel.", "de-DE", 20);

        Assert.Equal(1, dictionary.BatchCalls);
        Assert.Equal(2, Assert.Single(result.Items, item => item.Source.Equals("häuser", StringComparison.OrdinalIgnoreCase)).Frequency);
        Assert.Contains(result.Items, item => item.Source.Equals("straße", StringComparison.OrdinalIgnoreCase));
        Assert.All(result.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.Context)));
    }

    [Fact]
    public async Task Extract_HandlesRussianYoAndHyphenatedWords()
    {
        var dictionary = new FakeDictionary(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ёлка"] = ["der Tannenbaum"],
            ["северо-запад"] = ["der Nordwesten"]
        });
        var extractor = new BookVocabularyExtractor(dictionary);

        var result = await extractor.ExtractAsync("Ёлка и ещё одна ёлка. Северо-запад был близко.", "ru-RU", 10);

        Assert.Contains(result.Items, item => item.Source.Equals("ёлка", StringComparison.OrdinalIgnoreCase) && item.Frequency == 2);
        Assert.Contains(result.Items, item => item.Source.Equals("северо-запад", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Extract_PreservesGermanSurfaceCaseForDictionaryLookup()
    {
        var dictionary = new FakeDictionary(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Essen"] = ["еда"],
            ["essen"] = ["есть"],
            ["Arm"] = ["рука"],
            ["arm"] = ["бедный"]
        });
        var extractor = new BookVocabularyExtractor(dictionary);

        var result = await extractor.ExtractAsync("Essen und essen. Arm und arm.", "de-DE", 20);

        Assert.Equal(4, dictionary.LastBatch.Count);
        Assert.Contains("Essen", dictionary.LastBatch);
        Assert.Contains("essen", dictionary.LastBatch);
        Assert.Contains("Arm", dictionary.LastBatch);
        Assert.Contains("arm", dictionary.LastBatch);
        Assert.Contains(result.Items, item => item.Source == "Essen" && item.Translations.SequenceEqual(["еда"]));
        Assert.Contains(result.Items, item => item.Source == "essen" && item.Translations.SequenceEqual(["есть"]));
        Assert.Contains(result.Items, item => item.Source == "Arm" && item.Translations.SequenceEqual(["рука"]));
        Assert.Contains(result.Items, item => item.Source == "arm" && item.Translations.SequenceEqual(["бедный"]));
    }

    [Fact]
    public async Task Extract_RejectsOversizeAndHonorsCancellation()
    {
        var extractor = new BookVocabularyExtractor(new FakeDictionary(new Dictionary<string, string[]>()));
        await Assert.ThrowsAsync<ArgumentException>(() => extractor.ExtractAsync(new string('а', BookVocabularyExtractor.MaximumTextLength + 1), "ru-RU"));

        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ExtractAsync("Слово. Другое слово.", "ru-RU", cancellationToken: source.Token));
    }

    [Fact]
    public async Task Extract_BatchesLargeCandidateSetInsteadOfBuildingOneLargeDictionaryQuery()
    {
        var dictionary = new FakeDictionary(new Dictionary<string, string[]>());
        var extractor = new BookVocabularyExtractor(dictionary);
        var words = Enumerable.Range(0, 250).Select(ToAlphabeticWord);

        await extractor.ExtractAsync(string.Join(' ', words), "de-DE", 100);

        Assert.Equal(2, dictionary.BatchCalls);
        Assert.True(dictionary.LastBatch.Count <= 200);
    }

    private static string ToAlphabeticWord(int value)
    {
        Span<char> suffix = stackalloc char[3];
        for (var index = suffix.Length - 1; index >= 0; index--)
        {
            suffix[index] = (char)('a' + value % 26);
            value /= 26;
        }
        return "wort" + suffix.ToString();
    }

    private sealed class FakeDictionary(IReadOnlyDictionary<string, string[]> entries) : IOfflineDictionaryService
    {
        public int BatchCalls { get; private set; }
        public IReadOnlyList<string> LastBatch { get; private set; } = [];
        public string Attribution => "Test";

        public Task<DictionaryEntry?> LookupAsync(string sourceText, string sourceCulture, string targetCulture, CancellationToken cancellationToken = default) =>
            Task.FromResult<DictionaryEntry?>(null);

        public Task<IReadOnlyDictionary<string, DictionaryEntry>> LookupBatchAsync(
            IReadOnlyCollection<string> sourceTexts,
            string sourceCulture,
            string targetCulture,
            CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            LastBatch = sourceTexts.ToArray();
            var result = sourceTexts
                .Where(entries.ContainsKey)
                .ToDictionary(
                    source => source,
                    source => new DictionaryEntry(sourceCulture, targetCulture, source, entries[source], ""),
                    StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyDictionary<string, DictionaryEntry>>(result);
        }
    }
}
