using WortBruecke.Infrastructure.Paths;

namespace WortBruecke.Tests.Persistence;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LernTypePathTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnsureDataDirectory_CreatesCurrentDirectoryWithoutRenamingSqliteFiles()
    {
        var legacyRoot = Path.Combine(_root, "WortBruecke");
        var currentRoot = Path.Combine(_root, "LernType");
        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "wortbruecke.db"), "database");
        File.WriteAllText(Path.Combine(legacyRoot, "settings.json"), "settings");
        var paths = new AppPaths(Path.Combine(_root, "Content"), currentRoot, legacyRoot);

        paths.EnsureDataDirectory();

        Assert.True(Directory.Exists(legacyRoot));
        Assert.True(Directory.Exists(currentRoot));
        Assert.False(File.Exists(paths.DatabasePath));
        Assert.True(File.Exists(paths.LegacyDatabasePath));
    }

    [Fact]
    public void Constructor_ExposesRecoveryAndMigrationPathsInsideCurrentRoot()
    {
        var legacyRoot = Path.Combine(_root, "WortBruecke");
        var currentRoot = Path.Combine(_root, "LernType");
        var paths = new AppPaths(Path.Combine(_root, "Content"), currentRoot, legacyRoot);

        Assert.Equal(Path.Combine(currentRoot, "lerntype.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(legacyRoot, "wortbruecke.db"), paths.LegacyDatabasePath);
        Assert.Equal(Path.Combine(currentRoot, "migration-journal.jsonl"), paths.MigrationJournalPath);
        Assert.StartsWith(currentRoot, paths.BackupRoot, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(currentRoot, paths.RecoveryRoot, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
