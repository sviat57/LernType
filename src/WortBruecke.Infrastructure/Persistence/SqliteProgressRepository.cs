using Microsoft.Data.Sqlite;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;

namespace WortBruecke.Infrastructure.Persistence;

public sealed class SqliteProgressRepository(SqliteDatabase database) : IProgressRepository
{
    public async Task RecordAttemptAsync(ContentType contentType, long contentId, bool correct, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO user_progress(content_type, content_id, attempt_count, correct_count, last_attempt_utc)
            VALUES($type, $id, 1, $correct, $now)
            ON CONFLICT(content_type, content_id) DO UPDATE SET
                attempt_count = attempt_count + 1,
                correct_count = correct_count + excluded.correct_count,
                last_attempt_utc = excluded.last_attempt_utc;
            """;
        command.Parameters.AddWithValue("$type", contentType.ToString());
        command.Parameters.AddWithValue("$id", contentId);
        command.Parameters.AddWithValue("$correct", correct ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ProgressRecord?> GetAsync(ContentType contentType, long contentId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT attempt_count, correct_count, last_attempt_utc
            FROM user_progress WHERE content_type = $type AND content_id = $id;
            """;
        command.Parameters.AddWithValue("$type", contentType.ToString());
        command.Parameters.AddWithValue("$id", contentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new ProgressRecord(
            contentType,
            contentId,
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)));
    }

    public async Task<IReadOnlyList<ProgressRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var records = new List<ProgressRecord>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT content_type, content_id, attempt_count, correct_count, last_attempt_utc
            FROM user_progress
            ORDER BY last_attempt_utc DESC, content_type, content_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Enum.TryParse<ContentType>(reader.GetString(0), out var contentType))
            {
                continue;
            }

            records.Add(new ProgressRecord(
                contentType,
                reader.GetInt64(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4))));
        }

        return records;
    }
}
