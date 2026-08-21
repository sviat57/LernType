namespace WortBruecke.Infrastructure.Paths;

public sealed class AppPaths
{
    public AppPaths(string? contentRoot = null, string? dataRoot = null, string? legacyDataRoot = null)
    {
        ContentRoot = contentRoot ?? Path.Combine(AppContext.BaseDirectory, "Content");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var testOrPortableRoot = Environment.GetEnvironmentVariable("LERNTYPE_DATA_ROOT");
        var hasRootOverride = dataRoot is not null || !string.IsNullOrWhiteSpace(testOrPortableRoot);
        DataRoot = Path.GetFullPath(dataRoot ??
            (!string.IsNullOrWhiteSpace(testOrPortableRoot)
                ? testOrPortableRoot
                : Path.Combine(localAppData, "LernType")));
        LegacyDataRoot = legacyDataRoot ?? (!hasRootOverride ? Path.Combine(localAppData, "WortBruecke") : null);
    }

    public string ContentRoot { get; }
    public string DataRoot { get; }
    public string? LegacyDataRoot { get; }
    public string DatabasePath => Path.Combine(DataRoot, "lerntype.db");
    public string LegacyDatabasePath => Path.Combine(LegacyDataRoot ?? DataRoot, "wortbruecke.db");
    public string InPlaceLegacyDatabasePath => Path.Combine(DataRoot, "wortbruecke.db");
    public string LocalSettingsPath => Path.Combine(DataRoot, "settings.json");
    public string MigrationJournalPath => Path.Combine(DataRoot, "migration-journal.jsonl");
    public string BackupRoot => Path.Combine(DataRoot, "Backups");
    public string RollingBackupRoot => Path.Combine(BackupRoot, "rolling");
    public string PreUpgradeBackupRoot => Path.Combine(BackupRoot, "pre-upgrade");
    public string RecoveryRoot => Path.Combine(DataRoot, "Recovery");

    /// <summary>
    /// Ensures that the current application directory exists. Database migration is deliberately
    /// handled by <c>DataRootMigrator</c>; moving a SQLite main file here would detach its WAL.
    /// </summary>
    public void EnsureDataDirectory() => Directory.CreateDirectory(DataRoot);
}
