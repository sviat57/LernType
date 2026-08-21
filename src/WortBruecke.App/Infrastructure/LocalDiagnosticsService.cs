using System.IO;
using System.Text.Json;
using WortBruecke.Infrastructure.Paths;

namespace WortBruecke.App.Infrastructure;

/// <summary>
/// Minimal local diagnostics which deliberately records no exception messages, answers,
/// book text, API payloads, paths, account data, or other user content.
/// </summary>
public sealed class LocalDiagnosticsService
{
    private const long MaximumLogBytes = 1_048_576;
    private const int RetainedLogCount = 3;
    private readonly object _gate = new();
    private readonly string _logRoot;
    private readonly string _logPath;

    public LocalDiagnosticsService(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _logRoot = Path.Combine(paths.DataRoot, "Logs");
        _logPath = Path.Combine(_logRoot, "diagnostics.jsonl");
    }

    public string LogPath => _logPath;

    public void Write(string eventCode, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventCode);
        ArgumentNullException.ThrowIfNull(exception);
        var safeEventCode = IsSafeEventCode(eventCode) ? eventCode : "diagnostics.invalid-event-code";
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_logRoot);
                RotateIfNeeded();
                var record = JsonSerializer.Serialize(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    eventCode = safeEventCode,
                    exceptionType = exception.GetType().FullName,
                    exception.HResult
                });
                File.AppendAllText(_logPath, record + Environment.NewLine);
            }
        }
        catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException)
        {
            // Diagnostics are best-effort and must never destabilize the application.
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_logPath) || new FileInfo(_logPath).Length < MaximumLogBytes) return;
        var oldest = $"{_logPath}.{RetainedLogCount}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = RetainedLogCount - 1; index >= 1; index--)
        {
            var source = $"{_logPath}.{index}";
            if (File.Exists(source)) File.Move(source, $"{_logPath}.{index + 1}", overwrite: true);
        }
        File.Move(_logPath, $"{_logPath}.1", overwrite: true);
    }

    private static bool IsSafeEventCode(string value) => value.Length <= 64 && value.All(character =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
}
