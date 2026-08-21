namespace WortBruecke.Infrastructure.Persistence;

public enum DataRootMigrationStatus
{
    NoLegacyData,
    Migrated,
    AlreadyCompleted,
    CurrentProfilePreferred
}

public sealed record DataRootMigrationResult(
    DataRootMigrationStatus Status,
    string DestinationPath,
    string? SourcePath = null,
    string? VerifiedBackupPath = null,
    string? RollbackPath = null);

public sealed class DataRootConflictException : InvalidOperationException
{
    public DataRootConflictException(string currentDatabasePath, string legacyDatabasePath)
        : base(
            "Обнаружены два профиля LernType с пользовательскими данными. " +
            "Оба файла сохранены без изменений; выберите профиль через восстановление данных. " +
            $"Текущий: {currentDatabasePath}. Старый: {legacyDatabasePath}.")
    {
        CurrentDatabasePath = currentDatabasePath;
        LegacyDatabasePath = legacyDatabasePath;
    }

    public string CurrentDatabasePath { get; }
    public string LegacyDatabasePath { get; }
}

public sealed class DataMigrationValidationException : IOException
{
    public DataMigrationValidationException(string message, string databasePath, Exception? innerException = null)
        : base(message, innerException)
    {
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }
}

public enum DataMigrationPhase
{
    Started,
    SourcePreserved,
    SourceCheckpointed,
    BackupVerified,
    DestinationVerified,
    Promoted,
    AncillaryFilesHandled,
    Completed,
    CurrentProfilePreferred
}

public sealed class DataMigrationOptions
{
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Test hook invoked after a durable journal entry has been written.</summary>
    public Action<DataMigrationPhase>? AfterPhase { get; init; }
}
