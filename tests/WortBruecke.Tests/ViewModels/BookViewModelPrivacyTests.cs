using WortBruecke.App.Infrastructure;
using WortBruecke.App.ViewModels;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;

namespace WortBruecke.Tests.ViewModels;

public sealed class BookViewModelPrivacyTests
{
    [Fact]
    public async Task Analyze_DefaultsToEphemeralDraftAndExplicitSavePersistsIt()
    {
        var repository = new MemoryBookRepository();
        var viewModel = CreateViewModel(repository, new ImmediateExtractor());
        viewModel.Title = "Черновик";
        viewModel.BookText = "Das Meer ist dunkel.";

        await viewModel.AnalyzeCommand.ExecuteAsync();

        Assert.True(viewModel.IsDraft);
        Assert.False(viewModel.IsSaved);
        Assert.Equal(0, repository.SaveCalls);
        Assert.All(viewModel.Words, word => Assert.Equal(0, word.Item.Id));

        await viewModel.SaveBookCommand.ExecuteAsync();

        Assert.True(viewModel.IsSaved);
        Assert.Equal(1, repository.SaveCalls);
        Assert.All(viewModel.Words, word => Assert.True(word.Item.Id > 0));
    }

    [Fact]
    public async Task Analyze_CancellationLeavesNoPersistedText()
    {
        var repository = new MemoryBookRepository();
        var extractor = new BlockingExtractor();
        var viewModel = CreateViewModel(repository, extractor);
        viewModel.BookText = "Das Meer ist dunkel.";
        var operation = viewModel.AnalyzeCommand.ExecuteAsync();
        await extractor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.CancelPendingOperations();
        await operation;

        Assert.Equal(AsyncCommandStatus.Canceled, viewModel.AnalyzeCommand.Status);
        Assert.Equal(BookOperationState.Canceled, viewModel.OperationState);
        Assert.Equal(0, repository.SaveCalls);
        Assert.Empty(viewModel.Words);
    }

    [Fact]
    public async Task Save_LockedRepositoryExposesTypedStateWithoutRawExceptionMessage()
    {
        var repository = new MemoryBookRepository { SaveFailure = new SqliteLockedFixtureException("raw imported book text") };
        var viewModel = CreateViewModel(repository, new ImmediateExtractor());
        viewModel.Title = "Черновик";
        viewModel.BookText = "Das Meer ist dunkel.";
        await viewModel.AnalyzeCommand.ExecuteAsync();

        await viewModel.SaveBookCommand.ExecuteAsync();

        Assert.Equal(BookOperationState.Error, viewModel.OperationState);
        Assert.Equal(OperationErrorKind.StorageBusy, viewModel.OperationError?.Kind);
        Assert.DoesNotContain("raw imported", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.IsSaved);
    }

    [Fact]
    public async Task ConfirmDelete_UsesExplicitTwoStepFlowAndRefreshesSummaries()
    {
        var repository = new MemoryBookRepository();
        repository.Books.Add(new UserBook(7, "Одиссея", "ru-RU", "Море", DateTimeOffset.UtcNow,
        [
            new ExtractedVocabularyItem("море", ["das Meer"], 1, "Море", "noun", 71)
        ]));
        var viewModel = CreateViewModel(repository, new ImmediateExtractor());
        await viewModel.InitializeAsync();
        var summary = Assert.Single(viewModel.RecentBooks);

        viewModel.RequestDeleteCommand.Execute(summary);
        Assert.True(viewModel.HasPendingDeletion);
        Assert.Single(repository.Books);

        await viewModel.ConfirmDeleteCommand.ExecuteAsync();

        Assert.Empty(repository.Books);
        Assert.Empty(viewModel.RecentBooks);
        Assert.False(viewModel.HasPendingDeletion);
        Assert.Equal(7, repository.LastDeletedId);
    }

    [Fact]
    public async Task ConfirmDelete_LockedRepositoryKeepsDataAndConfirmationAvailableForRetry()
    {
        var repository = new MemoryBookRepository { DeleteFailure = new SqliteLockedFixtureException("raw book text") };
        repository.Books.Add(new UserBook(8, "Private", "de-DE", "Privater Text", DateTimeOffset.UtcNow, []));
        var viewModel = CreateViewModel(repository, new ImmediateExtractor());
        await viewModel.InitializeAsync();
        var summary = Assert.Single(viewModel.RecentBooks);
        viewModel.RequestDeleteCommand.Execute(summary);

        await viewModel.ConfirmDeleteCommand.ExecuteAsync();

        Assert.Single(repository.Books);
        Assert.True(viewModel.HasPendingDeletion);
        Assert.Equal(OperationErrorKind.StorageBusy, viewModel.OperationError?.Kind);
        Assert.DoesNotContain("raw book", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmDelete_CommittedCleanupFailureClearsDeletedTextButKeepsRetryConfirmation()
    {
        var repository = new MemoryBookRepository
        {
            DeleteCommittedFailure = new BookPrivacyCleanupException(
                "raw deleted book text",
                new IOException("raw deleted book text"))
        };
        repository.Books.Add(new UserBook(9, "Private", "de-DE", "Privater Text", DateTimeOffset.UtcNow,
        [
            new ExtractedVocabularyItem("Text", ["текст"], 1, "Privater Text", "noun", 91)
        ]));
        var viewModel = CreateViewModel(repository, new ImmediateExtractor());
        await viewModel.InitializeAsync();
        var summary = Assert.Single(viewModel.RecentBooks);
        await viewModel.SelectRecentCommand.ExecuteAsync(summary);
        Assert.Equal("Privater Text", viewModel.BookText);
        viewModel.RequestDeleteCommand.Execute(summary);

        await viewModel.ConfirmDeleteCommand.ExecuteAsync();

        Assert.Empty(repository.Books);
        Assert.Empty(viewModel.BookText);
        Assert.False(viewModel.IsSaved);
        Assert.True(viewModel.HasPendingDeletion);
        Assert.Equal(OperationErrorKind.StorageBusy, viewModel.OperationError?.Kind);
        Assert.DoesNotContain("raw deleted", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavedBookPractice_AppendsOneCanonicalPrivacyNamespacedAttemptPerSubmit()
    {
        var repository = new MemoryBookRepository();
        repository.Books.Add(new UserBook(42, "Ilias", "de-DE", "Das Meer.", DateTimeOffset.UtcNow,
        [
            new ExtractedVocabularyItem("Meer", ["море"], 1, "Das Meer.", "noun", 4201)
        ]));
        var attempts = new RecordingAttemptRepository();
        var viewModel = new BookViewModel(
            repository,
            new ImmediateExtractor(),
            attempts,
            new NoOpKeyboardLayoutService(),
            new AttributionOnlyDictionary());
        await viewModel.InitializeAsync();
        await viewModel.SelectRecentCommand.ExecuteAsync(Assert.Single(viewModel.RecentBooks));
        viewModel.StartPracticeCommand.Execute(null);
        viewModel.Answer = "море";

        await viewModel.CheckCommand.ExecuteAsync();

        var attempt = Assert.Single(attempts.Events);
        Assert.Equal("user.book.42.word.meer", attempt.ContentKey);
        Assert.Equal("book.custom.vocabulary", attempt.ObjectiveId);
        Assert.Equal(GermanLevel.A0, attempt.Level);
        Assert.Equal(LanguageSkill.Vocabulary, attempt.Skill);
        Assert.Equal(ExerciseType.BidirectionalTranslation, attempt.ExerciseFamily);
        Assert.Equal(AttemptDirection.GermanToRussian, attempt.Direction);
        Assert.Equal(EvidenceQuality.Deterministic, attempt.EvidenceQuality);
        Assert.Equal(1, attempt.Score);
    }

    private static BookViewModel CreateViewModel(IBookRepository repository, IBookVocabularyExtractor extractor) => new(
        repository,
        extractor,
        new NoOpProgressRepository(),
        new NoOpKeyboardLayoutService(),
        new AttributionOnlyDictionary());

    private sealed class ImmediateExtractor : IBookVocabularyExtractor
    {
        public Task<VocabularyExtractionResult> ExtractAsync(string text, string sourceCulture, int maximumItems = 40, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VocabularyExtractionResult(
            [
                new ExtractedVocabularyItem("Meer", ["море"], 1, "Das Meer ist dunkel.", "noun")
            ], 4, 3));
    }

    private sealed class BlockingExtractor : IBookVocabularyExtractor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<VocabularyExtractionResult> ExtractAsync(string text, string sourceCulture, int maximumItems = 40, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new VocabularyExtractionResult([], 0, 0);
        }
    }

    private sealed class MemoryBookRepository : IBookRepository
    {
        public List<UserBook> Books { get; } = [];
        public int SaveCalls { get; private set; }
        public long? LastDeletedId { get; private set; }
        public Exception? SaveFailure { get; init; }
        public Exception? DeleteFailure { get; init; }
        public Exception? DeleteCommittedFailure { get; init; }

        public Task<UserBook> SaveAsync(string title, string sourceCulture, string rawText, IReadOnlyList<ExtractedVocabularyItem> vocabulary, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            if (SaveFailure is not null) throw SaveFailure;
            var id = Books.Count == 0 ? 1 : Books.Max(book => book.Id) + 1;
            var nextWordId = id * 100;
            var book = new UserBook(id, title, sourceCulture, rawText, DateTimeOffset.UtcNow,
                vocabulary.Select(word => word with { Id = ++nextWordId }).ToArray());
            Books.Add(book);
            return Task.FromResult(book);
        }

        public Task<UserBook?> GetAsync(long bookId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Books.FirstOrDefault(book => book.Id == bookId));

        public Task<IReadOnlyList<UserBookSummary>> GetRecentSummariesAsync(int limit = 10, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UserBookSummary>>(Books.Take(limit).Select(book => new UserBookSummary(
                book.Id, book.Title, book.SourceCulture, book.CreatedUtc, book.RawText.Length, book.Vocabulary.Count)).ToArray());

        public Task<IReadOnlyList<UserBook>> GetRecentAsync(int limit = 10, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UserBook>>(Books.Take(limit).ToArray());

        public Task<bool> DeleteAsync(long bookId, CancellationToken cancellationToken = default)
        {
            if (DeleteFailure is not null) throw DeleteFailure;
            LastDeletedId = bookId;
            var deleted = Books.RemoveAll(book => book.Id == bookId) > 0;
            if (DeleteCommittedFailure is not null) throw DeleteCommittedFailure;
            return Task.FromResult(deleted);
        }

        public Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
        {
            if (DeleteFailure is not null) throw DeleteFailure;
            var count = Books.Count;
            Books.Clear();
            return Task.FromResult(count);
        }

        public async Task ExportAsync(long bookId, Stream destination, CancellationToken cancellationToken = default) =>
            await destination.WriteAsync("{}"u8.ToArray(), cancellationToken);
    }

    private sealed class SqliteLockedFixtureException(string message) : Exception(message);

    private sealed class RecordingAttemptRepository : IAttemptRepository
    {
        public List<AttemptEvent> Events { get; } = [];
        public Task<bool> AppendAsync(AttemptEvent attempt, CancellationToken cancellationToken = default)
        {
            Events.Add(attempt);
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<AttemptEvent>> GetAsync(AttemptQuery? query = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AttemptEvent>>(Events);
    }

    private sealed class NoOpProgressRepository : IProgressRepository
    {
        public Task RecordAttemptAsync(ContentType contentType, long contentId, bool correct, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ProgressRecord?> GetAsync(ContentType contentType, long contentId, CancellationToken cancellationToken = default) => Task.FromResult<ProgressRecord?>(null);
        public Task<IReadOnlyList<ProgressRecord>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProgressRecord>>([]);
    }

    private sealed class NoOpKeyboardLayoutService : IKeyboardLayoutService
    {
        public IReadOnlyList<LayoutAvailability> CheckInstalled(params string[] cultureCodes) => [];
        public bool SwitchTo(string cultureCode) => true;
    }

    private sealed class AttributionOnlyDictionary : IOfflineDictionaryService
    {
        public string Attribution => "fixture";
        public Task<DictionaryEntry?> LookupAsync(string sourceText, string sourceCulture, string targetCulture, CancellationToken cancellationToken = default) => Task.FromResult<DictionaryEntry?>(null);
        public Task<IReadOnlyDictionary<string, DictionaryEntry>> LookupBatchAsync(IReadOnlyCollection<string> sourceTexts, string sourceCulture, string targetCulture, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, DictionaryEntry>>(new Dictionary<string, DictionaryEntry>());
    }
}
