using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;

namespace WortBruecke.Infrastructure.Analysis;

public sealed class OpenAiLanguageAnalysisService(HttpClient httpClient, ISettingsStore settingsStore) : ILanguageAnalysisService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<TelcAnalysis> AnalyzeTelcAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var settings = await GetConfiguredSettingsAsync(cancellationToken);
        var payload = new
        {
            model = settings.ApiModel,
            store = false,
            instructions = "Du bewertest deutsche Lernertexte anhand der CEFR/TELC-Deskriptoren für Wortschatz, grammatische Richtigkeit und Komplexität sowie Kohärenz. Gib die Begründung, Stärken, Fehler und Vorschläge auf Russisch zurück. Zitiere nur kurze relevante Ausschnitte.",
            input = text,
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "telc_analysis",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            level = new { type = "string", @enum = new[] { "A1", "A2", "B1", "B2", "C1", "C2" } },
                            confidence = new { type = "number", minimum = 0, maximum = 1 },
                            strengths = new { type = "array", items = new { type = "string" } },
                            errors = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    additionalProperties = false,
                                    properties = new
                                    {
                                        excerpt = new { type = "string" },
                                        issue = new { type = "string" },
                                        suggestion = new { type = "string" }
                                    },
                                    required = new[] { "excerpt", "issue", "suggestion" }
                                }
                            },
                            summary = new { type = "string" }
                        },
                        required = new[] { "level", "confidence", "strengths", "errors", "summary" }
                    }
                }
            }
        };

        var output = await SendAsync(settings.ApiKey, payload, cancellationToken);
        return JsonSerializer.Deserialize<TelcAnalysis>(output, SerializerOptions)
               ?? throw new InvalidDataException("API вернул пустой TELC-анализ.");
    }

    public async Task<string> AnalyzeGrammarAsync(string sourceText, string instruction, string response, CancellationToken cancellationToken = default)
    {
        var settings = await GetConfiguredSettingsAsync(cancellationToken);
        var payload = new
        {
            model = settings.ApiModel,
            store = false,
            instructions = "Du bist ein Deutschlehrer. Prüfe, ob die Umformung die Aufgabenstellung erfüllt und den Sinn des Ausgangstextes bewahrt. Antworte knapp und konkret auf Russisch: erst Gesamturteil, dann bis zu drei Korrekturen.",
            input = $"AUFGABE:\n{instruction}\n\nAUSGANGSTEXT:\n{sourceText}\n\nANTWORT:\n{response}"
        };
        return await SendAsync(settings.ApiKey, payload, cancellationToken);
    }

    private async Task<AppSettings> GetConfiguredSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new LanguageAnalysisUnavailableException("API-ключ не настроен. Откройте «Настройки» и сохраните ключ OpenAI для TELC-анализа.");
        }
        return settings;
    }

    private async Task<string> SendAsync(string apiKey, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "OpenAI отклонил API-ключ. Проверьте ключ в настройках."
                : $"Сервис анализа временно недоступен (HTTP {(int)response.StatusCode}).";
            throw new LanguageAnalysisUnavailableException(message);
        }

        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("output", out var output))
        {
            throw new InvalidDataException("API не вернул поле output.");
        }
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content))
            {
                continue;
            }
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var type) && type.GetString() == "output_text" &&
                    part.TryGetProperty("text", out var text))
                {
                    return text.GetString() ?? string.Empty;
                }
            }
        }
        throw new InvalidDataException("API не вернул текстовый результат.");
    }
}
