using WortBruecke.Core.Learning;

namespace WortBruecke.Core.Abstractions;

public interface ILearningProgressRepository
{
    Task RecordAsync(LearningAttempt attempt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LearningAttempt>> GetAllAsync(CancellationToken cancellationToken = default);
}
