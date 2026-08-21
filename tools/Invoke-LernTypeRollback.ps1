#Requires -Version 7.0

[CmdletBinding(DefaultParameterSetName = 'Release', SupportsShouldProcess)]
param(
    [Parameter(Mandatory, ParameterSetName = 'Release')]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $PreviousArchive,

    [Parameter(Mandatory, ParameterSetName = 'Source')]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $RepositoryRoot,

    [Parameter(ParameterSetName = 'Source')]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $BaselineCommit = '9403b96c12de9b8c123d7160452722c4e66f283e',

    [Parameter(ParameterSetName = 'Release')]
    [ValidateRange(1, 100000)]
    [int] $MaximumEntryCount = 10000,

    [Parameter(ParameterSetName = 'Release')]
    [ValidateRange(1, [long]::MaxValue)]
    [long] $MaximumUncompressedBytes = 4GB,

    [Parameter(Mandatory)]
    [string] $Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

function Test-IsSameOrDescendant([string] $Candidate, [string] $Root) {
    $candidatePath = Get-NormalizedDirectoryPath $Candidate
    $rootPath = Get-NormalizedDirectoryPath $Root
    return $candidatePath.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
        $candidatePath.StartsWith((Get-DirectoryPrefix $rootPath), [StringComparison]::OrdinalIgnoreCase)
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
            throw "Reparse points are not accepted in rollback paths: $cursor"
        }
    }
}

function Resolve-FuturePath([string] $Path) {
    $parent = Split-Path -Parent $Path
    if ([string]::IsNullOrWhiteSpace($parent)) { $parent = '.' }
    $resolvedParent = Get-NormalizedDirectoryPath (Resolve-Path -LiteralPath $parent).Path
    Assert-NoReparsePointInExistingPath $resolvedParent
    return [IO.Path]::GetFullPath((Join-Path $resolvedParent (Split-Path -Leaf $Path)))
}

function Assert-EmptyDestination([string] $Path) {
    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Rollback destination cannot be a reparse point: $Path"
        }
        if (-not $item.PSIsContainer -or (Get-ChildItem -LiteralPath $Path -Force | Select-Object -First 1)) {
            throw "Rollback destination must be absent or empty: $Path"
        }
    }
}

function ConvertTo-SafeArchivePath([string] $EntryName) {
    $normalized = $EntryName.Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($normalized) -or
        $normalized.StartsWith('/') -or
        [IO.Path]::IsPathRooted($normalized)) {
        throw "Unsafe archive entry: $EntryName"
    }
    $isDirectory = $normalized.EndsWith('/')
    $canonical = $normalized.TrimEnd('/')
    if ([string]::IsNullOrWhiteSpace($canonical)) { throw "Unsafe archive entry: $EntryName" }
    $segments = $canonical.Split('/')
    foreach ($segment in $segments) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -in @('.', '..') -or
            $segment -match '[<>:"|?*\x00-\x1F]' -or $segment.EndsWith('.') -or $segment.EndsWith(' ') -or
            $segment -match '^(?i:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?$') {
            throw "Unsafe archive entry: $EntryName"
        }
    }
    [pscustomobject]@{
        Canonical = $segments -join '/'
        Segments = $segments
        IsDirectory = $isDirectory
    }
}

$destinationPath = Resolve-FuturePath $Destination
if ($destinationPath.Equals([IO.Path]::GetPathRoot($destinationPath), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'A filesystem root cannot be used as a rollback destination.'
}
Assert-EmptyDestination $destinationPath

if ($PSCmdlet.ParameterSetName -eq 'Source') {
    $repositoryPath = Get-NormalizedDirectoryPath (Resolve-Path -LiteralPath $RepositoryRoot).Path
    Assert-NoReparsePointInExistingPath $repositoryPath
    if (Test-IsSameOrDescendant $destinationPath $repositoryPath) {
        throw 'A rollback worktree must be created outside the current repository worktree.'
    }

    & git -C $repositoryPath cat-file -e "$BaselineCommit`^{commit}"
    if ($LASTEXITCODE -ne 0) { throw "Baseline commit was not found: $BaselineCommit" }

    $applied = $PSCmdlet.ShouldProcess($destinationPath, "Create a detached rollback worktree at $BaselineCommit")
    if ($applied) {
        $destinationExisted = Test-Path -LiteralPath $destinationPath
        try {
            & git -C $repositoryPath worktree add --detach $destinationPath $BaselineCommit
            if ($LASTEXITCODE -ne 0) { throw 'git worktree add failed.' }
            $actual = (& git -C $destinationPath rev-parse HEAD).Trim()
            if ($LASTEXITCODE -ne 0 -or -not $actual.Equals($BaselineCommit, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Rollback worktree verification failed. Expected $BaselineCommit, received $actual."
            }
        }
        catch {
            & git -C $repositoryPath worktree remove --force $destinationPath 2>$null
            if (-not $destinationExisted -and (Test-Path -LiteralPath $destinationPath) -and
                (Test-IsSameOrDescendant $destinationPath (Split-Path -Parent $destinationPath))) {
                Remove-Item -LiteralPath $destinationPath -Recurse -Force
            }
            throw
        }
    }

    [pscustomobject]@{
        Mode = 'Source'
        Destination = $destinationPath
        BaselineCommit = $BaselineCommit.ToLowerInvariant()
        PreservedCurrentWorktree = $true
        Applied = [bool]$applied
    }
    return
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archivePath = (Resolve-Path -LiteralPath $PreviousArchive).Path
$archiveItem = Get-Item -LiteralPath $archivePath -Force
if (($archiveItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'PreviousArchive cannot be a reparse point.'
}
$archiveStream = [IO.File]::Open($archivePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { $archiveHash = [Convert]::ToHexString($sha256.ComputeHash($archiveStream)).ToLowerInvariant() }
    finally { $sha256.Dispose() }
    $archiveStream.Position = 0
    $archive = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Read, $false)
    try {
        if ($archive.Entries.Count -gt $MaximumEntryCount) {
            throw "The rollback archive exceeds the $MaximumEntryCount-entry safety limit."
        }

        $paths = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        $manifest = [Collections.Generic.List[object]]::new()
        $totalBytes = [long]0
        foreach ($entry in $archive.Entries) {
            $safePath = ConvertTo-SafeArchivePath $entry.FullName
            if ($paths.ContainsKey($safePath.Canonical)) {
                throw "Duplicate or file/directory-colliding archive entry: $($entry.FullName)"
            }
            $paths.Add($safePath.Canonical, $safePath)
            if ($safePath.IsDirectory -and $entry.Length -ne 0) {
                throw "Directory archive entry contains file data: $($entry.FullName)"
            }
            if (-not $safePath.IsDirectory) {
                if ($entry.Length -gt ($MaximumUncompressedBytes - $totalBytes)) {
                    throw "The rollback archive exceeds the $MaximumUncompressedBytes-byte extraction safety limit."
                }
                $totalBytes += $entry.Length
            }
            $manifest.Add([pscustomobject]@{
                Entry = $entry
                Canonical = $safePath.Canonical
                Segments = $safePath.Segments
                IsDirectory = $safePath.IsDirectory
                Length = [long]$entry.Length
            })
        }
        if ($manifest.Count -eq 0) { throw 'The rollback archive is empty.' }

        foreach ($item in $manifest) {
            for ($index = 1; $index -lt $item.Segments.Count; $index++) {
                $parentPath = ($item.Segments[0..($index - 1)] -join '/')
                if ($paths.ContainsKey($parentPath) -and -not $paths[$parentPath].IsDirectory) {
                    throw "Archive entry has a file as its parent path: $($item.Canonical)"
                }
            }
        }

        $executables = @($manifest | Where-Object {
            -not $_.IsDirectory -and $_.Segments[-1] -ieq 'LernType.exe'
        })
        if ($executables.Count -ne 1 -or $executables[0].Segments.Count -gt 2) {
            throw 'The rollback archive must contain exactly one LernType.exe at its root or one top-level payload folder.'
        }
        $payloadPrefix = if ($executables[0].Segments.Count -eq 2) { $executables[0].Segments[0] } else { '' }
        if ($payloadPrefix) {
            $payloadPathPrefix = "$payloadPrefix/"
            $outsidePayload = $manifest | Where-Object {
                $_.Canonical -ine $payloadPrefix -and
                -not $_.Canonical.StartsWith($payloadPathPrefix, [StringComparison]::OrdinalIgnoreCase)
            } | Select-Object -First 1
            if ($null -ne $outsidePayload) {
                throw "Archive entry is outside the single payload folder: $($outsidePayload.Canonical)"
            }
        }
        $recordPath = if ($payloadPrefix) { "$payloadPrefix/rollback-record.json" } else { 'rollback-record.json' }
        if ($paths.ContainsKey($recordPath)) {
            throw "The rollback archive contains the reserved metadata path: $recordPath"
        }

        $applied = $PSCmdlet.ShouldProcess($destinationPath, "Extract and verify rollback release $archiveHash")
        if ($applied) {
            $parent = Get-NormalizedDirectoryPath (Split-Path -Parent $destinationPath)
            $stage = Join-Path $parent ('.lerntype-rollback-' + [guid]::NewGuid().ToString('N'))
            if (-not (Test-IsSameOrDescendant $stage $parent) -or
                (Get-NormalizedDirectoryPath $stage).Equals($parent, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Rollback staging path failed its parent-boundary check.'
            }
            try {
                [void][IO.Directory]::CreateDirectory($stage)
                $stagePrefix = Get-DirectoryPrefix $stage
                foreach ($item in $manifest) {
                    $relativeWindowsPath = $item.Canonical.Replace('/', [IO.Path]::DirectorySeparatorChar)
                    $target = [IO.Path]::GetFullPath((Join-Path $stage $relativeWindowsPath))
                    if (-not $target.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                        throw "Rollback extraction escaped its staging directory: $($item.Canonical)"
                    }
                    if ($item.IsDirectory) {
                        [void][IO.Directory]::CreateDirectory($target)
                        continue
                    }
                    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $target))
                    $entryStream = $item.Entry.Open()
                    try {
                        $outputStream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                        try { $entryStream.CopyTo($outputStream) }
                        finally { $outputStream.Dispose() }
                    }
                    finally { $entryStream.Dispose() }
                    if ((Get-Item -LiteralPath $target).Length -ne $item.Length) {
                        throw "Rollback extraction length verification failed: $($item.Canonical)"
                    }
                }

                $payloadRoot = if ($payloadPrefix) { Join-Path $stage $payloadPrefix } else { $stage }
                $payloadRoot = Get-NormalizedDirectoryPath $payloadRoot
                if (-not (Test-IsSameOrDescendant $payloadRoot $stage)) {
                    throw 'Rollback payload root failed its staging-directory boundary check.'
                }
                $executablePath = Join-Path $payloadRoot 'LernType.exe'
                if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
                    throw 'Rollback extraction verification failed: LernType.exe is missing.'
                }
                $record = [ordered]@{
                    restoredUtc = [DateTimeOffset]::UtcNow.ToString('O')
                    sourceArchive = $archivePath
                    sourceSha256 = $archiveHash
                    executable = (Join-Path $destinationPath 'LernType.exe')
                    preservedCurrentInstallation = $true
                }
                $record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payloadRoot 'rollback-record.json') -Encoding utf8NoBOM

                if (Test-Path -LiteralPath $destinationPath) { Remove-Item -LiteralPath $destinationPath }
                if (-not $payloadPrefix) {
                    Move-Item -LiteralPath $stage -Destination $destinationPath
                    $stage = $null
                }
                else {
                    Move-Item -LiteralPath $payloadRoot -Destination $destinationPath
                }
            }
            finally {
                if ($stage -and (Test-Path -LiteralPath $stage)) {
                    $parent = Get-NormalizedDirectoryPath (Split-Path -Parent $stage)
                    $stageLeaf = Split-Path -Leaf $stage
                    if (-not (Test-IsSameOrDescendant $stage $parent) -or
                        (Get-NormalizedDirectoryPath $stage).Equals($parent, [StringComparison]::OrdinalIgnoreCase) -or
                        -not $stageLeaf.StartsWith('.lerntype-rollback-', [StringComparison]::Ordinal)) {
                        throw 'Rollback staging cleanup failed its parent-boundary check.'
                    }
                    Remove-Item -LiteralPath $stage -Recurse -Force
                }
            }
        }
    }
    finally { $archive.Dispose() }
}
finally { $archiveStream.Dispose() }

[pscustomobject]@{
    Mode = 'Release'
    Destination = $destinationPath
    SourceArchive = $archivePath
    SourceSha256 = $archiveHash
    PreservedCurrentInstallation = $true
    Applied = [bool]$applied
}
