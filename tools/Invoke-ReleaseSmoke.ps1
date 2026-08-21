[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PublishDirectory,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\verification\smoke'),
    [ValidateRange(5, 120)] [int] $TimeoutSeconds = 25,
    [ValidateRange(720, 3840)] [int] $WindowWidth = 1180,
    [ValidateRange(520, 2160)] [int] $WindowHeight = 760,
    [string] $InvokeAutomationName,
    [string] $ExpectedAutomationName
)

$ErrorActionPreference = 'Stop'
$publish = (Resolve-Path $PublishDirectory).Path
$executable = Join-Path $publish 'LernType.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'PublishDirectory does not contain LernType.exe.'
}
if (-not [string]::IsNullOrWhiteSpace($InvokeAutomationName) -and
    [string]::IsNullOrWhiteSpace($ExpectedAutomationName)) {
    throw 'ExpectedAutomationName is required when InvokeAutomationName is supplied.'
}
$expectedLandmarkName = if ([string]::IsNullOrWhiteSpace($ExpectedAutomationName)) {
    'Главный экран LernType'
} else {
    $ExpectedAutomationName
}
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
$repositoryPrefix = $repositoryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($output -eq $repositoryRoot -or
    -not $output.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Smoke output must be a child directory inside the repository.'
}
New-Item -ItemType Directory -Force -Path $output | Out-Null
$dataRoot = Join-Path $output "isolated-data-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
$screenshot = Join-Path $output 'LernType-screen.png'
$recordPath = Join-Path $output 'smoke-result.json'

if (-not ('LernTypeSmoke.NativeMethods' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace LernTypeSmoke {
    public static class NativeMethods {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);
        [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);
    }
}
'@
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Find-AutomationElementByName {
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)][string]$Name
    )

    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $elements = $Root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
    foreach ($element in $elements) {
        if (Test-AutomationElementVisible $element) { return $element }
    }
    return $null
}

function Test-AutomationElementVisible {
    param($Element)

    if (-not $Element) { return $false }
    try {
        $current = $Element.Current
        return -not $current.IsOffscreen -and
            $current.BoundingRectangle.Width -gt 0 -and
            $current.BoundingRectangle.Height -gt 0
    }
    catch [Windows.Automation.ElementNotAvailableException] {
        return $false
    }
}

function Test-VisibleAutomationName {
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)][string]$Name
    )

    Test-AutomationElementVisible (Find-AutomationElementByName -Root $Root -Name $Name)
}

function Test-VisibleAutomationTextName {
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)][string]$Name
    )

    $condition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::NameProperty,
            $Name),
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Text))
    $elements = $Root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
    foreach ($element in $elements) {
        if (Test-AutomationElementVisible $element) { return $true }
    }
    return $false
}

function Test-VisibleTechnicalCode {
    param([Parameter(Mandatory)]$Root)

    $elements = $Root.FindAll(
        [Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $elements) {
        if (-not (Test-AutomationElementVisible $element)) { continue }
        try {
            if ($element.Current.Name -match '^\s*Код:\s*\S+') { return $true }
        }
        catch [Windows.Automation.ElementNotAvailableException] {
            continue
        }
    }
    return $false
}

function Wait-SmokeUiState {
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)][string]$LandmarkName,
        [Parameter(Mandatory)][datetime]$Deadline
    )

    $shellError = $false
    $technicalCode = $false
    $landmark = $false
    $loading = $false
    do {
        $shellError = Test-VisibleAutomationName -Root $Root -Name 'Ошибка приложения'
        $technicalCode = Test-VisibleTechnicalCode -Root $Root
        $landmark = Test-VisibleAutomationName -Root $Root -Name $LandmarkName
        $loading = Test-VisibleAutomationName -Root $Root -Name 'Загрузка раздела'
        if ($shellError -or $technicalCode -or ($landmark -and -not $loading)) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $Deadline)

    [pscustomobject]@{
        ShellErrorVisible = [bool]$shellError
        TechnicalCodeVisible = [bool]$technicalCode
        LandmarkVisible = [bool]$landmark
        LoadingVisible = [bool]$loading
    }
}

$startedAt = [DateTimeOffset]::UtcNow
$timer = [Diagnostics.Stopwatch]::StartNew()
$process = $null
$result = [ordered]@{
    schemaVersion = 2
    startedAtUtc = $startedAt.ToString('O')
    executable = 'LernType.exe'
    windowVisible = $false
    windowTitle = $null
    requestedWindowSize = "$WindowWidth`x$WindowHeight"
    windowSize = $null
    usableShellMilliseconds = $null
    screenshot = $null
    invokedAutomationName = $null
    expectedAutomationName = $expectedLandmarkName
    expectedLandmarkVisible = $false
    shellErrorVisible = $false
    technicalCodeVisible = $false
    loadingVisible = $false
    layoutMode = $null
    navigationLabelVisible = $null
    layoutVerificationPassed = $false
    uiVerificationPassed = $false
    gracefulExit = $false
    exitCode = $null
    failure = $null
}
try {
    $info = [Diagnostics.ProcessStartInfo]::new($executable)
    $info.WorkingDirectory = $publish
    $info.UseShellExecute = $false
    $info.Environment['LERNTYPE_DATA_ROOT'] = $dataRoot
    $process = [Diagnostics.Process]::Start($info)
    if (-not $process) { throw 'The LernType process did not start.' }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) {
            throw "LernType exited before its shell appeared (exit $($process.ExitCode))."
        }
    } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)

    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw "No LernType window appeared within $TimeoutSeconds seconds."
    }
    $timer.Stop()
    $result.windowVisible = $true
    $result.windowTitle = $process.MainWindowTitle
    $result.usableShellMilliseconds = $timer.ElapsedMilliseconds

    [LernTypeSmoke.NativeMethods]::MoveWindow($process.MainWindowHandle, 80, 80, $WindowWidth, $WindowHeight, $true) | Out-Null
    [LernTypeSmoke.NativeMethods]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 700
    $rootElement = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    if (-not $rootElement) { throw 'UI Automation could not attach to the LernType window.' }
    $pendingUiFailure = $null

    if (-not [string]::IsNullOrWhiteSpace($InvokeAutomationName)) {
        $initialState = Wait-SmokeUiState -Root $rootElement -LandmarkName 'Главный экран LernType' -Deadline ([DateTime]::UtcNow.AddSeconds($TimeoutSeconds))
        if ($initialState.ShellErrorVisible -or $initialState.TechnicalCodeVisible) {
            $pendingUiFailure = 'The initial shell exposed a visible application error or technical code.'
        }
        elseif (-not $initialState.LandmarkVisible -or $initialState.LoadingVisible) {
            $pendingUiFailure = 'The initial shell did not reach its ready landmark before the timeout.'
        }

        if (-not $pendingUiFailure) {
            $action = Find-AutomationElementByName -Root $rootElement -Name $InvokeAutomationName
            if (-not (Test-AutomationElementVisible $action)) {
                throw "Visible automation action was not found: $InvokeAutomationName"
            }
            $pattern = $action.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
            ([Windows.Automation.InvokePattern]$pattern).Invoke()
            $result.invokedAutomationName = $InvokeAutomationName
        }
    }

    $uiState = if ($pendingUiFailure) {
        [pscustomobject]@{
            ShellErrorVisible = $initialState.ShellErrorVisible
            TechnicalCodeVisible = $initialState.TechnicalCodeVisible
            LandmarkVisible = Test-VisibleAutomationName -Root $rootElement -Name $expectedLandmarkName
            LoadingVisible = $initialState.LoadingVisible
        }
    } else {
        Wait-SmokeUiState -Root $rootElement -LandmarkName $expectedLandmarkName -Deadline ([DateTime]::UtcNow.AddSeconds($TimeoutSeconds))
    }
    $result.expectedLandmarkVisible = $uiState.LandmarkVisible
    $result.shellErrorVisible = $uiState.ShellErrorVisible
    $result.technicalCodeVisible = $uiState.TechnicalCodeVisible
    $result.loadingVisible = $uiState.LoadingVisible
    if (-not $pendingUiFailure -and ($uiState.ShellErrorVisible -or $uiState.TechnicalCodeVisible)) {
        $pendingUiFailure = 'The target screen exposed a visible application error or technical code.'
    }
    if (-not $pendingUiFailure -and (-not $uiState.LandmarkVisible -or $uiState.LoadingVisible)) {
        $pendingUiFailure = "Expected visible UI Automation landmark was not ready: $expectedLandmarkName"
    }
    $rect = [LernTypeSmoke.NativeMethods+RECT]::new()
    if ([LernTypeSmoke.NativeMethods]::GetWindowRect($process.MainWindowHandle, [ref]$rect)) {
        $width = [Math]::Max(1, $rect.Right - $rect.Left)
        $height = [Math]::Max(1, $rect.Bottom - $rect.Top)
        $result.windowSize = "$width`x$height"
        $result.layoutMode = if ($width -ge 1060) { 'Wide' } else { 'Compact' }
        $result.navigationLabelVisible = Test-VisibleAutomationTextName -Root $rootElement -Name 'Путь Pre-A1–C2'
        $result.layoutVerificationPassed = if ($result.layoutMode -eq 'Wide') {
            [bool]$result.navigationLabelVisible
        } else {
            -not [bool]$result.navigationLabelVisible
        }
        if (-not $pendingUiFailure -and -not $result.layoutVerificationPassed) {
            $pendingUiFailure = "Navigation layout did not switch to the expected $($result.layoutMode) mode."
        }
        Add-Type -AssemblyName System.Drawing.Common
        $bitmap = [Drawing.Bitmap]::new($width, $height)
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $deviceContext = $graphics.GetHdc()
                try {
                    $printed = [LernTypeSmoke.NativeMethods]::PrintWindow($process.MainWindowHandle, $deviceContext, 2)
                } finally {
                    $graphics.ReleaseHdc($deviceContext)
                }
                if (-not $printed) {
                    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, [Drawing.Size]::new($width, $height))
                }
            } finally {
                $graphics.Dispose()
            }
            $bitmap.Save($screenshot, [Drawing.Imaging.ImageFormat]::Png)
            $result.screenshot = 'LernType-screen.png'
        } finally {
            $bitmap.Dispose()
        }
    }

    if (-not $pendingUiFailure -and (-not $result.windowSize -or -not $result.screenshot)) {
        $pendingUiFailure = 'Window bounds or screenshot capture was not produced.'
    }
    $result.uiVerificationPassed = -not [bool]$pendingUiFailure
    if ($pendingUiFailure) {
        throw $pendingUiFailure
    }

    if (-not $process.CloseMainWindow()) {
        throw 'The main window did not accept a graceful close request.'
    }
    if (-not $process.WaitForExit(10000)) {
        throw 'LernType did not exit within 10 seconds after closing its window.'
    }
    $result.exitCode = $process.ExitCode
    $result.gracefulExit = $process.ExitCode -eq 0
    if (-not $result.gracefulExit) { throw "LernType returned exit code $($process.ExitCode)." }
}
catch {
    $result.failure = $_.Exception.Message
    throw
}
finally {
    if ($process -and -not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit()
    }
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $recordPath -Encoding utf8NoBOM
    if (Test-Path -LiteralPath $dataRoot) {
        $resolvedData = [IO.Path]::GetFullPath($dataRoot)
        $outputPrefix = $output.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if ($resolvedData.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedData -Recurse -Force
        }
    }
}

[pscustomobject]$result
