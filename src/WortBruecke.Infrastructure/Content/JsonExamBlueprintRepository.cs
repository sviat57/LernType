using System.Text.Json;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;

namespace WortBruecke.Infrastructure.Content;

public sealed class JsonExamBlueprintRepository(string contentRoot) : IExamBlueprintRepository
{
    public async Task<ExamBlueprintCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(contentRoot, "exams.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Не найден каталог форматов экзаменов.", path);
        }

        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
        {
            throw new InvalidDataException("Версия каталога экзаменов не поддерживается.");
        }

        var providers = root.GetProperty("providers").EnumerateArray().ToDictionary(
            item => RequiredString(item, "id"),
            item => RequiredString(item, "name"),
            StringComparer.OrdinalIgnoreCase);
        var sources = root.GetProperty("sources").EnumerateArray().ToDictionary(
            item => RequiredString(item, "id"),
            item => new ExamSourceLink(RequiredString(item, "title"), RequiredString(item, "url")),
            StringComparer.OrdinalIgnoreCase);

        var exams = new List<ExamBlueprint>();
        foreach (var item in root.GetProperty("exams").EnumerateArray())
        {
            var providerId = RequiredString(item, "providerId");
            if (!providers.TryGetValue(providerId, out var providerName))
            {
                throw new InvalidDataException($"Экзамен ссылается на неизвестного провайдера: {providerId}.");
            }

            var segments = item.GetProperty("segments").EnumerateArray().Select(segment => new ExamBlueprintSegment(
                RequiredString(segment, "id"),
                ReadStrings(segment, "skills"),
                segment.GetProperty("parts").GetInt32(),
                segment.GetProperty("durationMinutes").GetInt32(),
                segment.TryGetProperty("approximate", out var approximate) && approximate.GetBoolean(),
                ReadStrings(segment, "taskFamilies"),
                OptionalInt(segment, "items"),
                OptionalInt(segment, "preparationMinutes") ?? 0,
                OptionalInt(segment, "breakMinutes") ?? 0,
                OptionalBool(segment, "pairFormat"),
                OptionalBool(segment, "groupFormat"),
                OptionalBool(segment, "individualFormat"),
                OptionalBool(segment, "recordedComputerFormat"),
                OptionalBool(segment, "durationPerParticipant"))).ToArray();
            var scoring = item.GetProperty("scoring");
            var sourceLinks = ReadStrings(item, "sourceRefs").Select(sourceId =>
                sources.TryGetValue(sourceId, out var source)
                    ? source
                    : throw new InvalidDataException($"Экзамен ссылается на неизвестный источник: {sourceId}.")).ToArray();

            exams.Add(new ExamBlueprint(
                RequiredString(item, "id"),
                providerId,
                providerName,
                RequiredString(item, "name"),
                ReadStrings(item, "cefrLevels"),
                segments,
                RequiredString(scoring, "type"),
                ParseScoring(scoring),
                DescribeScoring(scoring),
                ReadStrings(item, "appTrainingRequirements"),
                sourceLinks,
                OptionalBool(item, "partialExamAvailable"),
                OptionalInt(item, "breakMinutes") ?? 0,
                OptionalString(item, "navigationPolicy"),
                ReadStrings(item, "delivery")));
        }

        return new ExamBlueprintCatalog(
            DateOnly.Parse(RequiredString(root, "lastVerified")),
            RequiredString(root.GetProperty("coverage"), "noteRu"),
            RequiredString(root.GetProperty("coverage"), "readinessDisclaimerRu"),
            exams.AsReadOnly());
    }

    private static ExamScoringDefinition ParseScoring(JsonElement scoring)
    {
        var type = RequiredString(scoring, "type");
        return type switch
        {
            "whole-exam-threshold" => new ExamScoringDefinition(
                ExamScoringKind.WholeExamThreshold,
                OverallPassRatio: scoring.GetProperty("passPercent").GetDouble() / 100),
            "whole-exam-with-component-thresholds" => new ExamScoringDefinition(
                ExamScoringKind.WholeExamWithComponentThresholds,
                OverallPassRatio: Ratio(scoring, "passPoints", "maxPoints"),
                WrittenPassRatio: Ratio(scoring, "writtenPassPoints", "writtenMaxPoints"),
                OralPassRatio: Ratio(scoring, "oralPassPoints", "oralMaxPoints")),
            "independent-modules" => new ExamScoringDefinition(
                ExamScoringKind.IndependentModules,
                ModulePassRatio: scoring.GetProperty("modulePassPercent").GetDouble() / 100),
            "written-and-oral-thresholds" => new ExamScoringDefinition(
                ExamScoringKind.WrittenAndOralThresholds,
                OverallPassRatio: Ratio(scoring, "passPoints", "maxPoints"),
                WrittenPassRatio: Ratio(scoring, "writtenPassPoints", "writtenMaxPoints"),
                OralPassRatio: Ratio(scoring, "oralPassPoints", "oralMaxPoints")),
            "band-per-skill" => new ExamScoringDefinition(
                ExamScoringKind.BandPerSkill,
                SkillMaximumPoints: scoring.GetProperty("skillPointRange")[1].GetInt32(),
                Bands: scoring.GetProperty("bands").EnumerateArray().Select(item => new ExamScoreBand(
                    RequiredString(item, "name"),
                    item.GetProperty("min").GetInt32(),
                    item.GetProperty("max").GetInt32())).ToArray(),
                UniversalPass: scoring.GetProperty("universalPass").GetBoolean()),
            "dual-level-profile" => ParseDtzScoring(scoring),
            _ => throw new InvalidDataException($"Неизвестная схема оценивания экзамена: {type}.")
        };
    }

    private static ExamScoringDefinition ParseDtzScoring(JsonElement scoring)
    {
        var receptive = scoring.GetProperty("receptiveCombined");
        return new ExamScoringDefinition(
            ExamScoringKind.DualLevelProfile,
            UniversalPass: false,
            ReceptiveMaximumItems: receptive.GetProperty("maxItems").GetInt32(),
            ReceptiveA2MinimumCorrect: receptive.GetProperty("a2MinCorrect").GetInt32(),
            ReceptiveB1MinimumCorrect: receptive.GetProperty("b1MinCorrect").GetInt32());
    }

    private static double Ratio(JsonElement element, string numerator, string denominator) =>
        element.GetProperty(numerator).GetDouble() / element.GetProperty(denominator).GetDouble();

    private static string DescribeScoring(JsonElement scoring)
    {
        var type = RequiredString(scoring, "type");
        return type switch
        {
            "whole-exam-threshold" => $"Общий результат: не менее {scoring.GetProperty("passPercent").GetInt32()}%.",
            "whole-exam-with-component-thresholds" =>
                $"Общий порог {scoring.GetProperty("passPoints").GetInt32()} из {scoring.GetProperty("maxPoints").GetInt32()}; " +
                $"письменно {scoring.GetProperty("writtenPassPoints").GetInt32()} из {scoring.GetProperty("writtenMaxPoints").GetInt32()}, " +
                $"устно {scoring.GetProperty("oralPassPoints").GetInt32()} из {scoring.GetProperty("oralMaxPoints").GetInt32()}.",
            "independent-modules" =>
                $"Каждый модуль оценивается отдельно; порог {scoring.GetProperty("modulePassPercent").GetInt32()}%.",
            "written-and-oral-thresholds" =>
                $"Не менее {scoring.GetProperty("componentPassPercent").GetInt32()}% отдельно в письменной и устной частях.",
            "band-per-skill" =>
                "Каждый навык получает отдельную шкалу TDN 3–5; единого проходного балла нет, требования задаёт вуз.",
            "dual-level-profile" =>
                "Итог A2 или B1 определяется профилем навыков; для B1 устная часть обязательна на B1.",
            _ => "Правила результата указаны в официальном источнике экзамена."
        };
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName).GetString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Пустое обязательное поле экзамена: {propertyName}.")
            : value;
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).EnumerateArray()
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToArray();

    private static int? OptionalInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) ? value.GetInt32() : null;

    private static bool OptionalBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.GetBoolean();

    private static string? OptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}
