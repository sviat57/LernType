#Requires -Version 7.0

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$PublishDirectory,

    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    [string]$Version = '1.2.0.0',

    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9.-]{1,48}[A-Za-z0-9])$')]
    [string]$Identity = 'sviat57.LernType',

    [ValidateNotNullOrEmpty()]
    [string]$Publisher = 'CN=Sviatoslav Kyselov',

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\msix'),

    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CertificatePath,

    [securestring]$CertificatePassword,

    [uri]$TimestampUri,

    [switch]$AllowUnsigned
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Assert-PackageVersion {
    param([Parameter(Mandatory)][string]$Value)

    if ($Value -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw 'MSIX version must contain four numeric components.'
    }

    foreach ($component in $Value.Split('.')) {
        $number = [uint32]::Parse($component, [Globalization.CultureInfo]::InvariantCulture)
        if ($number -gt 65535) {
            throw 'Every MSIX version component must be between 0 and 65535.'
        }
    }
}

function Find-WindowsSdkTool {
    param([Parameter(Mandatory)][string]$Name)

    $windowsKits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path -LiteralPath $windowsKits -PathType Container)) {
        return $null
    }

    Get-ChildItem -LiteralPath $windowsKits -Filter $Name -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object FullName -Match "\\x64\\$([regex]::Escape($Name))$" |
        Sort-Object { $_.VersionInfo.FileVersionRaw } -Descending |
        Select-Object -First 1
}

function Get-PeArchitecture {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "Executable is not a valid PE image: $Path"
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset -gt $stream.Length - 6) {
            throw "Executable has an invalid PE header offset: $Path"
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "Executable has an invalid PE signature: $Path"
        }
        switch ($reader.ReadUInt16()) {
            0x8664 { return 'x64' }
            0xAA64 { return 'arm64' }
            0x014C { return 'x86' }
            default { return 'unknown' }
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Invoke-NativeTool {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Operation
    )

    $output = @(& $FilePath @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        foreach ($line in $output) {
            Write-Host $line
        }
        throw "$Operation failed with exit code $exitCode."
    }

    if ($VerbosePreference -eq 'Continue') {
        foreach ($line in $output) {
            Write-Verbose ([string]$line)
        }
    }
    Write-Host "$Operation succeeded."
}

Assert-PackageVersion -Value $Version
if ($TimestampUri -and
    (-not $TimestampUri.IsAbsoluteUri -or $TimestampUri.Scheme -notin @('http', 'https') -or
     -not [string]::IsNullOrEmpty($TimestampUri.UserInfo))) {
    throw 'TimestampUri must be an absolute HTTP(S) URI without embedded credentials.'
}
if ($TimestampUri -and -not $CertificatePath) {
    throw 'TimestampUri is valid only when CertificatePath is supplied.'
}
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$executable = Join-Path $publish 'LernType.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'PublishDirectory does not contain LernType.exe.'
}
$actualArchitecture = Get-PeArchitecture -Path $executable
if ($actualArchitecture -ne $Architecture) {
    throw "LernType.exe architecture '$actualArchitecture' does not match requested MSIX architecture '$Architecture'."
}
$nestedExecutables = @(Get-ChildItem -LiteralPath $publish -Filter 'LernType.exe' -Recurse -File |
    Where-Object { $_.FullName -cne $executable })
if ($nestedExecutables.Count -gt 0) {
    throw 'PublishDirectory contains nested application outputs. Point to one isolated RID-specific dotnet publish directory.'
}

$requiredAssets = @(
    'Square44x44Logo.png',
    'Square150x150Logo.png',
    'Square310x310Logo.png',
    'Wide310x150Logo.png',
    'StoreLogo.png'
)
$brandRoot = Join-Path $repositoryRoot 'src\WortBruecke.App\Assets\Brand'
foreach ($asset in $requiredAssets) {
    if (-not (Test-Path -LiteralPath (Join-Path $brandRoot $asset) -PathType Leaf)) {
        throw "Required package asset is missing: $asset"
    }
}

$manifestTemplate = Join-Path $repositoryRoot 'packaging\AppxManifest.template.xml'
if (-not (Test-Path -LiteralPath $manifestTemplate -PathType Leaf)) {
    throw "MSIX manifest template is missing: $manifestTemplate"
}

$makeAppx = Find-WindowsSdkTool -Name 'makeappx.exe'
if (-not $makeAppx) {
    throw 'Windows SDK makeappx.exe was not found.'
}

$signTool = $null
$resolvedCertificate = $null
if ($CertificatePath) {
    $resolvedCertificate = (Resolve-Path -LiteralPath $CertificatePath).Path
    $signTool = Find-WindowsSdkTool -Name 'signtool.exe'
    if (-not $signTool) {
        throw 'Windows SDK signtool.exe was not found.'
    }
}
elseif (-not $AllowUnsigned) {
    throw 'A signing certificate is required. Use -AllowUnsigned only for local package validation.'
}

$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$suffix = if ($resolvedCertificate) { '' } else { '-unsigned' }
$fileName = "LernType-$Version-win-$Architecture$suffix.msix"
$output = Join-Path $outputRoot $fileName
$action = if ($resolvedCertificate) { 'build and verify signed MSIX package' } else { 'build unsigned MSIX validation package' }
if (-not $PSCmdlet.ShouldProcess($output, $action)) {
    return [pscustomobject]@{
        Package = $output
        ChecksumFile = "$output.sha256"
        Sha256 = $null
        Signed = [bool]$resolvedCertificate
        Timestamped = [bool]$TimestampUri
        Architecture = $Architecture
        Version = $Version
        Executed = $false
    }
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$stage = Join-Path $tempRoot "LernType-msix-$([guid]::NewGuid().ToString('N'))"
try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    Copy-Item -Path (Join-Path $publish '*') -Destination $stage -Recurse -Force

    $assetTarget = Join-Path $stage 'Assets'
    New-Item -ItemType Directory -Force -Path $assetTarget | Out-Null
    foreach ($asset in $requiredAssets) {
        Copy-Item -LiteralPath (Join-Path $brandRoot $asset) -Destination $assetTarget -Force
    }

    $manifest = Get-Content -LiteralPath $manifestTemplate -Raw
    $manifest = $manifest.Replace('__IDENTITY__', [Security.SecurityElement]::Escape($Identity)).
        Replace('__PUBLISHER__', [Security.SecurityElement]::Escape($Publisher)).
        Replace('__VERSION__', $Version).
        Replace('__ARCH__', $Architecture)
    if ($manifest -match '__[A-Z_]+__') {
        throw 'The generated MSIX manifest contains an unresolved template token.'
    }

    $manifestPath = Join-Path $stage 'AppxManifest.xml'
    [xml]$manifestXml = $manifest
    [IO.File]::WriteAllText($manifestPath, $manifestXml.OuterXml, [Text.UTF8Encoding]::new($false))

    Invoke-NativeTool -FilePath $makeAppx.FullName -Arguments @('pack', '/d', $stage, '/p', $output, '/o') -Operation 'makeappx pack'

    if ($resolvedCertificate) {
        $passwordPointer = [IntPtr]::Zero
        $plainPassword = ''
        $certificate = $null
        try {
            if ($CertificatePassword) {
                $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($CertificatePassword)
                $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
            }

            $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $resolvedCertificate,
                $plainPassword,
                [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
            if (-not $certificate.HasPrivateKey) {
                throw 'The signing certificate does not contain a private key.'
            }
            if ($certificate.Subject -cne $Publisher) {
                throw "Certificate subject '$($certificate.Subject)' does not exactly match manifest Publisher '$Publisher'."
            }
            if ([DateTimeOffset]::UtcNow -lt $certificate.NotBefore.ToUniversalTime() -or
                [DateTimeOffset]::UtcNow -gt $certificate.NotAfter.ToUniversalTime()) {
                throw 'The signing certificate is not currently valid.'
            }

            $signArguments = @('sign', '/fd', 'SHA256', '/f', $resolvedCertificate, '/p', $plainPassword)
            if ($TimestampUri) {
                $signArguments += @('/tr', $TimestampUri.AbsoluteUri, '/td', 'SHA256')
            }
            $signArguments += $output
            Invoke-NativeTool -FilePath $signTool.FullName -Arguments $signArguments -Operation 'signtool sign'
            Invoke-NativeTool -FilePath $signTool.FullName -Arguments @('verify', '/pa', '/v', $output) -Operation 'signtool verify'
        }
        finally {
            if ($certificate) {
                $certificate.Dispose()
            }
            $plainPassword = $null
            if ($passwordPointer -ne [IntPtr]::Zero) {
                [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
            }
        }
    }

    if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
        throw 'MSIX output was not produced.'
    }

    $hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumPath = "$output.sha256"
    "$hash  $fileName" | Set-Content -LiteralPath $checksumPath -Encoding ascii
    [pscustomobject]@{
        Package = $output
        ChecksumFile = $checksumPath
        Sha256 = $hash
        Signed = [bool]$resolvedCertificate
        Timestamped = [bool]$TimestampUri
        Architecture = $Architecture
        Version = $Version
        Executed = $true
    }
}
catch {
    if (Test-Path -LiteralPath $output -PathType Leaf) {
        Remove-Item -LiteralPath $output -Force
    }
    if (Test-Path -LiteralPath "$output.sha256" -PathType Leaf) {
        Remove-Item -LiteralPath "$output.sha256" -Force
    }
    throw
}
finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $expectedPrefix = "$tempRoot$([IO.Path]::DirectorySeparatorChar)LernType-msix-"
    if ((Test-Path -LiteralPath $resolvedStage) -and
        $resolvedStage.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
