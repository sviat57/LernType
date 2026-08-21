using System.Text;
using Microsoft.Data.Sqlite;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;

namespace WortBruecke.Infrastructure.Persistence;

public sealed class SqliteAttemptRepository : IAttemptRepository
{
    private readonly SqliteDatabase _database;
    private readonly ISpacedRepetitionScheduler _scheduler;

    public SqliteAttemptRepository(
        SqliteDatabase database,
        ISpacedRepetitionScheduler? scheduler = null)
    {
        _database = database;
        _scheduler = scheduler ?? new DeterministicSpacedRepetitionScheduler();
    }

    public async Task<bool> AppendAsync(AttemptEvent attempt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO attempt_events(
                event_id, content_key, content_revision, objective_id, level, skill,
                exercise_family, direction, score, assessment_mode, started_at_utc,
                completed_at_utc, duration_ms, session_id, rubric_version, evidence_quality,
                exam_id, module_id, is_timed, scheduler_version)
            VALUES(
                $eventId, $contentKey, $contentRevision, $objectiveId, $level, $skill,
                $family, $direction, $score, $mode, $startedAt, $completedAt, $durationMs,
                $sessionId, $rubricVersion, $quality, $examId, $moduleId, $isTimed,
                $schedulerVersion)
            ON CONFLICT(event_id) DO NOTHING;
            """;
        Bind(command, attempt);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (inserted)
        {
            var previous = await ReadReviewStateAsync(connection, transaction, attempt.ContentKey, cancellationToken);
            var rating = attempt.Score switch
            {
                < 0.50 => ReviewRating.Again,
                < 0.70 => ReviewRating.Hard,
                < 0.90 => ReviewRating.Good,
                _ => ReviewRating.Easy
            };
            var state = _scheduler.Schedule(previous, attempt, rating);
            await using var reviewCommand = connection.CreateCommand();
            reviewCommand.Transaction = transaction;
            reviewCommand.CommandText = SqliteReviewStateRepository.UpsertSql;
            SqliteReviewStateRepository.Bind(reviewCommand, state);
            await reviewCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<IReadOnlyList<AttemptEvent>> GetAsync(
        AttemptQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var sql = new StringBuilder("""
            SELECT event_id, content_key, content_revision, objective_id, level, skill,
                   exercise_family, direction, score, assessment_mode, started_at_utc,
                   completed_at_utc, session_id, rubric_version, evidence_quality,
                   exam_id, module_id, is_timed, scheduler_version
            FROM attempt_events
            """);
        var filters = new List<string>();
        if (query?.Level is { } level)
        {
            filters.Add("level = $queryLevel");
            command.Parameters.AddWithValue("$queryLevel", level.ToString());
        }
        AddOptionalFilter(command, filters, "objective_id", "$queryObjective", query?.ObjectiveId);
        AddOptionalFilter(command, filters, "exam_id", "$queryExam", query?.ExamId);
        AddOptionalFilter(command, filters, "module_id", "$queryModule", query?.ModuleId);
        if (query?.SessionId is { } sessionId)
        {
            filters.Add("session_id = $querySession");
            command.Parameters.AddWithValue("$querySession", sessionId.ToString("D"));
        }
        if (query?.CompletedSinceUtc is { } since)
        {
            filters.Add("completed_at_utc >= $querySince");
            command.Parameters.AddWithValue("$querySince", since.ToUniversalTime().ToString("O"));
        }
        if (filters.Count > 0)
        {
            sql.Append(" WHERE ").Append(string.Join(" AND ", filters));
        }
        sql.Append(" ORDER BY completed_at_utc DESC, event_id DESC;");
        command.CommandText = sql.ToString();

        var result = new List<AttemptEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!TryParse(reader, out var attempt))
            {
                continue;
            }
            result.Add(attempt!);
        }
        return result.AsReadOnly();
    }

    private static void Bind(SqliteCommand command, AttemptEvent attempt)
    {
        command.Parameters.AddWithValue("$eventId", attempt.EventId.ToString("D"));
        command.Parameters.AddWithValue("$contentKey", attempt.ContentKey);
        command.Parameters.AddWithValue("$contentRevision", attempt.ContentRevision);
        command.Parameters.AddWithValue("$objectiveId", (object?)attempt.ObjectiveId ?? DBNull.Value);
        command.Parameters.AddWithValue("$level", attempt.Level.ToString());
        command.Parameters.AddWithValue("$skill", attempt.Skill.ToString());
        command.Parameters.AddWithValue("$family", attempt.ExerciseFamily.ToString());
        command.Parameters.AddWithValue("$direction", attempt.Direction.ToString());
        command.Parameters.AddWithValue("$score", attempt.Score);
        command.Parameters.AddWithValue("$mode", attempt.Mode.ToString());
        command.Parameters.AddWithValue("$startedAt", attempt.StartedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$completedAt", attempt.CompletedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$durationMs", checked((long)Math.Round(attempt.Duration.TotalMilliseconds)));
        command.Parameters.AddWithValue("$sessionId", attempt.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$rubricVersion", attempt.RubricVersion);
        command.Parameters.AddWithValue("$quality", attempt.EvidenceQuality.ToString());
        command.Parameters.AddWithValue("$examId", (object?)attempt.ExamId ?? DBNull.Value);
        command.Parameters.AddWithValue("$moduleId", (object?)attempt.ModuleId ?? DBNull.Value);
        command.Parameters.AddWithValue("$isTimed", attempt.WasTimed ? 1 : 0);
        command.Parameters.AddWithValue("$schedulerVersion", attempt.SchedulerVersion);
    }

    private static bool TryParse(SqliteDataReader reader, out AttemptEvent? attempt)
    {
        attempt = null;
        if (!Guid.TryParse(reader.GetString(0), out var eventId)
            || !GermanLevelExtensions.TryParse(reader.GetString(4), out var level)
            || !Enum.TryParse<LanguageSkill>(reader.GetString(5), true, out var skill)
            || !Enum.TryParse<ExerciseType>(reader.GetString(6), true, out var family)
            || !Enum.TryParse<AttemptDirection>(reader.GetString(7), true, out var direction)
            || !Enum.TryParse<AssessmentMode>(reader.GetString(9), true, out var mode)
            || !Guid.TryParse(reader.GetString(12), out var sessionId)
            || !Enum.TryParse<EvidenceQuality>(reader.GetString(14), true, out var quality)
            || !DateTimeOffset.TryParse(reader.GetString(10), out var started)
            || !DateTimeOffset.TryParse(reader.GetString(11), out var completed))
        {
            return false;
        }

        attempt = new AttemptEvent(
            eventId,
            reader.GetString(1),
            reader.GetInt32(2),
            level,
            skill,
            family,
            direction,
            reader.GetDouble(8),
            mode,
            started,
            completed,
            sessionId,
            reader.GetString(13),
            quality,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(17) != 0,
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.GetString(18));
        return true;
    }

    private static void AddOptionalFilter(
        SqliteCommand command,
        ICollection<string> filters,
        string column,
        string parameter,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        filters.Add($"{column} = {parameter}");
        command.Parameters.AddWithValue(parameter, value.Trim());
    }

    private static async Task<ReviewState?> ReadReviewStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string contentKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT content_key, stability_days, difficulty, due_at_utc, last_reviewed_at_utc,
                   repetitions, lapses, scheduler_version
            FROM review_state WHERE content_key = $contentKey;
            """;
        command.Parameters.AddWithValue("$contentKey", contentKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? SqliteReviewStateRepository.Read(reader)
            : null;
    }
}
