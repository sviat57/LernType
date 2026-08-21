using Microsoft.Data.Sqlite;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;

namespace WortBruecke.Infrastructure.Persistence;

public sealed class SqliteReviewStateRepository(SqliteDatabase database) : IReviewStateRepository
{
    public async Task<ReviewState?> GetAsync(string contentKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(contentKey))
        {
            throw new ArgumentException("A content key is required.", nameof(contentKey));
        }
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT content_key, stability_days, difficulty, due_at_utc, last_reviewed_at_utc,
                   repetitions, lapses, scheduler_version
            FROM review_state WHERE content_key = $contentKey;
            """;
        command.Parameters.AddWithValue("$contentKey", contentKey.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<ReviewState>> GetDueAsync(
        DateTimeOffset asOfUtc,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is <= 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be between 1 and 1000.");
        }
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT content_key, stability_days, difficulty, due_at_utc, last_reviewed_at_utc,
                   repetitions, lapses, scheduler_version
            FROM review_state
            WHERE due_at_utc <= $asOf
            ORDER BY due_at_utc, content_key
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$asOf", asOfUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<ReviewState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Read(reader));
        }
        return result.AsReadOnly();
    }

    public async Task UpsertAsync(ReviewState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = UpsertSql;
        Bind(command, state);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal const string UpsertSql = """
        INSERT INTO review_state(
            content_key, stability_days, difficulty, due_at_utc, last_reviewed_at_utc,
            repetitions, lapses, scheduler_version)
        VALUES($reviewContentKey, $stability, $difficulty, $dueAt, $lastReviewedAt,
            $repetitions, $lapses, $reviewSchedulerVersion)
        ON CONFLICT(content_key) DO UPDATE SET
            stability_days = excluded.stability_days,
            difficulty = excluded.difficulty,
            due_at_utc = excluded.due_at_utc,
            last_reviewed_at_utc = excluded.last_reviewed_at_utc,
            repetitions = excluded.repetitions,
            lapses = excluded.lapses,
            scheduler_version = excluded.scheduler_version;
        """;

    internal static void Bind(SqliteCommand command, ReviewState state)
    {
        command.Parameters.AddWithValue("$reviewContentKey", state.ContentKey);
        command.Parameters.AddWithValue("$stability", state.StabilityDays);
        command.Parameters.AddWithValue("$difficulty", state.Difficulty);
        command.Parameters.AddWithValue("$dueAt", state.DueAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$lastReviewedAt", state.LastReviewedAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$repetitions", state.Repetitions);
        command.Parameters.AddWithValue("$lapses", state.Lapses);
        command.Parameters.AddWithValue("$reviewSchedulerVersion", state.SchedulerVersion);
    }

    internal static ReviewState Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetDouble(1),
        reader.GetDouble(2),
        DateTimeOffset.Parse(reader.GetString(3)),
        DateTimeOffset.Parse(reader.GetString(4)),
        reader.GetInt32(5),
        reader.GetInt32(6),
        reader.GetString(7));
}
