#Requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

$archiveScript = Join-Path $PSScriptRoot 'New-ReleaseArchive.ps1'
$rollbackScript = Join-Path $PSScriptRoot 'Invoke-LernTypeRollback.ps1'
$results = [Collections.Generic.List[object]]::new()

function Assert-True([bool] $Condition, [string] $Name) {
    if (-not $Condition) { throw "Assertion failed: $Name" }
    $results.Add([pscustomobject]@{ Test = $Name; Result = 'PASS' })
}

function Assert-Throws([scriptblock] $Action, [string] $MessagePattern, [string] $Name) {
    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw "Assertion failed: $Name emitted '$($_.Exception.Message)'"
        }
        $results.Add([pscustomobject]@{ Test = $Name; Result = 'PASS' })
        return
    }
    throw "Assertion failed: $Name did not throw"
}

function New-TestZip([string] $Path, [object[]] $Entries) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($item in $Entries) {
                $entry = $archive.CreateEntry([string]$item.Name)
                if (-not ([string]$item.Name).EndsWith('/')) {
                    $writer = [IO.StreamWriter]::new($entry.Open())
                    try { $writer.Write([string]$item.Content) }
                    finally { $writer.Dispose() }
                }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('lerntype-release-script-tests-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
try {
    $publish = Join-Path $testRoot 'publish'
    $output = Join-Path $testRoot 'output'
    [void][IO.Directory]::CreateDirectory((Join-Path $publish 'data'))
    [void][IO.Directory]::CreateDirectory($output)
    [IO.File]::WriteAllText((Join-Path $publish 'LernType.exe'), 'exe')
    [IO.File]::WriteAllText((Join-Path $publish 'data\lesson.json'), '{"lesson":1}')
    $hiddenPath = Join-Path $publish 'data\hidden.txt'
    [IO.File]::WriteAllText($hiddenPath, 'hidden')
    [IO.File]::SetAttributes($hiddenPath, [IO.File]::GetAttributes($hiddenPath) -bor [IO.FileAttributes]::Hidden)

    $firstPath = Join-Path $output 'first.zip'
    $secondPath = Join-Path $output 'second.zip'
    $first = & $archiveScript -PublishDirectory $publish -OutputPath $firstPath -RootFolder 'LernType-1.0.0'
    $second = & $archiveScript -PublishDirectory $publish -OutputPath $secondPath -RootFolder 'LernType-1.0.0'
    Assert-True ($first.Applied -and $second.Applied) 'archive creation reports Applied'
    Assert-True ($first.Sha256 -ceq $second.Sha256) 'archive bytes are deterministic across output names'
    Assert-True ((Get-Content -LiteralPath $first.Sha256File -Raw) -ceq "$($first.Sha256)  first.zip") 'SHA-256 sidecar is exact'
    $replacement = & $archiveScript -PublishDirectory $publish -OutputPath $firstPath -RootFolder 'LernType-1.0.0'
    Assert-True ($replacement.Sha256 -ceq $first.Sha256) 'existing archive and sidecar are transactionally replaced'
    Assert-True (@(Get-ChildItem -LiteralPath $output -Force -Filter '.lerntype-archive-*').Count -eq 0) 'successful replacement leaves no staging or backup files'

    $failedPromotionPath = Join-Path $output 'failed-promotion.zip'
    $failedPromotionSidecar = "$failedPromotionPath.sha256"
    [IO.File]::WriteAllText($failedPromotionPath, 'previous archive')
    [IO.File]::WriteAllText($failedPromotionSidecar, 'previous sidecar')
    $previousArchiveHash = (Get-FileHash -LiteralPath $failedPromotionPath -Algorithm SHA256).Hash
    $previousSidecarHash = (Get-FileHash -LiteralPath $failedPromotionSidecar -Algorithm SHA256).Hash
    $sidecarLock = [IO.File]::Open($failedPromotionSidecar, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        Assert-Throws {
            & $archiveScript -PublishDirectory $publish -OutputPath $failedPromotionPath -RootFolder 'LernType-1.0.0'
        } '.+' 'failed two-file promotion reports an error'
    }
    finally { $sidecarLock.Dispose() }
    Assert-True ((Get-FileHash -LiteralPath $failedPromotionPath -Algorithm SHA256).Hash -ceq $previousArchiveHash) 'failed promotion restores the previous archive'
    Assert-True ((Get-FileHash -LiteralPath $failedPromotionSidecar -Algorithm SHA256).Hash -ceq $previousSidecarHash) 'failed promotion preserves the previous sidecar'
    Assert-True (@(Get-ChildItem -LiteralPath $output -Force -Filter '.lerntype-archive-*').Count -eq 0) 'failed promotion leaves no staging or backup files after verified restoration'

    $zip = [IO.Compression.ZipFile]::OpenRead($firstPath)
    try {
        $names = @($zip.Entries.FullName)
        Assert-True ($names -ccontains 'LernType-1.0.0/data/hidden.txt') 'hidden publish files are archived'
        $actualTimestamp = ($zip.Entries | Where-Object { $_.FullName -eq 'LernType-1.0.0/LernType.exe' }).LastWriteTime
        Assert-True ($actualTimestamp.DateTime -eq [datetime]::new(2000, 1, 1, 0, 0, 0, [DateTimeKind]::Unspecified)) 'ZIP timestamp fields are fixed'
    }
    finally { $zip.Dispose() }

    $whatIfArchive = Join-Path $output 'what-if.zip'
    $whatIfResult = & $archiveScript -PublishDirectory $publish -OutputPath $whatIfArchive -RootFolder 'LernType-1.0.0' -WhatIf 6>$null
    Assert-True (-not $whatIfResult.Applied -and -not (Test-Path -LiteralPath $whatIfArchive)) 'archive WhatIf writes nothing'
    Assert-Throws {
        & $archiveScript -PublishDirectory $publish -OutputPath (Join-Path $publish 'inside.zip') -RootFolder 'LernType-1.0.0'
    } 'outside the publish directory' 'archive output cannot be inside publish tree'

    $junctionTarget = Join-Path $testRoot 'junction-target'
    [void][IO.Directory]::CreateDirectory($junctionTarget)
    [IO.File]::WriteAllText((Join-Path $junctionTarget 'outside.txt'), 'outside')
    $junction = Join-Path $publish 'linked'
    $junctionCreated = $false
    try {
        $null = New-Item -ItemType Junction -Path $junction -Target $junctionTarget
        $junctionCreated = $true
        Assert-Throws {
            & $archiveScript -PublishDirectory $publish -OutputPath (Join-Path $output 'junction.zip') -RootFolder 'LernType-1.0.0'
        } 'reparse point' 'publish reparse point is rejected'
    }
    finally {
        if ($junctionCreated -and (Test-Path -LiteralPath $junction)) { Remove-Item -LiteralPath $junction -Force }
    }

    $outputJunction = Join-Path $testRoot 'output-link'
    $outputJunctionCreated = $false
    try {
        $null = New-Item -ItemType Junction -Path $outputJunction -Target $output
        $outputJunctionCreated = $true
        Assert-Throws {
            & $archiveScript -PublishDirectory $publish -OutputPath (Join-Path $outputJunction 'through-junction.zip') -RootFolder 'LernType-1.0.0'
        } 'reparse points' 'archive output parent reparse point is rejected'
        Assert-Throws {
            & $rollbackScript -PreviousArchive $firstPath -Destination (Join-Path $outputJunction 'rollback-through-junction')
        } 'reparse points' 'rollback destination parent reparse point is rejected'
    }
    finally {
        if ($outputJunctionCreated -and (Test-Path -LiteralPath $outputJunction)) { Remove-Item -LiteralPath $outputJunction -Force }
    }

    $oneFolderDestination = Join-Path $testRoot 'restore-one-folder'
    $oneFolder = & $rollbackScript -PreviousArchive $firstPath -Destination $oneFolderDestination
    Assert-True ($oneFolder.Applied -and (Test-Path -LiteralPath (Join-Path $oneFolderDestination 'LernType.exe'))) 'one-folder payload restores without wrapper'
    Assert-True (Test-Path -LiteralPath (Join-Path $oneFolderDestination 'data\lesson.json')) 'one-folder nested payload is preserved'
    Assert-True (Test-Path -LiteralPath (Join-Path $oneFolderDestination 'rollback-record.json')) 'one-folder rollback record is written'

    $rootZip = Join-Path $output 'root.zip'
    New-TestZip $rootZip @(
        @{ Name = 'LernType.exe'; Content = 'exe' },
        @{ Name = 'data/'; Content = '' },
        @{ Name = 'data/root.txt'; Content = 'root' }
    )
    $rootDestination = Join-Path $testRoot 'restore-root'
    $rootRestore = & $rollbackScript -PreviousArchive $rootZip -Destination $rootDestination
    Assert-True ($rootRestore.Applied -and (Test-Path -LiteralPath (Join-Path $rootDestination 'LernType.exe'))) 'root-level payload restores'
    Assert-True (Test-Path -LiteralPath (Join-Path $rootDestination 'data\root.txt')) 'root-level nested payload is preserved'

    $rollbackWhatIfDestination = Join-Path $testRoot 'rollback-what-if'
    $rollbackWhatIf = & $rollbackScript -PreviousArchive $rootZip -Destination $rollbackWhatIfDestination -WhatIf 6>$null
    Assert-True (-not $rollbackWhatIf.Applied -and -not (Test-Path -LiteralPath $rollbackWhatIfDestination)) 'rollback WhatIf writes nothing'

    $mixedZip = Join-Path $output 'mixed.zip'
    New-TestZip $mixedZip @(
        @{ Name = 'payload/LernType.exe'; Content = 'exe' },
        @{ Name = 'outside.txt'; Content = 'outside' }
    )
    Assert-Throws {
        & $rollbackScript -PreviousArchive $mixedZip -Destination (Join-Path $testRoot 'mixed-destination')
    } 'outside the single payload folder' 'mixed one-folder payload is rejected'

    $traversalZip = Join-Path $output 'traversal.zip'
    New-TestZip $traversalZip @(
        @{ Name = 'LernType.exe'; Content = 'exe' },
        @{ Name = '../escape.txt'; Content = 'escape' }
    )
    $escapePath = Join-Path $testRoot 'escape.txt'
    Assert-Throws {
        & $rollbackScript -PreviousArchive $traversalZip -Destination (Join-Path $testRoot 'traversal-destination')
    } 'Unsafe archive entry' 'archive traversal is rejected'
    Assert-True (-not (Test-Path -LiteralPath $escapePath)) 'archive traversal creates no escaped file'

    $collisionZip = Join-Path $output 'collision.zip'
    New-TestZip $collisionZip @(
        @{ Name = 'LernType.exe'; Content = 'exe' },
        @{ Name = 'Data.txt'; Content = 'one' },
        @{ Name = 'data.TXT'; Content = 'two' }
    )
    Assert-Throws {
        & $rollbackScript -PreviousArchive $collisionZip -Destination (Join-Path $testRoot 'collision-destination')
    } 'Duplicate or file/directory-colliding' 'case-insensitive ZIP collision is rejected'

    Assert-Throws {
        & $rollbackScript -PreviousArchive $rootZip -Destination (Join-Path $testRoot 'size-limit-destination') -MaximumUncompressedBytes 1
    } 'extraction safety limit' 'uncompressed-size limit is enforced'

    $reservedZip = Join-Path $output 'reserved-record.zip'
    New-TestZip $reservedZip @(
        @{ Name = 'LernType.exe'; Content = 'exe' },
        @{ Name = 'rollback-record.json'; Content = 'untrusted' }
    )
    Assert-Throws {
        & $rollbackScript -PreviousArchive $reservedZip -Destination (Join-Path $testRoot 'reserved-record-destination')
    } 'reserved metadata path' 'archive cannot overwrite the generated rollback record'

    $nestedExecutableZip = Join-Path $output 'nested-executable.zip'
    New-TestZip $nestedExecutableZip @(
        @{ Name = 'LernType.exe'; Content = 'exe' },
        @{ Name = 'nested/LernType.exe'; Content = 'second' }
    )
    Assert-Throws {
        & $rollbackScript -PreviousArchive $nestedExecutableZip -Destination (Join-Path $testRoot 'nested-executable-destination')
    } 'exactly one LernType.exe' 'archive cannot contain a second nested executable'

    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    Assert-Throws {
        & $rollbackScript -RepositoryRoot $repositoryRoot -Destination (Join-Path $repositoryRoot '.rollback-inside-self-test') -WhatIf
    } 'outside the current repository' 'source rollback destination cannot be nested in the active worktree'
    $sourceWhatIfDestination = Join-Path $testRoot 'source-rollback-what-if'
    $sourceWhatIf = & $rollbackScript -RepositoryRoot $repositoryRoot -Destination $sourceWhatIfDestination -WhatIf 6>$null
    Assert-True (-not $sourceWhatIf.Applied -and -not (Test-Path -LiteralPath $sourceWhatIfDestination)) 'source rollback WhatIf validates commit and writes nothing'

    [pscustomobject]@{
        Result = 'PASS'
        Passed = $results.Count
        Tests = $results
        DeterministicSha256 = $first.Sha256
    } | ConvertTo-Json -Depth 4
}
finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $leaf = Split-Path -Leaf $resolvedTestRoot
    if (-not $resolvedTestRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not $leaf.StartsWith('lerntype-release-script-tests-', [StringComparison]::Ordinal)) {
        throw "Test cleanup boundary check failed: $resolvedTestRoot"
    }
    if ([IO.Directory]::Exists($resolvedTestRoot)) {
        $testRootPrefix = $resolvedTestRoot + [IO.Path]::DirectorySeparatorChar
        $reparsePoints = @(Get-ChildItem -LiteralPath $resolvedTestRoot -Recurse -Force | Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        } | Sort-Object FullName -Descending)
        foreach ($reparsePoint in $reparsePoints) {
            $reparsePath = [IO.Path]::GetFullPath($reparsePoint.FullName)
            if (-not $reparsePath.StartsWith($testRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Test reparse-point cleanup boundary check failed: $reparsePath"
            }
            if ($reparsePoint.PSIsContainer) { [IO.Directory]::Delete($reparsePath) }
            else { [IO.File]::Delete($reparsePath) }
        }
        [IO.Directory]::Delete($resolvedTestRoot, $true)
    }
}
