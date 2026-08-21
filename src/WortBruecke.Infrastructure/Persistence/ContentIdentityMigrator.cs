using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WortBruecke.Core.Models;

namespace WortBruecke.Infrastructure.Persistence;

internal static class ContentIdentityMigrator
{
    private static readonly string[] CatalogContentTypes = ["Word", "AssessmentWord", "Sentence", "Passage", "Grammar"];

    public static async Task PrepareCatalogTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int previousRevision,
        ContentCatalog nextCatalog,
        CancellationToken cancellationToken)
    {
        if (previousRevision >= 0)
        {
            await CaptureExistingIdentitiesAsync(connection, transaction, previousRevision, cancellationToken);
        }

        await RegisterCatalogIdentitiesAsync(connection, transaction, nextCatalog, cancellationToken);
        if (previousRevision >= 0 && previousRevision != nextCatalog.Revision)
        {
            await RekeyLegacyProgressAsync(
                connection,
                transaction,
                previousRevision,
                nextCatalog.Revision,
                cancellationToken);
        }
    }

    private static async Task CaptureExistingIdentitiesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int revision,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "word_groups", cancellationToken))
        {
            return;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT w.id, t.key, w.part_of_speech,
                       COALESCE(de.text, ''), COALESCE(ru.text, '')
                FROM word_groups w
                JOIN themes t ON t.id = w.theme_id
                LEFT JOIN word_translations de ON de.word_group_id = w.id AND de.lang_code = 'de-DE'
                LEFT JOIN word_translations ru ON ru.word_group_id = w.id AND ru.lang_code = 'ru-RU';
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var identities = new List<Identity>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                var theme = reader.GetString(1);
                var partOfSpeech = reader.GetString(2);
                var german = reader.GetString(3);
                var russian = reader.GetString(4);
                var key = WordKey(theme, german, partOfSpeech, id);
                var fingerprint = Fingerprint("Word", theme, partOfSpeech, german, russian);
                identities.Add(new Identity("Word", revision, id, key, "derived-v1", fingerprint));
                identities.Add(new Identity("AssessmentWord", revision, id, key, "derived-v1", fingerprint));
            }
            await reader.DisposeAsync();
            await InsertIdentitiesAsync(connection, transaction, identities, cancellationToken);
        }

        await CaptureQueryAsync(connection, transaction, revision, """
            SELECT s.id, t.key, COALESCE(de.text, ''), COALESCE(ru.text, '')
            FROM sentence_groups s
            JOIN themes t ON t.id = s.theme_id
            LEFT JOIN sentence_translations de ON de.sentence_group_id = s.id AND de.lang_code = 'de-DE'
            LEFT JOIN sentence_translations ru ON ru.sentence_group_id = s.id AND ru.lang_code = 'ru-RU';
            """, "Sentence", (id, theme, german, russian) =>
                new Identity("Sentence", revision, id, SentenceKey(theme, german), "derived-v1",
                    Fingerprint("Sentence", theme, german, russian)), cancellationToken);

        await CaptureQueryAsync(connection, transaction, revision, """
            SELECT p.id, p.key, p.kind, p.topic FROM passages p;
            """, "Passage", (id, key, kind, topic) =>
                new Identity("Passage", revision, id, $"core.passage.{Slug(key)}", "catalog-explicit",
                    Fingerprint("Passage", key, kind, topic)), cancellationToken);

        await CaptureQueryAsync(connection, transaction, revision, """
            SELECT g.id, g.key, g.level, g.source_text FROM grammar_tasks g;
            """, "Grammar", (id, key, level, source) =>
                new Identity("Grammar", revision, id, $"core.grammar.{Slug(key)}", "catalog-explicit",
                    Fingerprint("Grammar", key, level, source)), cancellationToken);
    }

    private static async Task RegisterCatalogIdentitiesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ContentCatalog catalog,
        CancellationToken cancellationToken)
    {
        var themes = catalog.Themes.ToDictionary(theme => theme.Id, theme => theme.Key);
        var identities = new List<Identity>();
        foreach (var word in catalog.Words)
        {
            var theme = themes.GetValueOrDefault(word.ThemeId, $"theme-{word.ThemeId}");
            var german = word.Translations.GetValueOrDefault("de-DE", string.Empty);
            var russian = word.Translations.GetValueOrDefault("ru-RU", string.Empty);
            var key = WordKey(theme, german, word.PartOfSpeech, word.Id);
            var fingerprint = Fingerprint("Word", theme, word.PartOfSpeech, german, russian);
            identities.Add(new Identity("Word", catalog.Revision, word.Id, key, "derived-v1", fingerprint));
            identities.Add(new Identity("AssessmentWord", catalog.Revision, word.Id, key, "derived-v1", fingerprint));
        }

        foreach (var sentence in catalog.Sentences)
        {
            var theme = themes.GetValueOrDefault(sentence.ThemeId, $"theme-{sentence.ThemeId}");
            var german = sentence.Translations.GetValueOrDefault("de-DE", string.Empty);
            var russian = sentence.Translations.GetValueOrDefault("ru-RU", string.Empty);
            identities.Add(new Identity(
                "Sentence", catalog.Revision, sentence.Id, SentenceKey(theme, german), "derived-v1",
                Fingerprint("Sentence", theme, german, russian)));
        }

        identities.AddRange(catalog.Passages.Select(passage => new Identity(
            "Passage", catalog.Revision, passage.Id, $"core.passage.{Slug(passage.Key)}", "catalog-explicit",
            Fingerprint("Passage", passage.Key, passage.Kind.ToString(), passage.Topic))));
        identities.AddRange(catalog.GrammarTasks.Select(task => new Identity(
            "Grammar", catalog.Revision, task.Id, $"core.grammar.{Slug(task.Key)}", "catalog-explicit",
            Fingerprint("Grammar", task.Key, task.Level, task.SourceText))));

        foreach (var duplicateGroup in identities
                     .GroupBy(identity => (identity.ContentType, identity.SemanticKey))
                     .Where(group => group.Count() > 1))
        {
            foreach (var duplicate in duplicateGroup)
            {
                var index = identities.IndexOf(duplicate);
                identities[index] = duplicate with { SemanticKey = $"{duplicate.SemanticKey}.{duplicate.NumericId}" };
            }
        }

        await InsertIdentitiesAsync(connection, transaction, identities, cancellationToken);
    }

    private static async Task RekeyLegacyProgressAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int fromRevision,
        int toRevision,
        CancellationToken cancellationToken)
    {
        var records = new List<LegacyProgress>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT content_type, content_id, attempt_count, correct_count, last_attempt_utc
                FROM user_progress
                WHERE content_type IN ('Word', 'AssessmentWord', 'Sentence', 'Passage', 'Grammar');
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                records.Add(new LegacyProgress(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        if (records.Count == 0)
        {
            return;
        }

        var resolved = new List<ResolvedProgress>();
        foreach (var record in records)
        {
            var identity = await ResolveIdentityAsync(
                connection, transaction, record.ContentType, record.NumericId, fromRevision, toRevision, cancellationToken);
            if (identity is null)
            {
                await QuarantineAsync(connection, transaction, record, fromRevision,
                    "No unambiguous semantic identity exists in the target catalog.", cancellationToken);
                continue;
            }

            resolved.Add(new ResolvedProgress(record, identity.Value.NumericId, identity.Value.SemanticKey));
        }

        await ExecuteAsync(connection, transaction, """
            DELETE FROM user_progress
            WHERE content_type IN ('Word', 'AssessmentWord', 'Sentence', 'Passage', 'Grammar');
            """, cancellationToken);

        foreach (var group in resolved.GroupBy(item => (item.Source.ContentType, item.TargetNumericId, item.SemanticKey)))
        {
            var attempts = group.Sum(item => item.Source.AttemptCount);
            var correct = group.Sum(item => item.Source.CorrectCount);
            var lastAttempt = group.Select(item => item.Source.LastAttemptUtc)
                .Where(value => value is not null)
                .Max(StringComparer.Ordinal);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO user_progress(
                    content_type, content_id, attempt_count, correct_count, last_attempt_utc,
                    semantic_key, catalog_revision, migration_status)
                VALUES($type, $id, $attempts, $correct, $last, $key, $revision, 'resolved')
                ON CONFLICT(content_type, content_id) DO UPDATE SET
                    attempt_count = user_progress.attempt_count + excluded.attempt_count,
                    correct_count = user_progress.correct_count + excluded.correct_count,
                    last_attempt_utc = CASE
                        WHEN user_progress.last_attempt_utc IS NULL THEN excluded.last_attempt_utc
                        WHEN excluded.last_attempt_utc IS NULL THEN user_progress.last_attempt_utc
                        WHEN user_progress.last_attempt_utc > excluded.last_attempt_utc THEN user_progress.last_attempt_utc
                        ELSE excluded.last_attempt_utc END,
                    semantic_key = excluded.semantic_key,
                    catalog_revision = excluded.catalog_revision,
                    migration_status = 'resolved';
                """, cancellationToken,
                ("$type", group.Key.ContentType), ("$id", group.Key.TargetNumericId),
                ("$attempts", attempts), ("$correct", correct), ("$last", lastAttempt),
                ("$key", group.Key.SemanticKey), ("$revision", toRevision));
        }
    }

    private static async Task<(long NumericId, string SemanticKey)?> ResolveIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string contentType,
        long numericId,
        int fromRevision,
        int toRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT target.legacy_numeric_id, target.semantic_key
            FROM content_identities source
            JOIN content_identities target
              ON target.content_type = source.content_type
             AND target.semantic_key = source.semantic_key
             AND target.catalog_revision = $toRevision
            WHERE source.content_type = $type
              AND source.catalog_revision = $fromRevision
              AND source.legacy_numeric_id = $id
            UNION ALL
            SELECT mapping.to_numeric_id, mapping.semantic_key
            FROM content_identity_migration_map mapping
            WHERE mapping.content_type = $type
              AND mapping.from_revision = $fromRevision
              AND mapping.to_revision <= $toRevision
              AND mapping.from_numeric_id = $id
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$type", contentType);
        command.Parameters.AddWithValue("$fromRevision", fromRevision);
        command.Parameters.AddWithValue("$toRevision", toRevision);
        command.Parameters.AddWithValue("$id", numericId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        (long NumericId, string SemanticKey)? result = null;
        while (await reader.ReadAsync(cancellationToken))
        {
            var candidate = (reader.GetInt64(0), reader.GetString(1));
            if (result is not null && result.Value != candidate)
            {
                return null;
            }
            result = candidate;
        }

        return result;
    }

    private static Task QuarantineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LegacyProgress record,
        int revision,
        string reason,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO legacy_progress_quarantine(
                content_type, legacy_numeric_id, source_catalog_revision, attempt_count,
                correct_count, last_attempt_utc, reason, quarantined_at_utc)
            VALUES($type, $id, $revision, $attempts, $correct, $last, $reason, $now);
            """, cancellationToken,
            ("$type", record.ContentType), ("$id", record.NumericId), ("$revision", revision),
            ("$attempts", record.AttemptCount), ("$correct", record.CorrectCount),
            ("$last", record.LastAttemptUtc), ("$reason", reason), ("$now", DateTimeOffset.UtcNow.ToString("O")));

    private static async Task CaptureQueryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int revision,
        string sql,
        string contentType,
        Func<long, string, string, string, Identity> factory,
        CancellationToken cancellationToken)
    {
        if (!CatalogContentTypes.Contains(contentType, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(contentType));
        }

        var identities = new List<Identity>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                identities.Add(factory(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        await InsertIdentitiesAsync(connection, transaction, identities, cancellationToken);
    }

    private static async Task InsertIdentitiesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<Identity> identities,
        CancellationToken cancellationToken)
    {
        foreach (var identity in identities)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO content_identities(
                    content_type, catalog_revision, legacy_numeric_id, semantic_key, identity_source, fingerprint)
                VALUES($type, $revision, $id, $key, $source, $fingerprint)
                ON CONFLICT(content_type, catalog_revision, legacy_numeric_id) DO UPDATE SET
                    semantic_key = excluded.semantic_key,
                    identity_source = excluded.identity_source,
                    fingerprint = excluded.fingerprint;
                """, cancellationToken,
                ("$type", identity.ContentType), ("$revision", identity.Revision),
                ("$id", identity.NumericId), ("$key", identity.SemanticKey),
                ("$source", identity.Source), ("$fingerprint", identity.Fingerprint));
        }
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$table);";
        command.Parameters.AddWithValue("$table", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string WordKey(string theme, string german, string partOfSpeech, long id)
    {
        var lemma = german.Trim();
        foreach (var article in new[] { "der ", "die ", "das ", "ein ", "eine " })
        {
            if (lemma.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                lemma = lemma[article.Length..];
                break;
            }
        }

        var slug = Slug(lemma);
        return $"core.word.{Slug(theme)}.{(slug.Length > 0 ? slug : $"item-{id}")}";
    }

    private static string SentenceKey(string theme, string german)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(german))))[..12].ToLowerInvariant();
        return $"core.sentence.{Slug(theme)}.{digest}";
    }

    private static string Fingerprint(params string[] values)
    {
        var normalized = string.Join('\u001f', values.Select(Normalize));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static string Slug(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD)
            .Replace("ß", "ss", StringComparison.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        var pendingSeparator = false;
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }
                builder.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Normalize(NormalizationForm.FormKC)
            .Trim()
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed record Identity(
        string ContentType,
        int Revision,
        long NumericId,
        string SemanticKey,
        string Source,
        string Fingerprint);

    private sealed record LegacyProgress(
        string ContentType,
        long NumericId,
        int AttemptCount,
        int CorrectCount,
        string? LastAttemptUtc);

    private sealed record ResolvedProgress(LegacyProgress Source, long TargetNumericId, string SemanticKey);
}
