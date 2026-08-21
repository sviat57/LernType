using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WortBruecke.Infrastructure.Paths;

namespace WortBruecke.Infrastructure.Persistence;

/// <summary>
/// Moves an installed WortBruecke profile to LernType without ever renaming a live SQLite main
/// file away from its WAL. The legacy profile remains in place until the verified destination has
/// been promoted, so every phase is restartable and reversible.
/// </summary>
public sealed class DataRootMigrator(AppPaths paths, DataMigrationOptions? options = null)
{
    private readonly DataMigrationOptions _options = options ?? new DataMigrationOptions();

    public async Task<DataRootMigrationResult> MigrateAsync(CancellationToken cancellationToken = default)
    {
        using var migrationLock = AcquireLock(cancellationToken);
        paths.EnsureDataDirectory();

        var journal = new MigrationJournal(paths.MigrationJournalPath);
        var destinationInspection = await SqliteDataSafety.InspectAsync(paths.DatabasePath, cancellationToken);
        StageDetachedLegacyWalIfPresent();
        var source = await SelectSourceAsync(paths.DatabasePath, cancellationToken);
        if (source is null)
        {
            HandleStandaloneLegacySettings(journal);
            return new DataRootMigrationResult(DataRootMigrationStatus.NoLegacyData, paths.DatabasePath);
        }

        var sourceInspection = await SqliteDataSafety.InspectAsync(source, cancellationToken);
        var completed = journal.ReadAll()
            .Where(entry => entry.Phase == DataMigrationPhase.Completed && PathsEqual(entry.SourcePath, source))
            .OrderByDescending(entry => entry.RecordedAtUtc)
            .FirstOrDefault();
        if (completed is not null && destinationInspection.IsValid && !SourceChangedAfter(source, completed.RecordedAtUtc))
        {
            return new DataRootMigrationResult(
                DataRootMigrationStatus.AlreadyCompleted,
                paths.DatabasePath,
                source,
                completed.BackupPath,
                completed.RollbackPath);
        }

        var promoted = journal.ReadAll()
            .Where(entry => entry.Phase == DataMigrationPhase.Promoted && PathsEqual(entry.SourcePath, source))
            .OrderByDescending(entry => entry.RecordedAtUtc)
            .FirstOrDefault();
        if (promoted is not null && destinationInspection.IsValid && !SourceChangedAfter(source, promoted.RecordedAtUtc))
        {
            var detail = MigrateSettings(source, promoted.OperationId);
            Append(journal, promoted.OperationId, DataMigrationPhase.AncillaryFilesHandled, source,
                promoted.SourceFingerprint, promoted.BackupPath, promoted.RollbackPath, detail);
            Append(journal, promoted.OperationId, DataMigrationPhase.Completed, source,
                promoted.SourceFingerprint, promoted.BackupPath, promoted.RollbackPath,
                "Resumed after an interruption following atomic promotion.");
            return new DataRootMigrationResult(
                DataRootMigrationStatus.AlreadyCompleted,
                paths.DatabasePath,
                source,
                promoted.BackupPath,
                promoted.RollbackPath);
        }

        if (!sourceInspection.IsValid)
        {
            throw new DataMigrationValidationException(
                "Старая база данных повреждена. Исходный файл сохранён для восстановления.",
                source);
        }

        var detachedWalRecovery = IsDetachedWalRecovery(source);
        if (destinationInspection.IsValid && destinationInspection.HasUserData && sourceInspection.IsValid && sourceInspection.HasUserData &&
            !detachedWalRecovery)
        {
            throw new DataRootConflictException(paths.DatabasePath, source);
        }

        if (detachedWalRecovery && destinationInspection.IsValid &&
            !InventoryContains(sourceInspection.TableRows, destinationInspection.TableRows))
        {
            throw new DataRootConflictException(paths.DatabasePath, source);
        }

        if (destinationInspection.IsValid && destinationInspection.HasUserData && !detachedWalRecovery)
        {
            PreserveConflictingAncillaryFile(source, "current-profile-preferred");
            var operationId = CreateOperationId();
            Append(journal, operationId, DataMigrationPhase.CurrentProfilePreferred, source, null, null, null,
                "Legacy database contained no user records; the current profile was retained.");
            Append(journal, operationId, DataMigrationPhase.Completed, source, null, null, null,
                "Current profile retained; legacy profile contained no user records.");
            return new DataRootMigrationResult(DataRootMigrationStatus.CurrentProfilePreferred, paths.DatabasePath, source);
        }

        return await MigrateSourceAsync(source, journal, cancellationToken);
    }

    public async Task<string?> RollbackAsync(
        string verifiedBackupPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedBackupPath);
        using var migrationLock = AcquireLock(cancellationToken);
        var backupInspection = await SqliteDataSafety.InspectAsync(verifiedBackupPath, cancellationToken);
        if (!backupInspection.IsValid)
        {
            throw new DataMigrationValidationException(
                "Резервная копия не прошла SQLite quick_check.",
                verifiedBackupPath);
        }

        paths.EnsureDataDirectory();
        var operationId = $"rollback-{CreateOperationId()}";
        var rollbackDirectory = Path.Combine(paths.BackupRoot, "rollbacks", operationId);
        Directory.CreateDirectory(rollbackDirectory);
        string? displacedPath = null;
        var temporaryPath = Path.Combine(paths.DataRoot, $".{operationId}.tmp.db");
        try
        {
            await CloneDatabaseAsync(verifiedBackupPath, temporaryPath, cancellationToken);
            if (File.Exists(paths.DatabasePath))
            {
                displacedPath = Path.Combine(rollbackDirectory, "displaced-current.db");
                File.Replace(temporaryPath, paths.DatabasePath, displacedPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, paths.DatabasePath);
            }

            var restored = await SqliteDataSafety.InspectAsync(paths.DatabasePath, cancellationToken);
            if (!restored.IsValid)
            {
                throw new DataMigrationValidationException("Восстановленная база не прошла проверку.", paths.DatabasePath);
            }

            return displacedPath;
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private async Task<DataRootMigrationResult> MigrateSourceAsync(
        string source,
        MigrationJournal journal,
        CancellationToken cancellationToken)
    {
        var operationId = CreateOperationId();
        var operationDirectory = Path.Combine(paths.BackupRoot, "migrations", operationId);
        var originalsDirectory = Path.Combine(operationDirectory, "original-files");
        var verifiedBackupPath = Path.Combine(operationDirectory, "verified-source.db");
        var temporaryPath = Path.Combine(paths.DataRoot, $".{operationId}.tmp.db");
        string? rollbackPath = null;
        Directory.CreateDirectory(originalsDirectory);
        Append(journal, operationId, DataMigrationPhase.Started, source, null, verifiedBackupPath, null);

        try
        {
            CopySqliteFileSet(source, originalsDirectory);
            Append(journal, operationId, DataMigrationPhase.SourcePreserved, source, null, verifiedBackupPath, null);

            var sourceConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = source,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString();
            await using var sourceConnection = new SqliteConnection(sourceConnectionString);
            await sourceConnection.OpenAsync(cancellationToken);
            if (!await SqliteDataSafety.QuickCheckAsync(sourceConnection, cancellationToken))
            {
                throw new DataMigrationValidationException("Старая база не прошла SQLite quick_check.", source);
            }

            await SqliteDataSafety.CheckpointWalAsync(sourceConnection, cancellationToken);
            var sourceInventory = await SqliteDataSafety.ReadTableRowsAsync(sourceConnection, cancellationToken);
            var sourceFingerprint = FingerprintInventory(sourceInventory);
            Append(journal, operationId, DataMigrationPhase.SourceCheckpointed, source, sourceFingerprint, verifiedBackupPath, null);

            TryDelete(temporaryPath);
            SqliteDataSafety.BackupDatabase(sourceConnection, temporaryPath);
            await VerifyCloneAsync(temporaryPath, sourceInventory, cancellationToken);
            File.Copy(temporaryPath, verifiedBackupPath, overwrite: false);
            await VerifyCloneAsync(verifiedBackupPath, sourceInventory, cancellationToken);
            Append(journal, operationId, DataMigrationPhase.BackupVerified, source, sourceFingerprint, verifiedBackupPath, null);

            await VerifyCloneAsync(temporaryPath, sourceInventory, cancellationToken);
            Append(journal, operationId, DataMigrationPhase.DestinationVerified, source, sourceFingerprint, verifiedBackupPath, null);

            if (File.Exists(paths.DatabasePath))
            {
                rollbackPath = Path.Combine(operationDirectory, "pre-migration-current.db");
                File.Replace(temporaryPath, paths.DatabasePath, rollbackPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, paths.DatabasePath);
            }

            var promoted = await SqliteDataSafety.InspectAsync(paths.DatabasePath, cancellationToken);
            if (!promoted.IsValid || !SqliteDataSafety.InventoriesEqual(sourceInventory, promoted.TableRows))
            {
                RestoreAfterFailedPromotion(rollbackPath);
                throw new DataMigrationValidationException(
                    "Новая база не совпадает с проверенной резервной копией.",
                    paths.DatabasePath);
            }
            Append(journal, operationId, DataMigrationPhase.Promoted, source, sourceFingerprint, verifiedBackupPath, rollbackPath);

            var ancillaryDetail = MigrateSettings(source, operationId);
            Append(journal, operationId, DataMigrationPhase.AncillaryFilesHandled, source, sourceFingerprint,
                verifiedBackupPath, rollbackPath, ancillaryDetail);
            Append(journal, operationId, DataMigrationPhase.Completed, source, sourceFingerprint,
                verifiedBackupPath, rollbackPath);

            return new DataRootMigrationResult(
                DataRootMigrationStatus.Migrated,
                paths.DatabasePath,
                source,
                verifiedBackupPath,
                rollbackPath);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private async Task<string?> SelectSourceAsync(string destinationPath, CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        AddCandidate(candidates, paths.InPlaceLegacyDatabasePath, destinationPath);
        if (paths.LegacyDataRoot is not null)
        {
            AddCandidate(candidates, Path.Combine(paths.LegacyDataRoot, "wortbruecke.db"), destinationPath);
            AddCandidate(candidates, Path.Combine(paths.LegacyDataRoot, "lerntype.db"), destinationPath);
        }
        AddCandidate(candidates, DetachedWalRecoveryPath, destinationPath);

        if (candidates.Count <= 1)
        {
            return candidates.FirstOrDefault();
        }

        var inspected = new List<(string Path, DatabaseInspection Inspection)>();
        foreach (var candidate in candidates)
        {
            inspected.Add((candidate, await SqliteDataSafety.InspectAsync(candidate, cancellationToken)));
        }

        var meaningful = inspected.Where(item => item.Inspection.IsValid && item.Inspection.HasUserData).ToList();
        if (meaningful.Count > 1)
        {
            throw new DataRootConflictException(meaningful[0].Path, meaningful[1].Path);
        }

        return meaningful.Count == 1
            ? meaningful[0].Path
            : inspected.FirstOrDefault(item => item.Inspection.IsValid).Path ?? candidates[0];
    }

    private static void AddCandidate(List<string> candidates, string candidate, string destination)
    {
        if (File.Exists(candidate) && !PathsEqual(candidate, destination) &&
            !candidates.Any(existing => PathsEqual(existing, candidate)))
        {
            candidates.Add(candidate);
        }
    }

    private string DetachedWalRecoveryPath =>
        Path.Combine(paths.RecoveryRoot, "detached-wal", "wortbruecke.db");

    private void StageDetachedLegacyWalIfPresent()
    {
        var detachedWal = paths.InPlaceLegacyDatabasePath + "-wal";
        var stagedMarker = DetachedWalRecoveryPath + ".staged";
        if (File.Exists(paths.InPlaceLegacyDatabasePath) || !File.Exists(paths.DatabasePath) ||
            !File.Exists(detachedWal) || new FileInfo(detachedWal).Length <= 32 || File.Exists(stagedMarker))
        {
            return;
        }

        var recoveryDirectory = Path.GetDirectoryName(DetachedWalRecoveryPath)!;
        Directory.CreateDirectory(recoveryDirectory);
        var temporaryMain = DetachedWalRecoveryPath + ".tmp";
        var temporaryWal = DetachedWalRecoveryPath + "-wal.tmp";
        try
        {
            TryDelete(DetachedWalRecoveryPath);
            TryDelete(DetachedWalRecoveryPath + "-wal");
            File.Copy(paths.DatabasePath, temporaryMain, overwrite: false);
            File.Copy(detachedWal, temporaryWal, overwrite: false);
            File.Move(temporaryMain, DetachedWalRecoveryPath);
            File.Move(temporaryWal, DetachedWalRecoveryPath + "-wal");
            File.WriteAllText(stagedMarker, DateTimeOffset.UtcNow.ToString("O"));
        }
        finally
        {
            TryDelete(temporaryMain);
            TryDelete(temporaryWal);
        }
    }

    private bool IsDetachedWalRecovery(string source) => PathsEqual(source, DetachedWalRecoveryPath);

    private static bool InventoryContains(
        IReadOnlyDictionary<string, long> superset,
        IReadOnlyDictionary<string, long> subset) =>
        subset.All(item => superset.TryGetValue(item.Key, out var count) && count >= item.Value);

    private async Task VerifyCloneAsync(
        string clonePath,
        IReadOnlyDictionary<string, long> expectedInventory,
        CancellationToken cancellationToken)
    {
        var inspection = await SqliteDataSafety.InspectAsync(clonePath, cancellationToken);
        if (!inspection.IsValid || !SqliteDataSafety.InventoriesEqual(expectedInventory, inspection.TableRows))
        {
            throw new DataMigrationValidationException(
                "Резервная копия SQLite не прошла проверку структуры и количества записей.",
                clonePath);
        }
    }

    private static async Task CloneDatabaseAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(sourceConnectionString);
        await connection.OpenAsync(cancellationToken);
        SqliteDataSafety.BackupDatabase(connection, destination);
    }

    private string? MigrateSettings(string databaseSource, string operationId)
    {
        var sourceSettings = Path.Combine(Path.GetDirectoryName(databaseSource)!, "settings.json");
        if (!File.Exists(sourceSettings) || PathsEqual(sourceSettings, paths.LocalSettingsPath))
        {
            return null;
        }

        if (!File.Exists(paths.LocalSettingsPath))
        {
            AtomicCopy(sourceSettings, paths.LocalSettingsPath);
            return "Legacy settings copied to the current profile.";
        }

        if (FilesEqual(sourceSettings, paths.LocalSettingsPath))
        {
            return "Current and legacy settings were identical.";
        }

        var recoveryDirectory = Path.Combine(paths.RecoveryRoot, operationId);
        Directory.CreateDirectory(recoveryDirectory);
        File.Copy(sourceSettings, Path.Combine(recoveryDirectory, "legacy-settings.json"), overwrite: false);
        return $"Both settings files were preserved; legacy copy: {recoveryDirectory}.";
    }

    private void HandleStandaloneLegacySettings(MigrationJournal journal)
    {
        if (paths.LegacyDataRoot is null)
        {
            return;
        }

        var sourceSettings = Path.Combine(paths.LegacyDataRoot, "settings.json");
        if (!File.Exists(sourceSettings))
        {
            return;
        }

        var sourceMarker = Path.Combine(paths.LegacyDataRoot, "settings-only.profile");
        if (journal.ReadAll().Any(entry =>
                entry.Phase == DataMigrationPhase.Completed && PathsEqual(entry.SourcePath, sourceMarker)))
        {
            return;
        }

        var operationId = CreateOperationId();
        var detail = MigrateSettings(sourceMarker, operationId);
        Append(journal, operationId, DataMigrationPhase.AncillaryFilesHandled, sourceMarker,
            null, null, null, detail);
        Append(journal, operationId, DataMigrationPhase.Completed, sourceMarker,
            null, null, null, "Standalone legacy settings migration completed.");
    }

    private void PreserveConflictingAncillaryFile(string databaseSource, string reason)
    {
        var sourceSettings = Path.Combine(Path.GetDirectoryName(databaseSource)!, "settings.json");
        if (!File.Exists(sourceSettings) || !File.Exists(paths.LocalSettingsPath) ||
            FilesEqual(sourceSettings, paths.LocalSettingsPath))
        {
            return;
        }

        var recoveryDirectory = Path.Combine(paths.RecoveryRoot, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{reason}");
        Directory.CreateDirectory(recoveryDirectory);
        File.Copy(sourceSettings, Path.Combine(recoveryDirectory, "legacy-settings.json"));
    }

    private void Append(
        MigrationJournal journal,
        string operationId,
        DataMigrationPhase phase,
        string source,
        string? sourceFingerprint,
        string? backupPath,
        string? rollbackPath,
        string? detail = null)
    {
        journal.Append(new MigrationJournalEntry(
            operationId,
            phase,
            Path.GetFullPath(source),
            Path.GetFullPath(paths.DatabasePath),
            sourceFingerprint,
            backupPath,
            rollbackPath,
            DateTimeOffset.UtcNow,
            detail));
        _options.AfterPhase?.Invoke(phase);
    }

    private IDisposable AcquireLock(CancellationToken cancellationToken)
    {
        var normalized = Path.GetFullPath(paths.DataRoot).ToUpperInvariant();
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
        var semaphore = new Semaphore(1, 1, $"Local\\LernType.DataMigration.{suffix}");
        int index;
        try
        {
            index = WaitHandle.WaitAny([semaphore, cancellationToken.WaitHandle], _options.LockTimeout);
        }
        catch
        {
            semaphore.Dispose();
            throw;
        }

        if (index == WaitHandle.WaitTimeout)
        {
            semaphore.Dispose();
            throw new TimeoutException("Истекло время ожидания блокировки миграции данных LernType.");
        }

        if (index == 1)
        {
            semaphore.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return new SemaphoreReleaser(semaphore);
    }

    private void RestoreAfterFailedPromotion(string? rollbackPath)
    {
        if (rollbackPath is not null && File.Exists(rollbackPath))
        {
            File.Replace(rollbackPath, paths.DatabasePath, null, ignoreMetadataErrors: true);
            return;
        }

        TryDelete(paths.DatabasePath);
    }

    private static void CopySqliteFileSet(string source, string destinationDirectory)
    {
        foreach (var path in new[] { source, source + "-wal", source + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Copy(path, Path.Combine(destinationDirectory, Path.GetFileName(path)), overwrite: false);
            }
        }
    }

    private static void AtomicCopy(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(source, temporary, overwrite: false);
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static bool FilesEqual(string first, string second)
    {
        var firstInfo = new FileInfo(first);
        var secondInfo = new FileInfo(second);
        if (firstInfo.Length != secondInfo.Length)
        {
            return false;
        }

        using var firstStream = firstInfo.OpenRead();
        using var secondStream = secondInfo.OpenRead();
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(firstStream), SHA256.HashData(secondStream));
    }

    private static bool SourceChangedAfter(string source, DateTimeOffset completedAt)
    {
        foreach (var path in new[] { source, source + "-wal", source + "-shm" })
        {
            if (File.Exists(path) && new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) > completedAt.AddSeconds(1))
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateOperationId() =>
        $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";

    private static string FingerprintInventory(IReadOnlyDictionary<string, long> inventory)
    {
        var value = string.Join('\n', inventory.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key}={item.Value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The source and verified backup still exist; stale temp files are safe to retry later.
        }
        catch (UnauthorizedAccessException)
        {
            // The source and verified backup still exist; stale temp files are safe to retry later.
        }
    }

    private sealed class SemaphoreReleaser(Semaphore semaphore) : IDisposable
    {
        public void Dispose()
        {
            semaphore.Release();
            semaphore.Dispose();
        }
    }
}
