using Microsoft.Data.Sqlite;
using WortBruecke.Infrastructure.Paths;

namespace WortBruecke.Infrastructure.Persistence;

public enum ManagedBackupKind
{
    Rolling,
    PreUpgrade
}

public sealed record ManagedBackupInfo(
    string Path,
    ManagedBackupKind Kind,
    DateTimeOffset LastModifiedUtc,
    long SizeBytes);

public sealed record ManagedBackupPurgeResult(
    int SanitizedBackups,
    int DeletedUnreadableBackups);

public sealed class ManagedBackupRetentionPolicy
{
    public int RollingBackupLimit { get; init; } = 3;
    public TimeSpan PreUpgradeMaximumAge { get; init; } = TimeSpan.FromDays(30);
}

public interface IManagedBackupService
{
    Task<IReadOnlyList<ManagedBackupInfo>> ListAsync(CancellationToken cancellationToken = default);

    Task<string> CreateRollingBackupAsync(CancellationToken cancellationToken = default);

    Task ApplyRetentionAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(string backupPath, CancellationToken cancellationToken = default);

    Task<ManagedBackupPurgeResult> PurgeBookDataFromManagedBackupsAsync(
        CancellationToken cancellationToken = default);

    Task<ManagedBackupPurgeResult> PurgeBookFromManagedBackupsAsync(
        long bookId,
        IReadOnlyCollection<long> bookWordIds,
        CancellationToken cancellationToken = default);
}

public sealed class ManagedBackupPurgeException : IOException
{
    public ManagedBackupPurgeException(IReadOnlyList<string> paths, Exception innerException)
        : base(
            $"Не удалось очистить {paths.Count} управляемых резервных копий от данных книги.",
            innerException)
    {
        BackupPaths = paths;
    }

    public IReadOnlyList<string> BackupPaths { get; }
}

/// <summary>
/// Owns every SQLite snapshot below AppPaths.BackupRoot. Book privacy cleanup deliberately opens
/// each database under its original basename so any associated WAL is checkpointed before rows and
/// free pages are securely removed.
/// </summary>
public sealed class ManagedBackupService : IManagedBackupService
{
    private readonly AppPaths _paths;
    private readonly ManagedBackupRetentionPolicy _policy;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ManagedBackupService(AppPaths paths, ManagedBackupRetentionPolicy? policy = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _policy = policy ?? new ManagedBackupRetentionPolicy();
        if (_policy.RollingBackupLimit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Rolling backup limit must not be negative.");
        }
        if (_policy.PreUpgradeMaximumAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Pre-upgrade retention must not be negative.");
        }
    }

    public async Task<IReadOnlyList<ManagedBackupInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return ListCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> CreateRollingBackupAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_paths.DatabasePath))
            {
                throw new FileNotFoundException("Активная база LernType ещё не создана.", _paths.DatabasePath);
            }

            Directory.CreateDirectory(_paths.RollingBackupRoot);
            var name = $"rolling-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.db";
            var destination = Path.Combine(_paths.RollingBackupRoot, name);
            var temporary = destination + ".tmp";
            try
            {
                var sourceConnectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = _paths.DatabasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString();
                await using (var source = new SqliteConnection(sourceConnectionString))
                {
                    await source.OpenAsync(cancellationToken);
                    SqliteDataSafety.BackupDatabase(source, temporary);
                }

                var inspection = await SqliteDataSafety.InspectAsync(temporary, cancellationToken);
                if (!inspection.IsValid)
                {
                    throw new DataMigrationValidationException("Новая резервная копия не прошла quick_check.", temporary);
                }

                File.Move(temporary, destination);
                await ApplyRetentionCoreAsync(DateTimeOffset.UtcNow, cancellationToken);
                return destination;
            }
            finally
            {
                DeleteFileSetBestEffort(temporary);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyRetentionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ApplyRetentionCoreAsync(DateTimeOffset.UtcNow, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var managedPath = RequireManagedDatabasePath(backupPath);
            DeleteFileSet(managedPath);
            PruneEmptyDirectories(Path.GetDirectoryName(managedPath)!);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ManagedBackupPurgeResult> PurgeBookDataFromManagedBackupsAsync(
        CancellationToken cancellationToken = default) =>
        PurgeAsync(bookId: null, [], cancellationToken);

    public Task<ManagedBackupPurgeResult> PurgeBookFromManagedBackupsAsync(
        long bookId,
        IReadOnlyCollection<long> bookWordIds,
        CancellationToken cancellationToken = default)
    {
        if (bookId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bookId));
        }
        ArgumentNullException.ThrowIfNull(bookWordIds);
        if (bookWordIds.Any(id => id <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(bookWordIds), "Book word IDs must be positive.");
        }
        return PurgeAsync(bookId, bookWordIds, cancellationToken);
    }

    private async Task<ManagedBackupPurgeResult> PurgeAsync(
        long? bookId,
        IReadOnlyCollection<long> bookWordIds,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sanitized = 0;
            var deletedUnreadable = 0;
            var failures = new List<(string Path, Exception Error)>();
            foreach (var backup in ListCore())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await PurgeDatabaseAsync(backup.Path, bookId, bookWordIds, cancellationToken);
                    sanitized++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    try
                    {
                        DeleteFileSet(backup.Path);
                        deletedUnreadable++;
                    }
                    catch (Exception deleteException) when (deleteException is IOException or UnauthorizedAccessException)
                    {
                        failures.Add((backup.Path, new AggregateException(exception, deleteException)));
                    }
                }
            }

            if (failures.Count > 0)
            {
                throw new ManagedBackupPurgeException(
                    failures.Select(item => item.Path).ToArray(),
                    new AggregateException(failures.Select(item => item.Error)));
            }

            return new ManagedBackupPurgeResult(sanitized, deletedUnreadable);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task PurgeDatabaseAsync(
        string path,
        long? bookId,
        IReadOnlyCollection<long> requestedWordIds,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteAsync(connection, null, "PRAGMA busy_timeout=5000; PRAGMA secure_delete=ON;", cancellationToken);
            await SqliteDataSafety.CheckpointWalAsync(connection, cancellationToken);
            if (!await SqliteDataSafety.QuickCheckAsync(connection, cancellationToken))
            {
                throw new InvalidDataException("Управляемая резервная копия повреждена.");
            }

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            if (bookId is null)
            {
                await PurgeAllBooksAsync(connection, transaction, cancellationToken);
            }
            else
            {
                await PurgeOneBookAsync(connection, transaction, bookId.Value, requestedWordIds, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);

            await SqliteDataSafety.CheckpointWalAsync(connection, cancellationToken);
            await ExecuteAsync(connection, null, "VACUUM;", cancellationToken);
            await SqliteDataSafety.CheckpointWalAsync(connection, cancellationToken);
            await ExecuteAsync(connection, null, "PRAGMA journal_mode=DELETE;", cancellationToken);
            if (!await SqliteDataSafety.QuickCheckAsync(connection, cancellationToken))
            {
                throw new InvalidDataException("Резервная копия повреждена после удаления данных книги.");
            }
            await VerifyPurgeAsync(connection, bookId, cancellationToken);
        }

        DeleteSidecars(path);
    }

    private static async Task PurgeAllBooksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await DeleteIfTableExistsAsync(connection, transaction, "user_book_words", null, cancellationToken);
        await DeleteIfTableExistsAsync(connection, transaction, "user_books", null, cancellationToken);
        await DeleteIfTableExistsAsync(connection, transaction, "user_progress", "content_type='BookWord'", cancellationToken);
        await DeleteIfTableExistsAsync(connection, transaction, "legacy_progress_quarantine", "content_type='BookWord'", cancellationToken);
        await DeleteIfTableExistsAsync(connection, transaction, "attempt_events", "content_key LIKE 'user.book.%' OR content_key LIKE 'user.book-word.%'", cancellationToken);
        await DeleteIfTableExistsAsync(connection, transaction, "review_state", "content_key LIKE 'user.book.%' OR content_key LIKE 'user.book-word.%'", cancellationToken);
    }

    private static async Task PurgeOneBookAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long bookId,
        IReadOnlyCollection<long> requestedWordIds,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            "CREATE TEMP TABLE IF NOT EXISTS purge_book_word_ids(id INTEGER PRIMARY KEY); DELETE FROM purge_book_word_ids;",
            cancellationToken);
        foreach (var wordId in requestedWordIds.Distinct())
        {
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO purge_book_word_ids(id) VALUES($id);", cancellationToken, ("$id", wordId));
        }
        if (await TableExistsAsync(connection, transaction, "user_book_words", cancellationToken))
        {
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO purge_book_word_ids(id) SELECT id FROM user_book_words WHERE book_id=$bookId;",
                cancellationToken, ("$bookId", bookId));
        }

        if (await TableExistsAsync(connection, transaction, "user_progress", cancellationToken))
        {
            await ExecuteAsync(connection, transaction, """
                DELETE FROM user_progress
                WHERE content_type='BookWord' AND content_id IN (SELECT id FROM purge_book_word_ids);
                """, cancellationToken);
        }
        if (await TableExistsAsync(connection, transaction, "legacy_progress_quarantine", cancellationToken))
        {
            await ExecuteAsync(connection, transaction, """
                DELETE FROM legacy_progress_quarantine
                WHERE content_type='BookWord' AND legacy_numeric_id IN (SELECT id FROM purge_book_word_ids);
                """, cancellationToken);
        }
        foreach (var table in new[] { "attempt_events", "review_state" })
        {
            if (!await TableExistsAsync(connection, transaction, table, cancellationToken))
            {
                continue;
            }
            await ExecuteAsync(connection, transaction, $"""
                DELETE FROM {table}
                WHERE content_key LIKE $bookPrefix
                   OR content_key IN (
                       SELECT 'user.book-word.' || id FROM purge_book_word_ids);
                """, cancellationToken, ("$bookPrefix", $"user.book.{bookId}.%"));
        }
        if (await TableExistsAsync(connection, transaction, "user_book_words", cancellationToken))
        {
            await ExecuteAsync(connection, transaction,
                "DELETE FROM user_book_words WHERE book_id=$bookId OR id IN (SELECT id FROM purge_book_word_ids);",
                cancellationToken, ("$bookId", bookId));
        }
        if (await TableExistsAsync(connection, transaction, "user_books", cancellationToken))
        {
            await ExecuteAsync(connection, transaction,
                "DELETE FROM user_books WHERE id=$bookId;", cancellationToken, ("$bookId", bookId));
        }
    }

    private static async Task VerifyPurgeAsync(
        SqliteConnection connection,
        long? bookId,
        CancellationToken cancellationToken)
    {
        if (bookId is null)
        {
            foreach (var (table, predicate) in new[]
                     {
                         ("user_books", "1=1"),
                         ("user_book_words", "1=1"),
                         ("user_progress", "content_type='BookWord'"),
                         ("legacy_progress_quarantine", "content_type='BookWord'"),
                         ("attempt_events", "content_key LIKE 'user.book.%' OR content_key LIKE 'user.book-word.%'"),
                         ("review_state", "content_key LIKE 'user.book.%' OR content_key LIKE 'user.book-word.%'")
                     })
            {
                if (await CountAsync(connection, table, predicate, [], cancellationToken) != 0)
                {
                    throw new InvalidDataException($"В резервной копии остались данные книги в таблице {table}.");
                }
            }
            return;
        }

        if (await CountAsync(connection, "user_books", "id=$bookId", [("$bookId", bookId.Value)], cancellationToken) != 0 ||
            await CountAsync(connection, "user_book_words", "book_id=$bookId", [("$bookId", bookId.Value)], cancellationToken) != 0 ||
            await CountAsync(connection, "user_progress",
                "content_type='BookWord' AND content_id IN (SELECT id FROM purge_book_word_ids)", [], cancellationToken) != 0 ||
            await CountAsync(connection, "legacy_progress_quarantine",
                "content_type='BookWord' AND legacy_numeric_id IN (SELECT id FROM purge_book_word_ids)", [], cancellationToken) != 0)
        {
            throw new InvalidDataException("В резервной копии остались строки или прогресс удалённой книги.");
        }
        foreach (var table in new[] { "attempt_events", "review_state" })
        {
            if (await CountAsync(connection, table, """
                    content_key LIKE $bookPrefix OR
                    content_key IN (SELECT 'user.book-word.' || id FROM purge_book_word_ids)
                    """, [("$bookPrefix", $"user.book.{bookId.Value}.%")], cancellationToken) != 0)
            {
                throw new InvalidDataException("В резервной копии осталось evidence удалённой книги.");
            }
        }
    }

    private async Task ApplyRetentionCoreAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var backups = ListCore();
        foreach (var expired in backups
                     .Where(item => item.Kind == ManagedBackupKind.Rolling)
                     .OrderByDescending(item => item.LastModifiedUtc)
                     .Skip(_policy.RollingBackupLimit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteFileSet(expired.Path);
        }

        var cutoff = now - _policy.PreUpgradeMaximumAge;
        foreach (var expired in backups.Where(item =>
                     item.Kind == ManagedBackupKind.PreUpgrade && item.LastModifiedUtc < cutoff))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteFileSet(expired.Path);
        }

        if (Directory.Exists(_paths.BackupRoot))
        {
            PruneEmptyDirectories(_paths.BackupRoot);
        }
        await Task.CompletedTask;
    }

    private IReadOnlyList<ManagedBackupInfo> ListCore()
    {
        if (!Directory.Exists(_paths.BackupRoot))
        {
            return [];
        }

        return Directory.EnumerateFiles(_paths.BackupRoot, "*.db", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".tmp.db", StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new ManagedBackupInfo(
                    info.FullName,
                    Classify(info.FullName),
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    info.Length);
            })
            .OrderByDescending(item => item.LastModifiedUtc)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private ManagedBackupKind Classify(string path)
    {
        var relative = Path.GetRelativePath(_paths.BackupRoot, path);
        var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return string.Equals(firstSegment, "rolling", StringComparison.OrdinalIgnoreCase)
            ? ManagedBackupKind.Rolling
            : ManagedBackupKind.PreUpgrade;
    }

    private string RequireManagedDatabasePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(_paths.BackupRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(fullPath), ".db", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Путь не относится к управляемым резервным копиям LernType.", nameof(path));
        }
        return fullPath;
    }

    private static async Task DeleteIfTableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string? predicate,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, table, cancellationToken))
        {
            return;
        }
        var sql = predicate is null ? $"DELETE FROM {table};" : $"DELETE FROM {table} WHERE {predicate};";
        await ExecuteAsync(connection, transaction, sql, cancellationToken);
    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        string table,
        string predicate,
        IReadOnlyCollection<(string Name, object Value)> parameters,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, null, table, cancellationToken))
        {
            return 0;
        }
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {predicate};";
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
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
        SqliteTransaction? transaction,
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

    private static void DeleteFileSet(string databasePath)
    {
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm", databasePath + "-journal" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void DeleteFileSetBestEffort(string databasePath)
    {
        try
        {
            DeleteFileSet(databasePath);
        }
        catch (IOException)
        {
            // A verified final backup or source database remains available.
        }
        catch (UnauthorizedAccessException)
        {
            // A verified final backup or source database remains available.
        }
    }

    private static void DeleteSidecars(string databasePath)
    {
        foreach (var path in new[] { databasePath + "-wal", databasePath + "-shm", databasePath + "-journal" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private void PruneEmptyDirectories(string startingDirectory)
    {
        var root = Path.GetFullPath(_paths.BackupRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetFullPath(startingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (current.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !PathsEqual(current, root))
        {
            if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
            {
                break;
            }
            Directory.Delete(current);
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
}
