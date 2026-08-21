using WortBruecke.Core.Models;

namespace WortBruecke.Core.Abstractions;

public interface IContentPackageService
{
    Task<ContentPackageVerificationResult> VerifyAsync(
        string packagePath,
        CancellationToken cancellationToken = default);

    Task<ContentPackageInstallResult> InstallAsync(
        string packagePath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InstalledContentPackage>> GetInstalledAsync(
        CancellationToken cancellationToken = default);

    Task<InstalledContentPackage> ActivateAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken = default);
}
