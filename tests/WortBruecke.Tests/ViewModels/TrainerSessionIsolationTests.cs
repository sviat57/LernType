using WortBruecke.App.ViewModels;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;

namespace WortBruecke.Tests.ViewModels;

public sealed class TrainerSessionIsolationTests
{
    [Fact]
    public async Task RussianVocabularyTypo_IsAcceptedWithVisibleFeedbackAndLenientRubric()
    {
        var repository = new MemoryAttemptRepository();
        var viewModel = CreateTrainer(
            Word(402, "A2", "профессия", "der Beruf"),
            repository);

        await viewModel.InitializeAsync();
        viewModel.Prepare(new PracticeLaunchRequest(
            GermanLevel.A2,
            PracticeUnit.Word,
            TranslationDirection.TargetToSource));
        await viewModel.StartSessionCommand.ExecuteAsync();
        viewModel.Answer = "професия";
        await viewModel.CheckAnswerCommand.ExecuteAsync();

        Assert.True(viewModel.IsCorrect);
        Assert.Equal("Зачтено — проверьте написание", viewModel.FeedbackTitle);
        Assert.Contains("профессия", viewModel.FeedbackDetail, StringComparison.Ordinal);
        Assert.Equal(LearningEvidenceFactory.RussianVocabularyLeniencyRubric, Assert.Single(repository.Attempts).RubricVersion);
    }

    [Fact]
    public async Task RussianCuratedAlias_IsAcceptedButGermanMissingArticleRemainsWrong()
    {
        var aliases = new LocalizedAnswerSet { ["ru-RU"] = ["речка"] };
        var word = Word(604, "A2", "река", "der Fluss") with { AcceptedAnswers = aliases };
        var viewModel = CreateTrainer(word, new MemoryAttemptRepository());
        await viewModel.InitializeAsync();

        viewModel.Prepare(new PracticeLaunchRequest(
            GermanLevel.A2,
            PracticeUnit.Word,
            TranslationDirection.TargetToSource));
        await viewModel.StartSessionCommand.ExecuteAsync();
        viewModel.Answer = "речка";
        await viewModel.CheckAnswerCommand.ExecuteAsync();
        Assert.True(viewModel.IsCorrect);
        Assert.Equal("Верно — допустимый вариант", viewModel.FeedbackTitle);

        viewModel.Prepare(new PracticeLaunchRequest(
            GermanLevel.A2,
            PracticeUnit.Word,
            TranslationDirection.SourceToTarget));
        await viewModel.StartSessionCommand.ExecuteAsync();
        viewModel.Answer = "Fluss";
        await viewModel.CheckAnswerCommand.ExecuteAsync();
        Assert.False(viewModel.IsCorrect);
    }

    [Fact]
    public async Task ImageBidirectional_RussianAliasAndTypoRequireAnExactGermanAnswer()
    {
        var aliasAttempts = new MemoryAttemptRepository();
        var river = Word(604, "A2", "река", "der Fluss", "images/river.svg") with
        {
            AcceptedAnswers = new LocalizedAnswerSet { ["ru-RU"] = ["речка"] }
        };
        var aliasViewModel = await StartImageSessionAsync(river, aliasAttempts);

        await SubmitImageAnswersAsync(aliasViewModel, "речка", "der Fluss");

        Assert.True(aliasViewModel.IsCorrect);
        Assert.Equal("Верно — допустимый вариант", aliasViewModel.FeedbackTitle);
        Assert.Equal(LearningEvidenceFactory.RussianVocabularyLeniencyRubric, Assert.Single(aliasAttempts.Attempts).RubricVersion);
        Assert.Equal(ExerciseType.ImageAssociation, aliasAttempts.Attempts[0].ExerciseFamily);
        Assert.Equal(AttemptDirection.Bidirectional, aliasAttempts.Attempts[0].Direction);

        var typoAttempts = new MemoryAttemptRepository();
        var profession = Word(402, "A2", "профессия", "der Beruf", "images/profession.svg");
        var typoViewModel = await StartImageSessionAsync(profession, typoAttempts);

        await SubmitImageAnswersAsync(typoViewModel, "професия", "der Beruf");

        Assert.True(typoViewModel.IsCorrect);
        Assert.Equal("Зачтено — проверьте написание", typoViewModel.FeedbackTitle);
        Assert.Contains("профессия", typoViewModel.FeedbackDetail, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(typoAttempts.Attempts).Score);

        var strictGermanAttempts = new MemoryAttemptRepository();
        var strictGermanViewModel = await StartImageSessionAsync(river, strictGermanAttempts);

        await SubmitImageAnswersAsync(strictGermanViewModel, "речка", "der Flussx");

        Assert.False(strictGermanViewModel.IsCorrect);
        Assert.Equal("Проверьте ответ", strictGermanViewModel.FeedbackTitle);
        Assert.Equal(0, Assert.Single(strictGermanAttempts.Attempts).Score);
    }

    [Fact]
    public async Task Prepare_WordProductionKeepsExactLevelAndCompletesWithoutTextRoute()
    {
        var words = Enumerable.Range(1, 12)
            .Select(index => Word(index, "A1", $"слово {index}", $"das Wort {index}"))
            .Concat([Word(100, "A2", "чужой уровень", "die Fremdstufe")])
            .ToArray();
        var viewModel = new TrainerViewModel(
            new WordContentRepository(words),
            new MemoryAttemptRepository(),
            new NoOpKeyboardLayoutService(),
            new NoOpImageProvider());
        var textRoutes = 0;
        viewModel.TextPracticeRequested += _ => textRoutes++;

        await viewModel.InitializeAsync();
        viewModel.Prepare(new PracticeLaunchRequest(
            GermanLevel.A1,
            PracticeUnit.Word,
            TranslationDirection.SourceToTarget));
        await viewModel.StartSessionCommand.ExecuteAsync();

        Assert.Equal("A1", viewModel.SelectedCefr?.Level);
        Assert.Equal(PracticeUnit.Word, viewModel.SelectedPracticeUnit?.Unit);
        Assert.Equal(2, viewModel.SelectedDifficulty?.Level);
        for (var index = 0; index < 10; index++)
        {
            Assert.DoesNotContain("чужой", viewModel.Prompt, StringComparison.OrdinalIgnoreCase);
            var number = int.Parse(viewModel.Prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
            viewModel.Answer = $"das Wort {number}";
            await viewModel.CheckAnswerCommand.ExecuteAsync();
            viewModel.NextCommand.Execute(null);
        }

        Assert.True(viewModel.IsComplete);
        Assert.False(viewModel.IsSessionActive);
        Assert.Equal(0, textRoutes);
    }

    [Fact]
    public async Task ClearLevelContext_ResetsActiveSessionAndRemovesExactLevelFilter()
    {
        var viewModel = CreateTrainer(
            Word(1, "A1", "река", "der Fluss"),
            new MemoryAttemptRepository());
        await viewModel.InitializeAsync();
        viewModel.Prepare(new PracticeLaunchRequest(
            GermanLevel.A1,
            PracticeUnit.Word,
            TranslationDirection.TargetToSource));
        await viewModel.StartSessionCommand.ExecuteAsync();

        viewModel.ClearLevelContext();

        Assert.False(viewModel.HasLevelContext);
        Assert.False(viewModel.IsSessionActive);
        Assert.False(viewModel.IsComplete);
        Assert.Null(viewModel.SelectedCefr?.Level);
        Assert.Null(viewModel.SelectedTheme?.Id);
        Assert.True(viewModel.IsSelectionVisible);
    }

    private static async Task<TrainerViewModel> StartImageSessionAsync(
        WordEntry word,
        MemoryAttemptRepository attempts)
    {
        var viewModel = new TrainerViewModel(
            new WordContentRepository([word]),
            attempts,
            new NoOpKeyboardLayoutService(),
            new FixtureImageProvider());
        await viewModel.InitializeAsync();
        viewModel.SelectedDifficulty = viewModel.Difficulties.Single(option => option.Level == 3);

        await viewModel.StartSessionCommand.ExecuteAsync();

        Assert.True(viewModel.IsLevelThree);
        Assert.True(viewModel.HasImage);
        Assert.True(viewModel.IsSourceStep);
        return viewModel;
    }

    private static async Task SubmitImageAnswersAsync(
        TrainerViewModel viewModel,
        string russian,
        string german)
    {
        viewModel.SourceAnswer = russian;
        viewModel.AdvanceLanguageCommand.Execute(null);
        Assert.True(viewModel.IsTargetStep);
        viewModel.TargetAnswer = german;
        await viewModel.CheckAnswerCommand.ExecuteAsync();
    }

    private static WordEntry Word(
        int id,
        string level,
        string russian,
        string german,
        string imagePath = "") => new(
        id,
        1,
        "isolation",
        imagePath,
        level,
        "noun",
        Text(("ru-RU", russian), ("de-DE", german)),
        []);

    private static TrainerViewModel CreateTrainer(WordEntry word, MemoryAttemptRepository repository) => new(
        new WordContentRepository([word]),
        repository,
        new NoOpKeyboardLayoutService(),
        new NoOpImageProvider());

    private static LocalizedText Text(params (string Culture, string Value)[] values)
    {
        var text = new LocalizedText();
        foreach (var (culture, value) in values) text[culture] = value;
        return text;
    }

    private sealed class WordContentRepository(WordEntry[] words) : IContentRepository
    {
        public Task<IReadOnlyList<Theme>> GetThemesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Theme>>([new Theme(1, "isolation", "", Text(("ru-RU", "Тест"), ("de-DE", "Test")))]);
        public Task<IReadOnlyList<WordEntry>> GetWordsAsync(int? themeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WordEntry>>(words);
        public Task<IReadOnlyList<SentenceEntry>> GetSentencesAsync(int? themeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SentenceEntry>>([]);
        public Task<IReadOnlyList<Passage>> GetPassagesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Passage>>([]);
        public Task<IReadOnlyList<GrammarTask>> GetGrammarTasksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GrammarTask>>([]);
    }

    private sealed class MemoryAttemptRepository : IAttemptRepository
    {
        public List<AttemptEvent> Attempts { get; } = [];
        public Task<bool> AppendAsync(AttemptEvent attempt, CancellationToken cancellationToken = default)
        {
            Attempts.Add(attempt);
            return Task.FromResult(true);
        }
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

    private sealed class FixtureImageProvider : IImageProvider
    {
        public string? Resolve(string relativePath) => string.IsNullOrWhiteSpace(relativePath)
            ? null
            : $"fixture://{relativePath}";
    }
}
