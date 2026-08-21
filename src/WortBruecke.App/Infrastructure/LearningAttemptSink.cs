using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;

namespace WortBruecke.App.Infrastructure;

/// <summary>
/// Single-write bridge used while old callers are upgraded. A sink writes either canonical
/// evidence or the legacy aggregate, never both.
/// </summary>
internal sealed class LearningAttemptSink
{
    private readonly IAttemptRepository? _attempts;
    private readonly IProgressRepository? _legacy;

    public LearningAttemptSink(IAttemptRepository attempts) =>
        _attempts = attempts ?? throw new ArgumentNullException(nameof(attempts));

    public LearningAttemptSink(IProgressRepository legacy) =>
        _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));

    public async Task RecordAsync(
        AttemptEvent attempt,
        ContentType legacyContentType,
        long legacyContentId,
        CancellationToken cancellationToken = default)
    {
        if (_attempts is not null)
        {
            await _attempts.AppendAsync(attempt, cancellationToken);
            return;
        }
        await _legacy!.RecordAttemptAsync(legacyContentType, legacyContentId, attempt.Score >= 0.5, cancellationToken);
    }
}
