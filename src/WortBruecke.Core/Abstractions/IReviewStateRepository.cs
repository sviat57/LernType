using WortBruecke.Core.Learning;

namespace WortBruecke.Core.Abstractions;

public interface IReviewStateRepository
{
    Task<ReviewState?> GetAsync(string contentKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReviewState>> GetDueAsync(
        DateTimeOffset asOfUtc,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(ReviewState state, CancellationToken cancellationToken = default);
}
