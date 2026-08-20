using WortBruecke.Infrastructure.Dictionary;

namespace WortBruecke.Tests.Persistence;

public sealed class FreeDictOfflineDictionaryServiceTests
{
    [Fact]
    public async Task BundledDictionary_LooksUpBothDirections()
    {
        var root = FindSolutionRoot();
        var databasePath = Path.Combine(root, "src", "WortBruecke.App", "Assets", "Dictionary", "FreeDict", "freedict-ru-de-2025.11.23.sqlite");
        var dictionary = new FreeDictOfflineDictionaryService(databasePath);

        var german = await dictionary.LookupAsync("Haus", "de-DE", "ru-RU");
        var russian = await dictionary.LookupAsync("яблоко", "ru-RU", "de-DE");

        Assert.NotNull(german);
        Assert.NotEmpty(german.Translations);
        Assert.NotNull(russian);
        Assert.NotEmpty(russian.Translations);
    }

    [Fact]
    public async Task BundledDictionary_PrefersExactHeadwordCaseWithoutMixingHomographs()
    {
        var root = FindSolutionRoot();
        var databasePath = Path.Combine(root, "src", "WortBruecke.App", "Assets", "Dictionary", "FreeDict", "freedict-ru-de-2025.11.23.sqlite");
        var dictionary = new FreeDictOfflineDictionaryService(databasePath);

        var entries = await dictionary.LookupBatchAsync(["Essen", "essen", "Arm", "arm"], "de-DE", "ru-RU");

        Assert.Equal(4, entries.Count);
        Assert.Equal("Essen", entries["Essen"].Headword);
        Assert.Contains("еда", entries["Essen"].Translations);
        Assert.DoesNotContain("есть", entries["Essen"].Translations);
        Assert.Equal("essen", entries["essen"].Headword);
        Assert.Contains("есть", entries["essen"].Translations);
        Assert.DoesNotContain("еда", entries["essen"].Translations);
        Assert.Equal("Arm", entries["Arm"].Headword);
        Assert.Contains("рука", entries["Arm"].Translations);
        Assert.DoesNotContain("бедный", entries["Arm"].Translations);
        Assert.Equal("arm", entries["arm"].Headword);
        Assert.Contains("бедный", entries["arm"].Translations);
        Assert.DoesNotContain("рука", entries["arm"].Translations);
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
