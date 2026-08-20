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
        var settings = new AppSettings { ApiKey = "sk-test-secret-value", PassageFrequency = 11, UseDarkTheme = true };

        await store.SaveAsync(settings);
        var fileText = await File.ReadAllTextAsync(paths.LocalSettingsPath);
        var loaded = await store.LoadAsync();

        Assert.DoesNotContain(settings.ApiKey, fileText, StringComparison.Ordinal);
        Assert.Equal(settings.ApiKey, loaded.ApiKey);
        Assert.Equal(11, loaded.PassageFrequency);
        Assert.True(loaded.UseDarkTheme);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
