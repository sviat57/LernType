using WortBruecke.Infrastructure.Paths;

namespace WortBruecke.Tests.Persistence;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LernTypePathTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnsureDataDirectory_MigratesLegacyDirectoryAndDatabaseName()
    {
        var legacyRoot = Path.Combine(_root, "WortBruecke");
        var currentRoot = Path.Combine(_root, "LernType");
        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "wortbruecke.db"), "database");
        File.WriteAllText(Path.Combine(legacyRoot, "settings.json"), "settings");
        var paths = new AppPaths(Path.Combine(_root, "Content"), currentRoot, legacyRoot);

        paths.EnsureDataDirectory();

        Assert.False(Directory.Exists(legacyRoot));
        Assert.True(File.Exists(paths.DatabasePath));
        Assert.Equal("database", File.ReadAllText(paths.DatabasePath));
        Assert.True(File.Exists(paths.LocalSettingsPath));
    }

    [Fact]
    public void EnsureDataDirectory_DoesNotReplaceCurrentDatabaseWithLegacyFile()
    {
        var legacyRoot = Path.Combine(_root, "WortBruecke");
        var currentRoot = Path.Combine(_root, "LernType");
        Directory.CreateDirectory(legacyRoot);
        Directory.CreateDirectory(currentRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "wortbruecke.db"), "legacy");
        File.WriteAllText(Path.Combine(currentRoot, "lerntype.db"), "current");
        var paths = new AppPaths(Path.Combine(_root, "Content"), currentRoot, legacyRoot);

        paths.EnsureDataDirectory();

        Assert.Equal("current", File.ReadAllText(paths.DatabasePath));
        Assert.True(Directory.Exists(legacyRoot));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
