using WortBruecke.App.ViewModels;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;

namespace WortBruecke.Tests.ViewModels;

public sealed class LearningPathLevelRoutingTests
{
    [Fact]
    public async Task EveryLevelCard_OpensItsExactLevelEvenWhenSequentialPathIsLocked()
    {
        var opened = new List<LevelStudyRequest>();
        var viewModel = new LearningPathViewModel(
            new PassagePerLevelContentRepository(),
            new EmptyAttemptRepository(),
            new EmptyExamRepository(),
            _ => { },
            opened.Add);

        await viewModel.InitializeAsync();

        Assert.Equal(Enum.GetValues<GermanLevel>().Length, viewModel.Levels.Count);
        var lockedB2 = viewModel.Levels.Single(item => item.LevelKey == GermanLevel.B2);
        Assert.False(lockedB2.IsUnlocked);
        Assert.True(lockedB2.OpenPracticeCommand.CanExecute(null));

        lockedB2.OpenPracticeCommand.Execute(null);

        Assert.Equal(new LevelStudyRequest(GermanLevel.B2), Assert.Single(opened));
    }

    [Fact]
    public async Task LevelCards_HaveDistinctAccessibleNames()
    {
        var viewModel = new LearningPathViewModel(
            new PassagePerLevelContentRepository(),
            new EmptyAttemptRepository(),
            new EmptyExamRepository(),
            _ => { },
            _ => { });

        await viewModel.InitializeAsync();

        Assert.Equal(viewModel.Levels.Count, viewModel.Levels.Select(item => item.PracticeAutomationName).Distinct().Count());
        Assert.Contains("A1", viewModel.Levels.Single(item => item.LevelKey == GermanLevel.A1).PracticeAutomationName);
        Assert.Contains("Pre-A1", viewModel.Levels.Single(item => item.LevelKey == GermanLevel.A0).PracticeAutomationName);
    }

    private sealed class PassagePerLevelContentRepository : IContentRepository
    {
        private static readonly Passage[] Passages = Enum.GetValues<GermanLevel>()
            .Select((level, index) => new Passage(
                index + 1,
                $"path-{level.ToString().ToLowerInvariant()}",
                Text(("ru-RU", level.ToString()), ("de-DE", level.ToString())),
                PassageKind.Everyday,
                level.ToString(),
                "path",
                [new PassageSegment(index + 100, 1, Text(("ru-RU", "Текст."), ("de-DE", "Text.")))]))
            .ToArray();

        public Task<IReadOnlyList<Theme>> GetThemesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Theme>>([]);

        public Task<IReadOnlyList<WordEntry>> GetWordsAsync(int? themeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WordEntry>>([]);

        public Task<IReadOnlyList<SentenceEntry>> GetSentencesAsync(int? themeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SentenceEntry>>([]);

        public Task<IReadOnlyList<Passage>> GetPassagesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Passage>>(Passages);

        public Task<IReadOnlyList<GrammarTask>> GetGrammarTasksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GrammarTask>>([]);
    }

    private sealed class EmptyAttemptRepository : IAttemptRepository
    {
        public Task<bool> AppendAsync(AttemptEvent attempt, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<AttemptEvent>> GetAsync(
            AttemptQuery? query = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AttemptEvent>>([]);
    }

    private sealed class EmptyExamRepository : IExamBlueprintRepository
    {
        public Task<ExamBlueprintCatalog> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExamBlueprintCatalog(
                new DateOnly(2026, 8, 22),
                "Тестовый каталог",
                "Тест",
                []));
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
}
