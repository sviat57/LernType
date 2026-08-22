using WortBruecke.App.ViewModels;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;

namespace WortBruecke.Tests.ViewModels;

public sealed class LevelStudyViewModelTests
{
    [Fact]
    public async Task PrepareAsync_BuildsOnlyRequestedLevelAndKeepsModulesIndependent()
    {
        var repository = new MemoryContentRepository(
            words: [Word(1, "A1"), Word(2, "B1")],
            sentences: [Sentence(1, "A1"), Sentence(2, "B1")],
            passages: [Passage(1, "A1"), Passage(2, "B1")],
            grammar: [Grammar(1, "A1"), Grammar(2, "B1")]);
        var launches = new List<LevelModuleLaunch>();
        var viewModel = new LevelStudyViewModel(
            repository,
            new MemoryAttemptRepository(),
            launches.Add,
            () => { },
            audioPracticeAvailable: false);

        await viewModel.PrepareAsync(new LevelStudyRequest(GermanLevel.A1));

        Assert.Equal(GermanLevel.A1, viewModel.Level);
        Assert.Equal("A1", viewModel.LevelLabel);
        Assert.Equal(7, viewModel.Modules.Count);
        Assert.Equal("1 слов", Module(viewModel, LevelModuleKind.WordGermanToRussian).ContentText);
        Assert.Equal("1 предложений", Module(viewModel, LevelModuleKind.SentenceRussianToGerman).ContentText);
        Assert.Equal("1 текстов", Module(viewModel, LevelModuleKind.Text).ContentText);
        Assert.True(Module(viewModel, LevelModuleKind.Grammar).IsOptional);
        Assert.False(Module(viewModel, LevelModuleKind.Audio).IsAvailable);

        Module(viewModel, LevelModuleKind.WordRussianToGerman).LaunchCommand.Execute(null);

        var launch = Assert.Single(launches);
        Assert.Equal(new LevelModuleLaunch(GermanLevel.A1, LevelModuleKind.WordRussianToGerman), launch);
    }

    [Fact]
    public async Task Continue_ChoosesFirstUnmasteredPublishedModuleWithContent()
    {
        var repository = new MemoryContentRepository(
            sentences: [Sentence(1, "B2")],
            passages: [Passage(1, "B2")],
            grammar: [Grammar(1, "B2")]);
        LevelModuleLaunch? launch = null;
        var viewModel = new LevelStudyViewModel(
            repository,
            new MemoryAttemptRepository(),
            value => launch = value,
            () => { },
            audioPracticeAvailable: true);

        await viewModel.PrepareAsync(new LevelStudyRequest(GermanLevel.B2));
        viewModel.ContinueCommand.Execute(null);

        Assert.Equal(new LevelModuleLaunch(GermanLevel.B2, LevelModuleKind.SentenceGermanToRussian), launch);
        Assert.False(Module(viewModel, LevelModuleKind.WordGermanToRussian).IsAvailable);
        Assert.True(Module(viewModel, LevelModuleKind.Audio).IsOptional);
    }

    [Fact]
    public async Task Continue_SkipsMasteredDirectionButNotUnmasteredReverseDirection()
    {
        var objective = GermanCurriculum.CreateDefault().Levels
            .Single(level => level.Level == GermanLevel.A1).Objectives
            .Single(item => item.Skill == LanguageSkill.Vocabulary);
        var now = DateTimeOffset.UtcNow;
        var attempts = Enumerable.Range(1, objective.MinimumAttempts)
            .Select(index => Attempt(
                objective,
                $"core.word.test.word-{index}",
                AttemptDirection.GermanToRussian,
                now.AddDays(-(index % 2))))
            .ToArray();
        LevelModuleLaunch? launch = null;
        var viewModel = new LevelStudyViewModel(
            new MemoryContentRepository(words: [Word(1, "A1"), Word(2, "A1"), Word(3, "A1")]),
            new MemoryAttemptRepository(attempts),
            value => launch = value,
            () => { },
            audioPracticeAvailable: false);

        await viewModel.PrepareAsync(new LevelStudyRequest(GermanLevel.A1));
        viewModel.ContinueCommand.Execute(null);

        Assert.True(Module(viewModel, LevelModuleKind.WordGermanToRussian).IsMastered);
        Assert.False(Module(viewModel, LevelModuleKind.WordRussianToGerman).IsMastered);
        Assert.Equal(new LevelModuleLaunch(GermanLevel.A1, LevelModuleKind.WordRussianToGerman), launch);
    }

    [Fact]
    public async Task OverlappingPrepareAsync_DoesNotLetOlderLevelReplaceNewerLevelSnapshot()
    {
        var repository = new OverlappingLevelContentRepository();
        var viewModel = new LevelStudyViewModel(
            repository,
            new MemoryAttemptRepository(),
            _ => { },
            () => { },
            audioPracticeAvailable: false);

        var older = viewModel.PrepareAsync(new LevelStudyRequest(GermanLevel.A1));
        await repository.FirstWordLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.PrepareAsync(new LevelStudyRequest(GermanLevel.B1));
        repository.CompleteFirstWordLoad();
        await older;

        Assert.Equal(GermanLevel.B1, viewModel.Level);
        Assert.Equal("B1", viewModel.LevelLabel);
        Assert.Equal("1 слов", Module(viewModel, LevelModuleKind.WordGermanToRussian).ContentText);
    }

    [Theory]
    [InlineData(LevelModuleKind.WordGermanToRussian, PracticeUnit.Word, TranslationDirection.TargetToSource)]
    [InlineData(LevelModuleKind.WordRussianToGerman, PracticeUnit.Word, TranslationDirection.SourceToTarget)]
    [InlineData(LevelModuleKind.SentenceGermanToRussian, PracticeUnit.Sentence, TranslationDirection.TargetToSource)]
    [InlineData(LevelModuleKind.SentenceRussianToGerman, PracticeUnit.Sentence, TranslationDirection.SourceToTarget)]
    public void TrainerModuleMapping_PreservesUnitAndDirection(
        LevelModuleKind module,
        PracticeUnit expectedUnit,
        TranslationDirection expectedDirection)
    {
        var launch = new LevelModuleLaunch(GermanLevel.A2, module);

        var mapped = launch.TryGetPracticeRequest(out var request);

        Assert.True(mapped);
        Assert.Equal(new PracticeLaunchRequest(GermanLevel.A2, expectedUnit, expectedDirection), request);
    }

    [Theory]
    [InlineData(LevelModuleKind.Text)]
    [InlineData(LevelModuleKind.Grammar)]
    [InlineData(LevelModuleKind.Audio)]
    public void NonTrainerModuleMapping_RemainsExplicit(LevelModuleKind module)
    {
        var mapped = new LevelModuleLaunch(GermanLevel.C1, module).TryGetPracticeRequest(out var request);

        Assert.False(mapped);
        Assert.Null(request);
    }

    private static LevelStudyModuleViewModel Module(LevelStudyViewModel viewModel, LevelModuleKind kind) =>
        viewModel.Modules.Single(item => item.Kind == kind);

    private static AttemptEvent Attempt(
        LearningObjective objective,
        string contentKey,
        AttemptDirection direction,
        DateTimeOffset completedAtUtc) => new(
        Guid.NewGuid(),
        contentKey,
        1,
        objective.Level,
        objective.Skill,
        ExerciseType.BidirectionalTranslation,
        direction,
        1,
        AssessmentMode.Practice,
        completedAtUtc.AddSeconds(-5),
        completedAtUtc,
        Guid.NewGuid(),
        LearningEvidenceFactory.ExactAnswerRubric,
        EvidenceQuality.Deterministic,
        objective.Id);

    private static WordEntry Word(int id, string level) => new(
        id,
        1,
        "test",
        string.Empty,
        level,
        "noun",
        Text(("ru-RU", $"слово {id}"), ("de-DE", $"Wort {id}")),
        new LocalizedText());

    private static SentenceEntry Sentence(int id, string level) => new(
        id,
        1,
        "test",
        level,
        Text(("ru-RU", $"Предложение {id}."), ("de-DE", $"Satz {id}.")));

    private static Passage Passage(int id, string level) => new(
        id,
        $"passage-{id}",
        Text(("ru-RU", $"Текст {id}"), ("de-DE", $"Text {id}")),
        PassageKind.Everyday,
        level,
        "test",
        [new PassageSegment(id * 10, 1, Text(("ru-RU", "Фрагмент."), ("de-DE", "Abschnitt.")))]);

    private static GrammarTask Grammar(int id, string level) => new(
        id,
        $"grammar-{id}",
        level,
        "Составьте фразу.",
        Text(("ru-RU", "Инструкция"), ("de-DE", "Anweisung")),
        "token:ist");

    private static LocalizedText Text(params (string Culture, string Value)[] values)
    {
        var text = new LocalizedText();
        foreach (var (culture, value) in values)
        {
            text[culture] = value;
        }
        return text;
    }

    private sealed class MemoryContentRepository(
        WordEntry[]? words = null,
        SentenceEntry[]? sentences = null,
        Passage[]? passages = null,
        GrammarTask[]? grammar = null) : IContentRepository
    {
        public Task<IReadOnlyList<Theme>> GetThemesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Theme>>([]);

        public Task<IReadOnlyList<WordEntry>> GetWordsAsync(int? themeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WordEntry>>(words ?? []);

        public Task<IReadOnlyList<SentenceEntry>> GetSentencesAsync(int? themeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SentenceEntry>>(sentences ?? []);

        public Task<IReadOnlyList<Passage>> GetPassagesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Passage>>(passages ?? []);

        public Task<IReadOnlyList<GrammarTask>> GetGrammarTasksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GrammarTask>>(grammar ?? []);
    }

    private sealed class MemoryAttemptRepository(params AttemptEvent[] attempts) : IAttemptRepository
    {
        public Task<bool> AppendAsync(AttemptEvent attempt, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<AttemptEvent>> GetAsync(
            AttemptQuery? query = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AttemptEvent>>(attempts);
    }

    private sealed class OverlappingLevelContentRepository : IContentRepository
    {
        private readonly TaskCompletionSource<IReadOnlyList<WordEntry>> _firstWords =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _wordLoadCount;

        public TaskCompletionSource FirstWordLoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<Theme>> GetThemesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Theme>>([]);

        public Task<IReadOnlyList<WordEntry>> GetWordsAsync(
            int? themeId = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _wordLoadCount) == 1)
            {
                FirstWordLoadStarted.TrySetResult();
                return _firstWords.Task.WaitAsync(cancellationToken);
            }

            return Task.FromResult<IReadOnlyList<WordEntry>>([Word(3, "B1")]);
        }

        public Task<IReadOnlyList<SentenceEntry>> GetSentencesAsync(
            int? themeId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SentenceEntry>>([]);

        public Task<IReadOnlyList<Passage>> GetPassagesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Passage>>([]);

        public Task<IReadOnlyList<GrammarTask>> GetGrammarTasksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GrammarTask>>([]);

        public void CompleteFirstWordLoad() =>
            _firstWords.TrySetResult([Word(1, "A1"), Word(2, "A1")]);
    }
}
