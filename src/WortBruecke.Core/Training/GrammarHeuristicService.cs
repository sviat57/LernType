using System.Text.RegularExpressions;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;

namespace WortBruecke.Core.Training;

public sealed class GrammarHeuristicService : IGrammarHeuristicService
{
    private static readonly RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public GrammarFeedback Analyze(string markerRule, string response)
    {
        var normalizedRule = markerRule.Trim().ToLowerInvariant();
        return normalizedRule switch
        {
            "basic-sentence" => AnalyzeTwoPart(
                response,
                "простого предложения",
                (@"\b(ich|du|er|sie|es|wir|ihr|Sie)\b", "подлежащее"),
                (@"\b(heiße|heißt|bin|bist|ist|sind|seid|komme|kommst|kommt|wohne|wohnst|wohnt|lerne|lernst|lernt)\b", "спрягаемый глагол")),
            "negation" => AnalyzeSingle(
                response,
                "отрицания",
                @"\b(nicht|kein|keine|keinen|keinem|keiner|keines)\b",
                "nicht или форма kein"),
            "perfekt" => AnalyzeTwoPart(
                response,
                "Perfekt",
                (@"\b(habe|hast|hat|haben|habt|bin|bist|ist|sind|seid)\b", "вспомогательный глагол haben/sein"),
                (@"\b(ge\p{L}+(?:t|en)|besucht|gekocht|gesehen|gelesen|geschrieben|gegangen|gekommen|gewesen|gemacht)\b", "Partizip II")),
            "passiv" => AnalyzeTwoPart(
                response,
                "Passiv",
                (@"\b(wird|werden|wurde|wurden|worden)\b", "форма werden"),
                (@"\b(ge\p{L}+(?:t|en)|besucht|gebaut|gemacht|geschrieben|gelesen|gesehen|gekocht)\b", "Partizip II")),
            "konjunktiv2" or "konjunktiv-ii" => AnalyzeSingle(
                response,
                "Konjunktiv II",
                @"\b(würde|würdest|würden|würdet|hätte|hättest|hätten|wäre|wärest|wären|könnte|müsste|sollte|dürfte)\b",
                "форма würde/hätte/wäre или модальный глагол"),
            "indirekte" or "indirekte-rede" => AnalyzeSingle(
                response,
                "indirekte Rede",
                @"\b(sei|seien|habe|haben|werde|werden|könne|könnten|müsse|müssten|solle|sollten)\b",
                "форма Konjunktiv I/II"),
            "nominalisierung" or "nominalstil" => AnalyzeSingle(
                response,
                "Nominalstil",
                @"\b\p{L}+(?:ung|heit|keit|schaft|tion|nis)\b",
                "номинализация"),
            "partizipialattribut" or "partizipialkonstruktion" => AnalyzeSingle(
                response,
                "Partizipialkonstruktion",
                @"\b(?:ge\p{L}+(?:te|ten|ter|tes|tem|t|ene|enen|ener|enes|enem)|\p{L}+end(?:e|en|er|es|em))\b",
                "причастное определение"),
            _ => new GrammarFeedback(
                false,
                "Для этого задания пока нет офлайн-правила. Можно проверить текст через LLM-анализ.",
                [],
                ["неизвестное правило"])
        };
    }

    private static GrammarFeedback AnalyzeTwoPart(
        string response,
        string ruleName,
        (string Pattern, string Label) first,
        (string Pattern, string Label) second)
    {
        var firstMatch = Regex.Match(response, first.Pattern, Options);
        var secondMatch = Regex.Match(response, second.Pattern, Options);
        var found = new List<string>();
        var missing = new List<string>();
        AddResult(firstMatch, first.Label, found, missing);
        AddResult(secondMatch, second.Label, found, missing);
        var complete = firstMatch.Success && secondMatch.Success;
        return new GrammarFeedback(
            complete,
            complete
                ? $"Основные маркеры {ruleName} найдены. Эвристика не оценивает смысл и согласование."
                : $"Для {ruleName} не хватает одного или нескольких ожидаемых маркеров.",
            found,
            missing);
    }

    private static GrammarFeedback AnalyzeSingle(string response, string ruleName, string pattern, string label)
    {
        var matches = Regex.Matches(response, pattern, Options).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        var found = matches.Select(value => $"{label}: {value}").ToList();
        var success = found.Count > 0;
        return new GrammarFeedback(
            success,
            success
                ? $"Маркер {ruleName} найден. Проверьте, что форма уместна по смыслу во всём пересказе."
                : $"Ожидаемый маркер {ruleName} не найден.",
            found,
            success ? [] : [label]);
    }

    private static void AddResult(Match match, string label, ICollection<string> found, ICollection<string> missing)
    {
        if (match.Success)
        {
            found.Add($"{label}: {match.Value}");
        }
        else
        {
            missing.Add(label);
        }
    }
}
