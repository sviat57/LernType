using Microsoft.Data.Sqlite;

namespace WortBruecke.Infrastructure.Persistence;

internal sealed class DatabaseMigrationRunner(string backupRoot)
{
    public const int LatestVersion = 3;

    public async Task MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var currentVersion = await ReadUserVersionAsync(connection, cancellationToken);
        if (currentVersion > LatestVersion)
        {
            throw new InvalidOperationException(
                $"Версия базы данных {currentVersion} новее поддерживаемой {LatestVersion}.");
        }

        string? preMigrationBackupPath = null;
        if (currentVersion < LatestVersion && await HasApplicationTablesAsync(connection, cancellationToken))
        {
            preMigrationBackupPath = await CreatePreMigrationBackupAsync(
                connection,
                currentVersion,
                cancellationToken);
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    applied_at_utc TEXT NOT NULL
                );
                """, cancellationToken);

            var recordedVersion = await ReadRecordedVersionAsync(connection, transaction, cancellationToken);
            if (recordedVersion > currentVersion)
            {
                throw new InvalidDataException(
                    $"Журнал схемы ({recordedVersion}) опережает PRAGMA user_version ({currentVersion}).");
            }

            if (currentVersion > 0 && recordedVersion < currentVersion)
            {
                for (var version = recordedVersion + 1; version <= currentVersion; version++)
                {
                    await RecordMigrationAsync(connection, transaction, version, "legacy-version-marker", cancellationToken);
                }
            }

            for (var version = currentVersion + 1; version <= LatestVersion; version++)
            {
                switch (version)
                {
                    case 1:
                        await SqliteDatabase.ApplyBaselineSchemaAsync(connection, transaction, cancellationToken);
                        await RecordMigrationAsync(connection, transaction, version, "baseline-schema", cancellationToken);
                        break;
                    case 2:
                        await ApplyDataSafetySchemaAsync(connection, transaction, cancellationToken);
                        await RecordMigrationAsync(connection, transaction, version, "data-safety-and-attempt-events", cancellationToken);
                        break;
                    case 3:
                        await ApplyAcceptedAnswersSchemaAsync(
                            connection,
                            transaction,
                            preMigrationBackupPath,
                            cancellationToken);
                        await RecordMigrationAsync(connection, transaction, version, "localized-word-accepted-answers", cancellationToken);
                        break;
                }

                await ExecuteAsync(connection, transaction, $"PRAGMA user_version = {version};", cancellationToken);
            }

            if (!await SqliteDataSafety.QuickCheckAsync(connection, cancellationToken, transaction))
            {
                throw new InvalidDataException("База данных не прошла quick_check внутри миграционной транзакции.");
            }
            if (!await SqliteDataSafety.ForeignKeyCheckAsync(connection, cancellationToken, transaction))
            {
                throw new InvalidDataException("База данных не прошла foreign_key_check внутри миграционной транзакции.");
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        if (!await SqliteDataSafety.QuickCheckAsync(connection, cancellationToken))
        {
            throw new InvalidDataException("База данных не прошла quick_check после миграции схемы.");
        }
        if (!await SqliteDataSafety.ForeignKeyCheckAsync(connection, cancellationToken))
        {
            throw new InvalidDataException("База данных не прошла foreign_key_check после миграции схемы.");
        }
    }

    private static async Task ApplyDataSafetySchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS content_identities (
                content_type TEXT NOT NULL,
                catalog_revision INTEGER NOT NULL,
                legacy_numeric_id INTEGER NOT NULL,
                semantic_key TEXT NOT NULL,
                identity_source TEXT NOT NULL,
                fingerprint TEXT NOT NULL,
                PRIMARY KEY(content_type, catalog_revision, legacy_numeric_id),
                UNIQUE(content_type, catalog_revision, semantic_key)
            );
            CREATE INDEX IF NOT EXISTS ix_content_identities_semantic_key
                ON content_identities(content_type, semantic_key, catalog_revision DESC);

            CREATE TABLE IF NOT EXISTS content_identity_migration_map (
                content_type TEXT NOT NULL,
                from_revision INTEGER NOT NULL,
                to_revision INTEGER NOT NULL,
                from_numeric_id INTEGER NOT NULL,
                to_numeric_id INTEGER NOT NULL,
                semantic_key TEXT NOT NULL,
                reason TEXT NOT NULL,
                PRIMARY KEY(content_type, from_revision, to_revision, from_numeric_id)
            );

            CREATE TABLE IF NOT EXISTS legacy_progress_quarantine (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                content_type TEXT NOT NULL,
                legacy_numeric_id INTEGER NOT NULL,
                source_catalog_revision INTEGER,
                attempt_count INTEGER NOT NULL,
                correct_count INTEGER NOT NULL,
                last_attempt_utc TEXT,
                reason TEXT NOT NULL,
                quarantined_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS attempt_events (
                event_id TEXT PRIMARY KEY,
                content_key TEXT NOT NULL,
                content_revision INTEGER NOT NULL,
                objective_id TEXT,
                level TEXT NOT NULL,
                skill TEXT NOT NULL,
                exercise_family TEXT NOT NULL,
                direction TEXT NOT NULL,
                score REAL NOT NULL CHECK(score >= 0 AND score <= 1),
                assessment_mode TEXT NOT NULL,
                started_at_utc TEXT NOT NULL,
                completed_at_utc TEXT NOT NULL,
                duration_ms INTEGER NOT NULL CHECK(duration_ms >= 0),
                session_id TEXT NOT NULL,
                rubric_version TEXT NOT NULL,
                evidence_quality TEXT NOT NULL,
                exam_id TEXT,
                module_id TEXT,
                is_timed INTEGER NOT NULL CHECK(is_timed IN (0, 1)),
                scheduler_version TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_attempt_events_completed_at
                ON attempt_events(completed_at_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_attempt_events_objective
                ON attempt_events(objective_id, completed_at_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_attempt_events_session
                ON attempt_events(session_id, completed_at_utc);
            CREATE INDEX IF NOT EXISTS ix_attempt_events_exam
                ON attempt_events(exam_id, module_id, completed_at_utc DESC);

            CREATE TABLE IF NOT EXISTS review_state (
                content_key TEXT PRIMARY KEY,
                stability_days REAL NOT NULL,
                difficulty REAL NOT NULL,
                due_at_utc TEXT NOT NULL,
                last_reviewed_at_utc TEXT NOT NULL,
                repetitions INTEGER NOT NULL CHECK(repetitions >= 0),
                lapses INTEGER NOT NULL CHECK(lapses >= 0),
                scheduler_version TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_review_state_due_at
                ON review_state(due_at_utc);

            INSERT OR IGNORE INTO content_identity_migration_map
                (content_type, from_revision, to_revision, from_numeric_id, to_numeric_id, semantic_key, reason)
            VALUES
                ('Word', 1, 2, 105, 115, 'core.word.essen.kochen', 'Revision 1 ID 105 was reused for Milch.'),
                ('Word', 1, 2, 203, 204, 'core.word.familie.schwester', 'Revision 2 reordered family words.'),
                ('Word', 1, 2, 204, 205, 'core.word.familie.bruder', 'Revision 2 reordered family words.'),
                ('Word', 1, 2, 205, 203, 'core.word.familie.eltern', 'Revision 2 reordered family words.');
            """, cancellationToken);

        if (!await HasColumnAsync(connection, transaction, "user_progress", "semantic_key", cancellationToken))
        {
            await ExecuteAsync(connection, transaction,
                "ALTER TABLE user_progress ADD COLUMN semantic_key TEXT;", cancellationToken);
        }

        if (!await HasColumnAsync(connection, transaction, "user_progress", "catalog_revision", cancellationToken))
        {
            await ExecuteAsync(connection, transaction,
                "ALTER TABLE user_progress ADD COLUMN catalog_revision INTEGER;", cancellationToken);
        }

        if (!await HasColumnAsync(connection, transaction, "user_progress", "migration_status", cancellationToken))
        {
            await ExecuteAsync(connection, transaction,
                "ALTER TABLE user_progress ADD COLUMN migration_status TEXT NOT NULL DEFAULT 'legacy_unclassified';",
                cancellationToken);
        }
    }

    private static async Task ApplyAcceptedAnswersSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? preMigrationBackupPath,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS word_accepted_answers (
                word_group_id INTEGER NOT NULL REFERENCES word_groups(id) ON DELETE CASCADE,
                lang_code TEXT NOT NULL,
                text TEXT NOT NULL,
                sort_order INTEGER NOT NULL CHECK(sort_order >= 0),
                PRIMARY KEY(word_group_id, lang_code, sort_order),
                UNIQUE(word_group_id, lang_code, text)
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
            """, cancellationToken);

        var orphanCount = await CountOrphanBookWordsAsync(connection, transaction, cancellationToken);
        if (orphanCount == 0)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(preMigrationBackupPath) || !File.Exists(preMigrationBackupPath))
        {
            throw new InvalidDataException(
                "Обнаружены слова без родительской книги, но проверенная резервная копия до миграции отсутствует.");
        }

        var backupInspection = await SqliteDataSafety.InspectAsync(preMigrationBackupPath, cancellationToken);
        if (!backupInspection.IsValid)
        {
            throw new DataMigrationValidationException(
                "Резервная копия со словами без родительской книги не прошла quick_check.",
                preMigrationBackupPath);
        }

        await ExecuteAsync(connection, transaction, """
            INSERT OR IGNORE INTO orphan_book_word_quarantine(
                legacy_word_id, missing_book_id, backup_path, reason, quarantined_at_utc)
            SELECT orphan.id, orphan.book_id, $backup_path, $reason, $quarantined_at_utc
            FROM user_book_words orphan
            LEFT JOIN user_books parent ON parent.id = orphan.book_id
            WHERE parent.id IS NULL;

            DELETE FROM user_book_words
            WHERE NOT EXISTS (
                SELECT 1 FROM user_books parent WHERE parent.id = user_book_words.book_id);
            """, cancellationToken,
            ("$backup_path", Path.GetFullPath(preMigrationBackupPath)),
            ("$reason", "Missing parent user_books row before schema-v3 migration."),
            ("$quarantined_at_utc", DateTimeOffset.UtcNow.ToString("O")));
    }

    private static async Task<long> CountOrphanBookWordsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "user_book_words", cancellationToken) ||
            !await TableExistsAsync(connection, transaction, "user_books", cancellationToken))
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM user_book_words orphan
            LEFT JOIN user_books parent ON parent.id = orphan.book_id
            WHERE parent.id IS NULL;
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<string> CreatePreMigrationBackupAsync(
        SqliteConnection connection,
        int currentVersion,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(backupRoot, "schema");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory,
            $"schema-v{currentVersion}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.db");
        SqliteDataSafety.BackupDatabase(connection, path);
        var inspection = await SqliteDataSafety.InspectAsync(path, cancellationToken);
        if (!inspection.IsValid)
        {
            throw new DataMigrationValidationException("Резервная копия перед миграцией схемы повреждена.", path);
        }
        return path;
    }

    private static async Task<bool> HasApplicationTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%');";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<int> ReadUserVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<int> ReadRecordedVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

    private static Task RecordMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        string name,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO schema_migrations(version, name, applied_at_utc)
            VALUES($version, $name, $now)
            ON CONFLICT(version) DO NOTHING;
            """, cancellationToken,
            ("$version", version), ("$name", name), ("$now", DateTimeOffset.UtcNow.ToString("O")));

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
}
