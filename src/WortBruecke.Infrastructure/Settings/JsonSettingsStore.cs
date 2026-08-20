using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;
using WortBruecke.Infrastructure.Paths;

namespace WortBruecke.Infrastructure.Settings;

public sealed class JsonSettingsStore(AppPaths paths) : ISettingsStore
{
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("WortBruecke.Settings.v1");
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.LocalSettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(paths.LocalSettingsPath);
            var persisted = await JsonSerializer.DeserializeAsync<PersistedSettings>(stream, SerializerOptions, cancellationToken);
            if (persisted is null)
            {
                return new AppSettings();
            }
            return new AppSettings
            {
                SourceCulture = persisted.SourceCulture,
                TargetCulture = persisted.TargetCulture,
                PassageFrequency = Math.Clamp(persisted.PassageFrequency, 1, 20),
                PassageMode = persisted.PassageMode,
                ApiModel = string.IsNullOrWhiteSpace(persisted.ApiModel) ? "gpt-5-mini" : persisted.ApiModel,
                ApiKey = Unprotect(persisted.ProtectedApiKey),
                UseDarkTheme = persisted.UseDarkTheme
            };
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _saveGate.WaitAsync(cancellationToken);
        string? temporaryPath = null;
        try
        {
            paths.EnsureDataDirectory();
            var persisted = new PersistedSettings
            {
                SourceCulture = settings.SourceCulture,
                TargetCulture = settings.TargetCulture,
                PassageFrequency = Math.Clamp(settings.PassageFrequency, 1, 20),
                PassageMode = settings.PassageMode,
                ApiModel = settings.ApiModel,
                ProtectedApiKey = Protect(settings.ApiKey),
                UseDarkTheme = settings.UseDarkTheme
            };
            temporaryPath = $"{paths.LocalSettingsPath}.{Guid.NewGuid():N}.tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, persisted, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, paths.LocalSettingsPath, true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // A failed cleanup must not hide the original save error.
                }
                catch (UnauthorizedAccessException)
                {
                    // A failed cleanup must not hide the original save error.
                }
            }
            _saveGate.Release();
        }
    }

    private static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), OptionalEntropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string Unprotect(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        try
        {
            var plainBytes = ProtectedData.Unprotect(Convert.FromBase64String(value), OptionalEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }

    private sealed class PersistedSettings
    {
        public string SourceCulture { get; init; } = "ru-RU";
        public string TargetCulture { get; init; } = "de-DE";
        public int PassageFrequency { get; init; } = 8;
        public PassagePracticeMode PassageMode { get; init; } = PassagePracticeMode.Translation;
        public string ApiModel { get; init; } = "gpt-5-mini";
        public string ProtectedApiKey { get; init; } = string.Empty;
        public bool UseDarkTheme { get; init; }
    }
}
