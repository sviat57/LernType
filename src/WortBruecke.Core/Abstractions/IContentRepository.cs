using WortBruecke.Core.Models;

namespace WortBruecke.Core.Abstractions;

public interface IContentRepository
{
    Task<IReadOnlyList<Theme>> GetThemesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WordEntry>> GetWordsAsync(int? themeId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SentenceEntry>> GetSentencesAsync(int? themeId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Passage>> GetPassagesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GrammarTask>> GetGrammarTasksAsync(CancellationToken cancellationToken = default);
}
