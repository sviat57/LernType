namespace WortBruecke.Core.Models;

public sealed record TelcError(string Excerpt, string Issue, string Suggestion);

public sealed record TelcAnalysis(
    string Level,
    double Confidence,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<TelcError> Errors,
    string Summary);

public sealed record GrammarFeedback(
    bool HasExpectedMarkers,
    string Summary,
    IReadOnlyList<string> FoundMarkers,
    IReadOnlyList<string> MissingMarkers);
