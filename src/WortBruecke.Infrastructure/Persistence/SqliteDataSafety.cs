using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace WortBruecke.Infrastructure.Persistence;

internal sealed record DatabaseInspection(bool Exists, bool IsValid, bool HasUserData, IReadOnlyDictionary<string, long> TableRows)
{
    public static DatabaseInspection Missing { get; } = new(false, false, false, new Dictionary<string, long>());
}

internal static class SqliteDataSafety
{
    private static readonly HashSet<string> NonUserTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "metadata", "schema_migrations", "themes", "theme_translations", "word_groups", "word_translations",
        "sentence_groups", "sentence_translations", "passages", "passage_translations", "passage_segments",
        "passage_segment_translations", "grammar_tasks", "grammar_task_translations", "content_identities",
        "content_identity_migration_map", "sqlite_sequence"
    };

    public static async Task<DatabaseInspection> InspectAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return File.Exists(path)
                ? new DatabaseInspection(true, false, false, new Dictionary<string, long>())
                : DatabaseInspection.Missing;
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            if (!await QuickCheckAsync(connection, cancellationToken))
            {
                return new DatabaseInspection(true, false, false, new Dictionary<string, long>());
            }

            var rows = await ReadTableRowsAsync(connection, cancellationToken);
            var hasUserData = rows.Any(item => !NonUserTables.Contains(item.Key) && item.Value > 0);
            return new DatabaseInspection(true, true, hasUserData, rows);
        }
        catch (SqliteException)
        {
            return new DatabaseInspection(true, false, false, new Dictionary<string, long>());
        }
    }

    public static async Task<bool> QuickCheckAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA quick_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sawRow = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            sawRow = true;
            if (!string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return sawRow;
    }

    public static async Task<IReadOnlyDictionary<string, long>> ReadTableRowsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var rows = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            if (!IsSafeIdentifier(table))
            {
                throw new InvalidDataException($"Некорректное имя таблицы SQLite: {table}.");
            }

            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM \"{table}\";";
            rows[table] = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
        }

        return rows;
    }

    public static async Task CheckpointWalAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var timeout = connection.CreateCommand())
        {
            timeout.CommandText = "PRAGMA busy_timeout=5000;";
            await timeout.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await using var reader = await checkpoint.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException("SQLite не вернул результат WAL checkpoint.");
        }

        var busy = reader.GetInt32(0);
        if (busy != 0)
        {
            throw new IOException("WAL занят другим процессом; исходный профиль сохранён без изменений.");
        }
    }

    public static void BackupDatabase(SqliteConnection source, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        using var destination = new SqliteConnection(connectionString);
        destination.Open();
        source.BackupDatabase(destination);
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    public static bool InventoriesEqual(
        IReadOnlyDictionary<string, long> first,
        IReadOnlyDictionary<string, long> second) =>
        first.Count == second.Count &&
        first.All(item => second.TryGetValue(item.Key, out var count) && count == item.Value);

    private static bool IsSafeIdentifier(string value) =>
        value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}
