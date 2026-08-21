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
            new FakeSettingsStore(new AppSettings { ApiKey = "sk-test", ApiModel = "gpt-5-mini", AllowOnlineLanguageAnalysis = true }));

        var result = await service.AnalyzeTelcAsync("Ich schreibe einen ausreichend langen deutschen Text.");

        Assert.Equal("B1", result.Level);
        Assert.Equal(0.82, result.Confidence);
        Assert.Single(result.Errors);
        Assert.NotNull(handler.RequestBody);
        Assert.Contains("\"store\":false", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("\"max_output_tokens\":1200", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"json_schema\"", handler.RequestBody, StringComparison.Ordinal);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
    }

    [Fact]
    public async Task AnalyzeTelc_WithoutKeyReturnsActionableErrorAndSendsNothing()
    {
        var handler = new RecordingHandler("{}");
        var service = new OpenAiLanguageAnalysisService(new HttpClient(handler), new FakeSettingsStore(new AppSettings
        {
            AllowOnlineLanguageAnalysis = true
        }));

        var exception = await Assert.ThrowsAsync<LanguageAnalysisUnavailableException>(() =>
            service.AnalyzeTelcAsync("Ein ausreichend langer Text ohne konfigurierten Schlüssel."));

        Assert.Contains("Настройки", exception.Message);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task AnalyzeTelc_WithoutExplicitConsentSendsNothing()
    {
        var handler = new RecordingHandler("{}");
        var service = new OpenAiLanguageAnalysisService(new HttpClient(handler), new FakeSettingsStore(new AppSettings
        {
            ApiKey = "sk-test"
        }));

        var exception = await Assert.ThrowsAsync<LanguageAnalysisUnavailableException>(() =>
            service.AnalyzeTelcAsync("Ein ausreichend langer Text."));

        Assert.Equal(LanguageAnalysisFailureKind.ConsentRequired, exception.Kind);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task AnalyzeTelc_InputAboveLocalCapSendsNothing()
    {
        var handler = new RecordingHandler("{}");
        var service = CreateConfiguredService(handler);

        var exception = await Assert.ThrowsAsync<LanguageAnalysisUnavailableException>(() =>
            service.AnalyzeTelcAsync(new string('a', OpenAiLanguageAnalysisService.MaximumTelcInputCharacters + 1)));

        Assert.Equal(LanguageAnalysisFailureKind.InputTooLarge, exception.Kind);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task AnalyzeTelc_InvalidApiKeyFormatSendsNothing()
    {
        var handler = new RecordingHandler("{}");
        var service = new OpenAiLanguageAnalysisService(new HttpClient(handler), new FakeSettingsStore(new AppSettings
        {
            ApiKey = "sk-invalid\r\nInjected: header",
            AllowOnlineLanguageAnalysis = true
        }));

        var exception = await Assert.ThrowsAsync<LanguageAnalysisUnavailableException>(() =>
            service.AnalyzeTelcAsync("Ein ausreichend langer Text."));

        Assert.Equal(LanguageAnalysisFailureKind.NotConfigured, exception.Kind);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("42")]
    [InlineData("{\"output\":42}")]
    [InlineData("{\"output\":[42]}")]
    [InlineData("{\"output\":[]}")]
    public async Task AnalyzeTelc_MalformedSuccessfulResponseReturnsTypedProtocolFailure(string response)
    {
        var handler = new RecordingHandler(response);
        var service = CreateConfiguredService(handler);

        var exception = await Assert.ThrowsAsync<LanguageAnalysisUnavailableException>(() =>
            service.AnalyzeTelcAsync("Ein ausreichend langer Text."));

        Assert.Equal(LanguageAnalysisFailureKind.InvalidResponse, exception.Kind);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task AnalyzeTelc_CancellationStopsInFlightRequest()
    {
        var handler = new BlockingHandler();
        var service = new OpenAiLanguageAnalysisService(new HttpClient(handler), new FakeSettingsStore(new AppSettings
        {
            ApiKey = "sk-test",
            AllowOnlineLanguageAnalysis = true
        }));
        using var cancellation = new CancellationTokenSource();
        var operation = service.AnalyzeTelcAsync("Ein ausreichend langer Text.", cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(handler.ObservedCancellation);
    }

    [Fact]
    public async Task AnalyzeTelc_InvalidStructuredPayloadReturnsTypedProtocolFailure()
    {
        var handler = new RecordingHandler("""
            {"output":[{"content":[{"type":"output_text","text":"{\"level\":\"B1\"}"}]}]}
            """);
        var service = CreateConfiguredService(handler);

        var exception = await Assert.ThrowsAsync<LanguageAnalysisUnavailableException>(() =>
            service.AnalyzeTelcAsync("Ein ausreichend langer Text."));

        Assert.Equal(LanguageAnalysisFailureKind.InvalidResponse, exception.Kind);
    }

    [Fact]
    public async Task AnalyzeTelc_NullFieldsInSuccessfulStructuredPayloadReturnTypedProtocolFailure()
    {
        var handler = new RecordingHandler("""
            {"output":[{"content":[{"type":"output_text","text":"{\"level\":\"B1\",\"confidence\":0.7,\"strengths\":[\"Связность\"],\"errors\":[{\"excerpt\":null,\"issue\":\"Ошибка\",\"suggestion\":null}],\"summary\":\"Итог\"}"}]}]}
            """);
        var service = CreateConfiguredService(handler);

        var exception = await Assert.ThrowsAsync<LanguageAnalysisUnavailableException>(() =>
            service.AnalyzeTelcAsync("Ein ausreichend langer Text."));

        Assert.Equal(LanguageAnalysisFailureKind.InvalidResponse, exception.Kind);
    }

    [Fact]
    public async Task AnalyzeTelc_HttpTimeoutReturnsTypedFailure()
    {
        var service = CreateConfiguredService(new TimeoutHandler());

        var exception = await Assert.ThrowsAsync<LanguageAnalysisUnavailableException>(() =>
            service.AnalyzeTelcAsync("Ein ausreichend langer Text."));

        Assert.Equal(LanguageAnalysisFailureKind.Timeout, exception.Kind);
    }

    [Fact]
    public async Task AnalyzeTelc_ResponseAboveCapIsRejectedBeforeParsing()
    {
        var handler = new RecordingHandler(new string('x', OpenAiLanguageAnalysisService.MaximumResponseBytes + 1));
        var service = CreateConfiguredService(handler);

        var exception = await Assert.ThrowsAsync<LanguageAnalysisUnavailableException>(() =>
            service.AnalyzeTelcAsync("Ein ausreichend langer Text."));

        Assert.Equal(LanguageAnalysisFailureKind.InvalidResponse, exception.Kind);
    }

    private static OpenAiLanguageAnalysisService CreateConfiguredService(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        new FakeSettingsStore(new AppSettings
        {
            ApiKey = "sk-test",
            ApiModel = "gpt-5-mini",
            AllowOnlineLanguageAnalysis = true
        }));

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

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ObservedCancellation { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException();
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("fixture timeout", new TimeoutException());
    }
}
