namespace WortBruecke.Infrastructure.Audio;

/// <summary>
/// Owns one process-local temporary recording directory. An exclusive lease keeps concurrent
/// LernType processes from deleting each other's recordings, while stale sessions are removed
/// on the next cleanup sweep.
/// </summary>
public sealed class TemporaryAudioRecordingStore : IAsyncDisposable
{
    private const int MaxEntriesPerSweep = 256;
    private const string LeaseSuffix = ".active.lock";
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(75),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(600)
    ];

    private readonly string _rootDirectory;
    private readonly string _sessionDirectory;
    private readonly string _leasePath;
    private readonly TimeSpan[] _retryDelays;
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private readonly object _sessionSync = new();
    private FileStream? _lease;
    private Task? _disposeTask;

    public TemporaryAudioRecordingStore(
        string? rootDirectory = null,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory ?? Path.Combine(
            Path.GetTempPath(),
            "LernType",
            "recordings"));
        var sessionId = Guid.NewGuid().ToString("N");
        _sessionDirectory = Path.Combine(_rootDirectory, sessionId);
        _leasePath = Path.Combine(_rootDirectory, $".{sessionId}{LeaseSuffix}");
        _retryDelays = retryDelays?.ToArray() ?? DefaultRetryDelays;
        if (_retryDelays.Length == 0 || _retryDelays.Any(delay => delay < TimeSpan.Zero))
        {
            throw new ArgumentException("At least one non-negative retry delay is required.", nameof(retryDelays));
        }
    }

    public string CreateRecordingPath()
    {
        EnsureSession();
        return Path.Combine(_sessionDirectory, $"{Guid.NewGuid():N}.wav");
    }

    /// <summary>
    /// Deletes recordings left by interrupted sessions without touching a concurrently running
    /// session. Legacy root-level GUID WAV files are included for upgrades from LernType 1.0.
    /// </summary>
    public async Task CleanupOrphansAsync(CancellationToken cancellationToken = default)
    {
        lock (_sessionSync)
        {
            if (_disposeTask is not null)
            {
                return;
            }
        }

        await _cleanupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sessionSync)
            {
                if (_disposeTask is not null)
                {
                    return;
                }
            }

            if (!Directory.Exists(_rootDirectory))
            {
                return;
            }

            var processed = 0;
            foreach (var file in EnumerateFilesSafely(_rootDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsLegacyRecording(file))
                {
                    if (++processed > MaxEntriesPerSweep)
                    {
                        break;
                    }
                    await TryDeleteFileAsync(file, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (TryGetLeaseSessionId(file, out var leaseSessionId) &&
                    !Directory.Exists(Path.Combine(_rootDirectory, leaseSessionId)) &&
                    !IsLeaseActive(file))
                {
                    if (++processed > MaxEntriesPerSweep)
                    {
                        break;
                    }
                    await TryDeleteFileAsync(file, cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var directory in EnumerateDirectoriesSafely(_rootDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sessionId = Path.GetFileName(directory);
                if (!IsGuidName(sessionId) ||
                    string.Equals(directory, _sessionDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var leasePath = Path.Combine(_rootDirectory, $".{sessionId}{LeaseSuffix}");
                if (IsLeaseActive(leasePath))
                {
                    continue;
                }
                if (++processed > MaxEntriesPerSweep)
                {
                    break;
                }

                if (await TryDeleteDirectoryAsync(directory, cancellationToken).ConfigureAwait(false))
                {
                    await TryDeleteFileAsync(leasePath, cancellationToken).ConfigureAwait(false);
                }
            }

            TryDeleteEmptyRoot();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Temporary storage is best-effort. A later session repeats the bounded sweep.
        }
        finally
        {
            _cleanupGate.Release();
        }
    }

    /// <summary>
    /// Applies bounded retry to a recording delete. When all attempts are exhausted the file
    /// remains inside the leased session directory, so session disposal/startup cleanup retains
    /// responsibility for it.
    /// </summary>
    public async Task<bool> DeleteAsync(string recordingPath, CancellationToken cancellationToken = default)
    {
        var fullPath = ValidateRecordingPath(recordingPath);
        return await TryDeleteFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureSession()
    {
        lock (_sessionSync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_lease is not null)
            {
                return;
            }

            Directory.CreateDirectory(_rootDirectory);
            try
            {
                _lease = new FileStream(
                    _leasePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                Directory.CreateDirectory(_sessionDirectory);
            }
            catch
            {
                _lease?.Dispose();
                _lease = null;
                TryDeleteFileOnce(_leasePath);
                throw;
            }
        }
    }

    private string ValidateRecordingPath(string recordingPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingPath);
        var fullPath = Path.GetFullPath(recordingPath);
        if (!string.Equals(Path.GetDirectoryName(fullPath), _sessionDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(fullPath), ".wav", StringComparison.OrdinalIgnoreCase) ||
            !IsGuidName(Path.GetFileNameWithoutExtension(fullPath)))
        {
            throw new ArgumentException("The recording is outside the active temporary audio session.", nameof(recordingPath));
        }
        return fullPath;
    }

    private async Task<bool> TryDeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        foreach (var delay in _retryDelays)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The audio driver may release its handle asynchronously; try the next delay.
            }
        }
        return !File.Exists(path);
    }

    private async Task<bool> TryDeleteDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        foreach (var delay in _retryDelays)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(path))
                {
                    return true;
                }
                var attributes = File.GetAttributes(path);
                Directory.Delete(path, recursive: !attributes.HasFlag(FileAttributes.ReparsePoint));
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A recording or antivirus scanner can still own a handle briefly.
            }
        }
        return !Directory.Exists(path);
    }

    private static IEnumerable<string> EnumerateFilesSafely(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesSafely(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsLegacyRecording(string path) =>
        string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase) &&
        IsGuidName(Path.GetFileNameWithoutExtension(path));

    private static bool TryGetLeaseSessionId(string path, out string sessionId)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith(".", StringComparison.Ordinal) &&
            name.EndsWith(LeaseSuffix, StringComparison.Ordinal) &&
            name.Length == 1 + 32 + LeaseSuffix.Length)
        {
            sessionId = name.Substring(1, 32);
            return IsGuidName(sessionId);
        }
        sessionId = string.Empty;
        return false;
    }

    private static bool IsGuidName(string value) =>
        Guid.TryParseExact(value, "N", out _);

    private static bool IsLeaseActive(string leasePath)
    {
        if (!File.Exists(leasePath))
        {
            return false;
        }
        try
        {
            using var probe = new FileStream(
                leasePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private void TryDeleteEmptyRoot()
    {
        try
        {
            if (Directory.Exists(_rootDirectory) && !Directory.EnumerateFileSystemEntries(_rootDirectory).Any())
            {
                Directory.Delete(_rootDirectory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A concurrent app instance may have created its lease after the empty check.
        }
    }

    private static void TryDeleteFileOnce(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A future orphan sweep owns the leftover lease.
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sessionSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _cleanupGate.WaitAsync().ConfigureAwait(false);
        try
        {
            FileStream? lease;
            lock (_sessionSync)
            {
                lease = _lease;
                _lease = null;
            }
            lease?.Dispose();

            await TryDeleteDirectoryAsync(_sessionDirectory, CancellationToken.None).ConfigureAwait(false);
            await TryDeleteFileAsync(_leasePath, CancellationToken.None).ConfigureAwait(false);
            TryDeleteEmptyRoot();
        }
        finally
        {
            _cleanupGate.Release();
        }
    }
}
