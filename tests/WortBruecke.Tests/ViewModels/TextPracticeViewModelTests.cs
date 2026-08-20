using WortBruecke.App.ViewModels;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;

namespace WortBruecke.Tests.ViewModels;

public sealed class TextPracticeViewModelTests
{
    [Fact]
    public async Task RevisitingAndResubmittingSegment_DoesNotInflateCompletionScore()
    {
        var passage = new Passage(
            41,
            "navigation-score",
            Text(("ru-RU", "Навигация"), ("de-DE", "Navigation")),
            PassageKind.Everyday,
            "A1",
            "audit",
            [
                new PassageSegment(411, 1, Text(("ru-RU", "Привет"), ("de-DE", "Hallo"))),
                new PassageSegment(412, 2, Text(("ru-RU", "Спасибо"), ("de-DE", "Danke")))
            ]);
        var progress = new RecordingProgressRepository();
        var viewModel = new TextPracticeViewModel(
            new PassageContentRepository(passage),
            progress,
            new NoOpKeyboardLayoutService());
        await viewModel.InitializeAsync();

        viewModel.StartCommand.Execute(null);
        Submit(viewModel, "Hallo");
        viewModel.NextCommand.Execute(null);
        viewModel.PreviousCommand.Execute(null);
        Submit(viewModel, "Hallo");
        viewModel.NextCommand.Execute(null);
        Submit(viewModel, "Danke");
        viewModel.NextCommand.Execute(null);

        Assert.True(viewModel.IsComplete);
        Assert.Equal("2 из 2 точно", viewModel.CompletionTitle);
        Assert.Equal(3, progress.Attempts.Count);
    }

    private static void Submit(TextPracticeViewModel viewModel, string answer)
    {
        viewModel.Answer = answer;
        Assert.True(viewModel.CheckCommand.CanExecute(null));
        viewModel.CheckCommand.Execute(null);
        Assert.True(viewModel.ShowFeedback);
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

    private sealed class PassageContentRepository(params Passage[] passages) : IContentRepository
    {
        public Task<IReadOnlyList<Theme>> GetThemesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Theme>>([]);

        public Task<IReadOnlyList<WordEntry>> GetWordsAsync(int? themeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WordEntry>>([]);

        public Task<IReadOnlyList<SentenceEntry>> GetSentencesAsync(int? themeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SentenceEntry>>([]);

        public Task<IReadOnlyList<Passage>> GetPassagesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Passage>>(passages);

        public Task<IReadOnlyList<GrammarTask>> GetGrammarTasksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GrammarTask>>([]);
    }

    private sealed class RecordingProgressRepository : IProgressRepository
    {
        public List<(ContentType Type, long Id, bool Correct)> Attempts { get; } = [];

        public Task RecordAttemptAsync(
            ContentType contentType,
            long contentId,
            bool correct,
            CancellationToken cancellationToken = default)
        {
            Attempts.Add((contentType, contentId, correct));
            return Task.CompletedTask;
        }

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
}
