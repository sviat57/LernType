using WortBruecke.Core.Learning;
using WortBruecke.Infrastructure.Content;
using WortBruecke.Infrastructure.Paths;
using WortBruecke.Infrastructure.Persistence;

namespace WortBruecke.Tests.Persistence;

public sealed class AttemptRepositoryPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LernTypeAttemptTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Append_IsIdempotentAndUpdatesReviewStateInTheSameStore()
    {
        var (attempts, reviews) = await CreateRepositoriesAsync();
        var first = Event(Guid.NewGuid(), "core.a1.word.food.sample", 1, new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero));

        Assert.True(await attempts.AppendAsync(first));
        Assert.False(await attempts.AppendAsync(first));

        var saved = Assert.Single(await attempts.GetAsync(new AttemptQuery(Level: GermanLevel.A1)));
        var firstState = Assert.IsType<ReviewState>(await reviews.GetAsync(first.ContentKey));
        Assert.Equal(first.EventId, saved.EventId);
        Assert.Equal(1, firstState.Repetitions);
        Assert.Equal(0, firstState.Lapses);

        var second = Event(Guid.NewGuid(), first.ContentKey, 0, first.CompletedAtUtc.AddDays(1));
        Assert.True(await attempts.AppendAsync(second));
        var secondState = Assert.IsType<ReviewState>(await reviews.GetAsync(first.ContentKey));
        Assert.Equal(2, secondState.Repetitions);
        Assert.Equal(1, secondState.Lapses);
        Assert.Equal(2, (await attempts.GetAsync()).Count);
    }

    [Fact]
    public async Task DueQuery_IsOrderedAndBounded()
    {
        var (_, reviews) = await CreateRepositoriesAsync();
        var now = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        await reviews.UpsertAsync(new ReviewState("b", 1, 5, now.AddMinutes(-1), now.AddDays(-1), 1, 0, "v1"));
        await reviews.UpsertAsync(new ReviewState("a", 1, 5, now.AddMinutes(-2), now.AddDays(-1), 1, 0, "v1"));
        await reviews.UpsertAsync(new ReviewState("future", 1, 5, now.AddDays(1), now, 1, 0, "v1"));

        var due = await reviews.GetDueAsync(now, 1);

        Assert.Single(due);
        Assert.Equal("a", due[0].ContentKey);
    }

    private async Task<(SqliteAttemptRepository Attempts, SqliteReviewStateRepository Reviews)> CreateRepositoriesAsync()
    {
        var contentRoot = Path.Combine(_root, "Content");
        Directory.CreateDirectory(contentRoot);
        await File.WriteAllTextAsync(Path.Combine(contentRoot, "catalog.json"), """
            {
              "revision": 1,
              "themes": [{ "id": 1, "key": "base", "iconKey": "cards", "names": { "ru-RU": "База", "de-DE": "Basis" } }],
              "words": [], "sentences": [], "passages": [], "grammarTasks": []
            }
            """);
        var paths = new AppPaths(contentRoot, Path.Combine(_root, "Data"));
        var database = new SqliteDatabase(paths, new JsonContentLoader());
        await database.InitializeAsync();
        return (new SqliteAttemptRepository(database), new SqliteReviewStateRepository(database));
    }

    private static AttemptEvent Event(Guid id, string contentKey, double score, DateTimeOffset completed) => new(
        id,
        contentKey,
        1,
        GermanLevel.A1,
        LanguageSkill.Vocabulary,
        ExerciseType.BidirectionalTranslation,
        AttemptDirection.RussianToGerman,
        score,
        AssessmentMode.Practice,
        completed.AddSeconds(-2),
        completed,
        Guid.NewGuid(),
        "exact-v1",
        EvidenceQuality.Deterministic,
        "a1.vocabulary.everyday-vocabulary");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
