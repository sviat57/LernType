#Requires -Version 7.0

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$CommitSha,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$AssetPath,

    [string]$Owner = 'sviat57',
    [string]$Repository = 'LernType',
    [string]$Branch = 'main',
    [string]$Tag = 'v0.2.0',
    [string]$ReleaseName = 'LernType v0.2.0',
    [string]$RemoteName = 'origin',
    [string]$RepositoryDescription = 'LernType — автономный тренажёр немецкого языка RU↔DE для Windows.',
    [string]$ReleaseNotesPath,
    [switch]$SkipQa
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Invoke-Git {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [switch]$AllowFailure
    )

    $output = @(& git @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "git $($Arguments -join ' ') завершился с кодом $exitCode.`n$($output -join "`n")"
    }

    [pscustomobject]@{
        ExitCode = $exitCode
        Output = ($output -join "`n").Trim()
    }
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & $script:DotNetPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') завершился с кодом $LASTEXITCODE."
    }
}

function Get-HttpStatusCode {
    param([Parameter(Mandatory)]$ErrorRecord)

    if ($null -ne $ErrorRecord.Exception.Response -and
        $null -ne $ErrorRecord.Exception.Response.StatusCode) {
        return [int]$ErrorRecord.Exception.Response.StatusCode
    }

    return $null
}

function Invoke-GitHubApi {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Get', 'Post', 'Patch')]
        [string]$Method,

        [Parameter(Mandatory)]
        [string]$Uri,

        [hashtable]$Body,
        [string]$InFile,
        [string]$ContentType = 'application/vnd.github+json'
    )

    $parameters = @{
        Method = $Method
        Uri = $Uri
        Headers = $script:GitHubHeaders
        ContentType = $ContentType
    }

    if ($null -ne $Body) {
        $parameters.Body = $Body | ConvertTo-Json -Depth 10 -Compress
    }

    if (-not [string]::IsNullOrWhiteSpace($InFile)) {
        $parameters.InFile = $InFile
    }

    Invoke-RestMethod @parameters
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repoRoot

$insideWorkTree = (Invoke-Git -Arguments @('rev-parse', '--is-inside-work-tree')).Output
if ($insideWorkTree -ne 'true') {
    throw "Каталог не является Git-репозиторием: $repoRoot"
}

$headSha = (Invoke-Git -Arguments @('rev-parse', 'HEAD')).Output.ToLowerInvariant()
$expectedSha = $CommitSha.ToLowerInvariant()
if ($headSha -ne $expectedSha) {
    throw "HEAD ($headSha) не совпадает с подтверждённым commit SHA ($expectedSha)."
}

$workingTree = (Invoke-Git -Arguments @('status', '--porcelain=v1', '--untracked-files=all')).Output
if (-not [string]::IsNullOrWhiteSpace($workingTree)) {
    throw "Перед публикацией рабочее дерево должно быть чистым.`n$workingTree"
}

$secretPattern = '(AKIA[0-9A-Z]{16}|github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|sk-(proj-)?[A-Za-z0-9]{20,}|-----BEGIN (RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----|xox[baprs]-[A-Za-z0-9-]{10,}|AIza[0-9A-Za-z_-]{30,})'
$secretFindings = [Collections.Generic.List[string]]::new()
$commits = (Invoke-Git -Arguments @('rev-list', '--all')).Output -split "`n"
foreach ($commit in $commits) {
    if ([string]::IsNullOrWhiteSpace($commit)) {
        continue
    }

    $scan = Invoke-Git -Arguments @('grep', '-I', '-n', '-E', $secretPattern, $commit, '--') -AllowFailure
    if ($scan.ExitCode -gt 1) {
        throw "Не удалось проверить commit $commit на секреты."
    }

    if ($scan.ExitCode -eq 0) {
        foreach ($match in ($scan.Output -split "`n")) {
            $parts = $match -split ':', 4
            if ($parts.Count -ge 3) {
                $secretFindings.Add("$($commit.Substring(0, 12)):$($parts[1]):$($parts[2])")
            }
        }
    }
}

if ($secretFindings.Count -gt 0) {
    throw "В Git-истории найдены сигнатуры секретов (значения скрыты):`n$($secretFindings -join "`n")"
}

$sensitiveFilePattern = '(^|/)(\.env($|\.)|id_(rsa|dsa|ecdsa|ed25519)(\.pub)?$)|\.(pfx|p12|pem|key)$'
$sensitiveTrackedFiles = @((Invoke-Git -Arguments @('ls-tree', '-r', '--name-only', $expectedSha)).Output -split "`n" |
    Where-Object { $_ -match $sensitiveFilePattern })
if ($sensitiveTrackedFiles.Count -gt 0) {
    throw "В commit отслеживаются чувствительные файлы:`n$($sensitiveTrackedFiles -join "`n")"
}

$oversizedFiles = [Collections.Generic.List[string]]::new()
foreach ($trackedPath in ((Invoke-Git -Arguments @('ls-tree', '-r', '--name-only', $expectedSha)).Output -split "`n")) {
    if ([string]::IsNullOrWhiteSpace($trackedPath) -or -not (Test-Path -LiteralPath $trackedPath -PathType Leaf)) {
        continue
    }

    $trackedFile = Get-Item -LiteralPath $trackedPath
    if ($trackedFile.Length -ge 100MB) {
        $oversizedFiles.Add("$trackedPath ($([math]::Round($trackedFile.Length / 1MB, 2)) MiB)")
    }
}

if ($oversizedFiles.Count -gt 0) {
    throw "GitHub отклонит файлы размером 100 MiB и больше:`n$($oversizedFiles -join "`n")"
}

$resolvedAssetPath = (Resolve-Path -LiteralPath $AssetPath).Path
if ([IO.Path]::GetExtension($resolvedAssetPath) -ne '.zip') {
    throw "Release asset должен быть ZIP-архивом: $resolvedAssetPath"
}

$assetInfo = Get-Item -LiteralPath $resolvedAssetPath
if ($assetInfo.Length -le 0) {
    throw "Release asset пуст: $resolvedAssetPath"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($resolvedAssetPath)
try {
    $containsExecutable = $archive.Entries.Where({
        $_.FullName -match '(^|/)LernType\.exe$'
    }, 'First').Count -gt 0
}
finally {
    $archive.Dispose()
}

if (-not $containsExecutable) {
    throw 'В ZIP-архиве отсутствует LernType.exe.'
}

$assetSha256 = (Get-FileHash -LiteralPath $resolvedAssetPath -Algorithm SHA256).Hash.ToLowerInvariant()
$solutionPath = Join-Path $repoRoot 'LernType.sln'
if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "Solution-файл не найден: $solutionPath"
}

$bundledDotNet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $bundledDotNet -PathType Leaf) {
    $script:DotNetPath = $bundledDotNet
}
else {
    $dotnetCommand = Get-Command dotnet -ErrorAction Stop
    $script:DotNetPath = $dotnetCommand.Source
}

if (-not $SkipQa) {
    Write-Host 'QA: Release build с предупреждениями как ошибками...'
    Invoke-DotNet -Arguments @('build', $solutionPath, '-c', 'Release', '--no-restore', '-warnaserror')
    Write-Host 'QA: полный набор Release-тестов...'
    Invoke-DotNet -Arguments @('test', $solutionPath, '-c', 'Release', '--no-build')
}

$env:GCM_INTERACTIVE = 'Never'
$credentialQuery = @(
    'protocol=https'
    'host=github.com'
    "username=$Owner"
    ''
) -join "`n"
$credentialLines = @($credentialQuery | git credential fill 2>$null)
$credentialUser = ($credentialLines | Where-Object { $_ -like 'username=*' } | Select-Object -First 1)
$credentialSecret = ($credentialLines | Where-Object { $_ -like 'password=*' } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($credentialUser) -or [string]::IsNullOrWhiteSpace($credentialSecret)) {
    throw 'Git Credential Manager не вернул учётные данные GitHub.'
}

$authenticatedUser = $credentialUser.Substring('username='.Length)
$token = $credentialSecret.Substring('password='.Length)
$script:GitHubHeaders = @{
    Authorization = "Bearer $token"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'LernType-release-publisher'
}

try {
    $profile = Invoke-GitHubApi -Method Get -Uri 'https://api.github.com/user'
    if ($profile.login -cne $Owner) {
        throw "GitHub credential принадлежит '$($profile.login)', ожидался '$Owner'."
    }

    $repositoryUri = "https://api.github.com/repos/$Owner/$Repository"
    $remoteRepository = $null
    try {
        $remoteRepository = Invoke-GitHubApi -Method Get -Uri $repositoryUri
    }
    catch {
        if ((Get-HttpStatusCode -ErrorRecord $_) -ne 404) {
            throw
        }
    }

    if ($null -ne $remoteRepository -and $remoteRepository.private) {
        throw "Репозиторий $Owner/$Repository уже существует, но не является публичным."
    }

    $actionSummary = "создать при необходимости PUBLIC-репозиторий, отправить $expectedSha в $Branch и опубликовать $Tag с $($assetInfo.Name)"
    if (-not $PSCmdlet.ShouldProcess("https://github.com/$Owner/$Repository", $actionSummary)) {
        return
    }

    if ($null -eq $remoteRepository) {
        $remoteRepository = Invoke-GitHubApi -Method Post -Uri 'https://api.github.com/user/repos' -Body @{
            name = $Repository
            description = $RepositoryDescription
            private = $false
            visibility = 'public'
            has_issues = $true
            has_projects = $false
            has_wiki = $false
            auto_init = $false
        }
        Write-Host "Создан PUBLIC-репозиторий: $($remoteRepository.html_url)"
    }

    $currentBranch = (Invoke-Git -Arguments @('branch', '--show-current')).Output
    if ([string]::IsNullOrWhiteSpace($currentBranch)) {
        throw 'Публикация из detached HEAD не поддерживается.'
    }

    if ($currentBranch -cne $Branch) {
        $existingTargetBranch = Invoke-Git -Arguments @('show-ref', '--verify', '--quiet', "refs/heads/$Branch") -AllowFailure
        if ($existingTargetBranch.ExitCode -eq 0) {
            throw "Локальная ветка '$Branch' уже существует; автоматическое переименование '$currentBranch' остановлено."
        }

        Invoke-Git -Arguments @('branch', '-m', $Branch) | Out-Null
    }

    $expectedHttpsRemote = "https://github.com/$Owner/$Repository.git"
    $expectedSshRemote = "git@github.com:$Owner/$Repository.git"
    $currentRemote = Invoke-Git -Arguments @('remote', 'get-url', $RemoteName) -AllowFailure
    if ($currentRemote.ExitCode -ne 0) {
        Invoke-Git -Arguments @('remote', 'add', $RemoteName, $expectedHttpsRemote) | Out-Null
    }
    elseif ($currentRemote.Output -notin @($expectedHttpsRemote, $expectedSshRemote)) {
        throw "Remote '$RemoteName' уже указывает на '$($currentRemote.Output)', ожидался '$expectedHttpsRemote'."
    }

    Invoke-Git -Arguments @('push', '--set-upstream', $RemoteName, $Branch) | Out-Null
    Invoke-GitHubApi -Method Patch -Uri $repositoryUri -Body @{ default_branch = $Branch } | Out-Null

    $encodedTag = [Uri]::EscapeDataString($Tag)
    $release = $null
    $releaseAlreadyExisted = $false
    try {
        $release = Invoke-GitHubApi -Method Get -Uri "$repositoryUri/releases/tags/$encodedTag"
        $releaseAlreadyExisted = $true
    }
    catch {
        if ((Get-HttpStatusCode -ErrorRecord $_) -ne 404) {
            throw
        }
    }

    if ($releaseAlreadyExisted) {
        $tagReference = Invoke-GitHubApi -Method Get -Uri "$repositoryUri/git/ref/tags/$encodedTag"
        $tagObject = $tagReference.object
        if ($tagObject.type -eq 'tag') {
            $annotatedTag = Invoke-GitHubApi -Method Get -Uri "$repositoryUri/git/tags/$($tagObject.sha)"
            $tagObject = $annotatedTag.object
        }

        if ($tagObject.type -ne 'commit' -or $tagObject.sha.ToLowerInvariant() -ne $expectedSha) {
            throw "Существующий tag $Tag не указывает на подтверждённый commit $expectedSha."
        }
    }

    $notes = if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesPath)) {
        Get-Content -LiteralPath (Resolve-Path -LiteralPath $ReleaseNotesPath).Path -Raw
    }
    else {
        @"
Windows x64 release of LernType.

1. Download and extract ``$($assetInfo.Name)``.
2. Run ``LernType.exe``.

SHA-256: ``$assetSha256``
"@
    }

    if ($null -eq $release) {
        $release = Invoke-GitHubApi -Method Post -Uri "$repositoryUri/releases" -Body @{
            tag_name = $Tag
            target_commitish = $expectedSha
            name = $ReleaseName
            body = $notes
            draft = $false
            prerelease = $false
            make_latest = 'true'
        }
    }

    $existingAsset = @($release.assets | Where-Object { $_.name -ceq $assetInfo.Name } | Select-Object -First 1)
    if ($existingAsset.Count -gt 0) {
        $expectedDigest = "sha256:$assetSha256"
        $digestProperty = $existingAsset[0].PSObject.Properties['digest']
        $remoteDigest = if ($null -ne $digestProperty) { [string]$digestProperty.Value } else { '' }
        if (-not [string]::IsNullOrWhiteSpace($remoteDigest) -and
            $remoteDigest -ceq $expectedDigest) {
            $uploadedAsset = $existingAsset[0]
        }
        else {
            throw "В release $Tag уже существует asset '$($assetInfo.Name)' с неподтверждённым содержимым."
        }
    }
    else {
        $uploadBase = $release.upload_url -replace '\{\?name,label\}$', ''
        $encodedAssetName = [Uri]::EscapeDataString($assetInfo.Name)
        $uploadUri = "${uploadBase}?name=$encodedAssetName"
        $uploadedAsset = Invoke-GitHubApi -Method Post -Uri $uploadUri -InFile $resolvedAssetPath -ContentType 'application/zip'
    }

    [pscustomobject]@{
        Repository = $remoteRepository.html_url
        Release = $release.html_url
        Download = $uploadedAsset.browser_download_url
        Asset = $assetInfo.Name
        Sha256 = $assetSha256
        Commit = $expectedSha
    } | Format-List
}
finally {
    if ($null -ne $script:GitHubHeaders) {
        $script:GitHubHeaders.Clear()
    }

    $token = $null
    $credentialSecret = $null
    $credentialLines = $null
}
