namespace WortBruecke.Core.Models;

public enum ContentPackageKind
{
    Curriculum,
    Exam,
    Media,
    Dictionary
}

public sealed record ContentPackageFile(
    string Path,
    long Size,
    string Sha256);

public sealed record ContentPackageManifest(
    string SchemaVersion,
    string PackageId,
    string Version,
    ContentPackageKind Kind,
    string Title,
    string Culture,
    string MinimumAppVersion,
    string LicenseId,
    string LicenseUrl,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<ContentPackageFile> Files);

public sealed record ContentPackageVerificationResult(
    bool IsValid,
    ContentPackageManifest? Manifest,
    IReadOnlyList<string> Errors);

public sealed record InstalledContentPackage(
    string PackageId,
    string Version,
    string InstallPath,
    bool IsActive);

public sealed record ContentPackageInstallResult(
    InstalledContentPackage Package,
    bool WasAlreadyInstalled);
