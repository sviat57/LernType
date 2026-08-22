using WortBruecke.App.ViewModels;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;

namespace WortBruecke.Tests.ViewModels;

public sealed class VocabularyTestViewModelTests
{
    [Fact]
    public async Task CompleteTest_ExposesRussianTypoAcceptancesWithNormativeForms()
    {
        var words = Enumerable.Range(1, 20).Select(CreateWord).ToArray();
        var attempts = new RecordingAttemptRepository();
        var viewModel = new VocabularyTestViewModel(
            new WordContentRepository(words),
            attempts,
            new NoOpKeyboardLayoutService());
        await viewModel.InitializeAsync();

        viewModel.StartCommand.Execute(null);
        while (viewModel.IsTestActive)
        {
            var question = Assert.IsType<VocabularyTestQuestion>(viewModel.CurrentQuestion);
            viewModel.Answer = question.AnswerCultureCode == "ru-RU"
                ? RemoveOneLetter(question.ExpectedAnswer)
                : question.ExpectedAnswer;
            await viewModel.SubmitCommand.ExecuteAsync();
        }

        Assert.True(viewModel.IsComplete);
        Assert.True(viewModel.HasLenientAcceptances);
        Assert.Equal(10, viewModel.LenientAcceptances.Count);
        Assert.Empty(viewModel.Mistakes);
        Assert.All(viewModel.LenientAcceptances, accepted =>
        {
            Assert.Equal("DE → RU", accepted.Direction);
            Assert.Equal("Зачтено — проверьте написание", accepted.Message);
            Assert.NotEqual(accepted.ExpectedAnswer, accepted.SubmittedAnswer);
            Assert.StartsWith("профессия", accepted.ExpectedAnswer, StringComparison.Ordinal);
        });
        Assert.Equal(20, attempts.Events.Count);
        Assert.Equal(
            10,
            attempts.Events.Count(attempt =>
                attempt.RubricVersion == LearningEvidenceFactory.RussianVocabularyLeniencyRubric));
        Assert.Equal(
            10,
            attempts.Events.Count(attempt =>
                attempt.RubricVersion == LearningEvidenceFactory.ExactAnswerRubric));
    }

    [Fact]
    public void CompletionView_BindsLenientAcceptancesCollectionAndVisibility()
    {
        var source = File.ReadAllText(RepositoryFile(
            "src",
            "WortBruecke.App",
            "Views",
            "VocabularyTestView.xaml"));

        Assert.Contains("Visibility=\"{Binding HasLenientAcceptances", source, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding LenientAcceptances}\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Message}\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ExpectedAnswer", source, StringComparison.Ordinal);
    }

    private static WordEntry CreateWord(int id)
    {
        var russian = $"профессия{new string('а', id)}";
        return new WordEntry(
            id,
            ThemeId: 1,
            ThemeKey: "vocabulary-test",
            ImagePath: string.Empty,
            Level: "A1",
            PartOfSpeech: "noun",
            Translations: Text(("ru-RU", russian), ("de-DE", $"der Beruf {id}")),
            Examples: []);
    }

    private static string RemoveOneLetter(string value)
    {
        var index = value.IndexOf("сс", StringComparison.Ordinal);
        Assert.True(index >= 0);
        return value.Remove(index, 1);
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

    private static string RepositoryFile(params string[] pathSegments)
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "LernType.sln")))
                {
                    return Path.Combine([directory.FullName, .. pathSegments]);
                }
            }
        }

        throw new DirectoryNotFoundException("LernType.sln was not found above the test working directory.");
    }

    private sealed class WordContentRepository(WordEntry[] words) : IContentRepository
    {
        public Task<IReadOnlyList<Theme>> GetThemesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Theme>>([]);

        public Task<IReadOnlyList<WordEntry>> GetWordsAsync(
            int? themeId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WordEntry>>(words);

        public Task<IReadOnlyList<SentenceEntry>> GetSentencesAsync(
            int? themeId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SentenceEntry>>([]);

        public Task<IReadOnlyList<Passage>> GetPassagesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Passage>>([]);

        public Task<IReadOnlyList<GrammarTask>> GetGrammarTasksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GrammarTask>>([]);
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

    private sealed class NoOpKeyboardLayoutService : IKeyboardLayoutService
    {
        public IReadOnlyList<LayoutAvailability> CheckInstalled(params string[] cultureCodes) => [];
        public bool SwitchTo(string cultureCode) => true;
    }
}
