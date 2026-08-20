using WortBruecke.Core.Models;

namespace WortBruecke.Core.Abstractions;

public interface IGrammarHeuristicService
{
    GrammarFeedback Analyze(string markerRule, string response);
}
