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
        Assert.Equal(5, goetheB2.Segments.Single(item => item.Id == "reading").Parts);

        var testDaf = catalog.Exams.Single(exam => exam.Id == "testdaf-digital");
        Assert.Equal(34, testDaf.Segments.Single(item => item.Id == "reading").Items);
        Assert.Equal("fixed-order-no-return-to-previous-task", testDaf.NavigationPolicy);
        Assert.False(testDaf.Scoring.UniversalPass);

        var telcC1 = catalog.Exams.Single(exam => exam.Id == "telc-c1-general");
        Assert.Equal(20, telcC1.BreakMinutes);
        Assert.Equal(20, telcC1.Segments.Single(item => item.Id == "speaking").PreparationMinutes);
        Assert.True(telcC1.Segments.Single(item => item.Id == "speaking").IsPairFormat);

        var dtz = catalog.Exams.Single(exam => exam.Id == "dtz-a2-b1");
        Assert.Equal(16, dtz.Segments.Single(item => item.Id == "speaking").DurationMinutes);
        Assert.True(dtz.Segments.Single(item => item.Id == "speaking").IsDurationPerParticipant);

        AssertParts(catalog, "goethe-a1-adults", ("reading", 3), ("listening", 3), ("writing", 2), ("speaking", 3));
        AssertParts(catalog, "goethe-a2-adults", ("reading", 4), ("listening", 4), ("writing", 2), ("speaking", 3));
        AssertParts(catalog, "goethe-b1", ("reading", 5), ("listening", 4), ("writing", 3), ("speaking", 3));
        AssertParts(catalog, "goethe-b2", ("reading", 5), ("listening", 4), ("writing", 2), ("speaking", 2));
        AssertParts(catalog, "goethe-c1", ("reading", 4), ("listening", 4), ("writing", 2), ("speaking", 2));
        AssertParts(catalog, "goethe-c2", ("reading", 4), ("listening", 3), ("writing", 2), ("speaking", 2));
    }

    private static void AssertParts(
        WortBruecke.Core.Models.ExamBlueprintCatalog catalog,
        string examId,
        params (string Module, int Parts)[] expected)
    {
        var exam = catalog.Exams.Single(item => item.Id == examId);
        Assert.All(expected, item => Assert.Equal(item.Parts, exam.Segments.Single(segment => segment.Id == item.Module).Parts));
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
