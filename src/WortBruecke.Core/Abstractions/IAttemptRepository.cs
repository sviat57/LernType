using WortBruecke.Core.Learning;

namespace WortBruecke.Core.Abstractions;

/// <summary>
/// Canonical append-only evidence store. Append is idempotent by EventId and returns true only
/// when the event was inserted for the first time.
/// </summary>
public interface IAttemptRepository
{
    Task<bool> AppendAsync(AttemptEvent attempt, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AttemptEvent>> GetAsync(
        AttemptQuery? query = null,
        CancellationToken cancellationToken = default);
}
