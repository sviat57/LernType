using WortBruecke.App.ViewModels;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;

namespace WortBruecke.Tests.ViewModels;

public sealed class TrainerReviewPriorityTests
{
    [Fact]
    public async Task StartSession_PlacesDueReviewBeforeNewItems()
    {
        var words = Enumerable.Range(1, 12)
            .Select(index => Word(index, $"слово {index}", $"Wort {index}"))
            .ToArray();
        var dueWord = words[10];
        var due = new ReviewState(
            LearningContentKey.ForWord(dueWord),
            1,
            5,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow.AddDays(-1),
            2,
            0,
            DeterministicSpacedRepetitionScheduler.CurrentVersion);
        var viewModel = new TrainerViewModel(
            new WordContentRepository(words),
            new MemoryAttemptRepository(),
            new NoOpKeyboardLayoutService(),
            new NoOpImageProvider(),
            new DueReviewRepository(due));

        await viewModel.InitializeAsync();
        await viewModel.StartSessionCommand.ExecuteAsync();

        Assert.True(viewModel.IsSessionActive);
        Assert.Equal("Wort 11", viewModel.Prompt);
        Assert.Equal("1 / 10", viewModel.ProgressText);
    }

    private static WordEntry Word(int id, string russian, string german) => new(
        id,
        1,
        "review",
        string.Empty,
        "A1",
        "noun",
        Text(("ru-RU", russian), ("de-DE", german)),
        new LocalizedText());

    private static LocalizedText Text(params (string Culture, string Value)[] values)
    {
        var text = new LocalizedText();
        foreach (var (culture, value) in values) text[culture] = value;
        return text;
    }

    private sealed class WordContentRepository(WordEntry[] words) : IContentRepository
    {
        public Task<IReadOnlyList<Theme>> GetThemesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Theme>>([new Theme(1, "review", "", Text(("ru-RU", "Повторение"), ("de-DE", "Wiederholung")))]);
        public Task<IReadOnlyList<WordEntry>> GetWordsAsync(int? themeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WordEntry>>(words);
        public Task<IReadOnlyList<SentenceEntry>> GetSentencesAsync(int? themeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SentenceEntry>>([]);
        public Task<IReadOnlyList<Passage>> GetPassagesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Passage>>([]);
        public Task<IReadOnlyList<GrammarTask>> GetGrammarTasksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GrammarTask>>([]);
    }

    private sealed class DueReviewRepository(params ReviewState[] due) : IReviewStateRepository
    {
        public Task<ReviewState?> GetAsync(string contentKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(due.FirstOrDefault(item => item.ContentKey == contentKey));
        public Task<IReadOnlyList<ReviewState>> GetDueAsync(DateTimeOffset asOfUtc, int limit = 100, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReviewState>>(due.Take(limit).ToArray());
        public Task UpsertAsync(ReviewState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryAttemptRepository : IAttemptRepository
    {
        public Task<bool> AppendAsync(AttemptEvent attempt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<AttemptEvent>> GetAsync(AttemptQuery? query = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AttemptEvent>>([]);
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
}
