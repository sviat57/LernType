#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $RuntimeDirectory,

    [string] $OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('LernTypeDataRollbackTests-' + [guid]::NewGuid().ToString('N'))),

    [switch] $KeepArtifacts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Import-SqliteRuntime([string] $Directory) {
    $directoryPath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Directory).Path)
    $assemblies = @(
        'SQLitePCLRaw.core.dll',
        'SQLitePCLRaw.provider.e_sqlite3.dll',
        'SQLitePCLRaw.batteries_v2.dll',
        'Microsoft.Data.Sqlite.dll'
    )
    foreach ($file in @($assemblies + 'e_sqlite3.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $directoryPath $file) -PathType Leaf)) {
            throw "SQLite runtime file is missing: $file"
        }
    }
    $env:PATH = $directoryPath + [IO.Path]::PathSeparator + $env:PATH
    foreach ($assemblyName in $assemblies) {
        $loaded = [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.Location) -and
            [IO.Path]::GetFileName($_.Location).Equals($assemblyName, [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
        if (-not $loaded) { Add-Type -Path (Join-Path $directoryPath $assemblyName) }
    }
    [SQLitePCL.Batteries_V2]::Init()
    return $directoryPath
}

function Open-FixtureConnection([string] $Path, [bool] $ReadOnly = $false) {
    $builder = [Microsoft.Data.Sqlite.SqliteConnectionStringBuilder]::new()
    $builder.DataSource = $Path
    $builder.Mode = if ($ReadOnly) {
        [Microsoft.Data.Sqlite.SqliteOpenMode]::ReadOnly
    }
    else {
        [Microsoft.Data.Sqlite.SqliteOpenMode]::ReadWriteCreate
    }
    $builder.Pooling = $false
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new($builder.ToString())
    $connection.Open()
    return $connection
}

function Invoke-FixtureSql([string] $Path, [string] $Sql) {
    $connection = Open-FixtureConnection $Path
    try {
        $command = $connection.CreateCommand()
        try {
            $command.CommandText = $Sql
            [void]$command.ExecuteNonQuery()
        }
        finally { $command.Dispose() }
    }
    finally { $connection.Dispose() }
}

function Get-FixtureScalar([string] $Path, [string] $Sql) {
    $connection = Open-FixtureConnection $Path $true
    try {
        $command = $connection.CreateCommand()
        try {
            $command.CommandText = $Sql
            return $command.ExecuteScalar()
        }
        finally { $command.Dispose() }
    }
    finally { $connection.Dispose() }
}

function Get-FileSha256([string] $Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function New-SchemaV3TargetFixture([string] $Path) {
    Invoke-FixtureSql $Path @'
        PRAGMA foreign_keys=OFF;
        CREATE TABLE metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        INSERT INTO metadata VALUES('content_revision', '5');
        CREATE TABLE sample_data(id INTEGER PRIMARY KEY, payload TEXT NOT NULL);
        INSERT INTO sample_data VALUES(1, 'schema-v3-value');
        CREATE TABLE user_books(
            id INTEGER PRIMARY KEY,
            title TEXT NOT NULL,
            source_culture TEXT NOT NULL,
            raw_text TEXT NOT NULL,
            created_utc TEXT NOT NULL);
        CREATE TABLE user_book_words(
            id INTEGER PRIMARY KEY,
            book_id INTEGER NOT NULL REFERENCES user_books(id) ON DELETE CASCADE,
            source_text TEXT NOT NULL,
            translations_json TEXT NOT NULL,
            frequency INTEGER NOT NULL,
            context_text TEXT NOT NULL,
            part_of_speech TEXT NOT NULL);
        INSERT INTO user_books VALUES(1, 'Valid', 'de-DE', 'Valid source', '2026-08-22T00:00:00Z');
        INSERT INTO user_book_words VALUES(11, 1, 'valid', '["верный"]', 1, 'Valid context', 'adjective');
        INSERT INTO user_book_words VALUES(22, 2, 'orphan', '["сирота"]', 1, 'Recoverable context', 'noun');
        CREATE TABLE user_progress(
            content_type TEXT NOT NULL,
            content_id INTEGER NOT NULL,
            attempt_count INTEGER NOT NULL,
            correct_count INTEGER NOT NULL,
            last_attempt_utc TEXT,
            semantic_key TEXT,
            catalog_revision INTEGER,
            migration_status TEXT NOT NULL,
            PRIMARY KEY(content_type, content_id));
        INSERT INTO user_progress VALUES('BookWord', 11, 3, 2, '2026-08-22T00:00:00Z', NULL, NULL, 'active');
        INSERT INTO user_progress VALUES('BookWord', 22, 4, 3, '2026-08-22T00:00:00Z', NULL, NULL, 'active');
        PRAGMA user_version=3;
'@
}

function New-SchemaV4Fixture([string] $Path, [string] $Payload) {
    Invoke-FixtureSql $Path @"
        PRAGMA foreign_keys=ON;
        CREATE TABLE metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        INSERT INTO metadata VALUES('content_revision', '5');
        CREATE TABLE sample_data(id INTEGER PRIMARY KEY, payload TEXT NOT NULL);
        INSERT INTO sample_data VALUES(1, '$Payload');
        CREATE TABLE user_books(
            id INTEGER PRIMARY KEY,
            title TEXT NOT NULL,
            source_culture TEXT NOT NULL,
            raw_text TEXT NOT NULL,
            created_utc TEXT NOT NULL);
        CREATE TABLE user_book_words(
            id INTEGER PRIMARY KEY,
            book_id INTEGER NOT NULL REFERENCES user_books(id) ON DELETE CASCADE,
            source_text TEXT NOT NULL,
            translations_json TEXT NOT NULL,
            frequency INTEGER NOT NULL,
            context_text TEXT NOT NULL,
            part_of_speech TEXT NOT NULL);
        INSERT INTO user_books VALUES(3, 'Current', 'de-DE', 'Current v4 source', '2026-08-31T00:00:00Z');
        INSERT INTO user_book_words VALUES(33, 3, 'current', '["текущий"]', 1, 'Current context', 'adjective');
        CREATE TABLE course_progress(
            course_id TEXT NOT NULL,
            node_id TEXT NOT NULL,
            status TEXT NOT NULL,
            best_score REAL NOT NULL,
            attempt_count INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL,
            PRIMARY KEY(course_id, node_id));
        INSERT INTO course_progress VALUES('german-a0', 'lesson:a0-01', 'Completed', 1.0, 1, '2026-08-31T00:00:00Z');
        PRAGMA user_version=4;
"@
}

function Add-StrandedWalCommit(
    [string] $DatabasePath,
    [string] $RuntimePath,
    [string] $WorkingDirectory
) {
    $helperPath = Join-Path $WorkingDirectory 'Create-StrandedWal.ps1'
    $markerPath = Join-Path $WorkingDirectory 'wal-ready.txt'
    $stdoutPath = Join-Path $WorkingDirectory 'wal-helper.stdout.log'
    $stderrPath = Join-Path $WorkingDirectory 'wal-helper.stderr.log'
    $helper = @'
param([string] $Database, [string] $Runtime, [string] $Marker)
$ErrorActionPreference = 'Stop'
$env:PATH = $Runtime + [IO.Path]::PathSeparator + $env:PATH
foreach ($name in 'SQLitePCLRaw.core.dll','SQLitePCLRaw.provider.e_sqlite3.dll','SQLitePCLRaw.batteries_v2.dll','Microsoft.Data.Sqlite.dll') {
    Add-Type -Path (Join-Path $Runtime $name)
}
[SQLitePCL.Batteries_V2]::Init()
$builder = [Microsoft.Data.Sqlite.SqliteConnectionStringBuilder]::new()
$builder.DataSource = $Database
$builder.Mode = [Microsoft.Data.Sqlite.SqliteOpenMode]::ReadWrite
$builder.Pooling = $false
$connection = [Microsoft.Data.Sqlite.SqliteConnection]::new($builder.ToString())
$connection.Open()
$command = $connection.CreateCommand()
$command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; UPDATE sample_data SET payload='current-v4-success-wal' WHERE id=1;"
[void]$command.ExecuteNonQuery()
[IO.File]::WriteAllText($Marker, 'ready')
Start-Sleep -Seconds 300
'@
    [IO.File]::WriteAllText($helperPath, $helper, [Text.UTF8Encoding]::new($false))
    $process = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') `
        -ArgumentList @('-NoProfile', '-File', $helperPath, '-Database', $DatabasePath, '-Runtime', $RuntimePath, '-Marker', $markerPath) `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (-not (Test-Path -LiteralPath $markerPath -PathType Leaf) -and [DateTime]::UtcNow -lt $deadline) {
            if ($process.HasExited) {
                throw "WAL helper exited early with code $($process.ExitCode): $(Get-Content -LiteralPath $stderrPath -Raw)"
            }
            Start-Sleep -Milliseconds 100
        }
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            throw 'WAL helper did not create its readiness marker.'
        }
    }
    finally {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
        }
        $process.Dispose()
    }
    Assert-True (Test-Path -LiteralPath ($DatabasePath + '-wal') -PathType Leaf) 'stranded WAL exists'
    Assert-True ((Get-Item -LiteralPath ($DatabasePath + '-wal')).Length -gt 0) 'stranded WAL contains committed pages'
    Assert-True (Test-Path -LiteralPath ($DatabasePath + '-shm') -PathType Leaf) 'stranded SHM exists'
}

$runtimePath = Import-SqliteRuntime $RuntimeDirectory
$rollbackScript = Join-Path $PSScriptRoot 'Invoke-LernTypeDataRollback.ps1'
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) {
    if (Get-ChildItem -LiteralPath $root -Force | Select-Object -First 1) {
        throw "OutputDirectory must be absent or empty: $root"
    }
}
else {
    [void][IO.Directory]::CreateDirectory($root)
}
$results = [Collections.Generic.List[object]]::new()
$completed = $false
try {
    # Successful atomic promotion and exact recovery artifacts.
    $successRoot = Join-Path $root 'success'
    $successRecovery = Join-Path $successRoot 'recovery'
    [void][IO.Directory]::CreateDirectory($successRecovery)
    $successCurrent = Join-Path $successRoot 'current.db'
    $successBackup = Join-Path $successRoot 'schema-v3.db'
    New-SchemaV4Fixture $successCurrent 'current-v4-success'
    New-SchemaV3TargetFixture $successBackup
    Add-StrandedWalCommit $successCurrent $runtimePath $successRoot
    $successCurrentHash = Get-FileSha256 $successCurrent
    $successBackupHash = Get-FileSha256 $successBackup
    $success = & $rollbackScript `
        -CurrentDatabase $successCurrent `
        -TargetSchemaBackup $successBackup `
        -RuntimeDirectory $runtimePath `
        -RecoveryRoot $successRecovery `
        -ExpectedContentRevision 5 `
        -Confirm:$false
    Assert-True ($success.status -eq 'completed' -and $success.applied) 'successful rollback reports completed'
    Assert-True ([int](Get-FixtureScalar $successCurrent 'PRAGMA user_version;') -eq 3) 'promoted database is schema v3'
    Assert-True ([int](Get-FixtureScalar $successCurrent "SELECT value FROM metadata WHERE key='content_revision';") -eq 5) 'promoted catalog revision is 5'
    Assert-True ([string](Get-FixtureScalar $successCurrent 'PRAGMA quick_check;') -eq 'ok') 'promoted quick_check is ok'
    Assert-True ([int](Get-FixtureScalar $successCurrent 'SELECT COUNT(*) FROM pragma_foreign_key_check;') -eq 1) 'known v3 FK inventory is preserved exactly'
    Assert-True ([string](Get-FixtureScalar $successCurrent 'SELECT payload FROM sample_data WHERE id=1;') -eq 'schema-v3-value') 'v3 payload is promoted'
    Assert-True ((Get-FileSha256 $successBackup) -eq $successBackupHash) 'schema-v3 source backup is not changed'
    Assert-True (Test-Path -LiteralPath $success.recordPath -PathType Leaf) 'success record exists'
    Assert-True (Test-Path -LiteralPath ($success.recordPath + '.sha256') -PathType Leaf) 'success record hash exists'
    $successRecord = Get-Content -LiteralPath $success.recordPath -Raw | ConvertFrom-Json
    Assert-True ($successRecord.status -eq 'completed') 'success record status is exact'
    Assert-True ($successRecord.sourceBackup.verifiedWorkingDatabase.foreignKeyViolationCount -eq 1) 'record captures expected FK inventory'
    Assert-True ($successRecord.promotedDatabase.inventorySha256 -eq $successRecord.sourceBackup.verifiedWorkingDatabase.inventorySha256) 'record captures exact promoted inventory'
    $rawMain = @($successRecord.preservedCurrent.rawFileSet.files | Where-Object { $_.suffix -eq '' -and $_.existed })
    Assert-True ($rawMain.Count -eq 1 -and $rawMain[0].sha256 -eq $successCurrentHash) 'original current v4 main is preserved byte-for-byte'
    $rawWal = @($successRecord.preservedCurrent.rawFileSet.files | Where-Object { $_.suffix -eq '-wal' -and $_.existed })
    $rawShm = @($successRecord.preservedCurrent.rawFileSet.files | Where-Object { $_.suffix -eq '-shm' -and $_.existed })
    Assert-True ($rawWal.Count -eq 1 -and $rawWal[0].sizeBytes -gt 0) 'original non-empty WAL is preserved byte-for-byte'
    Assert-True ($rawShm.Count -eq 1 -and $rawShm[0].sizeBytes -gt 0) 'original SHM is preserved byte-for-byte'
    Assert-True (Test-Path -LiteralPath $successRecord.preservedCurrent.consistentDatabase.path -PathType Leaf) 'consistent current v4 rollback backup exists'
    Assert-True ([string](Get-FixtureScalar $successRecord.preservedCurrent.consistentDatabase.path 'SELECT payload FROM sample_data WHERE id=1;') -eq 'current-v4-success-wal') 'consistent v4 backup includes committed WAL content'
    $results.Add([pscustomobject][ordered]@{
            name = 'successful-promotion'
            passed = $true
            recordPath = $success.recordPath
            recordSha256 = $success.recordSha256
        })

    # Injected post-promotion failure must restore the exact logical v4 profile.
    $failureRoot = Join-Path $root 'failure-recovery'
    $failureRecovery = Join-Path $failureRoot 'recovery'
    [void][IO.Directory]::CreateDirectory($failureRecovery)
    $failureCurrent = Join-Path $failureRoot 'current.db'
    $failureBackup = Join-Path $failureRoot 'schema-v3.db'
    New-SchemaV4Fixture $failureCurrent 'current-v4-must-return'
    New-SchemaV3TargetFixture $failureBackup
    $env:LERNTYPE_DATA_ROLLBACK_TEST_MODE = '1'
    $env:LERNTYPE_DATA_ROLLBACK_TEST_FAIL_PHASE = 'after-promotion'
    $failureThrown = $false
    try {
        & $rollbackScript `
            -CurrentDatabase $failureCurrent `
            -TargetSchemaBackup $failureBackup `
            -RuntimeDirectory $runtimePath `
            -RecoveryRoot $failureRecovery `
            -ExpectedContentRevision 5 `
            -Confirm:$false
    }
    catch { $failureThrown = $true }
    finally {
        Remove-Item Env:LERNTYPE_DATA_ROLLBACK_TEST_MODE -ErrorAction SilentlyContinue
        Remove-Item Env:LERNTYPE_DATA_ROLLBACK_TEST_FAIL_PHASE -ErrorAction SilentlyContinue
    }
    Assert-True $failureThrown 'post-promotion injected failure is surfaced'
    Assert-True ([int](Get-FixtureScalar $failureCurrent 'PRAGMA user_version;') -eq 4) 'failure restores schema v4'
    Assert-True ([int](Get-FixtureScalar $failureCurrent "SELECT value FROM metadata WHERE key='content_revision';") -eq 5) 'failure restores catalog revision 5'
    Assert-True ([string](Get-FixtureScalar $failureCurrent 'SELECT payload FROM sample_data WHERE id=1;') -eq 'current-v4-must-return') 'failure restores current v4 payload'
    Assert-True ([string](Get-FixtureScalar $failureCurrent 'PRAGMA quick_check;') -eq 'ok') 'restored v4 quick_check is ok'
    Assert-True ([int](Get-FixtureScalar $failureCurrent 'SELECT COUNT(*) FROM pragma_foreign_key_check;') -eq 0) 'restored v4 FK check is clean'
    $failureRecordPath = (Get-ChildItem -LiteralPath $failureRecovery -Recurse -Filter 'rollback-record.json' -File | Select-Object -First 1).FullName
    $failureRecord = Get-Content -LiteralPath $failureRecordPath -Raw | ConvertFrom-Json
    Assert-True ($failureRecord.status -eq 'failed-restored') 'failure record confirms automatic recovery'
    Assert-True ($failureRecord.recovery.status -eq 'restored-current-schema') 'failure recovery status is exact'
    $results.Add([pscustomobject][ordered]@{
            name = 'post-promotion-failure-recovery'
            passed = $true
            recordPath = $failureRecordPath
        })

    # A process guard failure must occur before the current database changes.
    $guardRoot = Join-Path $root 'process-guard'
    $guardRecovery = Join-Path $guardRoot 'recovery'
    [void][IO.Directory]::CreateDirectory($guardRecovery)
    $guardCurrent = Join-Path $guardRoot 'current.db'
    $guardBackup = Join-Path $guardRoot 'schema-v3.db'
    New-SchemaV4Fixture $guardCurrent 'current-v4-guard'
    New-SchemaV3TargetFixture $guardBackup
    $guardHash = Get-FileSha256 $guardCurrent
    $currentProcessName = (Get-Process -Id $PID).ProcessName
    $guardThrown = $false
    try {
        & $rollbackScript `
            -CurrentDatabase $guardCurrent `
            -TargetSchemaBackup $guardBackup `
            -RuntimeDirectory $runtimePath `
            -RecoveryRoot $guardRecovery `
            -ApplicationProcessName @($currentProcessName) `
            -Confirm:$false
    }
    catch { $guardThrown = $true }
    Assert-True $guardThrown 'application process guard blocks rollback'
    Assert-True ((Get-FileSha256 $guardCurrent) -eq $guardHash) 'process guard preserves current DB bytes'
    Assert-True (@(Get-ChildItem -LiteralPath $guardRecovery -Force).Count -eq 0) 'process guard creates no operation files'
    $results.Add([pscustomobject][ordered]@{
            name = 'application-process-guard'
            passed = $true
        })

    # A schema-v4 input posing as the target backup must fail before current modification.
    $invalidRoot = Join-Path $root 'invalid-backup'
    $invalidRecovery = Join-Path $invalidRoot 'recovery'
    [void][IO.Directory]::CreateDirectory($invalidRecovery)
    $invalidCurrent = Join-Path $invalidRoot 'current.db'
    $invalidBackup = Join-Path $invalidRoot 'not-schema-v3.db'
    New-SchemaV4Fixture $invalidCurrent 'current-v4-invalid-backup'
    New-SchemaV4Fixture $invalidBackup 'wrong-backup'
    $invalidHash = Get-FileSha256 $invalidCurrent
    $invalidThrown = $false
    try {
        & $rollbackScript `
            -CurrentDatabase $invalidCurrent `
            -TargetSchemaBackup $invalidBackup `
            -RuntimeDirectory $runtimePath `
            -RecoveryRoot $invalidRecovery `
            -Confirm:$false
    }
    catch { $invalidThrown = $true }
    Assert-True $invalidThrown 'schema-v4 backup is rejected as a schema-v3 target'
    Assert-True ((Get-FileSha256 $invalidCurrent) -eq $invalidHash) 'invalid backup leaves current DB bytes unchanged'
    $invalidRecordPath = (Get-ChildItem -LiteralPath $invalidRecovery -Recurse -Filter 'rollback-record.json' -File | Select-Object -First 1).FullName
    $invalidRecord = Get-Content -LiteralPath $invalidRecordPath -Raw | ConvertFrom-Json
    Assert-True ($invalidRecord.status -eq 'failed-source-preserved') 'invalid backup record confirms source preservation'
    $results.Add([pscustomobject][ordered]@{
            name = 'invalid-backup-rejected'
            passed = $true
            recordPath = $invalidRecordPath
        })

    $completed = $true
    [pscustomobject][ordered]@{
        format = 'lerntype-data-rollback-self-test'
        passed = $true
        testCount = $results.Count
        artifactRoot = $root
        tests = @($results)
    }
}
finally {
    if (-not $KeepArtifacts -and $completed -and (Test-Path -LiteralPath $root)) {
        $parent = [IO.Path]::GetFullPath((Split-Path -Parent $root))
        if ([IO.Path]::GetFullPath($root).StartsWith(
                $parent.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $root -Recurse -Force
        }
    }
}
