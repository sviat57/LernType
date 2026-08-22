using Microsoft.Data.Sqlite;
using WortBruecke.Core.Models;
using WortBruecke.Infrastructure.Content;
using WortBruecke.Infrastructure.Paths;

namespace WortBruecke.Infrastructure.Persistence;

public sealed class SqliteDatabase(AppPaths paths, JsonContentLoader contentLoader)
{
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = paths.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        // WAL already provides concurrent readers. Shared-cache mode adds lock coupling between
        // connections and is explicitly discouraged for modern SQLite workloads.
        Cache = SqliteCacheMode.Default,
        ForeignKeys = true,
        Pooling = false
    }.ToString();

    public string ConnectionString => _connectionString;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            await new DataRootMigrator(paths).MigrateAsync(cancellationToken);
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
                await pragma.ExecuteNonQueryAsync(cancellationToken);
            }

            await new DatabaseMigrationRunner(paths.BackupRoot).MigrateAsync(connection, cancellationToken);
            var catalog = await contentLoader.LoadAsync(paths.ContentRoot, cancellationToken);
            var currentRevision = await GetRevisionAsync(connection, cancellationToken);
            if (currentRevision != catalog.Revision)
            {
                await ImportCatalogAsync(connection, catalog, currentRevision, cancellationToken);
            }
            await new ManagedBackupService(paths).ApplyRetentionAsync(cancellationToken);
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    internal static async Task ApplyBaselineSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS themes (
                id INTEGER PRIMARY KEY,
                key TEXT NOT NULL UNIQUE,
                icon_key TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS theme_translations (
                theme_id INTEGER NOT NULL REFERENCES themes(id) ON DELETE CASCADE,
                lang_code TEXT NOT NULL,
                text TEXT NOT NULL,
                PRIMARY KEY(theme_id, lang_code)
            );
            CREATE TABLE IF NOT EXISTS word_groups (
                id INTEGER PRIMARY KEY,
                theme_id INTEGER NOT NULL REFERENCES themes(id),
                image_path TEXT NOT NULL,
                level TEXT NOT NULL,
                part_of_speech TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS word_translations (
                word_group_id INTEGER NOT NULL REFERENCES word_groups(id) ON DELETE CASCADE,
                lang_code TEXT NOT NULL,
                text TEXT NOT NULL,
                example_text TEXT,
                PRIMARY KEY(word_group_id, lang_code)
            );
            CREATE TABLE IF NOT EXISTS word_accepted_answers (
                word_group_id INTEGER NOT NULL REFERENCES word_groups(id) ON DELETE CASCADE,
                lang_code TEXT NOT NULL,
                text TEXT NOT NULL,
                sort_order INTEGER NOT NULL CHECK(sort_order >= 0),
                PRIMARY KEY(word_group_id, lang_code, sort_order),
                UNIQUE(word_group_id, lang_code, text)
            );
            CREATE TABLE IF NOT EXISTS sentence_groups (
                id INTEGER PRIMARY KEY,
                theme_id INTEGER NOT NULL REFERENCES themes(id),
                level TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sentence_translations (
                sentence_group_id INTEGER NOT NULL REFERENCES sentence_groups(id) ON DELETE CASCADE,
                lang_code TEXT NOT NULL,
                text TEXT NOT NULL,
                PRIMARY KEY(sentence_group_id, lang_code)
            );
            CREATE TABLE IF NOT EXISTS passages (
                id INTEGER PRIMARY KEY,
                key TEXT NOT NULL UNIQUE,
                kind TEXT NOT NULL,
                level TEXT NOT NULL,
                topic TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS passage_translations (
                passage_id INTEGER NOT NULL REFERENCES passages(id) ON DELETE CASCADE,
                lang_code TEXT NOT NULL,
                title TEXT NOT NULL,
                PRIMARY KEY(passage_id, lang_code)
            );
            CREATE TABLE IF NOT EXISTS passage_segments (
                id INTEGER PRIMARY KEY,
                passage_id INTEGER NOT NULL REFERENCES passages(id) ON DELETE CASCADE,
                sort_order INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS passage_segment_translations (
                segment_id INTEGER NOT NULL REFERENCES passage_segments(id) ON DELETE CASCADE,
                lang_code TEXT NOT NULL,
                text TEXT NOT NULL,
                PRIMARY KEY(segment_id, lang_code)
            );
            CREATE TABLE IF NOT EXISTS grammar_tasks (
                id INTEGER PRIMARY KEY,
                key TEXT NOT NULL UNIQUE,
                level TEXT NOT NULL,
                source_text TEXT NOT NULL,
                marker_rule TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS grammar_task_translations (
                grammar_task_id INTEGER NOT NULL REFERENCES grammar_tasks(id) ON DELETE CASCADE,
                lang_code TEXT NOT NULL,
                instruction TEXT NOT NULL,
                PRIMARY KEY(grammar_task_id, lang_code)
            );
            CREATE TABLE IF NOT EXISTS user_progress (
                content_type TEXT NOT NULL,
                content_id INTEGER NOT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                correct_count INTEGER NOT NULL DEFAULT 0,
                last_attempt_utc TEXT,
                PRIMARY KEY(content_type, content_id)
            );
            CREATE TABLE IF NOT EXISTS learning_attempts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                level TEXT NOT NULL,
                skill TEXT NOT NULL,
                exercise_type TEXT NOT NULL,
                score REAL NOT NULL CHECK(score >= 0 AND score <= 1),
                completed_at_utc TEXT NOT NULL,
                assessment_mode TEXT NOT NULL,
                was_timed INTEGER NOT NULL CHECK(was_timed IN (0, 1)),
                objective_id TEXT,
                session_id TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_learning_attempts_level_time
                ON learning_attempts(level, completed_at_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_learning_attempts_objective_time
                ON learning_attempts(objective_id, completed_at_utc DESC);
            CREATE TABLE IF NOT EXISTS user_books (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                source_culture TEXT NOT NULL,
                raw_text TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS user_book_words (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                book_id INTEGER NOT NULL REFERENCES user_books(id) ON DELETE CASCADE,
                source_text TEXT NOT NULL,
                translations_json TEXT NOT NULL,
                frequency INTEGER NOT NULL,
                context_text TEXT NOT NULL,
                part_of_speech TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS orphan_book_word_quarantine (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                legacy_word_id INTEGER NOT NULL,
                missing_book_id INTEGER NOT NULL,
                backup_path TEXT NOT NULL,
                reason TEXT NOT NULL,
                quarantined_at_utc TEXT NOT NULL,
                UNIQUE(legacy_word_id, missing_book_id, backup_path)
            );
            CREATE INDEX IF NOT EXISTS ix_user_books_identity ON user_books(source_culture, title);
            CREATE INDEX IF NOT EXISTS ix_user_book_words_book_id ON user_book_words(book_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> GetRevisionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = 'content_revision';";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string value && int.TryParse(value, out var revision) ? revision : -1;
    }

    private static async Task ImportCatalogAsync(
        SqliteConnection connection,
        ContentCatalog catalog,
        int previousRevision,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ContentIdentityMigrator.PrepareCatalogTransitionAsync(
            connection,
            (SqliteTransaction)transaction,
            previousRevision,
            catalog,
            cancellationToken);
        foreach (var table in new[]
                 {
                     "grammar_task_translations", "grammar_tasks", "passage_segment_translations", "passage_segments",
                     "passage_translations", "passages", "sentence_translations", "sentence_groups",
                     "word_accepted_answers", "word_translations", "word_groups", "theme_translations", "themes"
                 })
        {
            await ExecuteAsync(connection, transaction, $"DELETE FROM {table};", cancellationToken);
        }

        foreach (var theme in catalog.Themes)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO themes(id, key, icon_key) VALUES($id, $key, $icon);", cancellationToken,
                ("$id", theme.Id), ("$key", theme.Key), ("$icon", theme.IconKey));
            foreach (var translation in theme.Names)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO theme_translations(theme_id, lang_code, text) VALUES($id, $lang, $text);", cancellationToken,
                    ("$id", theme.Id), ("$lang", translation.Key), ("$text", translation.Value));
            }
        }

        foreach (var word in catalog.Words)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO word_groups(id, theme_id, image_path, level, part_of_speech) VALUES($id, $theme, $image, $level, $pos);",
                cancellationToken, ("$id", word.Id), ("$theme", word.ThemeId), ("$image", word.ImagePath),
                ("$level", word.Level), ("$pos", word.PartOfSpeech));
            foreach (var translation in word.Translations)
            {
                word.Examples.TryGetValue(translation.Key, out var example);
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO word_translations(word_group_id, lang_code, text, example_text) VALUES($id, $lang, $text, $example);",
                    cancellationToken, ("$id", word.Id), ("$lang", translation.Key), ("$text", translation.Value), ("$example", example));
            }
            foreach (var answerSet in word.AcceptedAnswers)
            {
                var answers = answerSet.Value
                    .Where(answer => !string.IsNullOrWhiteSpace(answer))
                    .Select(answer => answer.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                for (var index = 0; index < answers.Length; index++)
                {
                    await ExecuteAsync(connection, transaction, """
                        INSERT INTO word_accepted_answers(word_group_id, lang_code, text, sort_order)
                        VALUES($id, $lang, $text, $order);
                        """, cancellationToken,
                        ("$id", word.Id), ("$lang", answerSet.Key),
                        ("$text", answers[index]), ("$order", index));
                }
            }
        }

        foreach (var sentence in catalog.Sentences)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO sentence_groups(id, theme_id, level) VALUES($id, $theme, $level);",
                cancellationToken, ("$id", sentence.Id), ("$theme", sentence.ThemeId), ("$level", sentence.Level));
            foreach (var translation in sentence.Translations)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO sentence_translations(sentence_group_id, lang_code, text) VALUES($id, $lang, $text);",
                    cancellationToken, ("$id", sentence.Id), ("$lang", translation.Key), ("$text", translation.Value));
            }
        }

        foreach (var passage in catalog.Passages)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO passages(id, key, kind, level, topic) VALUES($id, $key, $kind, $level, $topic);", cancellationToken,
                ("$id", passage.Id), ("$key", passage.Key), ("$kind", passage.Kind.ToString()), ("$level", passage.Level), ("$topic", passage.Topic));
            foreach (var title in passage.Titles)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO passage_translations(passage_id, lang_code, title) VALUES($id, $lang, $title);", cancellationToken,
                    ("$id", passage.Id), ("$lang", title.Key), ("$title", title.Value));
            }
            foreach (var segment in passage.Segments)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO passage_segments(id, passage_id, sort_order) VALUES($id, $passage, $order);", cancellationToken,
                    ("$id", segment.Id), ("$passage", passage.Id), ("$order", segment.Order));
                foreach (var translation in segment.Translations)
                {
                    await ExecuteAsync(connection, transaction,
                        "INSERT INTO passage_segment_translations(segment_id, lang_code, text) VALUES($id, $lang, $text);", cancellationToken,
                        ("$id", segment.Id), ("$lang", translation.Key), ("$text", translation.Value));
                }
            }
        }

        foreach (var task in catalog.GrammarTasks)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO grammar_tasks(id, key, level, source_text, marker_rule) VALUES($id, $key, $level, $source, $rule);", cancellationToken,
                ("$id", task.Id), ("$key", task.Key), ("$level", task.Level), ("$source", task.SourceText), ("$rule", task.MarkerRule));
            foreach (var instruction in task.Instructions)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO grammar_task_translations(grammar_task_id, lang_code, instruction) VALUES($id, $lang, $instruction);", cancellationToken,
                    ("$id", task.Id), ("$lang", instruction.Key), ("$instruction", instruction.Value));
            }
        }

        await ExecuteAsync(connection, transaction,
            "INSERT INTO metadata(key, value) VALUES('content_revision', $revision) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
            cancellationToken, ("$revision", catalog.Revision.ToString()));
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
