using WortBruecke.Core.Courses;
using WortBruecke.Infrastructure.Content;
using WortBruecke.Infrastructure.Paths;
using WortBruecke.Infrastructure.Persistence;

namespace WortBruecke.Tests.Persistence;

public sealed class CourseProgressRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LernTypeCourseProgress", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Repository_RoundTripsBestScoreStatusAndExactResumeSnapshot()
    {
        var content = Path.Combine(_root, "Content");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(Path.Combine(content, "catalog.json"), """
            { "revision": 1, "themes": [], "words": [], "sentences": [], "passages": [], "grammarTasks": [] }
            """);
        var database = new SqliteDatabase(new AppPaths(content, Path.Combine(_root, "Data")), new JsonContentLoader());
        await database.InitializeAsync();
        var repository = new SqliteCourseProgressRepository(database);
        var now = DateTimeOffset.UtcNow;

        await repository.UpsertAsync(new("a1", "lesson:a1-01", CourseNodeStatus.Completed, 0.875, 2, now));
        await repository.SaveResumeAsync(new(
            "a1",
            "a1-u1",
            "a1-01",
            "read",
            now,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["briefing:task-1"] = 1,
                ["speaking:task-2"] = 1,
                ["writing:task-3"] = 0
            },
            new HashSet<string>(["speaking:task-2"], StringComparer.Ordinal)));

        var progress = Assert.Single(await repository.GetCourseAsync("a1"));
        Assert.Equal(CourseNodeStatus.Completed, progress.Status);
        Assert.Equal(0.875, progress.BestScore);
        Assert.Equal(2, progress.AttemptCount);
        var resume = Assert.IsType<CourseResumeState>(await repository.GetResumeAsync("a1"));
        Assert.Equal(("a1-u1", "a1-01", "read"), (resume.UnitId, resume.LessonId, resume.StepId));
        Assert.Equal(3, resume.TaskScores.Count);
        Assert.Equal(1, resume.TaskScores["briefing:task-1"]);
        Assert.Equal(1, resume.TaskScores["speaking:task-2"]);
        Assert.Equal(0, resume.TaskScores["writing:task-3"]);
        Assert.Equal(["speaking:task-2"], resume.SelfReportedTaskKeys);
    }

    [Fact]
    public async Task UpsertAsync_AtomicallyPreservesMonotonicProgressAcrossStaleWriters()
    {
        var (database, repository) = await CreateRepositoryAsync();
        var secondRepository = new SqliteCourseProgressRepository(database);
        var now = DateTimeOffset.UtcNow;
        await repository.UpsertAsync(new(
            "a1",
            "exam:a1",
            CourseNodeStatus.Passed,
            0.8,
            1,
            now));

        var staleUpdates = new[]
        {
            repository.UpsertAsync(new(
                "a1",
                "exam:a1",
                CourseNodeStatus.Completed,
                0.2,
                2,
                now.AddMinutes(1))),
            secondRepository.UpsertAsync(new(
                "a1",
                "exam:a1",
                CourseNodeStatus.InProgress,
                0.4,
                2,
                now.AddMinutes(2)))
        };
        await Task.WhenAll(staleUpdates);

        var progress = Assert.Single(await repository.GetCourseAsync("a1"));
        Assert.Equal(CourseNodeStatus.Passed, progress.Status);
        Assert.Equal(0.8, progress.BestScore);
        Assert.Equal(3, progress.AttemptCount);
        Assert.Equal(now.AddMinutes(2), progress.UpdatedAtUtc);
    }

    [Fact]
    public async Task SaveResumeAsync_ReplacesPreviousSnapshotWithEmptySnapshot()
    {
        var (database, repository) = await CreateRepositoryAsync();
        var now = DateTimeOffset.UtcNow;
        await repository.SaveResumeAsync(new(
            "a1",
            "a1-u1",
            "a1-01",
            "write",
            now,
            new Dictionary<string, double>(StringComparer.Ordinal) { ["write:task"] = 1 },
            new HashSet<string>(StringComparer.Ordinal)));

        await repository.SaveResumeAsync(new("a1", "a1-u1", "a1-01", "briefing", now.AddMinutes(1)));

        var resume = Assert.IsType<CourseResumeState>(await repository.GetResumeAsync("a1"));
        Assert.Equal("briefing", resume.StepId);
        Assert.Empty(resume.TaskScores);
        Assert.Empty(resume.SelfReportedTaskKeys);
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT task_scores_json, self_reported_task_keys_json FROM course_resume_state WHERE course_id='a1';";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("{}", reader.GetString(0));
        Assert.Equal("[]", reader.GetString(1));
    }

    [Fact]
    public async Task SaveResumeAsync_RejectsInvalidScoringSnapshot()
    {
        var (_, repository) = await CreateRepositoryAsync();
        var now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.SaveResumeAsync(new(
            "a1",
            "a1-u1",
            "a1-01",
            "write",
            now,
            new Dictionary<string, double>(StringComparer.Ordinal) { ["write:task"] = 1.01 },
            new HashSet<string>(StringComparer.Ordinal))));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveResumeAsync(new(
            "a1",
            "a1-u1",
            "a1-01",
            "speaking",
            now,
            new Dictionary<string, double>(StringComparer.Ordinal),
            new HashSet<string>(["speaking:task"], StringComparer.Ordinal))));
    }

    [Fact]
    public async Task GetResumeAsync_RejectsSemanticallyInvalidJsonSnapshot()
    {
        var (database, repository) = await CreateRepositoryAsync();
        await repository.SaveResumeAsync(new("a1", "a1-u1", "a1-01", "write", DateTimeOffset.UtcNow));
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE course_resume_state SET task_scores_json='{\"write:task\":2}' WHERE course_id='a1';";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetResumeAsync("a1"));
    }

    private async Task<(SqliteDatabase Database, SqliteCourseProgressRepository Repository)> CreateRepositoryAsync()
    {
        var content = Path.Combine(_root, $"Content-{Guid.NewGuid():N}");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(Path.Combine(content, "catalog.json"), """
            { "revision": 1, "themes": [], "words": [], "sentences": [], "passages": [], "grammarTasks": [] }
            """);
        var database = new SqliteDatabase(
            new AppPaths(content, Path.Combine(_root, $"Data-{Guid.NewGuid():N}")),
            new JsonContentLoader());
        await database.InitializeAsync();
        return (database, new SqliteCourseProgressRepository(database));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
