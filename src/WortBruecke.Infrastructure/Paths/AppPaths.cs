namespace WortBruecke.Infrastructure.Paths;

public sealed class AppPaths
{
    private readonly string? _legacyDataRoot;

    public AppPaths(string? contentRoot = null, string? dataRoot = null, string? legacyDataRoot = null)
    {
        ContentRoot = contentRoot ?? Path.Combine(AppContext.BaseDirectory, "Content");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DataRoot = dataRoot ?? Path.Combine(localAppData, "LernType");
        _legacyDataRoot = legacyDataRoot ?? (dataRoot is null ? Path.Combine(localAppData, "WortBruecke") : null);
    }

    public string ContentRoot { get; }
    public string DataRoot { get; }
    public string DatabasePath => Path.Combine(DataRoot, "lerntype.db");
    public string LocalSettingsPath => Path.Combine(DataRoot, "settings.json");

    public void EnsureDataDirectory()
    {
        if (!Directory.Exists(DataRoot) && _legacyDataRoot is not null && Directory.Exists(_legacyDataRoot))
        {
            Directory.Move(_legacyDataRoot, DataRoot);
        }
        else
        {
            Directory.CreateDirectory(DataRoot);
        }

        var legacyDatabase = Path.Combine(DataRoot, "wortbruecke.db");
        if (!File.Exists(DatabasePath) && File.Exists(legacyDatabase))
        {
            File.Move(legacyDatabase, DatabasePath);
        }
    }
}
