using Microsoft.Data.Sqlite;
using System.Text;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;
using WortBruecke.Core.Training;

namespace WortBruecke.Infrastructure.Dictionary;

public sealed class FreeDictOfflineDictionaryService(string databasePath) : IOfflineDictionaryService
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Cache = SqliteCacheMode.Shared,
        Pooling = true
    }.ToString();

    public string Attribution => "FreeDict+WikDict 2025.11.23 · CC BY-SA 3.0";

    public async Task<DictionaryEntry?> LookupAsync(
        string sourceText,
        string sourceCulture,
        string targetCulture,
        CancellationToken cancellationToken = default)
    {
        var result = await LookupBatchAsync([sourceText], sourceCulture, targetCulture, cancellationToken);
        return result.TryGetValue(sourceText, out var entry) ? entry : null;
    }

    public async Task<IReadOnlyDictionary<string, DictionaryEntry>> LookupBatchAsync(
        IReadOnlyCollection<string> sourceTexts,
        string sourceCulture,
        string targetCulture,
        CancellationToken cancellationToken = default)
    {
        if (sourceTexts.Count == 0 || !File.Exists(databasePath))
        {
            return new Dictionary<string, DictionaryEntry>(StringComparer.Ordinal);
        }

        var requested = sourceTexts
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => DictionaryKeyNormalizer.Normalize(value, sourceCulture), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var buildersByLookupKey = new Dictionary<string, HeadwordGroup>(StringComparer.Ordinal);
        var keys = requested.Keys.ToArray();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        for (var offset = 0; offset < keys.Length; offset += 400)
        {
            var batch = keys.Skip(offset).Take(400).ToArray();
            await using var command = connection.CreateCommand();
            var parameterNames = new List<string>(batch.Length);
            for (var index = 0; index < batch.Length; index++)
            {
                var name = $"$key{index}";
                parameterNames.Add(name);
                command.Parameters.AddWithValue(name, batch[index]);
            }
            command.Parameters.AddWithValue("$source", sourceCulture);
            command.Parameters.AddWithValue("$target", targetCulture);
            command.CommandText = $"""
                SELECT lookup_key, headword, translation, part_of_speech
                FROM dictionary_entries
                WHERE source_culture = $source AND target_culture = $target
                  AND lookup_key IN ({string.Join(",", parameterNames)})
                ORDER BY lookup_key, id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = reader.GetString(0);
                if (!buildersByLookupKey.TryGetValue(key, out var headwordGroup))
                {
                    headwordGroup = new HeadwordGroup();
                    buildersByLookupKey.Add(key, headwordGroup);
                }
                var headword = reader.GetString(1);
                var builder = headwordGroup.GetOrAdd(headword, reader.GetString(3));
                if (builder.Translations.Count < 12)
                {
                    builder.Translations.Add(reader.GetString(2));
                }
            }
        }

        var result = new Dictionary<string, DictionaryEntry>(StringComparer.Ordinal);
        foreach (var pair in requested)
        {
            if (!buildersByLookupKey.TryGetValue(pair.Key, out var headwordGroup))
            {
                continue;
            }
            foreach (var original in pair.Value)
            {
                var builder = headwordGroup.FindBestMatch(original);
                var entry = new DictionaryEntry(sourceCulture, targetCulture, builder.Headword,
                    builder.Translations.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), builder.PartOfSpeech);
                result[original] = entry;
            }
        }
        return result;
    }

    private sealed class HeadwordGroup
    {
        private readonly Dictionary<string, EntryBuilder> _byExactHeadword = new(StringComparer.Ordinal);
        private readonly List<EntryBuilder> _inSourceOrder = [];

        public EntryBuilder GetOrAdd(string headword, string partOfSpeech)
        {
            if (_byExactHeadword.TryGetValue(headword, out var builder))
            {
                return builder;
            }

            builder = new EntryBuilder(headword, partOfSpeech);
            _byExactHeadword.Add(headword, builder);
            _inSourceOrder.Add(builder);
            return builder;
        }

        public EntryBuilder FindBestMatch(string requestedSurface)
        {
            var exactSurface = NormalizeSurface(requestedSurface);
            return _byExactHeadword.TryGetValue(exactSurface, out var exact)
                ? exact
                : _inSourceOrder[0];
        }
    }

    private static string NormalizeSurface(string value) => value
        .Trim()
        .Trim('.', ',', ';', ':', '!', '?', '"', '„', '“', '«', '»', '(', ')', '[', ']')
        .Normalize(NormalizationForm.FormC);

    private sealed record EntryBuilder(string Headword, string PartOfSpeech)
    {
        public List<string> Translations { get; } = [];
    }
}
