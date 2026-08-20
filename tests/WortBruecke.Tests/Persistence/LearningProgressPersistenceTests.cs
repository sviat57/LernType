using WortBruecke.Core.Learning;
using WortBruecke.Infrastructure.Content;
using WortBruecke.Infrastructure.Paths;
using WortBruecke.Infrastructure.Persistence;

namespace WortBruecke.Tests.Persistence;

public sealed class LearningProgressPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LernTypeLearningTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Attempts_RoundTripAllExamEvidenceAndSurviveCatalogRevision()
    {
        var contentRoot = Path.Combine(_root, "Content");
        Directory.CreateDirectory(contentRoot);
        var catalogPath = Path.Combine(contentRoot, "catalog.json");
        await WriteCatalogAsync(catalogPath, 1);
        var paths = new AppPaths(contentRoot, Path.Combine(_root, "Data"));
        var database = new SqliteDatabase(paths, new JsonContentLoader());
        await database.InitializeAsync();
        var repository = new SqliteLearningProgressRepository(database);
        var sessionId = Guid.NewGuid();
        var completedAt = new DateTimeOffset(2026, 8, 20, 12, 30, 0, TimeSpan.Zero);

        await repository.RecordAsync(new LearningAttempt(
            GermanLevel.B2,
            LanguageSkill.Listening,
            ExerciseType.ListeningComprehension,
            0.82,
            completedAt,
            AssessmentMode.MockExam,
            wasTimed: true,
            objectiveId: "b2.listening.interviews",
            sessionId: sessionId));

        await WriteCatalogAsync(catalogPath, 2);
        await database.InitializeAsync();
        var saved = Assert.Single(await repository.GetAllAsync());

        Assert.Equal(GermanLevel.B2, saved.Level);
        Assert.Equal(LanguageSkill.Listening, saved.Skill);
        Assert.Equal(ExerciseType.ListeningComprehension, saved.ExerciseType);
        Assert.Equal(0.82, saved.Score, precision: 6);
        Assert.Equal(completedAt, saved.CompletedAtUtc);
        Assert.Equal(AssessmentMode.MockExam, saved.Mode);
        Assert.True(saved.WasTimed);
        Assert.Equal("b2.listening.interviews", saved.ObjectiveId);
        Assert.Equal(sessionId, saved.SessionId);
    }

    private static Task WriteCatalogAsync(string path, int revision) => File.WriteAllTextAsync(path, $$"""
        {
          "revision": {{revision}},
          "themes": [{ "id": 1, "key": "base", "iconKey": "cards", "names": { "ru-RU": "База", "de-DE": "Basis" } }],
          "words": [],
          "sentences": [],
          "passages": [],
          "grammarTasks": []
        }
        """);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
