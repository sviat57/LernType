using WortBruecke.Core.Models;

namespace WortBruecke.Core.Abstractions;

public interface IOfflineDictionaryService
{
    string Attribution { get; }
    Task<DictionaryEntry?> LookupAsync(
        string sourceText,
        string sourceCulture,
        string targetCulture,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, DictionaryEntry>> LookupBatchAsync(
        IReadOnlyCollection<string> sourceTexts,
        string sourceCulture,
        string targetCulture,
        CancellationToken cancellationToken = default);
}
