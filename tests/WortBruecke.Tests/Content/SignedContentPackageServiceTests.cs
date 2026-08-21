using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WortBruecke.Core.Models;
using WortBruecke.Infrastructure.Content;

namespace WortBruecke.Tests.Content;

public sealed class SignedContentPackageServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lerntype-pack-tests-{Guid.NewGuid():N}");
    private readonly RSA _key = RSA.Create(2048);

    [Fact]
    public async Task VerifyAndInstallAsync_ValidSignedPackage_InstallsAtomically()
    {
        var package = CreatePackage("core.a1", "1.2.0", "content/catalog.json", "{\"revision\":4}");
        var service = CreateService();

        var verification = await service.VerifyAsync(package);
        var result = await service.InstallAsync(package);
        var installed = await service.GetInstalledAsync();

        Assert.True(verification.IsValid, string.Join(Environment.NewLine, verification.Errors));
        Assert.Equal("core.a1", result.Package.PackageId);
        Assert.True(result.Package.IsActive);
        Assert.False(result.WasAlreadyInstalled);
        Assert.Single(installed);
        Assert.True(File.Exists(Path.Combine(result.Package.InstallPath, "content", "catalog.json")));
        Assert.Empty(Directory.EnumerateDirectories(
            Path.GetDirectoryName(result.Package.InstallPath)!, ".staging-*"));
    }

    [Fact]
    public async Task VerifyAsync_TamperedPayload_IsRejected()
    {
        var package = CreatePackage("core.a1", "1.0.0", "content.txt", "original");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("content.txt")!;
            entry.Delete();
            entry = archive.CreateEntry("content.txt");
            await using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            await writer.WriteAsync("tampered");
        }

        var verification = await CreateService().VerifyAsync(package);

        Assert.False(verification.IsValid);
        Assert.Contains(verification.Errors, error => error.Contains("контрольная сумма", StringComparison.OrdinalIgnoreCase) ||
                                                     error.Contains("Размер файла", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyAsync_PathTraversal_IsRejected()
    {
        var package = CreatePackage("core.a1", "1.0.0", "../outside.txt", "unsafe");

        var verification = await CreateService().VerifyAsync(package);

        Assert.False(verification.IsValid);
        Assert.Contains(verification.Errors, error => error.Contains("Небезопасный путь", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    [Fact]
    public async Task VerifyAsync_NewerMinimumAppVersion_IsRejected()
    {
        var package = CreatePackage("core.a1", "1.0.0", "content.txt", "data", "99.0.0");

        var verification = await CreateService().VerifyAsync(package);

        Assert.False(verification.IsValid);
        Assert.Contains(verification.Errors, error => error.Contains("требует LernType", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoveAsync_ActiveVersion_IsProtectedAndRollbackCanBeActivated()
    {
        var first = CreatePackage("core.a1", "1.0.0", "content.txt", "first");
        var second = CreatePackage("core.a1", "2.0.0", "content.txt", "second");
        var service = CreateService();
        await service.InstallAsync(first);
        await service.InstallAsync(second);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync("core.a1", "2.0.0"));
        var active = await service.ActivateAsync("core.a1", "1.0.0");
        await service.RemoveAsync("core.a1", "2.0.0");

        Assert.Equal("1.0.0", active.Version);
        Assert.Single(await service.GetInstalledAsync());
    }

    private SignedContentPackageService CreateService() => new(
        Path.Combine(_root, "installed"),
        new Version(1, 0, 0),
        _key.ExportSubjectPublicKeyInfoPem());

    private string CreatePackage(
        string packageId,
        string version,
        string payloadPath,
        string payload,
        string minimumAppVersion = "1.0.0")
    {
        Directory.CreateDirectory(_root);
        var packagePath = Path.Combine(_root, $"{packageId}-{version}-{Guid.NewGuid():N}.ltpack");
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var manifest = new ContentPackageManifest(
            "1",
            packageId,
            version,
            ContentPackageKind.Curriculum,
            "A1 starter",
            "de-DE",
            minimumAppVersion,
            "MIT",
            "https://example.test/license",
            DateTimeOffset.Parse("2026-08-20T00:00:00Z"),
            [new(payloadPath, payloadBytes.Length, Convert.ToHexString(SHA256.HashData(payloadBytes)))]);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions);
        var signature = _key.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        WriteEntry(archive, SignedContentPackageService.ManifestEntryName, manifestBytes);
        WriteEntry(archive, SignedContentPackageService.SignatureEntryName, signature);
        WriteEntry(archive, payloadPath, payloadBytes);
        return packagePath;
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
