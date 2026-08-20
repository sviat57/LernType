using WortBruecke.Core.Models;

namespace WortBruecke.Core.Abstractions;

public interface IBookVocabularyExtractor
{
    Task<VocabularyExtractionResult> ExtractAsync(
        string text,
        string sourceCulture,
        int maximumItems = 40,
        CancellationToken cancellationToken = default);
}
