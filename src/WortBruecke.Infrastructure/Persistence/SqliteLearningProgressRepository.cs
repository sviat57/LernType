using Microsoft.Data.Sqlite;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;

namespace WortBruecke.Infrastructure.Persistence;

public sealed class SqliteLearningProgressRepository(SqliteDatabase database) : ILearningProgressRepository
{
    public async Task RecordAsync(LearningAttempt attempt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO learning_attempts(
                level, skill, exercise_type, score, completed_at_utc,
                assessment_mode, was_timed, objective_id, session_id)
            VALUES(
                $level, $skill, $exerciseType, $score, $completedAt,
                $mode, $wasTimed, $objectiveId, $sessionId);
            """;
        command.Parameters.AddWithValue("$level", attempt.Level.ToString());
        command.Parameters.AddWithValue("$skill", attempt.Skill.ToString());
        command.Parameters.AddWithValue("$exerciseType", attempt.ExerciseType.ToString());
        command.Parameters.AddWithValue("$score", attempt.Score);
        command.Parameters.AddWithValue("$completedAt", attempt.CompletedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$mode", attempt.Mode.ToString());
        command.Parameters.AddWithValue("$wasTimed", attempt.WasTimed ? 1 : 0);
        command.Parameters.AddWithValue("$objectiveId", (object?)attempt.ObjectiveId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionId", attempt.SessionId?.ToString("D") ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LearningAttempt>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var attempts = new List<LearningAttempt>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT level, skill, exercise_type, score, completed_at_utc,
                   assessment_mode, was_timed, objective_id, session_id
            FROM learning_attempts
            ORDER BY completed_at_utc DESC, id DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!GermanLevelExtensions.TryParse(reader.GetString(0), out var level)
                || !Enum.TryParse<LanguageSkill>(reader.GetString(1), true, out var skill)
                || !Enum.TryParse<ExerciseType>(reader.GetString(2), true, out var exerciseType)
                || !Enum.TryParse<AssessmentMode>(reader.GetString(5), true, out var mode))
            {
                continue;
            }

            Guid? sessionId = reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8));
            attempts.Add(new LearningAttempt(
                level,
                skill,
                exerciseType,
                reader.GetDouble(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                mode,
                reader.GetInt32(6) != 0,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                sessionId));
        }

        return attempts;
    }
}
