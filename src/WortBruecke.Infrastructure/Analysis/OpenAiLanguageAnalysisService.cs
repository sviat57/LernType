using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;

namespace WortBruecke.Infrastructure.Analysis;

public sealed partial class OpenAiLanguageAnalysisService(HttpClient httpClient, ISettingsStore settingsStore) : ILanguageAnalysisService
{
    public const int MaximumTelcInputCharacters = 20_000;
    public const int MaximumGrammarSourceCharacters = 8_000;
    public const int MaximumGrammarInstructionCharacters = 2_000;
    public const int MaximumGrammarResponseCharacters = 12_000;
    public const int MaximumResponseBytes = 256 * 1024;
    public const int TelcMaximumOutputTokens = 1_200;
    public const int GrammarMaximumOutputTokens = 700;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);

    private static readonly HashSet<string> AllowedLevels = new(StringComparer.Ordinal)
    {
        "A1", "A2", "B1", "B2", "C1", "C2"
    };
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32
    };

    public async Task<TelcAnalysis> AnalyzeTelcAsync(string text, CancellationToken cancellationToken = default)
    {
        ValidateInput(text, MaximumTelcInputCharacters, "Текст для анализа");
        var settings = await GetConfiguredSettingsAsync(cancellationToken);
        var payload = new
        {
            model = settings.ApiModel,
            store = false,
            max_output_tokens = TelcMaximumOutputTokens,
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
                            level = new { type = "string", @enum = AllowedLevels.ToArray() },
                            confidence = new { type = "number", minimum = 0, maximum = 1 },
                            strengths = new { type = "array", maxItems = 8, items = new { type = "string", maxLength = 600 } },
                            errors = new
                            {
                                type = "array",
                                maxItems = 12,
                                items = new
                                {
                                    type = "object",
                                    additionalProperties = false,
                                    properties = new
                                    {
                                        excerpt = new { type = "string", maxLength = 300 },
                                        issue = new { type = "string", maxLength = 600 },
                                        suggestion = new { type = "string", maxLength = 600 }
                                    },
                                    required = new[] { "excerpt", "issue", "suggestion" }
                                }
                            },
                            summary = new { type = "string", maxLength = 2_000 }
                        },
                        required = new[] { "level", "confidence", "strengths", "errors", "summary" }
                    }
                }
            }
        };

        var output = await SendAsync(settings.ApiKey, payload, cancellationToken);
        TelcAnalysis? analysis;
        try
        {
            analysis = JsonSerializer.Deserialize<TelcAnalysis>(output, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw InvalidResponse("Сервис вернул повреждённый структурированный анализ.", exception);
        }
        ValidateAnalysis(analysis);
        return analysis!;
    }

    public async Task<string> AnalyzeGrammarAsync(
        string sourceText,
        string instruction,
        string response,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(sourceText, MaximumGrammarSourceCharacters, "Исходный текст");
        ValidateInput(instruction, MaximumGrammarInstructionCharacters, "Инструкция");
        ValidateInput(response, MaximumGrammarResponseCharacters, "Ответ");
        var settings = await GetConfiguredSettingsAsync(cancellationToken);
        var payload = new
        {
            model = settings.ApiModel,
            store = false,
            max_output_tokens = GrammarMaximumOutputTokens,
            instructions = "Du bist ein Deutschlehrer. Prüfe, ob die Umformung die Aufgabenstellung erfüllt und den Sinn des Ausgangstextes bewahrt. Antworte knapp und konkret auf Russisch: erst Gesamturteil, dann bis zu drei Korrekturen.",
            input = $"AUFGABE:\n{instruction}\n\nAUSGANGSTEXT:\n{sourceText}\n\nANTWORT:\n{response}"
        };
        return await SendAsync(settings.ApiKey, payload, cancellationToken);
    }

    private async Task<AppSettings> GetConfiguredSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        if (!settings.AllowOnlineLanguageAnalysis)
        {
            throw new LanguageAnalysisUnavailableException(
                "Онлайн-анализ выключен. В настройках прочитайте описание передачи текста и явно включите эту функцию.",
                LanguageAnalysisFailureKind.ConsentRequired);
        }
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new LanguageAnalysisUnavailableException(
                "API-ключ не настроен. Откройте «Настройки» и сохраните ключ OpenAI.",
                LanguageAnalysisFailureKind.NotConfigured);
        }
        var trimmedKey = settings.ApiKey.Trim();
        if (trimmedKey.Length > 512 || trimmedKey.Any(char.IsWhiteSpace) || trimmedKey.Any(char.IsControl))
        {
            throw new LanguageAnalysisUnavailableException(
                "API-ключ в настройках имеет неподдерживаемый формат.",
                LanguageAnalysisFailureKind.NotConfigured);
        }
        if (!ModelNamePattern().IsMatch(settings.ApiModel))
        {
            throw new LanguageAnalysisUnavailableException(
                "Имя модели в настройках имеет неподдерживаемый формат.",
                LanguageAnalysisFailureKind.NotConfigured);
        }
        settings.ApiKey = trimmedKey;
        return settings;
    }

    private async Task<string> SendAsync(string apiKey, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LanguageAnalysisUnavailableException(
                "Онлайн-анализ не ответил вовремя. Офлайн-функции продолжают работать.",
                LanguageAnalysisFailureKind.Timeout,
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new LanguageAnalysisUnavailableException(
                "Сервис анализа временно недоступен. Офлайн-функции продолжают работать.",
                LanguageAnalysisFailureKind.ServiceUnavailable,
                exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw CreateHttpFailure(response.StatusCode);
            }

            byte[] responseBody;
            try
            {
                responseBody = await ReadLimitedBodyAsync(response.Content, timeout.Token);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new LanguageAnalysisUnavailableException(
                    "Онлайн-анализ не ответил вовремя. Офлайн-функции продолжают работать.",
                    LanguageAnalysisFailureKind.Timeout,
                    exception);
            }
            catch (IOException exception)
            {
                throw new LanguageAnalysisUnavailableException(
                    "Соединение с сервисом анализа прервано. Офлайн-функции продолжают работать.",
                    LanguageAnalysisFailureKind.ServiceUnavailable,
                    exception);
            }
            try
            {
                using var document = JsonDocument.Parse(responseBody, new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
                {
                    throw InvalidResponse("Сервис не вернул структурированное поле output.");
                }
                foreach (var item in output.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.ValueKind == JsonValueKind.Object &&
                            part.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                            type.GetString() == "output_text" &&
                            part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        {
                            var value = text.GetString();
                            if (!string.IsNullOrWhiteSpace(value)) return value;
                        }
                    }
                }
                throw InvalidResponse("Сервис не вернул текстовый результат.");
            }
            catch (JsonException exception)
            {
                throw InvalidResponse("Сервис вернул повреждённый JSON-ответ.", exception);
            }
            finally
            {
                Array.Clear(responseBody);
            }
        }
    }

    private static async Task<byte[]> ReadLimitedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw InvalidResponse("Ответ сервиса превышает допустимый размер.");
        }
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                if (output.Length + read > MaximumResponseBytes)
                {
                    throw InvalidResponse("Ответ сервиса превышает допустимый размер.");
                }
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void ValidateInput(string value, int maximumCharacters, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new LanguageAnalysisUnavailableException(
                $"{label} пуст.",
                LanguageAnalysisFailureKind.InputTooLarge);
        }
        if (value.Length > maximumCharacters)
        {
            throw new LanguageAnalysisUnavailableException(
                $"{label} превышает локальный лимит {maximumCharacters:N0} символов.",
                LanguageAnalysisFailureKind.InputTooLarge);
        }
    }

    private static void ValidateAnalysis(TelcAnalysis? analysis)
    {
        if (analysis is null || !AllowedLevels.Contains(analysis.Level) ||
            !double.IsFinite(analysis.Confidence) || analysis.Confidence is < 0 or > 1 ||
            analysis.Strengths is null || analysis.Errors is null || string.IsNullOrWhiteSpace(analysis.Summary) ||
            analysis.Strengths.Count > 8 || analysis.Errors.Count > 12 ||
            analysis.Strengths.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 600) || analysis.Summary.Length > 2_000 ||
            analysis.Errors.Any(error => error is null || error.Excerpt is null ||
                string.IsNullOrWhiteSpace(error.Issue) || string.IsNullOrWhiteSpace(error.Suggestion) ||
                error.Excerpt.Length > 300 || error.Issue.Length > 600 || error.Suggestion.Length > 600))
        {
            throw InvalidResponse("Структурированный анализ не прошёл локальную проверку.");
        }
    }

    private static LanguageAnalysisUnavailableException CreateHttpFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(
            "OpenAI отклонил API-ключ. Проверьте ключ в настройках.",
            LanguageAnalysisFailureKind.Authentication),
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => new(
            "Онлайн-анализ не ответил вовремя. Повторите попытку позже.",
            LanguageAnalysisFailureKind.Timeout),
        (HttpStatusCode)429 => new(
            "Лимит онлайн-анализа временно исчерпан. Повторите попытку позже.",
            LanguageAnalysisFailureKind.RateLimited),
        _ => new(
            $"Сервис анализа временно недоступен (HTTP {(int)statusCode}).",
            LanguageAnalysisFailureKind.ServiceUnavailable)
    };

    private static LanguageAnalysisUnavailableException InvalidResponse(string message, Exception? innerException = null) =>
        new(message, LanguageAnalysisFailureKind.InvalidResponse, innerException);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelNamePattern();
}
