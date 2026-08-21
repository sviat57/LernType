using System.Text;
using System.Text.Json;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;
using WortBruecke.Infrastructure.Content;
using WortBruecke.Infrastructure.Paths;
using WortBruecke.Infrastructure.Persistence;

namespace WortBruecke.Tests.Persistence;

public sealed class BookRepositoryPrivacyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LernTypeBookPrivacyTests", Guid.NewGuid().ToString("N"));
    private string? _databasePath;

    [Fact]
    public async Task SummariesExportAndDeletion_HonorPrivacyLifecycleAndRemoveProgress()
    {
        var (repository, progress, attempts, reviews) = await CreateAsync();
        var first = await repository.SaveAsync("Одиссея", "ru-RU", "Море было тёмным.",
        [
            new ExtractedVocabularyItem("море", ["das Meer"], 1, "Море было тёмным.", "noun")
        ]);
        var second = await repository.SaveAsync("Ilias", "de-DE", "Der Himmel war rot.",
        [
            new ExtractedVocabularyItem("Himmel", ["небо"], 1, "Der Himmel war rot.", "noun")
        ]);
        var firstWord = Assert.Single(first.Vocabulary);
        await progress.RecordAttemptAsync(ContentType.BookWord, firstWord.Id, true);
        var canonicalContentKey = LearningContentKey.ForBookWord(first.Id, firstWord.Source);
        await attempts.AppendAsync(CreateAttempt(canonicalContentKey));
        Assert.NotNull(await reviews.GetAsync(canonicalContentKey));

        var summaries = await repository.GetRecentSummariesAsync();

        Assert.Equal(2, summaries.Count);
        var firstSummary = Assert.Single(summaries, item => item.Id == first.Id);
        Assert.Equal(first.RawText.Length, firstSummary.CharacterCount);
        Assert.Equal(1, firstSummary.VocabularyCount);

        await using var export = new MemoryStream();
        await repository.ExportAsync(first.Id, export);
        var json = Encoding.UTF8.GetString(export.ToArray());
        Assert.Contains("\"format\":\"lerntype-book\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceCulture\":\"ru-RU\"", json, StringComparison.Ordinal);
        using var exportDocument = JsonDocument.Parse(json);
        Assert.Equal("Море было тёмным.", exportDocument.RootElement.GetProperty("book").GetProperty("text").GetString());

        Assert.True(await repository.DeleteAsync(first.Id));
        Assert.Null(await repository.GetAsync(first.Id));
        Assert.Null(await progress.GetAsync(ContentType.BookWord, firstWord.Id));
        Assert.DoesNotContain(await attempts.GetAsync(), item => item.ContentKey == canonicalContentKey);
        Assert.Null(await reviews.GetAsync(canonicalContentKey));
        Assert.NotNull(await repository.GetAsync(second.Id));

        var secondContentKey = LearningContentKey.ForBookWord(second.Id, Assert.Single(second.Vocabulary).Source);
        await attempts.AppendAsync(CreateAttempt(secondContentKey));
        Assert.Equal(1, await repository.DeleteAllAsync());
        Assert.Empty(await repository.GetRecentSummariesAsync());
        Assert.DoesNotContain(await attempts.GetAsync(), item => item.ContentKey == secondContentKey);
        Assert.Null(await reviews.GetAsync(secondContentKey));
        Assert.NotNull(_databasePath);
        var databaseBytes = await File.ReadAllBytesAsync(_databasePath);
        Assert.DoesNotContain("Der Himmel war rot.", Encoding.UTF8.GetString(databaseBytes), StringComparison.Ordinal);
        var walPath = _databasePath + "-wal";
        Assert.False(File.Exists(walPath) && new FileInfo(walPath).Length > 0);
    }

    [Fact]
    public async Task Export_WithPreCanceledTokenDoesNotWriteUserText()
    {
        var (repository, _, _, _) = await CreateAsync();
        var book = await repository.SaveAsync("Private", "de-DE", "Geheimer Text.",
        [
            new ExtractedVocabularyItem("Text", ["текст"], 1, "Geheimer Text.", "noun")
        ]);
        await using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ExportAsync(book.Id, destination, cancellation.Token));

        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task ResavingBook_RemovesProgressForVocabularyItemsNoLongerPresent()
    {
        var (repository, progress, attempts, reviews) = await CreateAsync();
        var original = await repository.SaveAsync("Revision", "de-DE", "Meer und Himmel.",
        [
            new ExtractedVocabularyItem("Meer", ["море"], 1, "Meer und Himmel.", "noun"),
            new ExtractedVocabularyItem("Himmel", ["небо"], 1, "Meer und Himmel.", "noun")
        ]);
        var removedWord = Assert.Single(original.Vocabulary, item => item.Source == "Himmel");
        await progress.RecordAttemptAsync(ContentType.BookWord, removedWord.Id, true);
        var removedContentKey = LearningContentKey.ForBookWord(original.Id, removedWord.Source);
        await attempts.AppendAsync(CreateAttempt(removedContentKey));

        await repository.SaveAsync("Revision", "de-DE", "Meer und Himmel.",
        [
            new ExtractedVocabularyItem("Meer", ["море"], 1, "Meer und Himmel.", "noun")
        ]);

        Assert.Null(await progress.GetAsync(ContentType.BookWord, removedWord.Id));
        Assert.DoesNotContain(await attempts.GetAsync(), item => item.ContentKey == removedContentKey);
        Assert.Null(await reviews.GetAsync(removedContentKey));
        var saved = Assert.Single(await repository.GetRecentAsync());
        Assert.Single(saved.Vocabulary);
    }

    [Fact]
    public async Task Delete_PropagatesBookAndWordIdentityToManagedBackupCleanup()
    {
        var backups = new RecordingManagedBackupService();
        var (repository, _, _, _) = await CreateAsync(backups);
        var book = await repository.SaveAsync("Backup", "de-DE", "Meer und Himmel.",
        [
            new ExtractedVocabularyItem("Meer", ["море"], 1, "Meer und Himmel.", "noun"),
            new ExtractedVocabularyItem("Himmel", ["небо"], 1, "Meer und Himmel.", "noun")
        ]);

        await repository.DeleteAsync(book.Id);

        Assert.Equal(book.Id, backups.PurgedBookId);
        Assert.Equal(book.Vocabulary.Select(item => item.Id).Order(), backups.PurgedWordIds.Order());
    }

    [Fact]
    public async Task DeleteAll_PurgesAllManagedBackupBookData()
    {
        var backups = new RecordingManagedBackupService();
        var (repository, _, _, _) = await CreateAsync(backups);
        await repository.SaveAsync("Backup", "de-DE", "Meer.",
        [
            new ExtractedVocabularyItem("Meer", ["море"], 1, "Meer.", "noun")
        ]);

        await repository.DeleteAllAsync();

        Assert.Equal(1, backups.PurgeAllCalls);
    }

    private async Task<(
        SqliteBookRepository Books,
        SqliteProgressRepository Progress,
        SqliteAttemptRepository Attempts,
        SqliteReviewStateRepository Reviews)> CreateAsync(
        IManagedBackupService? managedBackups = null)
    {
        var contentRoot = Path.Combine(_root, "Content");
        Directory.CreateDirectory(contentRoot);
        await File.WriteAllTextAsync(Path.Combine(contentRoot, "catalog.json"), """
            {
              "revision": 1,
              "themes": [],
              "words": [],
              "sentences": [],
              "passages": [],
              "grammarTasks": []
            }
            """);
        var paths = new AppPaths(contentRoot, Path.Combine(_root, "Data"));
        _databasePath = paths.DatabasePath;
        var database = new SqliteDatabase(paths, new JsonContentLoader());
        await database.InitializeAsync();
        return (
            new SqliteBookRepository(database, managedBackups),
            new SqliteProgressRepository(database),
            new SqliteAttemptRepository(database),
            new SqliteReviewStateRepository(database));
    }

    private static AttemptEvent CreateAttempt(string contentKey)
    {
        var completed = DateTimeOffset.UtcNow;
        return new AttemptEvent(
            Guid.NewGuid(),
            contentKey,
            1,
            GermanLevel.A0,
            LanguageSkill.Vocabulary,
            ExerciseType.BidirectionalTranslation,
            AttemptDirection.GermanToRussian,
            1,
            AssessmentMode.Practice,
            completed.AddSeconds(-2),
            completed,
            Guid.NewGuid(),
            "exact-answer-v1",
            EvidenceQuality.Deterministic,
            objectiveId: "book.custom.vocabulary");
    }

    private sealed class RecordingManagedBackupService : IManagedBackupService
    {
        public long? PurgedBookId { get; private set; }
        public IReadOnlyList<long> PurgedWordIds { get; private set; } = [];
        public int PurgeAllCalls { get; private set; }

        public Task<IReadOnlyList<ManagedBackupInfo>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManagedBackupInfo>>([]);
        public Task<string> CreateRollingBackupAsync(CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task ApplyRetentionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string backupPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ManagedBackupPurgeResult> PurgeBookDataFromManagedBackupsAsync(CancellationToken cancellationToken = default)
        {
            PurgeAllCalls++;
            return Task.FromResult(new ManagedBackupPurgeResult(0, 0));
        }
        public Task<ManagedBackupPurgeResult> PurgeBookFromManagedBackupsAsync(long bookId, IReadOnlyCollection<long> bookWordIds, CancellationToken cancellationToken = default)
        {
            PurgedBookId = bookId;
            PurgedWordIds = bookWordIds.ToArray();
            return Task.FromResult(new ManagedBackupPurgeResult(0, 0));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
