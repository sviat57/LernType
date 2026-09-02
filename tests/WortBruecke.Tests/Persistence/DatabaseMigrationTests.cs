using Microsoft.Data.Sqlite;
using WortBruecke.Infrastructure.Content;
using WortBruecke.Infrastructure.Paths;
using WortBruecke.Infrastructure.Persistence;

namespace WortBruecke.Tests.Persistence;

public sealed class DatabaseMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LernTypeSchemaTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InitializeAsync_RecordsVersionedMigrationsAndCanonicalAttemptSchema()
    {
        var database = await CreateDatabaseAsync();

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(4L, await ScalarAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(4L, await ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(20L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('attempt_events');"));
        Assert.Equal(4L, await ScalarAsync(connection, """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type='index' AND name IN (
                'ix_attempt_events_completed_at', 'ix_attempt_events_objective',
                'ix_attempt_events_session', 'ix_attempt_events_exam');
            """));
        Assert.Equal(4L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM content_identity_migration_map WHERE from_revision=1 AND to_revision=2;"));
        Assert.Equal(8L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('review_state');"));
        Assert.Equal(1L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_review_state_due_at';"));
        Assert.Equal(1L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='word_accepted_answers';"));
        Assert.Equal(1L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='orphan_book_word_quarantine';"));
        Assert.Equal(1L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM schema_migrations WHERE version=3 AND name='localized-word-accepted-answers';"));
        Assert.Equal(1L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM schema_migrations WHERE version=4 AND name='offline-course-progress';"));
        Assert.Equal(1L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='course_progress';"));
        Assert.Equal(1L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='course_resume_state';"));
        Assert.Equal(7L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('course_resume_state');"));
        Assert.Equal(2L, await ScalarAsync(connection, """
            SELECT COUNT(*) FROM pragma_table_info('course_resume_state')
            WHERE (name='task_scores_json' AND dflt_value='''{}''')
               OR (name='self_reported_task_keys_json' AND dflt_value='''[]''');
            """));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task InitializeAsync_UpgradesVersionThreeWithValidatedResumeSnapshotColumns()
    {
        var database = await CreateDatabaseAsync();
        await using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE course_progress;
                DROP TABLE course_resume_state;
                DELETE FROM schema_migrations WHERE version=4;
                PRAGMA user_version=3;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await database.InitializeAsync();

        await using var verification = new SqliteConnection(database.ConnectionString);
        await verification.OpenAsync();
        Assert.Equal(4L, await ScalarAsync(verification, "PRAGMA user_version;"));
        Assert.Equal(7L, await ScalarAsync(verification, "SELECT COUNT(*) FROM pragma_table_info('course_resume_state');"));
        await using (var insert = verification.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO course_resume_state(course_id, unit_id, lesson_id, step_id, updated_at_utc)
                VALUES('a1', 'a1-u1', 'a1-01', 'briefing', '2026-08-31T00:00:00.0000000+00:00');
                """;
            await insert.ExecuteNonQueryAsync();
        }
        Assert.Equal("{}", await TextScalarAsync(verification,
            "SELECT task_scores_json FROM course_resume_state WHERE course_id='a1';"));
        Assert.Equal("[]", await TextScalarAsync(verification,
            "SELECT self_reported_task_keys_json FROM course_resume_state WHERE course_id='a1';"));
        await using var invalidJson = verification.CreateCommand();
        invalidJson.CommandText = """
            UPDATE course_resume_state
            SET task_scores_json='[]'
            WHERE course_id='a1';
            """;
        await Assert.ThrowsAsync<SqliteException>(() => invalidJson.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotentAndDoesNotDuplicateMigrationHistory()
    {
        var database = await CreateDatabaseAsync();

        await database.InitializeAsync();

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(4L, await ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal("ok", await TextScalarAsync(connection, "PRAGMA quick_check;"));
    }

    [Fact]
    public async Task InitializeAsync_UpgradesPublishedUnversionedDatabaseAndCreatesVerifiedBackup()
    {
        var contentRoot = await WriteMinimalCatalogAsync();
        var dataRoot = Path.Combine(_root, "Data");
        Directory.CreateDirectory(dataRoot);
        var databasePath = Path.Combine(dataRoot, "lerntype.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO metadata VALUES('content_revision', '1');
                CREATE TABLE user_progress(
                    content_type TEXT NOT NULL, content_id INTEGER NOT NULL,
                    attempt_count INTEGER NOT NULL, correct_count INTEGER NOT NULL,
                    last_attempt_utc TEXT, PRIMARY KEY(content_type, content_id));
                INSERT INTO user_progress VALUES('BookWord', 42, 3, 2, '2026-08-20T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }
        var database = new SqliteDatabase(new AppPaths(contentRoot, dataRoot), new JsonContentLoader());

        await database.InitializeAsync();

        var backup = Assert.Single(Directory.GetFiles(Path.Combine(dataRoot, "Backups", "schema"), "*.db"));
        await using var backupConnection = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False");
        await backupConnection.OpenAsync();
        Assert.Equal("ok", await TextScalarAsync(backupConnection, "PRAGMA quick_check;"));
        Assert.Equal(1L, await ScalarAsync(backupConnection, "SELECT COUNT(*) FROM user_progress;"));
        await using var current = new SqliteConnection(database.ConnectionString);
        await current.OpenAsync();
        Assert.Equal(4L, await ScalarAsync(current, "PRAGMA user_version;"));
        Assert.Equal(1L, await ScalarAsync(current, "SELECT COUNT(*) FROM user_progress WHERE content_type='BookWord';"));
    }

    [Fact]
    public async Task InitializeAsync_UpgradesVersionTwoWithoutChangingProgressOrBooks()
    {
        var database = await CreateDatabaseAsync();
        await using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO user_books(title, source_culture, raw_text, created_utc)
                VALUES('Книга', 'ru-RU', 'Текст', '2026-08-20T00:00:00Z');
                INSERT INTO user_progress(content_type, content_id, attempt_count, correct_count, last_attempt_utc,
                                          semantic_key, catalog_revision, migration_status)
                VALUES('BookWord', 42, 7, 5, '2026-08-20T00:00:00Z', NULL, NULL, 'active');
                DROP TABLE word_accepted_answers;
                DROP TABLE course_progress;
                DROP TABLE course_resume_state;
                DELETE FROM schema_migrations WHERE version>=3;
                PRAGMA user_version=2;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await database.InitializeAsync();

        await using var verification = new SqliteConnection(database.ConnectionString);
        await verification.OpenAsync();
        Assert.Equal(4L, await ScalarAsync(verification, "PRAGMA user_version;"));
        Assert.Equal(1L, await ScalarAsync(verification, "SELECT COUNT(*) FROM user_books WHERE title='Книга';"));
        Assert.Equal(7L, await ScalarAsync(verification,
            "SELECT attempt_count FROM user_progress WHERE content_type='BookWord' AND content_id=42;"));
        Assert.Equal(1L, await ScalarAsync(verification,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='word_accepted_answers';"));
    }

    [Fact]
    public async Task InitializeAsync_VersionTwoQuarantinesOrphanBookWordsAfterVerifiedBackup()
    {
        var database = await CreateDatabaseAsync();
        await using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys=OFF;

                INSERT INTO user_books(id, title, source_culture, raw_text, created_utc)
                VALUES(1, 'Valid book', 'de-DE', 'Valid raw text', '2026-08-20T00:00:00Z');
                INSERT INTO user_book_words(
                    id, book_id, source_text, translations_json, frequency, context_text, part_of_speech)
                VALUES
                    (11, 1, 'valid', '["действительный"]', 2, 'Valid context', 'adjective'),
                    (22, 2, 'recoverable', '["восстанавливаемый"]', 3, 'Recoverable context', 'adjective');

                INSERT INTO user_progress(
                    content_type, content_id, attempt_count, correct_count, last_attempt_utc,
                    semantic_key, catalog_revision, migration_status)
                VALUES
                    ('BookWord', 11, 7, 5, '2026-08-20T00:00:00Z', NULL, NULL, 'active'),
                    ('BookWord', 22, 4, 3, '2026-08-21T00:00:00Z', NULL, NULL, 'active');

                INSERT INTO attempt_events(
                    event_id, content_key, content_revision, objective_id, level, skill,
                    exercise_family, direction, score, assessment_mode, started_at_utc,
                    completed_at_utc, duration_ms, session_id, rubric_version, evidence_quality,
                    exam_id, module_id, is_timed, scheduler_version)
                VALUES
                    ('00000000-0000-0000-0000-000000000011', 'user.book.1.word.valid', 1, 'book.custom.vocabulary',
                     'A1', 'Vocabulary', 'BidirectionalTranslation', 'GermanToRussian', 1, 'Practice',
                     '2026-08-20T00:00:00Z', '2026-08-20T00:00:01Z', 1000,
                     '10000000-0000-0000-0000-000000000011', 'exact-answer-v2', 'Deterministic',
                     NULL, NULL, 0, 'fsrs-like-v1'),
                    ('00000000-0000-0000-0000-000000000022', 'user.book.2.word.recoverable', 1, 'book.custom.vocabulary',
                     'A1', 'Vocabulary', 'BidirectionalTranslation', 'GermanToRussian', 1, 'Practice',
                     '2026-08-21T00:00:00Z', '2026-08-21T00:00:01Z', 1000,
                     '10000000-0000-0000-0000-000000000022', 'exact-answer-v2', 'Deterministic',
                     NULL, NULL, 0, 'fsrs-like-v1');

                INSERT INTO review_state(
                    content_key, stability_days, difficulty, due_at_utc, last_reviewed_at_utc,
                    repetitions, lapses, scheduler_version)
                VALUES
                    ('user.book.1.word.valid', 3, 4, '2026-08-23T00:00:00Z', '2026-08-20T00:00:01Z', 1, 0, 'fsrs-like-v1'),
                    ('user.book.2.word.recoverable', 3, 4, '2026-08-24T00:00:00Z', '2026-08-21T00:00:01Z', 1, 0, 'fsrs-like-v1');

                DROP TABLE word_accepted_answers;
                DROP TABLE orphan_book_word_quarantine;
                DROP TABLE course_progress;
                DROP TABLE course_resume_state;
                DELETE FROM schema_migrations WHERE version>=3;
                PRAGMA user_version=2;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await database.InitializeAsync();

        var databasePath = new SqliteConnectionStringBuilder(database.ConnectionString).DataSource;
        var backup = Assert.Single(Directory.GetFiles(
            Path.Combine(Path.GetDirectoryName(databasePath)!, "Backups", "schema"),
            "schema-v2-*.db"));
        await using (var backupConnection = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False"))
        {
            await backupConnection.OpenAsync();
            Assert.Equal("ok", await TextScalarAsync(backupConnection, "PRAGMA quick_check;"));
            Assert.Equal(1L, await ScalarAsync(backupConnection, "SELECT COUNT(*) FROM user_books WHERE id=1;"));
            Assert.Equal(1L, await ScalarAsync(backupConnection, "SELECT COUNT(*) FROM user_book_words WHERE id=11;"));
            Assert.Equal(1L, await ScalarAsync(backupConnection, "SELECT COUNT(*) FROM user_book_words WHERE id=22;"));
        }

        await using var verification = new SqliteConnection(database.ConnectionString);
        await verification.OpenAsync();
        Assert.Equal(4L, await ScalarAsync(verification, "PRAGMA user_version;"));
        Assert.Equal(0L, await ScalarAsync(verification, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(1L, await ScalarAsync(verification, "SELECT COUNT(*) FROM user_books WHERE id=1;"));
        Assert.Equal(1L, await ScalarAsync(verification, "SELECT COUNT(*) FROM user_book_words WHERE id=11;"));
        Assert.Equal(0L, await ScalarAsync(verification, "SELECT COUNT(*) FROM user_book_words WHERE id=22;"));
        Assert.Equal(1L, await ScalarAsync(verification,
            "SELECT COUNT(*) FROM orphan_book_word_quarantine WHERE legacy_word_id=22 AND missing_book_id=2;"));
        Assert.Equal(Path.GetFullPath(backup), await TextScalarAsync(verification,
            "SELECT backup_path FROM orphan_book_word_quarantine WHERE legacy_word_id=22;"));
        Assert.Equal(0L, await ScalarAsync(verification, """
            SELECT COUNT(*) FROM pragma_table_info('orphan_book_word_quarantine')
            WHERE name IN ('source_text', 'translations_json', 'context_text', 'raw_text');
            """));
        Assert.Equal(2L, await ScalarAsync(verification,
            "SELECT COUNT(*) FROM user_progress WHERE content_type='BookWord' AND content_id IN (11, 22);"));
        Assert.Equal(11L, await ScalarAsync(verification,
            "SELECT SUM(attempt_count) FROM user_progress WHERE content_type='BookWord' AND content_id IN (11, 22);"));
        Assert.Equal(2L, await ScalarAsync(verification,
            "SELECT COUNT(*) FROM attempt_events WHERE content_key LIKE 'user.book.%';"));
        Assert.Equal(2L, await ScalarAsync(verification,
            "SELECT COUNT(*) FROM review_state WHERE content_key LIKE 'user.book.%';"));
    }

    [Fact]
    public async Task InitializeAsync_RejectsFutureSchemaVersionWithoutDowngrade()
    {
        var contentRoot = await WriteMinimalCatalogAsync();
        var dataRoot = Path.Combine(_root, "FutureData");
        Directory.CreateDirectory(dataRoot);
        var path = Path.Combine(dataRoot, "lerntype.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=99; CREATE TABLE future_marker(value TEXT);";
            await command.ExecuteNonQueryAsync();
        }
        var database = new SqliteDatabase(new AppPaths(contentRoot, dataRoot), new JsonContentLoader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.InitializeAsync());

        await using var verification = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await verification.OpenAsync();
        Assert.Equal(99L, await ScalarAsync(verification, "PRAGMA user_version;"));
        Assert.Equal(1L, await ScalarAsync(verification,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='future_marker';"));
    }

    private async Task<SqliteDatabase> CreateDatabaseAsync()
    {
        var contentRoot = await WriteMinimalCatalogAsync();
        var dataRoot = Path.Combine(_root, $"Data-{Guid.NewGuid():N}");
        var database = new SqliteDatabase(new AppPaths(contentRoot, dataRoot), new JsonContentLoader());
        await database.InitializeAsync();
        return database;
    }

    private async Task<string> WriteMinimalCatalogAsync()
    {
        var contentRoot = Path.Combine(_root, "Content");
        Directory.CreateDirectory(contentRoot);
        await File.WriteAllTextAsync(Path.Combine(contentRoot, "catalog.json"), """
            {
              "revision": 1,
              "themes": [{ "id": 1, "key": "home", "iconKey": "home", "names": { "ru-RU": "Дом", "de-DE": "Haus" } }],
              "words": [{ "id": 1, "themeId": 1, "imagePath": "1.png", "level": "A1", "partOfSpeech": "noun", "translations": { "ru-RU": "дом", "de-DE": "das Haus" }, "examples": {} }],
              "sentences": [], "passages": [], "grammarTasks": []
            }
            """);
        return contentRoot;
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> TextScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
