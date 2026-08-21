using WortBruecke.Core.Models;
using WortBruecke.Infrastructure.Paths;
using WortBruecke.Infrastructure.Settings;

namespace WortBruecke.Tests.Settings;

public sealed class JsonSettingsStoreResilienceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "WortBrueckeSettingsResilienceTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Load_InvalidProtectedApiKeyKeepsOtherSettingsAndClearsOnlySecret()
    {
        var paths = new AppPaths(Path.Combine(_root, "Content"), _root);
        paths.EnsureDataDirectory();
        await File.WriteAllTextAsync(paths.LocalSettingsPath, """
            {
              "SourceCulture": "ru-RU",
              "TargetCulture": "de-DE",
              "PassageFrequency": 13,
              "PassageMode": "GermanTyping",
              "ApiModel": "gpt-5-mini",
              "ProtectedApiKey": "this-is-not-base64!",
              "UseDarkTheme": true
            }
            """);

        var loaded = await new JsonSettingsStore(paths).LoadAsync();

        Assert.Equal(13, loaded.PassageFrequency);
        Assert.Equal(PassagePracticeMode.GermanTyping, loaded.PassageMode);
        Assert.True(loaded.UseDarkTheme);
        Assert.Empty(loaded.ApiKey);
        Assert.False(loaded.AllowOnlineLanguageAnalysis);
    }

    [Fact]
    public async Task Save_ConcurrentCallsCompleteWithoutSharingOrphanedTemporaryFile()
    {
        var paths = new AppPaths(Path.Combine(_root, "Content"), _root);
        var store = new JsonSettingsStore(paths);
        var saves = Enumerable.Range(1, 12)
            .Select(index => store.SaveAsync(new AppSettings
            {
                ApiKey = $"sk-concurrent-{index}",
                ApiModel = $"model-{index}",
                PassageFrequency = index
            }))
            .ToArray();

        await Task.WhenAll(saves);
        var loaded = await store.LoadAsync();

        Assert.Contains(loaded.ApiModel, Enumerable.Range(1, 12).Select(index => $"model-{index}"));
        Assert.Contains(loaded.ApiKey, Enumerable.Range(1, 12).Select(index => $"sk-concurrent-{index}"));
        Assert.Empty(Directory.EnumerateFiles(_root, "settings.json.*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
