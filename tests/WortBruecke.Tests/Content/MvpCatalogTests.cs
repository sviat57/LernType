using WortBruecke.Infrastructure.Content;

namespace WortBruecke.Tests.Content;

public sealed class MvpCatalogTests
{
    [Fact]
    public async Task Catalog_ContainsRunnableContentForEveryA0ToC2StageAndResolvableImages()
    {
        var solutionRoot = FindSolutionRoot();
        var appRoot = Path.Combine(solutionRoot, "src", "WortBruecke.App");
        var catalog = await new JsonContentLoader().LoadAsync(Path.Combine(appRoot, "Content"));

        Assert.Equal(10, catalog.Themes.Count);
        Assert.InRange(catalog.Words.Count, 150, 250);
        Assert.InRange(catalog.Sentences.Count, 70, 140);
        Assert.InRange(catalog.Passages.Count, 7, 14);
        Assert.InRange(catalog.GrammarTasks.Count, 7, 24);

        foreach (var theme in catalog.Themes)
        {
            var themeWords = catalog.Words.Count(word => word.ThemeId == theme.Id);
            Assert.InRange(themeWords, 15, 25);
        }

        foreach (var word in catalog.Words)
        {
            Assert.False(string.IsNullOrWhiteSpace(word.Translations.For("ru-RU")));
            Assert.False(string.IsNullOrWhiteSpace(word.Translations.For("de-DE")));
            Assert.True(File.Exists(Path.Combine(appRoot, word.ImagePath)), $"Missing image: {word.ImagePath}");
        }

        foreach (var sentence in catalog.Sentences)
        {
            Assert.Contains(sentence.Level, new[] { "A0", "A1", "A2", "B1", "B2", "C1", "C2" });
            Assert.Contains(catalog.Themes, theme => theme.Id == sentence.ThemeId);
            Assert.False(string.IsNullOrWhiteSpace(sentence.Translations.For("ru-RU")));
            Assert.False(string.IsNullOrWhiteSpace(sentence.Translations.For("de-DE")));
        }

        var completePath = new[] { "A0", "A1", "A2", "B1", "B2", "C1", "C2" };
        Assert.Equal(completePath, catalog.Sentences.Select(item => item.Level).Distinct().Order().ToArray());
        Assert.Equal(completePath, catalog.Passages.Select(item => item.Level).Distinct().Order().ToArray());
        Assert.Equal(completePath, catalog.GrammarTasks.Select(item => item.Level).Distinct().Order().ToArray());
        Assert.Equal(20, catalog.Words.Count(item => item.Level == "A0"));
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LernType.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the LernType solution root.");
    }
}
