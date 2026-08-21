#Requires -Version 7.0

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)]
    [uri]$BaseUri,

    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    [string]$Version = '1.0.0.0',

    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9.-]{1,48}[A-Za-z0-9])$')]
    [string]$Identity = 'sviat57.LernType',

    [ValidateNotNullOrEmpty()]
    [string]$Publisher = 'CN=Sviatoslav Kyselov',

    [string]$OutputPath = (Join-Path $PSScriptRoot "..\artifacts\msix\LernType-$Architecture.appinstaller")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $BaseUri.IsAbsoluteUri -or $BaseUri.Scheme -ne 'https') {
    throw 'BaseUri must be an absolute HTTPS URI.'
}
if (-not [string]::IsNullOrEmpty($BaseUri.UserInfo) -or
    -not [string]::IsNullOrEmpty($BaseUri.Query) -or
    -not [string]::IsNullOrEmpty($BaseUri.Fragment)) {
    throw 'BaseUri must not contain credentials, a query string or a fragment.'
}
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw 'Version must contain four numeric components.'
}
foreach ($component in $Version.Split('.')) {
    $number = [uint32]::Parse($component, [Globalization.CultureInfo]::InvariantCulture)
    if ($number -gt 65535) {
        throw 'Every version component must be between 0 and 65535.'
    }
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$templatePath = Join-Path $root 'packaging\LernType.appinstaller.template'
if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) {
    throw "App Installer template is missing: $templatePath"
}

$fileName = "LernType-$Version-win-$Architecture.msix"
$base = $BaseUri.AbsoluteUri.TrimEnd('/')
$packageUri = "$base/$fileName"
$appInstallerUri = "$base/LernType-$Architecture.appinstaller"
$content = (Get-Content -LiteralPath $templatePath -Raw).
    Replace('__IDENTITY__', [Security.SecurityElement]::Escape($Identity)).
    Replace('__PUBLISHER__', [Security.SecurityElement]::Escape($Publisher)).
    Replace('__VERSION__', $Version).
    Replace('__ARCH__', $Architecture).
    Replace('__PACKAGE_URI__', [Security.SecurityElement]::Escape($packageUri)).
    Replace('__APPINSTALLER_URI__', [Security.SecurityElement]::Escape($appInstallerUri))
if ($content -match '__[A-Z_]+__') {
    throw 'The generated App Installer file contains an unresolved template token.'
}

[xml]$xml = $content
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
if (-not $PSCmdlet.ShouldProcess($resolvedOutput, 'write signed-package App Installer feed descriptor')) {
    return [pscustomobject]@{
        Path = $resolvedOutput
        PackageUri = $packageUri
        AppInstallerUri = $appInstallerUri
        Architecture = $Architecture
        Version = $Version
        Executed = $false
    }
}

$directory = Split-Path -Parent $resolvedOutput
New-Item -ItemType Directory -Force -Path $directory | Out-Null
[IO.File]::WriteAllText($resolvedOutput, $xml.OuterXml, [Text.UTF8Encoding]::new($false))
[xml](Get-Content -LiteralPath $resolvedOutput -Raw) | Out-Null
[pscustomobject]@{
    Path = $resolvedOutput
    PackageUri = $packageUri
    AppInstallerUri = $appInstallerUri
    Architecture = $Architecture
    Version = $Version
    Executed = $true
}
