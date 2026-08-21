using WortBruecke.App.ViewModels;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;

namespace WortBruecke.Tests.ViewModels;

public sealed class CanonicalTextPracticeViewModelTests
{
    [Fact]
    public async Task EachSubmission_AppendsOneStableDetailedEvidenceEvent()
    {
        var passage = new Passage(
            7,
            "canonical-evidence",
            Text(("ru-RU", "Тест"), ("de-DE", "Test")),
            PassageKind.Everyday,
            "A1",
            "test",
            [new PassageSegment(71, 1, Text(("ru-RU", "Привет"), ("de-DE", "Hallo")))]);
        var attempts = new RecordingAttemptRepository();
        var viewModel = new TextPracticeViewModel(
            new PassageContentRepository(passage),
            attempts,
            new NoOpKeyboardLayoutService());
        await viewModel.InitializeAsync();

        viewModel.StartCommand.Execute(null);
        viewModel.Answer = "Hallo";
        viewModel.CheckCommand.Execute(null);

        var attempt = Assert.Single(attempts.Events);
        Assert.Equal("core.passage.canonical-evidence.segment-01", attempt.ContentKey);
        Assert.Equal("a1.mediation.basic-mediation", attempt.ObjectiveId);
        Assert.Equal(LanguageSkill.Mediation, attempt.Skill);
        Assert.Equal(ExerciseType.BidirectionalTranslation, attempt.ExerciseFamily);
        Assert.Equal(AttemptDirection.RussianToGerman, attempt.Direction);
        Assert.Equal(EvidenceQuality.Deterministic, attempt.EvidenceQuality);
        Assert.Equal(1, attempt.Score);
        Assert.NotEqual(Guid.Empty, attempt.SessionId);
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

    private sealed class RecordingAttemptRepository : IAttemptRepository
    {
        public List<AttemptEvent> Events { get; } = [];

        public Task<bool> AppendAsync(AttemptEvent attempt, CancellationToken cancellationToken = default)
        {
            Events.Add(attempt);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<AttemptEvent>> GetAsync(
            AttemptQuery? query = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AttemptEvent>>(Events);
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

    private sealed class NoOpKeyboardLayoutService : IKeyboardLayoutService
    {
        public IReadOnlyList<LayoutAvailability> CheckInstalled(params string[] cultureCodes) => [];
        public bool SwitchTo(string cultureCode) => true;
    }
}
