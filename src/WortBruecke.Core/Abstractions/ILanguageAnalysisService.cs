using WortBruecke.Core.Models;

namespace WortBruecke.Core.Abstractions;

public interface ILanguageAnalysisService
{
    Task<TelcAnalysis> AnalyzeTelcAsync(string text, CancellationToken cancellationToken = default);
    Task<string> AnalyzeGrammarAsync(string sourceText, string instruction, string response, CancellationToken cancellationToken = default);
}

public enum LanguageAnalysisFailureKind
{
    NotConfigured,
    ConsentRequired,
    InputTooLarge,
    Authentication,
    RateLimited,
    Timeout,
    ServiceUnavailable,
    InvalidResponse
}

public class LanguageAnalysisUnavailableException : InvalidOperationException
{
    public LanguageAnalysisUnavailableException(
        string message,
        LanguageAnalysisFailureKind kind = LanguageAnalysisFailureKind.ServiceUnavailable,
        Exception? innerException = null)
        : base(message, innerException) => Kind = kind;

    public LanguageAnalysisFailureKind Kind { get; }
}
