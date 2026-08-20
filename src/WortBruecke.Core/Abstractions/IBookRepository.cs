using WortBruecke.Core.Models;

namespace WortBruecke.Core.Abstractions;

public interface IBookRepository
{
    Task<UserBook> SaveAsync(
        string title,
        string sourceCulture,
        string rawText,
        IReadOnlyList<ExtractedVocabularyItem> vocabulary,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserBook>> GetRecentAsync(int limit = 10, CancellationToken cancellationToken = default);
}
