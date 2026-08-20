using WortBruecke.Core.Models;

namespace WortBruecke.Core.Abstractions;

public interface IProgressRepository
{
    Task RecordAttemptAsync(ContentType contentType, long contentId, bool correct, CancellationToken cancellationToken = default);
    Task<ProgressRecord?> GetAsync(ContentType contentType, long contentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProgressRecord>> GetAllAsync(CancellationToken cancellationToken = default);
}
