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
        Assert.Equal(2L, await ScalarAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations;"));
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
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotentAndDoesNotDuplicateMigrationHistory()
    {
        var database = await CreateDatabaseAsync();

        await database.InitializeAsync();

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations;"));
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
        Assert.Equal(2L, await ScalarAsync(current, "PRAGMA user_version;"));
        Assert.Equal(1L, await ScalarAsync(current, "SELECT COUNT(*) FROM user_progress WHERE content_type='BookWord';"));
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
