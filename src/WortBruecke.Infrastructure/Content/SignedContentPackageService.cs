using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;

namespace WortBruecke.Infrastructure.Content;

public sealed class SignedContentPackageService : IContentPackageService
{
    public const string ManifestEntryName = "manifest.json";
    public const string SignatureEntryName = "manifest.sig";
    private const long MaximumPackageBytes = 536_870_912;
    private const int MaximumEntries = 10_000;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _packagesRoot;
    private readonly Version _appVersion;
    private readonly RSA _trustedKey;

    public SignedContentPackageService(string packagesRoot, Version appVersion, string trustedPublicKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagesRoot);
        ArgumentNullException.ThrowIfNull(appVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedPublicKeyPem);

        _packagesRoot = Path.GetFullPath(packagesRoot);
        _appVersion = appVersion;
        _trustedKey = RSA.Create();
        _trustedKey.ImportFromPem(trustedPublicKeyPem);
    }

    public async Task<ContentPackageVerificationResult> VerifyAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        ContentPackageManifest? manifest = null;

        try
        {
            var fullPackagePath = Path.GetFullPath(packagePath);
            var packageInfo = new FileInfo(fullPackagePath);
            if (!packageInfo.Exists)
            {
                return Invalid("Файл пакета не найден.");
            }

            if (packageInfo.Length is <= 0 or > MaximumPackageBytes)
            {
                return Invalid("Размер пакета выходит за допустимый предел 512 МБ.");
            }

            await using var stream = new FileStream(
                fullPackagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                65_536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count is < 2 or > MaximumEntries)
            {
                return Invalid("Пакет содержит недопустимое количество файлов.");
            }

            var manifestEntry = GetSingleEntry(archive, ManifestEntryName);
            var signatureEntry = GetSingleEntry(archive, SignatureEntryName);
            var manifestBytes = await ReadEntryAsync(manifestEntry, 1_048_576, cancellationToken);
            var signatureBytes = await ReadEntryAsync(signatureEntry, 16_384, cancellationToken);

            if (!_trustedKey.VerifyData(
                    manifestBytes,
                    signatureBytes,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss))
            {
                errors.Add("Цифровая подпись manifest.json недействительна.");
            }

            manifest = JsonSerializer.Deserialize<ContentPackageManifest>(manifestBytes, SerializerOptions);
            if (manifest is null)
            {
                errors.Add("Manifest пакета пуст или повреждён.");
                return new(false, null, errors);
            }

            ValidateManifest(manifest, errors);
            if (errors.Count > 0)
            {
                return new(false, manifest, errors);
            }

            var expected = manifest.Files.ToDictionary(
                item => NormalizeEntryPath(item.Path),
                StringComparer.Ordinal);
            var payloadEntries = archive.Entries
                .Where(entry => !IsMetadataEntry(entry.FullName) && !string.IsNullOrEmpty(entry.Name))
                .ToArray();

            if (payloadEntries.Length != expected.Count)
            {
                errors.Add("Состав файлов пакета не совпадает с manifest.json.");
            }

            long totalSize = 0;
            foreach (var entry in payloadEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalizedPath = NormalizeEntryPath(entry.FullName);
                EnsureSafeRelativePath(normalizedPath);
                if (!expected.TryGetValue(normalizedPath, out var file))
                {
                    errors.Add($"Файл {normalizedPath} отсутствует в manifest.json.");
                    continue;
                }

                totalSize = checked(totalSize + entry.Length);
                if (totalSize > MaximumPackageBytes || entry.Length != file.Size)
                {
                    errors.Add($"Размер файла {normalizedPath} не совпадает с manifest.json.");
                    continue;
                }

                await using var entryStream = entry.Open();
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(entryStream, cancellationToken));
                if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Контрольная сумма файла {normalizedPath} не совпадает.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          JsonException or CryptographicException or UnauthorizedAccessException)
        {
            errors.Add(exception.Message);
        }

        return new(errors.Count == 0, manifest, errors);

        ContentPackageVerificationResult Invalid(string error) => new(false, null, [error]);
    }

    public async Task<ContentPackageInstallResult> InstallAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        var verification = await VerifyAsync(packagePath, cancellationToken);
        if (!verification.IsValid || verification.Manifest is null)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, verification.Errors));
        }

        var manifest = verification.Manifest;
        var packageRoot = GetSafePackageRoot(manifest.PackageId);
        var finalPath = GetSafeVersionRoot(manifest.PackageId, manifest.Version);
        Directory.CreateDirectory(packageRoot);
        if (Directory.Exists(finalPath))
        {
            var activeVersion = await ReadActiveVersionAsync(manifest.PackageId, cancellationToken);
            return new(new(manifest.PackageId, manifest.Version, finalPath,
                manifest.Version.Equals(activeVersion, StringComparison.OrdinalIgnoreCase)), true);
        }

        var stagingPath = Path.Combine(packageRoot, $".{manifest.Version}.staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingPath);
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalizedPath = NormalizeEntryPath(file.Path);
                EnsureSafeRelativePath(normalizedPath);
                var entry = GetSingleEntry(archive, normalizedPath);
                var destination = Path.GetFullPath(Path.Combine(stagingPath, normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
                EnsureInside(stagingPath, destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var source = entry.Open();
                await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, true);
                await source.CopyToAsync(target, cancellationToken);
                await target.FlushAsync(cancellationToken);
            }

            await File.WriteAllTextAsync(
                Path.Combine(stagingPath, ManifestEntryName),
                JsonSerializer.Serialize(manifest, SerializerOptions),
                cancellationToken);
            Directory.Move(stagingPath, finalPath);
            await WriteActiveVersionAsync(manifest.PackageId, manifest.Version, cancellationToken);
            return new(new(manifest.PackageId, manifest.Version, finalPath, true), false);
        }
        catch
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
            throw;
        }
    }

    public async Task<IReadOnlyList<InstalledContentPackage>> GetInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_packagesRoot))
        {
            return [];
        }

        var result = new List<InstalledContentPackage>();
        foreach (var packageRoot in Directory.EnumerateDirectories(_packagesRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packageId = Path.GetFileName(packageRoot);
            var active = await ReadActiveVersionAsync(packageId, cancellationToken);
            foreach (var versionRoot in Directory.EnumerateDirectories(packageRoot)
                         .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal)))
            {
                var version = Path.GetFileName(versionRoot);
                result.Add(new(packageId, version, versionRoot,
                    version.Equals(active, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return result
            .OrderBy(item => item.PackageId, StringComparer.Ordinal)
            .ThenByDescending(item => item.Version, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<InstalledContentPackage> ActivateAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken = default)
    {
        var versionRoot = GetSafeVersionRoot(packageId, version);
        if (!Directory.Exists(versionRoot))
        {
            throw new DirectoryNotFoundException("Указанная версия контент-пакета не установлена.");
        }

        await WriteActiveVersionAsync(packageId, version, cancellationToken);
        return new(packageId, version, versionRoot, true);
    }

    public async Task RemoveAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var versionRoot = GetSafeVersionRoot(packageId, version);
        if (!Directory.Exists(versionRoot))
        {
            return;
        }

        var active = await ReadActiveVersionAsync(packageId, cancellationToken);
        if (version.Equals(active, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Сначала активируйте другую версию пакета.");
        }

        Directory.Delete(versionRoot, recursive: true);
    }

    private void ValidateManifest(ContentPackageManifest manifest, ICollection<string> errors)
    {
        if (manifest.SchemaVersion != "1")
        {
            errors.Add("Версия схемы manifest.json не поддерживается.");
        }
        ValidateIdentifier(manifest.PackageId, "идентификатор пакета", errors);
        ValidateIdentifier(manifest.Version, "версия пакета", errors);
        if (!Version.TryParse(manifest.MinimumAppVersion, out var minimumVersion))
        {
            errors.Add("MinimumAppVersion имеет неверный формат.");
        }
        else if (minimumVersion > _appVersion)
        {
            errors.Add($"Пакет требует LernType {minimumVersion} или новее.");
        }
        if (string.IsNullOrWhiteSpace(manifest.LicenseId) || string.IsNullOrWhiteSpace(manifest.LicenseUrl))
        {
            errors.Add("В пакете не указана лицензия.");
        }
        if (manifest.Files.Count == 0 || manifest.Files.Count > MaximumEntries - 2)
        {
            errors.Add("Manifest содержит недопустимое количество файлов.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            try
            {
                var normalized = NormalizeEntryPath(file.Path);
                EnsureSafeRelativePath(normalized);
                if (!paths.Add(normalized))
                {
                    errors.Add($"Файл {normalized} указан несколько раз.");
                }
                if (file.Size < 0 || file.Size > MaximumPackageBytes)
                {
                    errors.Add($"Размер файла {normalized} недопустим.");
                }
                if (file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                {
                    errors.Add($"SHA-256 файла {normalized} имеет неверный формат.");
                }
            }
            catch (InvalidDataException exception)
            {
                errors.Add(exception.Message);
            }
        }
    }

    private static void ValidateIdentifier(string value, string title, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 ||
            value.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            errors.Add($"{title} содержит недопустимые символы.");
        }
    }

    private static ZipArchiveEntry GetSingleEntry(ZipArchive archive, string name)
    {
        var normalized = NormalizeEntryPath(name);
        var matches = archive.Entries
            .Where(entry => NormalizeEntryPath(entry.FullName).Equals(normalized, StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException($"Пакет должен содержать ровно один файл {normalized}.");
    }

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchiveEntry entry,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length > maximumBytes)
        {
            throw new InvalidDataException($"Файл {entry.FullName} слишком большой.");
        }
        await using var stream = entry.Open();
        using var memory = new MemoryStream((int)entry.Length);
        await stream.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }

    private static bool IsMetadataEntry(string path)
    {
        var normalized = NormalizeEntryPath(path);
        return normalized is ManifestEntryName or SignatureEntryName;
    }

    private static string NormalizeEntryPath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static void EnsureSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ||
            path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"Небезопасный путь в пакете: {path}");
        }
    }

    private string GetSafePackageRoot(string packageId)
    {
        ValidateIdentifierOrThrow(packageId);
        var result = Path.GetFullPath(Path.Combine(_packagesRoot, packageId));
        EnsureInside(_packagesRoot, result);
        return result;
    }

    private string GetSafeVersionRoot(string packageId, string version)
    {
        ValidateIdentifierOrThrow(version);
        var packageRoot = GetSafePackageRoot(packageId);
        var result = Path.GetFullPath(Path.Combine(packageRoot, version));
        EnsureInside(packageRoot, result);
        return result;
    }

    private static void ValidateIdentifierOrThrow(string value)
    {
        var errors = new List<string>();
        ValidateIdentifier(value, "идентификатор", errors);
        if (errors.Count > 0)
        {
            throw new ArgumentException(errors[0], nameof(value));
        }
    }

    private static void EnsureInside(string root, string candidate)
    {
        var rootWithSeparator = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Путь пакета выходит за пределы хранилища.");
        }
    }

    private string GetActiveFile(string packageId) => Path.Combine(GetSafePackageRoot(packageId), "active.version");

    private async Task<string?> ReadActiveVersionAsync(string packageId, CancellationToken cancellationToken)
    {
        var path = GetActiveFile(packageId);
        return File.Exists(path) ? (await File.ReadAllTextAsync(path, cancellationToken)).Trim() : null;
    }

    private async Task WriteActiveVersionAsync(string packageId, string version, CancellationToken cancellationToken)
    {
        var packageRoot = GetSafePackageRoot(packageId);
        Directory.CreateDirectory(packageRoot);
        var target = GetActiveFile(packageId);
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, version, cancellationToken);
        File.Move(temporary, target, overwrite: true);
    }
}
