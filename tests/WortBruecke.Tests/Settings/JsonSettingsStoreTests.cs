using WortBruecke.Core.Models;
using WortBruecke.Infrastructure.Paths;
using WortBruecke.Infrastructure.Settings;

namespace WortBruecke.Tests.Settings;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WortBrueckeSettingsTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_ProtectsApiKeyWithCurrentUserDpapi()
    {
        var paths = new AppPaths(Path.Combine(_root, "Content"), _root);
        var store = new JsonSettingsStore(paths);
        var settings = new AppSettings { ApiKey = "sk-test-secret-value", UseDarkTheme = true, AllowOnlineLanguageAnalysis = true };

        await store.SaveAsync(settings);
        var fileText = await File.ReadAllTextAsync(paths.LocalSettingsPath);
        var loaded = await store.LoadAsync();

        Assert.DoesNotContain(settings.ApiKey, fileText, StringComparison.Ordinal);
        Assert.Equal(settings.ApiKey, loaded.ApiKey);
        Assert.True(loaded.UseDarkTheme);
        Assert.True(loaded.AllowOnlineLanguageAnalysis);
    }

    [Fact]
    public async Task Save_RevokingOnlineAnalysisConsentPersistsDisabledState()
    {
        var paths = new AppPaths(Path.Combine(_root, "Content"), _root);
        var store = new JsonSettingsStore(paths);
        var settings = new AppSettings { ApiKey = "sk-test", AllowOnlineLanguageAnalysis = true };
        await store.SaveAsync(settings);

        settings.AllowOnlineLanguageAnalysis = false;
        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.False(loaded.AllowOnlineLanguageAnalysis);
        var persisted = await File.ReadAllTextAsync(paths.LocalSettingsPath);
        Assert.Contains("\"OnlineAnalysisConsentVersion\": 0", persisted, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
