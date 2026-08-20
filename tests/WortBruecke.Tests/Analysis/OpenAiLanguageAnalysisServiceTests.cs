using System.Net;
using System.Text;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;
using WortBruecke.Infrastructure.Analysis;

namespace WortBruecke.Tests.Analysis;

public sealed class OpenAiLanguageAnalysisServiceTests
{
    [Fact]
    public async Task AnalyzeTelc_ParsesStructuredResponseAndDisablesStorage()
    {
        var handler = new RecordingHandler("""
            {
              "output": [{
                "content": [{
                  "type": "output_text",
                  "text": "{\"level\":\"B1\",\"confidence\":0.82,\"strengths\":[\"Связность\"],\"errors\":[{\"excerpt\":\"ich hat\",\"issue\":\"Согласование\",\"suggestion\":\"ich habe\"}],\"summary\":\"Уверенный B1\"}"
                }]
              }]
            }
            """);
        var service = new OpenAiLanguageAnalysisService(
            new HttpClient(handler),
            new FakeSettingsStore(new AppSettings { ApiKey = "sk-test", ApiModel = "gpt-5-mini" }));

        var result = await service.AnalyzeTelcAsync("Ich schreibe einen ausreichend langen deutschen Text.");

        Assert.Equal("B1", result.Level);
        Assert.Equal(0.82, result.Confidence);
        Assert.Single(result.Errors);
        Assert.NotNull(handler.RequestBody);
        Assert.Contains("\"store\":false", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"json_schema\"", handler.RequestBody, StringComparison.Ordinal);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
    }

    [Fact]
    public async Task AnalyzeTelc_WithoutKeyReturnsActionableErrorAndSendsNothing()
    {
        var handler = new RecordingHandler("{}");
        var service = new OpenAiLanguageAnalysisService(new HttpClient(handler), new FakeSettingsStore(new AppSettings()));

        var exception = await Assert.ThrowsAsync<LanguageAnalysisUnavailableException>(() =>
            service.AnalyzeTelcAsync("Ein ausreichend langer Text ohne konfigurierten Schlüssel."));

        Assert.Contains("Настройки", exception.Message);
        Assert.Equal(0, handler.RequestCount);
    }

    private sealed class FakeSettingsStore(AppSettings settings) : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? RequestBody { get; private set; }
        public string? AuthorizationScheme { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
