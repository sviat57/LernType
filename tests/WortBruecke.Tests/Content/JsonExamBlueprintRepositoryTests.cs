using WortBruecke.Infrastructure.Content;

namespace WortBruecke.Tests.Content;

public sealed class JsonExamBlueprintRepositoryTests
{
    [Fact]
    public async Task Load_ProducesTypedCatalogWithResolvedProvidersScoringAndSources()
    {
        var root = FindSolutionRoot();
        var repository = new JsonExamBlueprintRepository(Path.Combine(root, "src", "WortBruecke.App", "Content"));

        var catalog = await repository.LoadAsync();

        Assert.Equal(new DateOnly(2026, 8, 20), catalog.LastVerified);
        Assert.Equal(14, catalog.Exams.Count);
        var goetheB2 = Assert.Single(catalog.Exams, exam => exam.Id == "goethe-b2");
        Assert.Equal("Goethe-Institut", goetheB2.ProviderName);
        Assert.Equal(["A2"], catalog.Exams.Single(exam => exam.Id == "goethe-a2-adults").Levels);
        Assert.Equal(195, goetheB2.TotalWorkingMinutes);
        Assert.Equal(4, goetheB2.Segments.Count);
        Assert.Contains("60%", goetheB2.ScoringSummary);
        Assert.All(goetheB2.Sources, source => Assert.StartsWith("https://", source.Url));
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
