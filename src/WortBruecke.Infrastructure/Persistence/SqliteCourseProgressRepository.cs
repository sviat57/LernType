using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Courses;

namespace WortBruecke.Infrastructure.Persistence;

/// <summary>Persists stable course progress and resume locations in the application database.</summary>
public sealed class SqliteCourseProgressRepository(SqliteDatabase database) : ICourseProgressRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<CourseNodeProgress>> GetCourseAsync(
        string courseId,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(courseId, nameof(courseId));
        var records = new List<CourseNodeProgress>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id, status, best_score, attempt_count, updated_at_utc
            FROM course_progress
            WHERE course_id = $course_id
            ORDER BY node_id;
            """;
        command.Parameters.AddWithValue("$course_id", courseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var statusText = reader.GetString(1);
            if (!Enum.TryParse<CourseNodeStatus>(statusText, ignoreCase: false, out var status)
                || !Enum.IsDefined(status))
            {
                throw new InvalidDataException($"В базе сохранён неизвестный статус узла курса: {statusText}.");
            }

            records.Add(new CourseNodeProgress(
                courseId,
                reader.GetString(0),
                status,
                reader.GetDouble(2),
                reader.GetInt32(3),
                ParseTimestamp(reader.GetString(4), "course_progress.updated_at_utc")));
        }

        return records.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task UpsertAsync(
        CourseNodeProgress progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ValidateProgress(progress);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO course_progress(
                course_id, node_id, status, best_score, attempt_count, updated_at_utc)
            VALUES(
                $course_id, $node_id, $status, $best_score, $attempt_count, $updated_at_utc)
            ON CONFLICT(course_id, node_id) DO UPDATE SET
                status = CASE
                    WHEN course_progress.status = 'Passed' OR excluded.status = 'Passed'
                        THEN 'Passed'
                    WHEN course_progress.status = 'Completed' OR excluded.status = 'Completed'
                        THEN 'Completed'
                    WHEN course_progress.status = 'InProgress' OR excluded.status = 'InProgress'
                        THEN 'InProgress'
                    ELSE 'NotStarted'
                END,
                best_score = MAX(course_progress.best_score, excluded.best_score),
                attempt_count = MAX(course_progress.attempt_count + 1, excluded.attempt_count),
                updated_at_utc = CASE
                    WHEN julianday(excluded.updated_at_utc) > julianday(course_progress.updated_at_utc)
                        THEN excluded.updated_at_utc
                    ELSE course_progress.updated_at_utc
                END;
            """;
        command.Parameters.AddWithValue("$course_id", progress.CourseId);
        command.Parameters.AddWithValue("$node_id", progress.NodeId);
        command.Parameters.AddWithValue("$status", progress.Status.ToString());
        command.Parameters.AddWithValue("$best_score", progress.BestScore);
        command.Parameters.AddWithValue("$attempt_count", progress.AttemptCount);
        command.Parameters.AddWithValue("$updated_at_utc", progress.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CourseResumeState?> GetResumeAsync(
        string courseId,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(courseId, nameof(courseId));
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT unit_id, lesson_id, step_id, task_scores_json,
                   self_reported_task_keys_json, updated_at_utc
            FROM course_resume_state
            WHERE course_id = $course_id;
            """;
        command.Parameters.AddWithValue("$course_id", courseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var taskScores = ParseTaskScores(reader.GetString(3));
        var selfReportedTaskKeys = ParseSelfReportedTaskKeys(reader.GetString(4));
        ValidateSelfReportedTaskKeys(taskScores, selfReportedTaskKeys, invalidData: true);
        return new CourseResumeState(
            courseId,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseTimestamp(reader.GetString(5), "course_resume_state.updated_at_utc"),
            taskScores,
            selfReportedTaskKeys);
    }

    /// <inheritdoc />
    public async Task SaveResumeAsync(
        CourseResumeState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateKey(state.CourseId, nameof(state.CourseId));
        ValidateKey(state.UnitId, nameof(state.UnitId));
        ValidateKey(state.LessonId, nameof(state.LessonId));
        ValidateKey(state.StepId, nameof(state.StepId));
        ValidateTimestamp(state.UpdatedAtUtc, nameof(state.UpdatedAtUtc));
        ValidateTaskScores(state.TaskScores);
        ValidateSelfReportedTaskKeys(state.TaskScores, state.SelfReportedTaskKeys, invalidData: false);

        var taskScoresJson = SerializeTaskScores(state.TaskScores);
        var selfReportedTaskKeysJson = SerializeSelfReportedTaskKeys(state.SelfReportedTaskKeys);

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO course_resume_state(
                course_id, unit_id, lesson_id, step_id, task_scores_json,
                self_reported_task_keys_json, updated_at_utc)
            VALUES(
                $course_id, $unit_id, $lesson_id, $step_id, $task_scores_json,
                $self_reported_task_keys_json, $updated_at_utc)
            ON CONFLICT(course_id) DO UPDATE SET
                unit_id = excluded.unit_id,
                lesson_id = excluded.lesson_id,
                step_id = excluded.step_id,
                task_scores_json = excluded.task_scores_json,
                self_reported_task_keys_json = excluded.self_reported_task_keys_json,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$course_id", state.CourseId);
        command.Parameters.AddWithValue("$unit_id", state.UnitId);
        command.Parameters.AddWithValue("$lesson_id", state.LessonId);
        command.Parameters.AddWithValue("$step_id", state.StepId);
        command.Parameters.AddWithValue("$task_scores_json", taskScoresJson);
        command.Parameters.AddWithValue("$self_reported_task_keys_json", selfReportedTaskKeysJson);
        command.Parameters.AddWithValue("$updated_at_utc", state.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateTaskScores(IReadOnlyDictionary<string, double> taskScores)
    {
        ArgumentNullException.ThrowIfNull(taskScores);
        foreach (var (taskKey, score) in taskScores)
        {
            ValidateKey(taskKey, nameof(CourseResumeState.TaskScores));
            if (!double.IsFinite(score) || score is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(CourseResumeState.TaskScores),
                    score,
                    $"Результат задания {taskKey} должен быть от 0 до 1.");
            }
        }
    }

    private static void ValidateSelfReportedTaskKeys(
        IReadOnlyDictionary<string, double> taskScores,
        IReadOnlySet<string> selfReportedTaskKeys,
        bool invalidData)
    {
        ArgumentNullException.ThrowIfNull(taskScores);
        ArgumentNullException.ThrowIfNull(selfReportedTaskKeys);
        foreach (var taskKey in selfReportedTaskKeys)
        {
            try
            {
                ValidateKey(taskKey, nameof(CourseResumeState.SelfReportedTaskKeys));
            }
            catch (ArgumentException exception) when (invalidData)
            {
                throw new InvalidDataException(
                    "В снимке продолжения курса сохранён некорректный ключ самопроверки.",
                    exception);
            }

            if (taskScores.ContainsKey(taskKey))
            {
                continue;
            }

            const string message = "Каждый ключ самопроверки должен иметь результат в снимке заданий.";
            if (invalidData)
            {
                throw new InvalidDataException(message);
            }

            throw new ArgumentException(message, nameof(CourseResumeState.SelfReportedTaskKeys));
        }
    }

    private static string SerializeTaskScores(IReadOnlyDictionary<string, double> taskScores)
    {
        var ordered = taskScores
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        return JsonSerializer.Serialize(ordered);
    }

    private static string SerializeSelfReportedTaskKeys(IReadOnlySet<string> selfReportedTaskKeys) =>
        JsonSerializer.Serialize(selfReportedTaskKeys.Order(StringComparer.Ordinal));

    private static IReadOnlyDictionary<string, double> ParseTaskScores(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Снимок результатов заданий должен быть JSON-объектом.");
            }

            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                try
                {
                    ValidateKey(property.Name, nameof(CourseResumeState.TaskScores));
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException(
                        "В снимке продолжения курса сохранён некорректный ключ задания.",
                        exception);
                }

                if (property.Value.ValueKind != JsonValueKind.Number
                    || !property.Value.TryGetDouble(out var score)
                    || !double.IsFinite(score)
                    || score is < 0 or > 1)
                {
                    throw new InvalidDataException(
                        $"В снимке продолжения курса сохранён некорректный результат задания {property.Name}.");
                }
                if (!result.TryAdd(property.Name, score))
                {
                    throw new InvalidDataException(
                        $"Снимок продолжения курса содержит повторяющийся ключ задания {property.Name}.");
                }
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Снимок результатов заданий содержит некорректный JSON.", exception);
        }
    }

    private static IReadOnlySet<string> ParseSelfReportedTaskKeys(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Снимок самопроверки должен быть JSON-массивом.");
            }

            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("Ключи самопроверки в снимке должны быть строками.");
                }

                var taskKey = element.GetString()!;
                try
                {
                    ValidateKey(taskKey, nameof(CourseResumeState.SelfReportedTaskKeys));
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException(
                        "В снимке продолжения курса сохранён некорректный ключ самопроверки.",
                        exception);
                }

                if (!result.Add(taskKey))
                {
                    throw new InvalidDataException(
                        $"Снимок продолжения курса содержит повторяющийся ключ самопроверки {taskKey}.");
                }
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Снимок самопроверки содержит некорректный JSON.", exception);
        }
    }

    private static void ValidateProgress(CourseNodeProgress progress)
    {
        ValidateKey(progress.CourseId, nameof(progress.CourseId));
        ValidateKey(progress.NodeId, nameof(progress.NodeId));
        if (!Enum.IsDefined(progress.Status))
        {
            throw new ArgumentOutOfRangeException(nameof(progress), progress.Status, "Неизвестный статус узла курса.");
        }
        if (!double.IsFinite(progress.BestScore) || progress.BestScore is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(progress), progress.BestScore, "Лучший результат должен быть от 0 до 1.");
        }
        if (progress.AttemptCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(progress), progress.AttemptCount, "Число попыток не может быть отрицательным.");
        }
        if (progress.Status == CourseNodeStatus.NotStarted
            && (progress.AttemptCount != 0 || progress.BestScore != 0))
        {
            throw new ArgumentException("Узел NotStarted должен иметь нулевые попытки и результат.", nameof(progress));
        }
        if (progress.Status != CourseNodeStatus.NotStarted && progress.AttemptCount == 0)
        {
            throw new ArgumentException("Начатый узел должен содержать хотя бы одну попытку.", nameof(progress));
        }
        ValidateTimestamp(progress.UpdatedAtUtc, nameof(progress.UpdatedAtUtc));
    }

    private static void ValidateKey(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Идентификатор не должен начинаться или заканчиваться пробелом.", parameterName);
        }
    }

    private static void ValidateTimestamp(DateTimeOffset value, string parameterName)
    {
        if (value == default)
        {
            throw new ArgumentException("Время обновления должно быть задано.", parameterName);
        }
    }

    private static DateTimeOffset ParseTimestamp(string value, string field)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            throw new InvalidDataException($"В базе сохранено некорректное время {field}: {value}.");
        }
        return timestamp;
    }
}
