using WortBruecke.App.ViewModels;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Courses;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;

namespace WortBruecke.Tests.ViewModels;

public sealed class MainViewModelNavigationTests
{
    [Fact]
    public async Task PublicNavigation_ContainsOnlyCourseFirstRoutes()
    {
        await using var viewModel = new MainViewModel(
            new DelayedTrainerContentRepository(),
            new NoOpKeyboardLayoutService(),
            new NoOpImageProvider(),
            new MemorySettingsStore(),
            new UnusedCourseCatalogRepository(),
            new MemoryCourseProgressRepository(),
            new MemoryAttemptRepository(),
            new EmptyReviewStateRepository());

        Assert.Equal(
            ["home", "path", "interactive", "progress", "settings"],
            viewModel.NavigationItems.Select(item => item.Key));
        Assert.DoesNotContain(viewModel.NavigationItems, item => item.Key is "books" or "exams" or "audio" or "grammar" or "telc");
    }

    [Fact]
    public async Task DelayedInteractiveInitialization_CannotOverwriteNewerNavigation()
    {
        var content = new DelayedTrainerContentRepository();
        await using var viewModel = new MainViewModel(
            content,
            new NoOpKeyboardLayoutService(),
            new NoOpImageProvider(),
            new MemorySettingsStore(),
            new UnusedCourseCatalogRepository(),
            new MemoryCourseProgressRepository(),
            new MemoryAttemptRepository());
        viewModel.MarkStorageReady();
        await viewModel.NavigateAsync("interactive");
        var hub = Assert.IsType<InteractiveExercisesViewModel>(viewModel.CurrentViewModel);
        hub.Exercises.Single(item => item.Title == "Слова и предложения").OpenCommand.Execute(null);
        await content.TrainerInitializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await viewModel.NavigateAsync("home");
        var newerScreen = viewModel.CurrentViewModel;
        content.CompleteTrainerInitialization();
        await content.TrainerInitializationReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);

        Assert.Same(newerScreen, viewModel.CurrentViewModel);
        Assert.IsType<HomeViewModel>(viewModel.CurrentViewModel);
        Assert.Equal("Сегодня", viewModel.CurrentTitle);
        Assert.False(viewModel.IsShellBusy);
        Assert.True(viewModel.NavigationItems.Single(item => item.Key == "home").IsSelected);
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> valueFactory) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!timeout.IsCancellationRequested)
        {
            if (valueFactory() is { } value)
            {
                return value;
            }

            await Task.Delay(10, timeout.Token);
        }

        throw new TimeoutException("The expected navigation state was not reached.");
    }

    private sealed class DelayedTrainerContentRepository : IContentRepository
    {
        private readonly TaskCompletionSource<IReadOnlyList<Theme>> _themes =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource TrainerInitializationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource TrainerInitializationReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<Theme>> GetThemesAsync(CancellationToken cancellationToken = default)
        {
            TrainerInitializationStarted.TrySetResult();
            var themes = await _themes.Task.WaitAsync(cancellationToken);
            TrainerInitializationReturned.TrySetResult();
            return themes;
        }

        public Task<IReadOnlyList<WordEntry>> GetWordsAsync(
            int? themeId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WordEntry>>
            ([
                new WordEntry(
                    1,
                    1,
                    "navigation",
                    string.Empty,
                    "A1",
                    "noun",
                    Text(("ru-RU", "река"), ("de-DE", "der Fluss")),
                    [])
            ]);

        public Task<IReadOnlyList<SentenceEntry>> GetSentencesAsync(
            int? themeId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SentenceEntry>>([]);

        public Task<IReadOnlyList<Passage>> GetPassagesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Passage>>([]);

        public Task<IReadOnlyList<GrammarTask>> GetGrammarTasksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GrammarTask>>([]);

        public void CompleteTrainerInitialization() =>
            _themes.TrySetResult([new Theme(1, "navigation", "", Text(("ru-RU", "Тест"), ("de-DE", "Test")))]);
    }

    private static LocalizedText Text(params (string Culture, string Value)[] values)
    {
        var text = new LocalizedText();
        foreach (var (culture, value) in values)
        {
            text[culture] = value;
        }

        return text;
    }

    private sealed class MemoryAttemptRepository : IAttemptRepository
    {
        public Task<bool> AppendAsync(AttemptEvent attempt, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<AttemptEvent>> GetAsync(
            AttemptQuery? query = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AttemptEvent>>([]);
    }

    private sealed class EmptyReviewStateRepository : IReviewStateRepository
    {
        public Task<ReviewState?> GetAsync(string contentKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReviewState?>(null);

        public Task<IReadOnlyList<ReviewState>> GetDueAsync(
            DateTimeOffset asOfUtc,
            int limit = 100,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReviewState>>([]);

        public Task UpsertAsync(ReviewState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class UnusedCourseCatalogRepository : ICourseCatalogRepository
    {
        public Task<CourseCatalog> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<CourseCatalog>(new NotSupportedException());
    }

    private sealed class MemoryCourseProgressRepository : ICourseProgressRepository
    {
        public Task<IReadOnlyList<CourseNodeProgress>> GetCourseAsync(string courseId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CourseNodeProgress>>([]);

        public Task UpsertAsync(CourseNodeProgress progress, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<CourseResumeState?> GetResumeAsync(string courseId, CancellationToken cancellationToken = default) =>
            Task.FromResult<CourseResumeState?>(null);

        public Task SaveResumeAsync(CourseResumeState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpProgressRepository : IProgressRepository
    {
        public Task RecordAttemptAsync(
            ContentType contentType,
            long contentId,
            bool correct,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ProgressRecord?> GetAsync(
            ContentType contentType,
            long contentId,
            CancellationToken cancellationToken = default) => Task.FromResult<ProgressRecord?>(null);

        public Task<IReadOnlyList<ProgressRecord>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProgressRecord>>([]);
    }

    private sealed class NoOpKeyboardLayoutService : IKeyboardLayoutService
    {
        public IReadOnlyList<LayoutAvailability> CheckInstalled(params string[] cultureCodes) => [];
        public bool SwitchTo(string cultureCode) => true;
    }

    private sealed class NoOpImageProvider : IImageProvider
    {
        public string? Resolve(string relativePath) => null;
    }

    private sealed class NoOpLanguageAnalysisService : ILanguageAnalysisService
    {
        public Task<TelcAnalysis> AnalyzeTelcAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromException<TelcAnalysis>(new NotSupportedException());

        public Task<string> AnalyzeGrammarAsync(
            string sourceText,
            string instruction,
            string response,
            CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new NotSupportedException());
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class EmptyBookRepository : IBookRepository
    {
        public Task<UserBook> SaveAsync(
            string title,
            string sourceCulture,
            string rawText,
            IReadOnlyList<ExtractedVocabularyItem> vocabulary,
            CancellationToken cancellationToken = default) =>
            Task.FromException<UserBook>(new NotSupportedException());

        public Task<UserBook?> GetAsync(long bookId, CancellationToken cancellationToken = default) =>
            Task.FromResult<UserBook?>(null);

        public Task<IReadOnlyList<UserBookSummary>> GetRecentSummariesAsync(
            int limit = 10,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UserBookSummary>>([]);

        public Task<IReadOnlyList<UserBook>> GetRecentAsync(
            int limit = 10,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UserBook>>([]);

        public Task<bool> DeleteAsync(long bookId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> DeleteAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task ExportAsync(
            long bookId,
            Stream destination,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptyBookExtractor : IBookVocabularyExtractor
    {
        public Task<VocabularyExtractionResult> ExtractAsync(
            string text,
            string sourceCulture,
            int maximumItems = 40,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new VocabularyExtractionResult([], 0, 0));
    }

    private sealed class EmptyDictionary : IOfflineDictionaryService
    {
        public string Attribution => "fixture";

        public Task<DictionaryEntry?> LookupAsync(
            string sourceText,
            string sourceCulture,
            string targetCulture,
            CancellationToken cancellationToken = default) => Task.FromResult<DictionaryEntry?>(null);

        public Task<IReadOnlyDictionary<string, DictionaryEntry>> LookupBatchAsync(
            IReadOnlyCollection<string> sourceTexts,
            string sourceCulture,
            string targetCulture,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, DictionaryEntry>>(
                new Dictionary<string, DictionaryEntry>());
    }

    private sealed class EmptyLearningProgressRepository : ILearningProgressRepository
    {
        public Task RecordAsync(LearningAttempt attempt, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<LearningAttempt>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LearningAttempt>>([]);
    }

    private sealed class EmptyExamBlueprintRepository : IExamBlueprintRepository
    {
        public Task<ExamBlueprintCatalog> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExamBlueprintCatalog(
                new DateOnly(2026, 8, 22),
                "fixture",
                "fixture",
                []));
    }
}
