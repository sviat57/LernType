#Requires -Version 7.0

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $PublishDirectory,

    [Parameter(Mandatory)]
    [string] $OutputPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$')]
    [string] $RootFolder
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-NormalizedDirectoryPath([string] $Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($fullPath)
    if ($fullPath.Length -gt $root.Length) {
        return $fullPath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    }
    return $root
}

function Get-DirectoryPrefix([string] $Path) {
    $normalized = Get-NormalizedDirectoryPath $Path
    if ($normalized.EndsWith([IO.Path]::DirectorySeparatorChar) -or
        $normalized.EndsWith([IO.Path]::AltDirectorySeparatorChar)) {
        return $normalized
    }
    return $normalized + [IO.Path]::DirectorySeparatorChar
}

function Assert-NoReparsePointInExistingPath([string] $Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($fullPath)
    $cursor = $root
    $relative = $fullPath.Substring($root.Length)
    foreach ($segment in $relative.Split(
        [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar),
        [StringSplitOptions]::RemoveEmptyEntries)) {
        $cursor = Join-Path $cursor $segment
        if (-not (Test-Path -LiteralPath $cursor)) {
            throw "Path component does not exist: $cursor"
        }
        $item = Get-Item -LiteralPath $cursor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse points are not accepted in release paths: $cursor"
        }
    }
}

if ($RootFolder.EndsWith('.') -or
    $RootFolder -match '^(?i:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?$') {
    throw "RootFolder is not portable to Windows ZIP consumers: $RootFolder"
}

$publish = Get-NormalizedDirectoryPath (Resolve-Path -LiteralPath $PublishDirectory).Path
$publishPrefix = Get-DirectoryPrefix $publish
$outputParentInput = Split-Path -Parent $OutputPath
if ([string]::IsNullOrWhiteSpace($outputParentInput)) { $outputParentInput = '.' }
$outputParent = Get-NormalizedDirectoryPath (Resolve-Path -LiteralPath $outputParentInput).Path
$output = [IO.Path]::GetFullPath((Join-Path $outputParent (Split-Path -Leaf $OutputPath)))
$sidecar = "$output.sha256"

Assert-NoReparsePointInExistingPath $publish
Assert-NoReparsePointInExistingPath $outputParent

if ($output.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The release archive must be written outside the publish directory.'
}
if (-not [IO.Path]::GetExtension($output).Equals('.zip', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputPath must use the .zip extension.'
}
if (Test-Path -LiteralPath $output -PathType Container) {
    throw "OutputPath identifies a directory: $output"
}
if (Test-Path -LiteralPath $sidecar -PathType Container) {
    throw "The SHA-256 sidecar path identifies a directory: $sidecar"
}
foreach ($existingOutput in @($output, $sidecar)) {
    if (Test-Path -LiteralPath $existingOutput) {
        $item = Get-Item -LiteralPath $existingOutput -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release outputs cannot replace a reparse point: $existingOutput"
        }
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $publish 'LernType.exe') -PathType Leaf)) {
    throw 'PublishDirectory does not contain LernType.exe at its root.'
}

$tree = @(Get-ChildItem -LiteralPath $publish -Recurse -Force)
$reparsePoint = $tree | Where-Object {
    ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
} | Select-Object -First 1
if ($null -ne $reparsePoint) {
    throw "PublishDirectory contains a reparse point: $($reparsePoint.FullName)"
}

$files = @($tree | Where-Object { -not $_.PSIsContainer })
if ($files.Count -eq 0) { throw 'PublishDirectory is empty.' }
$entryNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$manifest = [Collections.Generic.List[object]]::new()
foreach ($file in $files) {
    $fullPath = [IO.Path]::GetFullPath($file.FullName)
    if (-not $fullPath.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish file escaped the expected root: $fullPath"
    }
    $relative = [IO.Path]::GetRelativePath($publish, $fullPath).Replace('\', '/')
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Split('/') -contains '..') {
        throw "Unsafe publish path: $relative"
    }
    $entryName = "$RootFolder/$relative"
    if (-not $entryNames.Add($entryName)) { throw "Duplicate ZIP entry: $entryName" }
    $manifest.Add([pscustomobject]@{
        FullPath = $fullPath
        EntryName = $entryName
        Length = [long]$file.Length
    })
}
$manifest.Sort([Comparison[object]]{
    param($left, $right)
    [StringComparer]::Ordinal.Compare([string]$left.EntryName, [string]$right.EntryName)
})

$uncompressedBytes = [long]($manifest | Measure-Object Length -Sum).Sum
if (-not $PSCmdlet.ShouldProcess($output, "Create deterministic release archive and SHA-256 sidecar from $publish")) {
    [pscustomobject]@{
        Archive = $output
        Sha256 = $null
        Sha256File = $sidecar
        FileCount = $manifest.Count
        UncompressedBytes = $uncompressedBytes
        ArchiveBytes = $null
        Applied = $false
    }
    return
}

$token = [guid]::NewGuid().ToString('N')
$stagedArchive = Join-Path $outputParent ".lerntype-archive-$token.tmp"
$stagedSidecar = Join-Path $outputParent ".lerntype-archive-$token.sha256.tmp"
$archiveBackup = Join-Path $outputParent ".lerntype-archive-$token.zip.bak"
$sidecarBackup = Join-Path $outputParent ".lerntype-archive-$token.sha256.bak"
$archiveInstalled = $false
$sidecarInstalled = $false
$promotionComplete = $false
try {
    $stream = [IO.File]::Open($stagedArchive, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            $timestamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            foreach ($item in $manifest) {
                $entry = $archive.CreateEntry($item.EntryName, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $timestamp
                $input = [IO.File]::Open($item.FullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                try {
                    $entryStream = $entry.Open()
                    try { $input.CopyTo($entryStream) }
                    finally { $entryStream.Dispose() }
                }
                finally { $input.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }

    $verification = [IO.Compression.ZipFile]::OpenRead($stagedArchive)
    try {
        $actualEntries = @($verification.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
        if ($actualEntries.Count -ne $manifest.Count) {
            throw "ZIP verification failed: expected $($manifest.Count) files, found $($actualEntries.Count)."
        }
        for ($index = 0; $index -lt $manifest.Count; $index++) {
            $expected = $manifest[$index]
            $actual = $actualEntries[$index]
            if ($actual.FullName -cne $expected.EntryName -or $actual.Length -ne $expected.Length) {
                throw "ZIP verification failed at entry ${index}: expected $($expected.EntryName), found $($actual.FullName)."
            }
            $entryStream = $actual.Open()
            try { $entryStream.CopyTo([IO.Stream]::Null) }
            finally { $entryStream.Dispose() }
        }
        if (-not ($actualEntries | Where-Object { $_.FullName -ceq "$RootFolder/LernType.exe" })) {
            throw 'ZIP verification failed: LernType.exe is missing.'
        }
    }
    finally { $verification.Dispose() }

    $hash = (Get-FileHash -LiteralPath $stagedArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $output)" | Set-Content -LiteralPath $stagedSidecar -Encoding ascii -NoNewline

    if (Test-Path -LiteralPath $output) {
        [IO.File]::Replace($stagedArchive, $output, $archiveBackup, $true)
    }
    else {
        [IO.File]::Move($stagedArchive, $output)
    }
    $archiveInstalled = $true
    if (Test-Path -LiteralPath $sidecar) {
        [IO.File]::Replace($stagedSidecar, $sidecar, $sidecarBackup, $true)
    }
    else {
        [IO.File]::Move($stagedSidecar, $sidecar)
    }
    $sidecarInstalled = $true

    $installedHash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($installedHash -cne $hash) { throw 'Installed release archive hash changed after atomic promotion.' }
    $expectedSidecar = "$hash  $(Split-Path -Leaf $output)"
    $actualSidecar = (Get-Content -LiteralPath $sidecar -Raw).TrimEnd("`r", "`n")
    if ($actualSidecar -cne $expectedSidecar) { throw 'Installed SHA-256 sidecar verification failed.' }
    $promotionComplete = $true
}
catch {
    $promotionError = $_
    try {
        if ($sidecarInstalled -and (Test-Path -LiteralPath $sidecar)) { Remove-Item -LiteralPath $sidecar -Force }
        if ($archiveInstalled -and (Test-Path -LiteralPath $output)) { Remove-Item -LiteralPath $output -Force }
        if (Test-Path -LiteralPath $archiveBackup) { [IO.File]::Move($archiveBackup, $output) }
        if (Test-Path -LiteralPath $sidecarBackup) { [IO.File]::Move($sidecarBackup, $sidecar) }
    }
    catch {
        throw [InvalidOperationException]::new(
            "Release promotion failed and its previous outputs could not be fully restored. Backups were preserved beside the output. Promotion error: $($promotionError.Exception.Message)",
            $_.Exception)
    }
    throw
}
finally {
    foreach ($temporaryPath in @($stagedArchive, $stagedSidecar)) {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
}

if ($promotionComplete) {
    foreach ($backupPath in @($archiveBackup, $sidecarBackup)) {
        if (Test-Path -LiteralPath $backupPath) {
            try { Remove-Item -LiteralPath $backupPath -Force }
            catch { Write-Warning "The new release is verified, but a superseded backup remains: $backupPath" }
        }
    }
}

[pscustomobject]@{
    Archive = $output
    Sha256 = $hash
    Sha256File = $sidecar
    FileCount = $manifest.Count
    UncompressedBytes = $uncompressedBytes
    ArchiveBytes = (Get-Item -LiteralPath $output).Length
    Applied = $true
}
