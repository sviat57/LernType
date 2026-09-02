# Release engineering

This runbook is the reproducible gate for a LernType release. It does not treat an unsigned MSIX as an end-user installer.

## Prerequisites

- Windows 10/11 with the .NET 10 SDK selected by `global.json`;
- PowerShell 7 (`pwsh`) for packaging and release scripts;
- Windows 10/11 SDK containing x64 `makeappx.exe` and `signtool.exe`;
- a clean Git worktree at the exact reviewed commit;
- for distributable MSIX: a code-signing PFX whose subject exactly matches the package `Publisher`.

Keep PFX files and passwords outside the repository. `.gitignore` excludes common private-key formats, but that is not a substitute for secret scanning.

## 1. Version and source gate

Update the application version in `src/WortBruecke.App/WortBruecke.App.csproj`, the release heading in `CHANGELOG.md`, the top-level `APP_VERSION`/`MSIX_VERSION` values in `.github/workflows/ci.yml` and the explicit `-Version` arguments below. CI rejects drift between these values. MSIX uses four numeric components; SemVer `1.3.0` maps to MSIX `1.3.0.0`.

```powershell
$dotnet = 'dotnet'
& $dotnet --version
& $dotnet tool restore
& $dotnet restore LernType.sln --locked-mode
& $dotnet format LernType.sln --verify-no-changes --no-restore
& $dotnet build LernType.sln -c Debug --no-restore -warnaserror
& $dotnet build LernType.sln -c Release --no-restore -warnaserror
& $dotnet test LernType.sln -c Release --no-build --no-restore `
  --collect:'XPlat Code Coverage' --results-directory TestResults `
  --logger 'trx;LogFileName=tests.trx' --blame-hang-timeout 3m
```

The CI gate enforces at least 45% line and 30% branch coverage. Lowering that baseline requires a reviewed rationale rather than an inline workflow change.

Audit every direct and transitive NuGet package and generate the production dependency SBOM:

```powershell
& $dotnet list LernType.sln package --vulnerable --include-transitive --format json
& $dotnet CycloneDX src/WortBruecke.App/WortBruecke.App.csproj `
  --exclude-dev --disable-package-restore `
  --output artifacts/sbom --filename LernType.cdx.json --output-format Json `
  --set-name LernType --set-version 1.3.0 --set-type Application
```

Review the dependency diff on the pull request and confirm the license/provenance metadata in the CycloneDX file. CI rejects missing component-license metadata, known vulnerabilities at `low` severity or above, and GPL/AGPL/SSPL additions in dependency diffs.

## 2. Publish x64 and Arm64

```powershell
$project = 'src/WortBruecke.App/WortBruecke.App.csproj'
& $dotnet restore $project --locked-mode
foreach ($rid in 'win-x64', 'win-arm64') {
    & $dotnet publish $project -c Release -r $rid --self-contained true `
      --no-restore -o "artifacts/publish/$rid" -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid." }
}
```

Run the x64 smoke matrix on x64 Windows. The script writes a versioned JSON record and screenshot for every case. It accepts a route only when the expected UI Automation landmark is visible, `Загрузка раздела` is gone, neither `Ошибка приложения` nor a visible `Код: ...` is present, and the wide/compact navigation contract matches the actual window width. When `-InvokeAutomationName` is used, `-ExpectedAutomationName` is required.

```powershell
$publish = 'artifacts/publish/win-x64'
./tools/Invoke-ReleaseSmoke.ps1 -PublishDirectory $publish `
  -OutputDirectory artifacts/verification/home-wide `
  -WindowWidth 1180 -WindowHeight 760
./tools/Invoke-ReleaseSmoke.ps1 -PublishDirectory $publish `
  -OutputDirectory artifacts/verification/home-compact `
  -WindowWidth 820 -WindowHeight 600
./tools/Invoke-ReleaseSmoke.ps1 -PublishDirectory $publish `
  -OutputDirectory artifacts/verification/home-minimum `
  -WindowWidth 720 -WindowHeight 520

# Course-first route contract; use the exact names exposed by the reviewed UI.
./tools/Invoke-ReleaseSmoke.ps1 -PublishDirectory $publish `
  -OutputDirectory artifacts/verification/courses-wide `
  -InvokeAutomationName 'Курсы' `
  -ExpectedAutomationName 'Курсы немецкого LernType'
```

Inspect every `smoke-result.json`, not only the process exit: require `expectedLandmarkVisible`, `layoutVerificationPassed`, `uiVerificationPassed` and `gracefulExit` to be `true`, `shellErrorVisible` and `technicalCodeVisible` to be `false`, and `exitCode` to be `0`. Keep one negative contract self-test in release evidence: a missing fixture landmark must make the script exit non-zero with `uiVerificationPassed: false` while still writing its JSON record and screenshot.

Arm64 is cross-published in CI and its PE machine must be `0xAA64`; a stable Arm64 release additionally requires a native launch/route smoke on Arm64 hardware. A Windows 11 x64 smoke is not evidence for Windows 10 or Arm64 runtime behavior. Windows 10 22H2 remains a best-effort ZIP target until a separate run is recorded.

Create deterministic ZIPs and portable checksum records with the hardened archive tool (its
rollback/path-safety suite must pass before release):

```powershell
foreach ($rid in 'win-x64', 'win-arm64') {
    $zip = "artifacts/LernType-1.3.0-$rid.zip"
    ./tools/New-ReleaseArchive.ps1 `
      -PublishDirectory "artifacts/publish/$rid" `
      -OutputPath $zip `
      -RootFolder "LernType-1.3.0-$rid" `
      -Confirm:$false
}
```

## 3. MSIX validation and signing

First validate the package layout. The `-unsigned` filename is deliberate and must not be renamed into a stable installer:

```powershell
./tools/Build-Msix.ps1 `
  -PublishDirectory artifacts/publish/win-x64 `
  -Architecture x64 -Version 1.3.0.0 -AllowUnsigned
```

Build a distributable package only with the release certificate:

```powershell
$password = Read-Host 'PFX password' -AsSecureString
$x64 = ./tools/Build-Msix.ps1 `
  -PublishDirectory artifacts/publish/win-x64 `
  -Architecture x64 -Version 1.3.0.0 `
  -Publisher 'CN=Sviatoslav Kyselov' `
  -CertificatePath C:\secure\LernType-signing.pfx `
  -CertificatePassword $password `
  -TimestampUri 'https://timestamp.example.org'
$arm64 = ./tools/Build-Msix.ps1 `
  -PublishDirectory artifacts/publish/win-arm64 `
  -Architecture arm64 -Version 1.3.0.0 `
  -Publisher 'CN=Sviatoslav Kyselov' `
  -CertificatePath C:\secure\LernType-signing.pfx `
  -CertificatePassword $password `
  -TimestampUri 'https://timestamp.example.org'
```

`Build-Msix.ps1` validates the manifest with MakeAppx, signs and RFC 3161 timestamps with SHA-256, runs `signtool verify /pa /v`, checks the certificate subject and validity interval, and emits a `.sha256` sidecar. Replace the placeholder timestamp URI with the CA-approved service; a stable signature should be timestamped.

Generate App Installer descriptors only after the signed MSIX files are uploaded to their final HTTPS directory:

```powershell
./tools/New-AppInstaller.ps1 `
  -BaseUri 'https://downloads.example.org/lerntype/1.3.0' `
  -Architecture x64 -Version 1.3.0.0
./tools/New-AppInstaller.ps1 `
  -BaseUri 'https://downloads.example.org/lerntype/1.3.0' `
  -Architecture arm64 -Version 1.3.0.0
```

The HTTPS host must serve `.msix` and `.appinstaller` with the correct MIME types and an intact certificate chain.

## 4. Release record

Record, without secrets or personal test data:

- reviewed commit SHA and tag;
- exact gate commands with exit codes;
- test totals and coverage rates;
- vulnerability result and CycloneDX path/hash;
- ZIP/MSIX/App Installer paths, sizes and SHA-256;
- x64 smoke result and separate Arm64 native-smoke evidence;
- certificate subject, issuer, thumbprint and validity interval (never the private key/password);
- rollback command and the verified pre-upgrade data backup path.

`tools/Publish-GitHubRelease.ps1` may publish a verified ZIP only from a clean worktree whose HEAD equals the supplied 40-character commit SHA. Run it separately for x64 and Arm64 to attach both archives to the same tag. It performs network and repository mutations only after PowerShell confirmation; use `-WhatIf` during review.

## 5. Rollback

Run the copy-only rollback self-test against the SQLite runtime shipped in the reviewed x64 payload:

```powershell
./tools/Test-LernTypeDataRollback.ps1 `
  -RuntimeDirectory artifacts/publish/win-x64 `
  -OutputDirectory artifacts/verification/data-rollback `
  -KeepArtifacts
```

Before launching the previous v1.2 binary, restore its schema-v3 profile with the explicit verified
pre-upgrade backup created under `Backups\schema`. The tool stops when LernType is running, preserves
the complete current schema-v4 file set and a consistent SQLite snapshot, verifies the expected
schema/catalog/FK/table inventory, promotes atomically, and writes a hashed JSON record.
`RecoveryRoot` must be on the same volume as the active database.

```powershell
$dataRoot = Join-Path $env:LOCALAPPDATA 'LernType'
$schemaV3Backup = 'C:\ABSOLUTE\VERIFIED\schema-v3-....db'
$currentV13Runtime = 'C:\ABSOLUTE\LernType-1.3.0-win-x64'
$recoveryRoot = Join-Path $dataRoot 'Backups\data-rollback'
New-Item -ItemType Directory -Path $recoveryRoot -Force | Out-Null

./tools/Invoke-LernTypeDataRollback.ps1 `
  -CurrentDatabase (Join-Path $dataRoot 'lerntype.db') `
  -TargetSchemaBackup $schemaV3Backup `
  -RuntimeDirectory $currentV13Runtime `
  -RecoveryRoot $recoveryRoot `
  -ExpectedTargetUserVersion 3 `
  -ExpectedCurrentUserVersion 4 `
  -ExpectedContentRevision 5 `
  -Confirm:$false
```

Keep the returned `recordPath`, verify its `.sha256` sidecar, and only then reinstall the last
verified signed MSIX or extract the prior ZIP into a new directory. The result deliberately matches
the backup's exact foreign-key inventory; a known legacy violation is recorded rather than silently
discarded. Never replace only the `.db` file while detached `-wal`/`-shm` files are active.
