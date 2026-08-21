using System.Text;
using Microsoft.Data.Sqlite;
using WortBruecke.Core.Models;
using WortBruecke.Infrastructure.Content;
using WortBruecke.Infrastructure.Paths;
using WortBruecke.Infrastructure.Persistence;

namespace WortBruecke.Tests.Persistence;

public sealed class ManagedBackupServiceTests : IDisposable
{
    private const string DeletedSecret = "PRIVATE-BOOK-TEXT-7E642A986C";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LernTypeBackupTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateRollingBackupAsync_KeepsOnlyThreeNewestVerifiedSnapshots()
    {
        var (paths, _) = await CreateDatabaseAsync();
        var service = new ManagedBackupService(paths);

        for (var index = 0; index < 5; index++)
        {
            await service.CreateRollingBackupAsync();
        }

        var backups = await service.ListAsync();
        Assert.Equal(3, backups.Count(item => item.Kind == ManagedBackupKind.Rolling));
        foreach (var backup in backups)
        {
            await using var connection = new SqliteConnection($"Data Source={backup.Path};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            Assert.Equal("ok", Convert.ToString(await command.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task ApplyRetentionAsync_RemovesExpiredPreUpgradeButKeepsRecentSnapshot()
    {
        var (paths, _) = await CreateDatabaseAsync();
        var service = new ManagedBackupService(paths);
        var rolling = await service.CreateRollingBackupAsync();
        Directory.CreateDirectory(paths.PreUpgradeBackupRoot);
        var expired = Path.Combine(paths.PreUpgradeBackupRoot, "expired.db");
        var recent = Path.Combine(paths.PreUpgradeBackupRoot, "recent.db");
        File.Copy(rolling, expired);
        File.Copy(rolling, recent);
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-31));
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-29));

        await service.ApplyRetentionAsync();

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public async Task DeleteAsync_RejectsPathsOutsideManagedRoot()
    {
        var (paths, _) = await CreateDatabaseAsync();
        var service = new ManagedBackupService(paths);
        var backup = await service.CreateRollingBackupAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteAsync(paths.DatabasePath));
        await service.DeleteAsync(backup);

        Assert.False(File.Exists(backup));
        Assert.Empty(await service.ListAsync());
    }

    [Fact]
    public async Task PurgeBookDataFromManagedBackupsAsync_ScrubsRowsFreePagesWalAndUnreadableFiles()
    {
        var (paths, database) = await CreateDatabaseAsync();
        await SeedBooksAsync(database, includeSecondBook: false);
        var service = new ManagedBackupService(paths);
        var rolling = await service.CreateRollingBackupAsync();
        Directory.CreateDirectory(Path.Combine(paths.BackupRoot, "schema"));
        var schemaBackup = Path.Combine(paths.BackupRoot, "schema", "pre-upgrade.db");
        File.Copy(rolling, schemaBackup);
        var unreadable = Path.Combine(paths.BackupRoot, "schema", "unreadable.db");
        await File.WriteAllTextAsync(unreadable, DeletedSecret);

        var result = await service.PurgeBookDataFromManagedBackupsAsync();

        Assert.Equal(2, result.SanitizedBackups);
        Assert.Equal(1, result.DeletedUnreadableBackups);
        Assert.False(File.Exists(unreadable));
        foreach (var backup in new[] { rolling, schemaBackup })
        {
            Assert.Equal(0, await CountAsync(backup, "user_books"));
            Assert.Equal(0, await CountAsync(backup, "user_book_words"));
            Assert.Equal(0, await CountAsync(backup, "user_progress", "content_type='BookWord'"));
            Assert.False(ContainsBytes(await File.ReadAllBytesAsync(backup), Encoding.UTF8.GetBytes(DeletedSecret)));
            Assert.False(File.Exists(backup + "-wal"));
            Assert.False(File.Exists(backup + "-shm"));
        }
    }

    [Fact]
    public async Task PurgeBookFromManagedBackupsAsync_RemovesOnlySelectedBookAndItsProgress()
    {
        var (paths, database) = await CreateDatabaseAsync();
        await SeedBooksAsync(database, includeSecondBook: true);
        var service = new ManagedBackupService(paths);
        var backup = await service.CreateRollingBackupAsync();

        var result = await service.PurgeBookFromManagedBackupsAsync(1, [11]);

        Assert.Equal(1, result.SanitizedBackups);
        Assert.Equal(0, await CountAsync(backup, "user_books", "id=1"));
        Assert.Equal(1, await CountAsync(backup, "user_books", "id=2"));
        Assert.Equal(0, await CountAsync(backup, "user_progress", "content_type='BookWord' AND content_id=11"));
        Assert.Equal(1, await CountAsync(backup, "user_progress", "content_type='BookWord' AND content_id=22"));
        Assert.False(ContainsBytes(await File.ReadAllBytesAsync(backup), Encoding.UTF8.GetBytes(DeletedSecret)));
        Assert.True(ContainsBytes(await File.ReadAllBytesAsync(backup), Encoding.UTF8.GetBytes("KEEP-SECOND-BOOK")));
    }

    [Fact]
    public async Task DeleteAllAsync_WithManagedService_RemovesRawTextFromActiveDatabaseAndEveryBackup()
    {
        var (paths, database) = await CreateDatabaseAsync();
        var service = new ManagedBackupService(paths);
        var books = new SqliteBookRepository(database, service);
        await books.SaveAsync("Private", "de-DE", DeletedSecret,
        [
            new ExtractedVocabularyItem("privat", ["частный"], 1, DeletedSecret, "adjective")
        ]);
        var backup = await service.CreateRollingBackupAsync();

        Assert.Equal(1, await books.DeleteAllAsync());

        Assert.Equal(0, await CountAsync(paths.DatabasePath, "user_books"));
        Assert.Equal(0, await CountAsync(backup, "user_books"));
        Assert.False(ContainsBytes(await File.ReadAllBytesAsync(paths.DatabasePath), Encoding.UTF8.GetBytes(DeletedSecret)));
        Assert.False(ContainsBytes(await File.ReadAllBytesAsync(backup), Encoding.UTF8.GetBytes(DeletedSecret)));
    }

    private async Task<(AppPaths Paths, SqliteDatabase Database)> CreateDatabaseAsync()
    {
        var contentRoot = Path.Combine(_root, "Content");
        Directory.CreateDirectory(contentRoot);
        await File.WriteAllTextAsync(Path.Combine(contentRoot, "catalog.json"), """
            {
              "revision": 1,
              "themes": [{ "id": 1, "key": "base", "iconKey": "base", "names": { "ru-RU": "База", "de-DE": "Basis" } }],
              "words": [], "sentences": [], "passages": [], "grammarTasks": []
            }
            """);
        var paths = new AppPaths(contentRoot, Path.Combine(_root, "Data"));
        var database = new SqliteDatabase(paths, new JsonContentLoader());
        await database.InitializeAsync();
        return (paths, database);
    }

    private static async Task SeedBooksAsync(SqliteDatabase database, bool includeSecondBook)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO user_books(id, title, source_culture, raw_text, created_utc)
            VALUES(1, 'Private', 'ru-RU', $secret, '2026-08-20T00:00:00Z');
            INSERT INTO user_book_words(id, book_id, source_text, translations_json, frequency, context_text, part_of_speech)
            VALUES(11, 1, 'private', '["privat"]', 1, $secret, 'noun');
            INSERT INTO user_progress(content_type, content_id, attempt_count, correct_count, last_attempt_utc)
            VALUES('BookWord', 11, 2, 1, '2026-08-20T00:00:00Z');
            """;
        if (includeSecondBook)
        {
            command.CommandText += """
                INSERT INTO user_books(id, title, source_culture, raw_text, created_utc)
                VALUES(2, 'Keep', 'ru-RU', 'KEEP-SECOND-BOOK', '2026-08-20T00:00:00Z');
                INSERT INTO user_book_words(id, book_id, source_text, translations_json, frequency, context_text, part_of_speech)
                VALUES(22, 2, 'keep', '["behalten"]', 1, 'KEEP-SECOND-BOOK', 'verb');
                INSERT INTO user_progress(content_type, content_id, attempt_count, correct_count, last_attempt_utc)
                VALUES('BookWord', 22, 1, 1, '2026-08-20T00:00:00Z');
                """;
        }
        command.Parameters.AddWithValue("$secret", DeletedSecret);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(string databasePath, string table, string predicate = "1=1")
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {predicate};";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static bool ContainsBytes(byte[] source, byte[] value)
    {
        if (value.Length == 0 || value.Length > source.Length)
        {
            return false;
        }
        for (var index = 0; index <= source.Length - value.Length; index++)
        {
            if (source.AsSpan(index, value.Length).SequenceEqual(value))
            {
                return true;
            }
        }
        return false;
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
