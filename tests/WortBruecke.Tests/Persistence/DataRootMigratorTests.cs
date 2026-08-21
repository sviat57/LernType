using Microsoft.Data.Sqlite;
using WortBruecke.Infrastructure.Paths;
using WortBruecke.Infrastructure.Persistence;

namespace WortBruecke.Tests.Persistence;

public sealed class DataRootMigratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LernTypeMigrationTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MigrateAsync_CheckpointsLegacyWalAndPreservesCommittedRows()
    {
        var (paths, legacyDatabase) = CreatePaths();
        await using var writer = await CreateProfileAsync(legacyDatabase, "legacy-row", keepWalOpen: true);
        var walPath = legacyDatabase + "-wal";
        Assert.True(File.Exists(walPath));
        Assert.True(new FileInfo(walPath).Length > 0);

        var result = await new DataRootMigrator(paths).MigrateAsync();

        Assert.Equal(DataRootMigrationStatus.Migrated, result.Status);
        Assert.Equal("legacy-row", await ReadSingleValueAsync(paths.DatabasePath));
        Assert.True(File.Exists(legacyDatabase));
        Assert.NotNull(result.VerifiedBackupPath);
        Assert.Equal("legacy-row", await ReadSingleValueAsync(result.VerifiedBackupPath!));
        Assert.Contains("\"phase\":\"completed\"", await File.ReadAllTextAsync(paths.MigrationJournalPath));
    }

    [Fact]
    public async Task MigrateAsync_ReplacesPartialTargetButPreservesBothSettingsFiles()
    {
        var (paths, legacyDatabase) = CreatePaths();
        await using var profile = await CreateProfileAsync(legacyDatabase, "legacy-row");
        await profile.DisposeAsync();
        Directory.CreateDirectory(paths.DataRoot);
        await File.WriteAllBytesAsync(paths.DatabasePath, []);
        await File.WriteAllTextAsync(paths.LocalSettingsPath, "current-settings");
        await File.WriteAllTextAsync(Path.Combine(paths.LegacyDataRoot!, "settings.json"), "legacy-settings");

        var result = await new DataRootMigrator(paths).MigrateAsync();

        Assert.Equal("legacy-row", await ReadSingleValueAsync(paths.DatabasePath));
        Assert.Equal("current-settings", await File.ReadAllTextAsync(paths.LocalSettingsPath));
        Assert.NotNull(result.RollbackPath);
        Assert.True(File.Exists(result.RollbackPath!));
        var recoveredSettings = Assert.Single(Directory.GetFiles(paths.RecoveryRoot, "legacy-settings.json", SearchOption.AllDirectories));
        Assert.Equal("legacy-settings", await File.ReadAllTextAsync(recoveredSettings));
    }

    [Fact]
    public async Task MigrateAsync_WhenBothProfilesContainUserData_StopsAndPreservesBoth()
    {
        var (paths, legacyDatabase) = CreatePaths();
        await using var legacy = await CreateProfileAsync(legacyDatabase, "legacy-row");
        await legacy.DisposeAsync();
        await using var current = await CreateProfileAsync(paths.DatabasePath, "current-row");
        await current.DisposeAsync();

        var exception = await Assert.ThrowsAsync<DataRootConflictException>(
            () => new DataRootMigrator(paths).MigrateAsync());

        Assert.Equal(paths.DatabasePath, exception.CurrentDatabasePath);
        Assert.Equal(legacyDatabase, exception.LegacyDatabasePath);
        Assert.Equal("current-row", await ReadSingleValueAsync(paths.DatabasePath));
        Assert.Equal("legacy-row", await ReadSingleValueAsync(legacyDatabase));
    }

    [Fact]
    public async Task MigrateAsync_CurrentProfileWinsOverEmptyLegacyDatabaseAndDecisionIsJournaled()
    {
        var (paths, legacyDatabase) = CreatePaths();
        Directory.CreateDirectory(Path.GetDirectoryName(legacyDatabase)!);
        await using (var legacy = new SqliteConnection($"Data Source={legacyDatabase};Pooling=False"))
        {
            await legacy.OpenAsync();
            await using var command = legacy.CreateCommand();
            command.CommandText = "CREATE TABLE metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);";
            await command.ExecuteNonQueryAsync();
        }
        await using var current = await CreateProfileAsync(paths.DatabasePath, "current-row");
        await current.DisposeAsync();
        var migrator = new DataRootMigrator(paths);

        var first = await migrator.MigrateAsync();
        var second = await migrator.MigrateAsync();

        Assert.Equal(DataRootMigrationStatus.CurrentProfilePreferred, first.Status);
        Assert.Equal(DataRootMigrationStatus.AlreadyCompleted, second.Status);
        Assert.Equal("current-row", await ReadSingleValueAsync(paths.DatabasePath));
        Assert.Single(await File.ReadAllLinesAsync(paths.MigrationJournalPath),
            line => line.Contains("\"phase\":\"completed\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MigrateAsync_RepeatedRunIsIdempotent()
    {
        var (paths, legacyDatabase) = CreatePaths();
        await using var profile = await CreateProfileAsync(legacyDatabase, "legacy-row");
        await profile.DisposeAsync();
        var migrator = new DataRootMigrator(paths);

        var first = await migrator.MigrateAsync();
        var second = await migrator.MigrateAsync();

        Assert.Equal(DataRootMigrationStatus.Migrated, first.Status);
        Assert.Equal(DataRootMigrationStatus.AlreadyCompleted, second.Status);
        Assert.Equal("legacy-row", await ReadSingleValueAsync(paths.DatabasePath));
        Assert.Single(await File.ReadAllLinesAsync(paths.MigrationJournalPath),
            line => line.Contains("\"phase\":\"completed\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MigrateAsync_ResumesAfterInterruptionFollowingAtomicPromotion()
    {
        var (paths, legacyDatabase) = CreatePaths();
        await using var profile = await CreateProfileAsync(legacyDatabase, "legacy-row");
        await profile.DisposeAsync();
        var interrupted = new DataRootMigrator(paths, new DataMigrationOptions
        {
            AfterPhase = phase =>
            {
                if (phase == DataMigrationPhase.Promoted)
                {
                    throw new SimulatedInterruptionException();
                }
            }
        });

        await Assert.ThrowsAsync<SimulatedInterruptionException>(() => interrupted.MigrateAsync());
        var resumed = await new DataRootMigrator(paths).MigrateAsync();

        Assert.Equal(DataRootMigrationStatus.AlreadyCompleted, resumed.Status);
        Assert.Equal("legacy-row", await ReadSingleValueAsync(paths.DatabasePath));
        Assert.Contains("Resumed after an interruption", await File.ReadAllTextAsync(paths.MigrationJournalPath));
    }

    [Fact]
    public async Task MigrateAsync_NamedLockSerializesProcesses()
    {
        var (paths, legacyDatabase) = CreatePaths();
        await using var profile = await CreateProfileAsync(legacyDatabase, "legacy-row");
        await profile.DisposeAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => new DataRootMigrator(paths, new DataMigrationOptions
        {
            AfterPhase = phase =>
            {
                if (phase == DataMigrationPhase.Started)
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
            }
        }).MigrateAsync());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        await Assert.ThrowsAsync<TimeoutException>(() => new DataRootMigrator(paths, new DataMigrationOptions
        {
            LockTimeout = TimeSpan.FromMilliseconds(100)
        }).MigrateAsync());

        release.Set();
        await first;
        Assert.Equal("legacy-row", await ReadSingleValueAsync(paths.DatabasePath));
    }

    [Fact]
    public async Task RollbackAsync_RestoresVerifiedSnapshotAndPreservesDisplacedDatabase()
    {
        var (paths, legacyDatabase) = CreatePaths();
        await using var profile = await CreateProfileAsync(legacyDatabase, "before");
        await profile.DisposeAsync();
        var migrator = new DataRootMigrator(paths);
        var migration = await migrator.MigrateAsync();
        await InsertValueAsync(paths.DatabasePath, "after");

        var displaced = await migrator.RollbackAsync(migration.VerifiedBackupPath!);

        Assert.Equal(["before"], await ReadValuesAsync(paths.DatabasePath));
        Assert.NotNull(displaced);
        Assert.Equal(["before", "after"], await ReadValuesAsync(displaced!));
    }

    [Fact]
    public async Task MigrateAsync_CorruptLegacyDatabaseIsNeverOverwrittenOrDeleted()
    {
        var (paths, legacyDatabase) = CreatePaths();
        Directory.CreateDirectory(Path.GetDirectoryName(legacyDatabase)!);
        var original = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(legacyDatabase, original);

        var error = await Assert.ThrowsAsync<DataMigrationValidationException>(
            () => new DataRootMigrator(paths).MigrateAsync());

        Assert.Equal(legacyDatabase, error.DatabasePath);
        Assert.Equal(original, await File.ReadAllBytesAsync(legacyDatabase));
        Assert.False(File.Exists(paths.DatabasePath));
    }

    [Fact]
    public async Task MigrateAsync_RecoversWalDetachedByThePublishedFileRenameBug()
    {
        var (paths, _) = CreatePaths();
        Directory.CreateDirectory(paths.DataRoot);
        var originalPath = Path.Combine(_root, "DetachedSource", "wortbruecke.db");
        Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
        await using (var writer = new SqliteConnection($"Data Source={originalPath};Pooling=False"))
        {
            await writer.OpenAsync();
            await using (var baseline = writer.CreateCommand())
            {
                baseline.CommandText = """
                    PRAGMA journal_mode=WAL;
                    PRAGMA wal_autocheckpoint=0;
                    CREATE TABLE user_progress(
                        content_type TEXT NOT NULL, content_id INTEGER NOT NULL,
                        attempt_count INTEGER NOT NULL, correct_count INTEGER NOT NULL,
                        last_attempt_utc TEXT, PRIMARY KEY(content_type, content_id));
                    CREATE TABLE migration_test(value TEXT NOT NULL);
                    INSERT INTO user_progress VALUES('Word', 1, 1, 1, '2026-08-19T00:00:00Z');
                    INSERT INTO migration_test VALUES('from-main-file');
                    PRAGMA wal_checkpoint(TRUNCATE);
                    """;
                await baseline.ExecuteNonQueryAsync();
            }
            await using (var walOnly = writer.CreateCommand())
            {
                walOnly.CommandText = """
                    INSERT INTO user_progress VALUES('Word', 7, 1, 1, '2026-08-20T00:00:00Z');
                    INSERT INTO migration_test VALUES('from-detached-wal');
                    """;
                await walOnly.ExecuteNonQueryAsync();
            }
            Assert.True(File.Exists(originalPath + "-wal"));
            File.Copy(originalPath, paths.DatabasePath);
            File.Copy(originalPath + "-wal", paths.InPlaceLegacyDatabasePath + "-wal");
        }

        var result = await new DataRootMigrator(paths).MigrateAsync();

        Assert.Equal(DataRootMigrationStatus.Migrated, result.Status);
        Assert.Equal(["from-main-file", "from-detached-wal"], await ReadValuesAsync(paths.DatabasePath));
        Assert.True(File.Exists(paths.InPlaceLegacyDatabasePath + "-wal"));
        Assert.Contains(Path.Combine("Recovery", "detached-wal"), result.SourcePath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MigrateAsync_CopiesStandaloneLegacySettingsExactlyOnce()
    {
        var (paths, _) = CreatePaths();
        Directory.CreateDirectory(paths.LegacyDataRoot!);
        await File.WriteAllTextAsync(Path.Combine(paths.LegacyDataRoot!, "settings.json"), "legacy-settings");
        var migrator = new DataRootMigrator(paths);

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        Assert.Equal("legacy-settings", await File.ReadAllTextAsync(paths.LocalSettingsPath));
        Assert.Single(await File.ReadAllLinesAsync(paths.MigrationJournalPath),
            line => line.Contains("Standalone legacy settings migration completed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(DataMigrationPhase.Started)]
    [InlineData(DataMigrationPhase.SourcePreserved)]
    [InlineData(DataMigrationPhase.SourceCheckpointed)]
    [InlineData(DataMigrationPhase.BackupVerified)]
    [InlineData(DataMigrationPhase.DestinationVerified)]
    [InlineData(DataMigrationPhase.Promoted)]
    [InlineData(DataMigrationPhase.AncillaryFilesHandled)]
    [InlineData(DataMigrationPhase.Completed)]
    public async Task MigrateAsync_RerunCompletesAfterEveryDurableInterruptionPoint(DataMigrationPhase interruptedPhase)
    {
        var (paths, legacyDatabase) = CreatePaths();
        await using var profile = await CreateProfileAsync(legacyDatabase, "durable-row");
        await profile.DisposeAsync();
        var interrupted = new DataRootMigrator(paths, new DataMigrationOptions
        {
            AfterPhase = phase =>
            {
                if (phase == interruptedPhase)
                {
                    throw new SimulatedInterruptionException();
                }
            }
        });
        await Assert.ThrowsAsync<SimulatedInterruptionException>(() => interrupted.MigrateAsync());

        var result = await new DataRootMigrator(paths).MigrateAsync();

        Assert.True(result.Status is DataRootMigrationStatus.Migrated or DataRootMigrationStatus.AlreadyCompleted);
        Assert.Equal("durable-row", await ReadSingleValueAsync(paths.DatabasePath));
        Assert.Contains("\"phase\":\"completed\"", await File.ReadAllTextAsync(paths.MigrationJournalPath));
    }

    private (AppPaths Paths, string LegacyDatabase) CreatePaths()
    {
        var legacyRoot = Path.Combine(_root, "WortBruecke");
        var paths = new AppPaths(Path.Combine(_root, "Content"), Path.Combine(_root, "LernType"), legacyRoot);
        return (paths, Path.Combine(legacyRoot, "wortbruecke.db"));
    }

    private static async Task<SqliteConnection> CreateProfileAsync(string path, string value, bool keepWalOpen = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA wal_autocheckpoint=0;
            CREATE TABLE IF NOT EXISTS user_progress (
                content_type TEXT NOT NULL,
                content_id INTEGER NOT NULL,
                attempt_count INTEGER NOT NULL,
                correct_count INTEGER NOT NULL,
                last_attempt_utc TEXT,
                PRIMARY KEY(content_type, content_id));
            INSERT INTO user_progress VALUES('Word', 1, 1, 1, '2026-08-20T00:00:00Z');
            CREATE TABLE IF NOT EXISTS migration_test(value TEXT NOT NULL);
            INSERT INTO migration_test(value) VALUES($value);
            """;
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
        if (!keepWalOpen)
        {
            await connection.CloseAsync();
        }
        return connection;
    }

    private static async Task InsertValueAsync(string path, string value)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO migration_test(value) VALUES($value);";
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadSingleValueAsync(string path) =>
        Assert.Single(await ReadValuesAsync(path));

    private static async Task<string[]> ReadValuesAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM migration_test ORDER BY rowid;";
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }
        return [.. values];
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private sealed class SimulatedInterruptionException : Exception;
}
