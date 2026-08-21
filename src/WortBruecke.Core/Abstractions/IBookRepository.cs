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
    Task<UserBook?> GetAsync(long bookId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserBookSummary>> GetRecentSummariesAsync(int limit = 10, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserBook>> GetRecentAsync(int limit = 10, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(long bookId, CancellationToken cancellationToken = default);
    Task<int> DeleteAllAsync(CancellationToken cancellationToken = default);
    Task ExportAsync(long bookId, Stream destination, CancellationToken cancellationToken = default);
}

/// <summary>The row deletion committed, but the durable secure-delete/WAL cleanup needs a retry.</summary>
public sealed class BookPrivacyCleanupException : IOException
{
    public BookPrivacyCleanupException(string message, Exception innerException) : base(message, innerException) { }
}
