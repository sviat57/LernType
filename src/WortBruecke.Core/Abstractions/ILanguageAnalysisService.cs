using WortBruecke.Core.Models;

namespace WortBruecke.Core.Abstractions;

public interface ILanguageAnalysisService
{
    Task<TelcAnalysis> AnalyzeTelcAsync(string text, CancellationToken cancellationToken = default);
    Task<string> AnalyzeGrammarAsync(string sourceText, string instruction, string response, CancellationToken cancellationToken = default);
}

public sealed class LanguageAnalysisUnavailableException(string message) : InvalidOperationException(message);
