using Microsoft.Data.Sqlite;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;

namespace WortBruecke.Infrastructure.Persistence;

public sealed class SqliteContentRepository(SqliteDatabase database) : IContentRepository
{
    public async Task<IReadOnlyList<Theme>> GetThemesAsync(CancellationToken cancellationToken = default)
    {
        var themes = new Dictionary<int, ThemeBuilder>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id, t.key, t.icon_key, tt.lang_code, tt.text
            FROM themes t
            JOIN theme_translations tt ON tt.theme_id = t.id
            ORDER BY t.id, tt.lang_code;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt32(0);
            if (!themes.TryGetValue(id, out var builder))
            {
                builder = new ThemeBuilder(id, reader.GetString(1), reader.GetString(2));
                themes.Add(id, builder);
            }
            builder.Names[reader.GetString(3)] = reader.GetString(4);
        }
        return themes.Values.Select(x => new Theme(x.Id, x.Key, x.IconKey, x.Names)).ToList();
    }

    public async Task<IReadOnlyList<WordEntry>> GetWordsAsync(int? themeId = null, CancellationToken cancellationToken = default)
    {
        var words = new Dictionary<int, WordBuilder>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT w.id, w.theme_id, t.key, w.image_path, w.level, w.part_of_speech,
                   wt.lang_code, wt.text, wt.example_text
            FROM word_groups w
            JOIN themes t ON t.id = w.theme_id
            JOIN word_translations wt ON wt.word_group_id = w.id
            WHERE ($theme IS NULL OR w.theme_id = $theme)
            ORDER BY w.id, wt.lang_code;
            """;
        command.Parameters.AddWithValue("$theme", themeId is null ? DBNull.Value : themeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt32(0);
            if (!words.TryGetValue(id, out var builder))
            {
                builder = new WordBuilder(id, reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
                words.Add(id, builder);
            }
            var culture = reader.GetString(6);
            builder.Translations[culture] = reader.GetString(7);
            if (!reader.IsDBNull(8))
            {
                builder.Examples[culture] = reader.GetString(8);
            }
        }
        return words.Values.Select(x => new WordEntry(x.Id, x.ThemeId, x.ThemeKey, x.ImagePath, x.Level, x.PartOfSpeech, x.Translations, x.Examples)).ToList();
    }

    public async Task<IReadOnlyList<SentenceEntry>> GetSentencesAsync(int? themeId = null, CancellationToken cancellationToken = default)
    {
        var sentences = new Dictionary<int, SentenceBuilder>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.theme_id, t.key, s.level, st.lang_code, st.text
            FROM sentence_groups s
            JOIN themes t ON t.id = s.theme_id
            JOIN sentence_translations st ON st.sentence_group_id = s.id
            WHERE ($theme IS NULL OR s.theme_id = $theme)
            ORDER BY s.id, st.lang_code;
            """;
        command.Parameters.AddWithValue("$theme", themeId is null ? DBNull.Value : themeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt32(0);
            if (!sentences.TryGetValue(id, out var builder))
            {
                builder = new SentenceBuilder(id, reader.GetInt32(1), reader.GetString(2), reader.GetString(3));
                sentences.Add(id, builder);
            }
            builder.Translations[reader.GetString(4)] = reader.GetString(5);
        }
        return sentences.Values
            .Select(x => new SentenceEntry(x.Id, x.ThemeId, x.ThemeKey, x.Level, x.Translations))
            .ToList();
    }

    public async Task<IReadOnlyList<Passage>> GetPassagesAsync(CancellationToken cancellationToken = default)
    {
        var passages = new Dictionary<int, PassageBuilder>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT p.id, p.key, p.kind, p.level, p.topic, pt.lang_code, pt.title
                FROM passages p JOIN passage_translations pt ON pt.passage_id = p.id
                ORDER BY p.id, pt.lang_code;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt32(0);
                if (!passages.TryGetValue(id, out var builder))
                {
                    builder = new PassageBuilder(id, reader.GetString(1), Enum.Parse<PassageKind>(reader.GetString(2)), reader.GetString(3), reader.GetString(4));
                    passages.Add(id, builder);
                }
                builder.Titles[reader.GetString(5)] = reader.GetString(6);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT ps.id, ps.passage_id, ps.sort_order, pst.lang_code, pst.text
                FROM passage_segments ps
                JOIN passage_segment_translations pst ON pst.segment_id = ps.id
                ORDER BY ps.passage_id, ps.sort_order, pst.lang_code;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var passageId = reader.GetInt32(1);
                if (!passages.TryGetValue(passageId, out var passage))
                {
                    continue;
                }
                var segmentId = reader.GetInt32(0);
                if (!passage.Segments.TryGetValue(segmentId, out var segment))
                {
                    segment = new SegmentBuilder(segmentId, reader.GetInt32(2));
                    passage.Segments.Add(segmentId, segment);
                }
                segment.Translations[reader.GetString(3)] = reader.GetString(4);
            }
        }

        return passages.Values.Select(x => new Passage(
            x.Id, x.Key, x.Titles, x.Kind, x.Level, x.Topic,
            x.Segments.Values.OrderBy(s => s.Order).Select(s => new PassageSegment(s.Id, s.Order, s.Translations)).ToList())).ToList();
    }

    public async Task<IReadOnlyList<GrammarTask>> GetGrammarTasksAsync(CancellationToken cancellationToken = default)
    {
        var tasks = new Dictionary<int, GrammarTaskBuilder>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.id, g.key, g.level, g.source_text, g.marker_rule, gt.lang_code, gt.instruction
            FROM grammar_tasks g
            JOIN grammar_task_translations gt ON gt.grammar_task_id = g.id
            ORDER BY g.id, gt.lang_code;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt32(0);
            if (!tasks.TryGetValue(id, out var builder))
            {
                builder = new GrammarTaskBuilder(id, reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
                tasks.Add(id, builder);
            }
            builder.Instructions[reader.GetString(5)] = reader.GetString(6);
        }
        return tasks.Values.Select(x => new GrammarTask(x.Id, x.Key, x.Level, x.SourceText, x.Instructions, x.MarkerRule)).ToList();
    }

    private sealed record ThemeBuilder(int Id, string Key, string IconKey)
    {
        public LocalizedText Names { get; } = [];
    }

    private sealed record WordBuilder(int Id, int ThemeId, string ThemeKey, string ImagePath, string Level, string PartOfSpeech)
    {
        public LocalizedText Translations { get; } = [];
        public LocalizedText Examples { get; } = [];
    }

    private sealed record SentenceBuilder(int Id, int ThemeId, string ThemeKey, string Level)
    {
        public LocalizedText Translations { get; } = [];
    }

    private sealed record PassageBuilder(int Id, string Key, PassageKind Kind, string Level, string Topic)
    {
        public LocalizedText Titles { get; } = [];
        public Dictionary<int, SegmentBuilder> Segments { get; } = [];
    }

    private sealed record SegmentBuilder(int Id, int Order)
    {
        public LocalizedText Translations { get; } = [];
    }

    private sealed record GrammarTaskBuilder(int Id, string Key, string Level, string SourceText, string MarkerRule)
    {
        public LocalizedText Instructions { get; } = [];
    }
}
