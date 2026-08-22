#Requires -Version 7.0

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $CurrentDatabase,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $SchemaV2Backup,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $RuntimeDirectory,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $RecoveryRoot,

    [ValidateRange(0, [int]::MaxValue)]
    [int] $ExpectedContentRevision = 4,

    [ValidateNotNullOrEmpty()]
    [string[]] $ApplicationProcessName = @('LernType', 'WortBruecke')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NormalizedPath([string] $Path) {
    return [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
}

function Get-DirectoryPrefix([string] $Path) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    return $fullPath + [IO.Path]::DirectorySeparatorChar
}

function Assert-NoReparsePointInExistingPath([string] $Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($fullPath)
    $cursor = $root
    foreach ($segment in $fullPath.Substring($root.Length).Split(
            [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar),
            [StringSplitOptions]::RemoveEmptyEntries)) {
        $cursor = Join-Path $cursor $segment
        if (-not (Test-Path -LiteralPath $cursor)) {
            throw "Path component does not exist: $cursor"
        }
        if (((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse points are not accepted in data rollback paths: $cursor"
        }
    }
}

function Get-Sha256([string] $Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try { return [Convert]::ToHexString($sha256.ComputeHash($stream)) }
        finally { $sha256.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Get-StringSha256([string] $Value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}

function Write-AtomicJson([object] $Value, [string] $Path) {
    $json = $Value | ConvertTo-Json -Depth 30
    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temporary, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $Path, $true)
}

function Assert-ApplicationStopped([string[]] $Names) {
    $normalizedNames = @($Names | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $normalizedNames -contains $_.ProcessName
        })
    if ($running.Count -gt 0) {
        $details = $running | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }
        throw "LernType data rollback requires the application to be stopped. Running: $($details -join ', ')."
    }
}

function Import-SqliteRuntime([string] $Directory) {
    $managedAssemblies = @(
        'SQLitePCLRaw.core.dll',
        'SQLitePCLRaw.provider.e_sqlite3.dll',
        'SQLitePCLRaw.batteries_v2.dll',
        'Microsoft.Data.Sqlite.dll'
    )
    foreach ($file in @($managedAssemblies + 'e_sqlite3.dll')) {
        $path = Join-Path $Directory $file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "SQLite runtime file is missing: $path"
        }
    }

    $pathEntries = @($env:PATH -split [IO.Path]::PathSeparator)
    if ($pathEntries -notcontains $Directory) {
        $env:PATH = $Directory + [IO.Path]::PathSeparator + $env:PATH
    }
    foreach ($assemblyName in $managedAssemblies) {
        $assemblyPath = Join-Path $Directory $assemblyName
        $alreadyLoaded = [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.Location) -and
            [IO.Path]::GetFileName($_.Location).Equals($assemblyName, [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
        if (-not $alreadyLoaded) {
            Add-Type -Path $assemblyPath
        }
    }
    [SQLitePCL.Batteries_V2]::Init()
}

function New-SqliteConnection([string] $Path, [bool] $ReadOnly) {
    $builder = [Microsoft.Data.Sqlite.SqliteConnectionStringBuilder]::new()
    $builder.DataSource = $Path
    $builder.Mode = if ($ReadOnly) {
        [Microsoft.Data.Sqlite.SqliteOpenMode]::ReadOnly
    }
    else {
        [Microsoft.Data.Sqlite.SqliteOpenMode]::ReadWriteCreate
    }
    $builder.Cache = [Microsoft.Data.Sqlite.SqliteCacheMode]::Default
    $builder.Pooling = $false
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new($builder.ToString())
    $connection.Open()
    $command = $connection.CreateCommand()
    try {
        $command.CommandText = if ($ReadOnly) {
            'PRAGMA query_only=ON; PRAGMA busy_timeout=5000;'
        }
        else {
            'PRAGMA busy_timeout=5000;'
        }
        [void]$command.ExecuteNonQuery()
    }
    finally { $command.Dispose() }
    return $connection
}

function Quote-SqliteIdentifier([string] $Identifier) {
    if ($Identifier -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
        throw "Unsafe SQLite identifier: $Identifier"
    }
    return '"' + $Identifier + '"'
}

function ConvertTo-CanonicalSqliteValue([Microsoft.Data.Sqlite.SqliteDataReader] $Reader, [int] $Ordinal) {
    if ($Reader.IsDBNull($Ordinal)) { return 'N;' }
    $value = $Reader.GetValue($Ordinal)
    if ($value -is [byte[]]) {
        return "B:$($value.Length):$([Convert]::ToHexString($value));"
    }
    if ($value -is [float] -or $value -is [double] -or $value -is [decimal]) {
        return 'R:' + ([Convert]::ToDouble($value)).ToString('G17', [Globalization.CultureInfo]::InvariantCulture) + ';'
    }
    if ($value -is [byte] -or $value -is [sbyte] -or $value -is [short] -or $value -is [ushort] -or
        $value -is [int] -or $value -is [uint] -or $value -is [long] -or $value -is [ulong]) {
        return 'I:' + ([Convert]::ToString($value, [Globalization.CultureInfo]::InvariantCulture)) + ';'
    }
    $text = [Convert]::ToString($value, [Globalization.CultureInfo]::InvariantCulture)
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    return "S:$($bytes.Length):$([Convert]::ToBase64String($bytes));"
}

function Get-SqliteTableInspection(
    [Microsoft.Data.Sqlite.SqliteConnection] $Connection,
    [string] $Table,
    [string] $SchemaSql
) {
    $quotedTable = Quote-SqliteIdentifier $Table
    $columns = [Collections.Generic.List[object]]::new()
    $pragma = $Connection.CreateCommand()
    try {
        $pragma.CommandText = "PRAGMA table_info($quotedTable);"
        $reader = $pragma.ExecuteReader()
        try {
            while ($reader.Read()) {
                $columns.Add([pscustomobject][ordered]@{
                        ordinal = $reader.GetInt32(0)
                        name = $reader.GetString(1)
                        type = $reader.GetString(2)
                        notNull = $reader.GetInt32(3)
                        defaultValue = if ($reader.IsDBNull(4)) { $null } else { $reader.GetString(4) }
                        primaryKeyOrder = $reader.GetInt32(5)
                    })
            }
        }
        finally { $reader.Dispose() }
    }
    finally { $pragma.Dispose() }
    if ($columns.Count -eq 0) { throw "SQLite table has no columns: $Table" }

    $orderColumns = @($columns | Where-Object { $_.primaryKeyOrder -gt 0 } | Sort-Object primaryKeyOrder)
    if ($orderColumns.Count -eq 0) { $orderColumns = @($columns) }
    $selectColumns = @($columns | ForEach-Object { Quote-SqliteIdentifier $_.name }) -join ','
    $orderBy = @($orderColumns | ForEach-Object { Quote-SqliteIdentifier $_.name }) -join ','
    $command = $Connection.CreateCommand()
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    $rowCount = [long]0
    try {
        $command.CommandText = "SELECT $selectColumns FROM $quotedTable ORDER BY $orderBy;"
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $line = [Text.StringBuilder]::new()
                for ($ordinal = 0; $ordinal -lt $reader.FieldCount; $ordinal++) {
                    [void]$line.Append((ConvertTo-CanonicalSqliteValue $reader $ordinal))
                }
                [void]$line.Append("`n")
                $hash.AppendData([Text.Encoding]::UTF8.GetBytes($line.ToString()))
                $rowCount++
            }
        }
        finally { $reader.Dispose() }
        $digest = [Convert]::ToHexString($hash.GetHashAndReset())
    }
    finally {
        $hash.Dispose()
        $command.Dispose()
    }
    return [pscustomobject][ordered]@{
        name = $Table
        rowCount = $rowCount
        rowSha256 = $digest
        schemaSha256 = Get-StringSha256 $SchemaSql
        columns = @($columns)
    }
}

function Get-SqliteInspection([string] $Path) {
    $connection = New-SqliteConnection $Path $true
    try {
        $quickRows = [Collections.Generic.List[string]]::new()
        $quick = $connection.CreateCommand()
        try {
            $quick.CommandText = 'PRAGMA quick_check;'
            $reader = $quick.ExecuteReader()
            try { while ($reader.Read()) { $quickRows.Add($reader.GetString(0)) } }
            finally { $reader.Dispose() }
        }
        finally { $quick.Dispose() }

        $versionCommand = $connection.CreateCommand()
        try {
            $versionCommand.CommandText = 'PRAGMA user_version;'
            $userVersion = [Convert]::ToInt32($versionCommand.ExecuteScalar())
        }
        finally { $versionCommand.Dispose() }

        $contentRevision = $null
        $contentCommand = $connection.CreateCommand()
        try {
            $contentCommand.CommandText = @'
                SELECT CASE
                    WHEN EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='metadata')
                    THEN (SELECT value FROM metadata WHERE key='content_revision')
                    ELSE NULL
                END;
'@
            $contentValue = $contentCommand.ExecuteScalar()
            if ($null -ne $contentValue -and $contentValue -ne [DBNull]::Value) {
                $contentRevision = [Convert]::ToInt32($contentValue)
            }
        }
        finally { $contentCommand.Dispose() }

        $foreignKeys = [Collections.Generic.List[object]]::new()
        $foreignKeyCommand = $connection.CreateCommand()
        try {
            $foreignKeyCommand.CommandText = 'PRAGMA foreign_key_check;'
            $reader = $foreignKeyCommand.ExecuteReader()
            try {
                while ($reader.Read()) {
                    $foreignKeys.Add([pscustomobject][ordered]@{
                            table = $reader.GetString(0)
                            rowId = if ($reader.IsDBNull(1)) { $null } else { $reader.GetInt64(1) }
                            parent = $reader.GetString(2)
                            foreignKeyId = $reader.GetInt32(3)
                        })
                }
            }
            finally { $reader.Dispose() }
        }
        finally { $foreignKeyCommand.Dispose() }
        $sortedForeignKeys = @($foreignKeys | Sort-Object table, rowId, parent, foreignKeyId)

        $tableDefinitions = [Collections.Generic.List[object]]::new()
        $tableCommand = $connection.CreateCommand()
        try {
            $tableCommand.CommandText = @'
                SELECT name, COALESCE(sql, '')
                FROM sqlite_master
                WHERE type='table'
                ORDER BY name;
'@
            $reader = $tableCommand.ExecuteReader()
            try {
                while ($reader.Read()) {
                    $tableDefinitions.Add([pscustomobject]@{
                            Name = $reader.GetString(0)
                            Sql = $reader.GetString(1)
                        })
                }
            }
            finally { $reader.Dispose() }
        }
        finally { $tableCommand.Dispose() }

        $tables = [Collections.Generic.List[object]]::new()
        foreach ($definition in $tableDefinitions) {
            $tables.Add((Get-SqliteTableInspection $connection $definition.Name $definition.Sql))
        }
        $inventoryJson = @($tables | ForEach-Object {
                [ordered]@{
                    name = $_.name
                    rowCount = $_.rowCount
                    rowSha256 = $_.rowSha256
                    schemaSha256 = $_.schemaSha256
                }
            }) | ConvertTo-Json -Compress -Depth 6
        $foreignKeyJson = $sortedForeignKeys | ConvertTo-Json -Compress -Depth 6
        return [pscustomobject][ordered]@{
            path = [IO.Path]::GetFullPath($Path)
            sizeBytes = (Get-Item -LiteralPath $Path).Length
            sha256 = Get-Sha256 $Path
            quickCheck = @($quickRows)
            quickCheckPassed = $quickRows.Count -eq 1 -and $quickRows[0].Equals('ok', [StringComparison]::OrdinalIgnoreCase)
            userVersion = $userVersion
            contentRevision = $contentRevision
            foreignKeyViolationCount = $sortedForeignKeys.Count
            foreignKeySha256 = Get-StringSha256 $foreignKeyJson
            foreignKeyViolations = $sortedForeignKeys
            inventorySha256 = Get-StringSha256 $inventoryJson
            tables = @($tables)
        }
    }
    finally { $connection.Dispose() }
}

function Assert-EquivalentInspection([object] $Expected, [object] $Actual, [string] $Label) {
    if (-not $Actual.quickCheckPassed) { throw "$Label failed PRAGMA quick_check." }
    foreach ($property in @('userVersion', 'contentRevision', 'foreignKeyViolationCount', 'foreignKeySha256', 'inventorySha256')) {
        if ($Expected.$property -ne $Actual.$property) {
            throw "$Label differs from the verified source at $property. Expected '$($Expected.$property)', received '$($Actual.$property)'."
        }
    }
}

function Copy-RawSqliteFileSet([string] $SourceMain, [string] $DestinationDirectory) {
    [void][IO.Directory]::CreateDirectory($DestinationDirectory)
    $destinationPrefix = Get-DirectoryPrefix $DestinationDirectory
    $files = [Collections.Generic.List[object]]::new()
    foreach ($suffix in @('', '-wal', '-shm', '-journal')) {
        $source = $SourceMain + $suffix
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            $files.Add([pscustomobject][ordered]@{ suffix = $suffix; existed = $false })
            continue
        }
        if (((Get-Item -LiteralPath $source -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "SQLite file set contains a reparse point: $source"
        }
        $beforeHash = Get-Sha256 $source
        $destination = [IO.Path]::GetFullPath((Join-Path $DestinationDirectory ([IO.Path]::GetFileName($source))))
        if (-not $destination.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Raw SQLite preservation path escaped its destination: $destination"
        }
        [IO.File]::Copy($source, $destination, $false)
        $afterHash = Get-Sha256 $source
        $destinationHash = Get-Sha256 $destination
        if ($beforeHash -ne $afterHash -or $beforeHash -ne $destinationHash) {
            throw "SQLite file changed while it was being preserved: $source"
        }
        $files.Add([pscustomobject][ordered]@{
                suffix = $suffix
                existed = $true
                sourcePath = [IO.Path]::GetFullPath($source)
                preservedPath = $destination
                sizeBytes = (Get-Item -LiteralPath $source).Length
                sha256 = $beforeHash
            })
    }
    return [pscustomobject][ordered]@{
        sourceMain = [IO.Path]::GetFullPath($SourceMain)
        destinationDirectory = [IO.Path]::GetFullPath($DestinationDirectory)
        files = @($files)
    }
}

function Copy-SqliteDatabase([string] $Source, [string] $Destination) {
    if (Test-Path -LiteralPath $Destination) { throw "SQLite clone destination already exists: $Destination" }
    $sourceConnection = New-SqliteConnection $Source $true
    try {
        $destinationConnection = New-SqliteConnection $Destination $false
        try { $sourceConnection.BackupDatabase($destinationConnection) }
        finally { $destinationConnection.Dispose() }
    }
    finally { $sourceConnection.Dispose() }
}

function Remove-SqliteSidecars([string] $MainPath, [bool] $AllowNonEmpty) {
    foreach ($suffix in @('-wal', '-shm', '-journal')) {
        $path = $MainPath + $suffix
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        if (-not $AllowNonEmpty -and (Get-Item -LiteralPath $path).Length -gt 0) {
            throw "Non-empty SQLite sidecar remained after checkpoint: $path"
        }
        [IO.File]::Delete($path)
    }
}

function Normalize-SqliteDatabase([string] $Path) {
    $connection = New-SqliteConnection $Path $false
    try {
        $checkpoint = $connection.CreateCommand()
        try {
            $checkpoint.CommandText = 'PRAGMA wal_checkpoint(TRUNCATE);'
            $reader = $checkpoint.ExecuteReader()
            try {
                if (-not $reader.Read() -or $reader.GetInt32(0) -ne 0) {
                    throw "SQLite WAL checkpoint is busy: $Path"
                }
            }
            finally { $reader.Dispose() }
        }
        finally { $checkpoint.Dispose() }
        $journal = $connection.CreateCommand()
        try {
            $journal.CommandText = 'PRAGMA journal_mode=DELETE;'
            $mode = [Convert]::ToString($journal.ExecuteScalar())
            if (-not $mode.Equals('delete', [StringComparison]::OrdinalIgnoreCase)) {
                throw "SQLite journal mode did not switch to DELETE: $Path ($mode)."
            }
        }
        finally { $journal.Dispose() }
    }
    finally { $connection.Dispose() }
    Remove-SqliteSidecars $Path $false
}

function Write-RecordAndHash([object] $Record, [string] $Path) {
    Write-AtomicJson $Record $Path
    $hash = (Get-Sha256 $Path).ToLowerInvariant()
    [IO.File]::WriteAllText("$Path.sha256", "$hash *$([IO.Path]::GetFileName($Path))`n", [Text.UTF8Encoding]::new($false))
    return $hash
}

$currentPath = Get-NormalizedPath $CurrentDatabase
$backupPath = Get-NormalizedPath $SchemaV2Backup
$runtimePath = Get-NormalizedPath $RuntimeDirectory
$recoveryPath = Get-NormalizedPath $RecoveryRoot
foreach ($path in @($currentPath, $backupPath, $runtimePath, $recoveryPath)) {
    Assert-NoReparsePointInExistingPath $path
}
if ($currentPath.Equals($backupPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'CurrentDatabase and SchemaV2Backup must identify different files.'
}
if (-not [IO.Path]::GetPathRoot($currentPath).Equals(
        [IO.Path]::GetPathRoot($recoveryPath),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'RecoveryRoot must be on the same volume as CurrentDatabase for atomic promotion and recovery.'
}
if (-not [IO.Path]::GetExtension($currentPath).Equals('.db', [StringComparison]::OrdinalIgnoreCase) -or
    -not [IO.Path]::GetExtension($backupPath).Equals('.db', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'CurrentDatabase and SchemaV2Backup must use the .db extension.'
}
Assert-ApplicationStopped $ApplicationProcessName
Import-SqliteRuntime $runtimePath

if (-not $PSCmdlet.ShouldProcess($currentPath, "Atomically restore verified schema-v2 backup '$backupPath'")) {
    [pscustomobject][ordered]@{
        status = 'planned'
        currentDatabase = $currentPath
        schemaV2Backup = $backupPath
        recoveryRoot = $recoveryPath
        applied = $false
    }
    return
}

$operationId = 'data-rollback-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmssfff') + '-' + [guid]::NewGuid().ToString('N')
$operationDirectory = Join-Path $recoveryPath $operationId
[void][IO.Directory]::CreateDirectory($operationDirectory)
$statePath = Join-Path $operationDirectory 'operation-state.json'
$recordPath = Join-Path $operationDirectory 'rollback-record.json'
$startedUtc = [DateTimeOffset]::UtcNow
$stagePath = Join-Path (Split-Path -Parent $currentPath) ".$(Split-Path -Leaf $currentPath).$operationId.stage.db"
$recoveryStagePath = Join-Path (Split-Path -Parent $currentPath) ".$(Split-Path -Leaf $currentPath).$operationId.restore.db"
$displacedAdjacentPath = Join-Path (Split-Path -Parent $currentPath) ".$(Split-Path -Leaf $currentPath).$operationId.displaced.db"
$failedAdjacentPath = Join-Path (Split-Path -Parent $currentPath) ".$(Split-Path -Leaf $currentPath).$operationId.failed.db"
$currentConsistentPath = Join-Path $operationDirectory 'current-v3-consistent.db'
$schemaWorkingDirectory = Join-Path $operationDirectory 'schema-v2-source'
$currentRawDirectory = Join-Path $operationDirectory 'current-v3-raw'
$currentTouched = $false
$promoted = $false
$currentExpected = $null
$backupExpected = $null
$record = [ordered]@{
    format = 'lerntype-data-rollback-record'
    recordVersion = 1
    operationId = $operationId
    status = 'started'
    phase = 'initializing'
    startedUtc = $startedUtc.ToString('O')
    completedUtc = $null
    currentDatabase = $currentPath
    schemaV2Backup = $backupPath
    runtimeDirectory = $runtimePath
    recoveryDirectory = [IO.Path]::GetFullPath($operationDirectory)
    applicationProcessNames = @($ApplicationProcessName)
    expectedCurrentUserVersion = 3
    expectedBackupUserVersion = 2
    expectedContentRevision = $ExpectedContentRevision
    sourceBackup = $null
    preservedCurrentV3 = $null
    promotedDatabase = $null
    recovery = $null
    error = $null
}

try {
    $record.phase = 'preserving-source-files'
    Write-AtomicJson $record $statePath
    $sourceBackupRaw = Copy-RawSqliteFileSet $backupPath $schemaWorkingDirectory
    $currentRaw = Copy-RawSqliteFileSet $currentPath $currentRawDirectory
    $schemaWorkingPath = Join-Path $schemaWorkingDirectory ([IO.Path]::GetFileName($backupPath))
    $currentRawPath = Join-Path $currentRawDirectory ([IO.Path]::GetFileName($currentPath))

    $record.phase = 'validating-schema-v2-backup'
    Write-AtomicJson $record $statePath
    $backupExpected = Get-SqliteInspection $schemaWorkingPath
    if (-not $backupExpected.quickCheckPassed) { throw 'Schema-v2 backup failed PRAGMA quick_check.' }
    if ($backupExpected.userVersion -ne 2) {
        throw "SchemaV2Backup has user_version=$($backupExpected.userVersion); expected 2."
    }
    if ($backupExpected.contentRevision -ne $ExpectedContentRevision) {
        throw "SchemaV2Backup has content_revision=$($backupExpected.contentRevision); expected $ExpectedContentRevision."
    }
    $record.sourceBackup = [ordered]@{
        originalFileSet = $sourceBackupRaw
        verifiedWorkingDatabase = $backupExpected
    }

    $record.phase = 'preserving-current-v3'
    Write-AtomicJson $record $statePath
    Copy-SqliteDatabase $currentRawPath $currentConsistentPath
    Normalize-SqliteDatabase $currentConsistentPath
    $currentExpected = Get-SqliteInspection $currentConsistentPath
    if (-not $currentExpected.quickCheckPassed) { throw 'Current v3 preservation failed PRAGMA quick_check.' }
    if ($currentExpected.userVersion -ne 3) {
        throw "CurrentDatabase has user_version=$($currentExpected.userVersion); expected 3."
    }
    $record.preservedCurrentV3 = [ordered]@{
        rawFileSet = $currentRaw
        consistentDatabase = $currentExpected
        displacedMain = $null
    }

    $record.phase = 'staging-schema-v2'
    Write-AtomicJson $record $statePath
    Copy-SqliteDatabase $schemaWorkingPath $stagePath
    Normalize-SqliteDatabase $stagePath
    $stageInspection = Get-SqliteInspection $stagePath
    Assert-EquivalentInspection $backupExpected $stageInspection 'Staged schema-v2 database'

    Assert-ApplicationStopped $ApplicationProcessName
    foreach ($rawFile in @($currentRaw.files)) {
        $livePath = $currentPath + $rawFile.suffix
        if (-not $rawFile.existed) {
            if (Test-Path -LiteralPath $livePath -PathType Leaf) {
                throw "Current SQLite file set changed after preservation: $livePath"
            }
            continue
        }
        if (-not (Test-Path -LiteralPath $livePath -PathType Leaf) -or (Get-Sha256 $livePath) -ne $rawFile.sha256) {
            throw "Current SQLite file set changed after preservation: $livePath"
        }
    }

    $record.phase = 'normalizing-current-v3'
    Write-AtomicJson $record $statePath
    $currentTouched = $true
    Normalize-SqliteDatabase $currentPath
    $normalizedCurrent = Get-SqliteInspection $currentPath
    Assert-EquivalentInspection $currentExpected $normalizedCurrent 'Normalized current v3 database'

    $record.phase = 'atomic-promotion'
    Write-AtomicJson $record $statePath
    [IO.File]::Replace($stagePath, $currentPath, $displacedAdjacentPath, $true)
    $promoted = $true
    if ($env:LERNTYPE_DATA_ROLLBACK_TEST_MODE -eq '1' -and
        $env:LERNTYPE_DATA_ROLLBACK_TEST_FAIL_PHASE -eq 'after-promotion') {
        throw 'Injected test failure after atomic promotion.'
    }

    $record.phase = 'verifying-promoted-schema-v2'
    Write-AtomicJson $record $statePath
    $resultInspection = Get-SqliteInspection $currentPath
    Assert-EquivalentInspection $backupExpected $resultInspection 'Promoted schema-v2 database'
    if ($resultInspection.userVersion -ne 2) { throw 'Promoted database is not schema v2.' }

    $displacedFinalPath = Join-Path $operationDirectory 'displaced-current-v3.db'
    [IO.File]::Move($displacedAdjacentPath, $displacedFinalPath)
    $record.preservedCurrentV3.displacedMain = [ordered]@{
        path = [IO.Path]::GetFullPath($displacedFinalPath)
        sizeBytes = (Get-Item -LiteralPath $displacedFinalPath).Length
        sha256 = Get-Sha256 $displacedFinalPath
    }
    $record.promotedDatabase = $resultInspection
    $record.status = 'completed'
    $record.phase = 'completed'
    $record.completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Write-AtomicJson $record $statePath
    $recordSha256 = Write-RecordAndHash $record $recordPath
    [pscustomobject][ordered]@{
        status = $record.status
        applied = $true
        currentDatabase = $currentPath
        userVersion = $resultInspection.userVersion
        contentRevision = $resultInspection.contentRevision
        quickCheckPassed = $resultInspection.quickCheckPassed
        foreignKeyViolationCount = $resultInspection.foreignKeyViolationCount
        inventorySha256 = $resultInspection.inventorySha256
        preservedCurrentDatabase = $currentExpected.path
        recordPath = [IO.Path]::GetFullPath($recordPath)
        recordSha256 = $recordSha256
    }
}
catch {
    $failure = $_
    $recoverySucceeded = $false
    $recoveryError = $null
    if ($currentTouched -and $null -ne $currentExpected -and (Test-Path -LiteralPath $currentConsistentPath)) {
        try {
            Remove-SqliteSidecars $currentPath $true
            if (Test-Path -LiteralPath $recoveryStagePath) { [IO.File]::Delete($recoveryStagePath) }
            Copy-SqliteDatabase $currentConsistentPath $recoveryStagePath
            Normalize-SqliteDatabase $recoveryStagePath
            $recoveryStageInspection = Get-SqliteInspection $recoveryStagePath
            Assert-EquivalentInspection $currentExpected $recoveryStageInspection 'Current-v3 recovery stage'
            if (Test-Path -LiteralPath $currentPath) {
                [IO.File]::Replace($recoveryStagePath, $currentPath, $failedAdjacentPath, $true)
            }
            else {
                [IO.File]::Move($recoveryStagePath, $currentPath)
            }
            Remove-SqliteSidecars $currentPath $true
            $restoredInspection = Get-SqliteInspection $currentPath
            Assert-EquivalentInspection $currentExpected $restoredInspection 'Restored current v3 database'
            if (Test-Path -LiteralPath $failedAdjacentPath) {
                [IO.File]::Move($failedAdjacentPath, (Join-Path $operationDirectory 'failed-current.db'))
            }
            if (Test-Path -LiteralPath $displacedAdjacentPath) {
                $recoveredDisplacedPath = Join-Path $operationDirectory 'promotion-displaced-current-v3.db'
                [IO.File]::Move($displacedAdjacentPath, $recoveredDisplacedPath)
                $record.preservedCurrentV3.displacedMain = [ordered]@{
                    path = [IO.Path]::GetFullPath($recoveredDisplacedPath)
                    sizeBytes = (Get-Item -LiteralPath $recoveredDisplacedPath).Length
                    sha256 = Get-Sha256 $recoveredDisplacedPath
                }
            }
            $record.recovery = [ordered]@{
                status = 'restored-current-v3'
                restoredDatabase = $restoredInspection
            }
            $record.status = 'failed-restored'
            $recoverySucceeded = $true
        }
        catch {
            $recoveryError = $_
            $record.recovery = [ordered]@{
                status = 'manual-recovery-required'
                consistentDatabase = $currentConsistentPath
                errorType = $_.Exception.GetType().FullName
                errorMessage = $_.Exception.Message
            }
            $record.status = 'failed-recovery-required'
        }
    }
    else {
        $record.status = 'failed-source-preserved'
        $record.recovery = [ordered]@{ status = 'current-database-not-modified' }
    }
    $record.phase = 'failed'
    $record.completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $record.error = [ordered]@{
        type = $failure.Exception.GetType().FullName
        message = $failure.Exception.Message
        promotedBeforeFailure = $promoted
    }
    try {
        Write-AtomicJson $record $statePath
        [void](Write-RecordAndHash $record $recordPath)
    }
    catch {
        if ($null -eq $recoveryError) { $recoveryError = $_ }
    }
    if ($recoverySucceeded) {
        throw "Data rollback failed and the preserved current v3 database was restored. Record: $recordPath. Cause: $($failure.Exception.Message)"
    }
    if ($null -ne $recoveryError) {
        throw [AggregateException]::new(
            "Data rollback failed and automatic recovery needs attention. Recovery directory: $operationDirectory",
            @($failure.Exception, $recoveryError.Exception))
    }
    throw
}
finally {
    foreach ($temporary in @($stagePath, $recoveryStagePath)) {
        if (Test-Path -LiteralPath $temporary) {
            try { [IO.File]::Delete($temporary) } catch { }
        }
        foreach ($suffix in @('-wal', '-shm', '-journal')) {
            if (Test-Path -LiteralPath ($temporary + $suffix)) {
                try { [IO.File]::Delete($temporary + $suffix) } catch { }
            }
        }
    }
}
