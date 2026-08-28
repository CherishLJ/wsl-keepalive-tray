param(
    [string]$ReportPath = (Join-Path $PSScriptRoot '..\build\recovery-test.txt'),
    [string]$Distro = 'Ubuntu-24.04',
    [int]$MinimumContainers = 0,
    [string[]]$HealthUrls = @()
)

$ErrorActionPreference = 'Stop'
$distro = $Distro
$appName = 'WSLKeepAliveTray'
$logPath = Join-Path $env:LOCALAPPDATA 'WSLKeepAliveTray\tray.log'

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class RecoveryTestNative
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
'@

function Get-VisibleConsoleWindows {
    $results = [System.Collections.Generic.List[object]]::new()
    $names = @('cmd', 'conhost', 'OpenConsole', 'powershell', 'pwsh', 'wsl')
    $callback = [RecoveryTestNative+EnumWindowsProc]{
        param([IntPtr]$hWnd, [IntPtr]$lParam)

        if (-not [RecoveryTestNative]::IsWindowVisible($hWnd)) {
            return $true
        }
        $processId = [uint32]0
        [void][RecoveryTestNative]::GetWindowThreadProcessId($hWnd, [ref]$processId)
        try {
            $process = Get-Process -Id $processId -ErrorAction Stop
            if ($names -contains $process.ProcessName) {
                $title = [System.Text.StringBuilder]::new(256)
                [void][RecoveryTestNative]::GetWindowText($hWnd, $title, $title.Capacity)
                $results.Add([pscustomobject]@{
                    Handle = $hWnd.ToInt64()
                    Process = $process.ProcessName
                    Title = $title.ToString()
                })
            }
        }
        catch {
        }
        return $true
    }
    [void][RecoveryTestNative]::EnumWindows($callback, [IntPtr]::Zero)
    return $results
}

function Hide-Dashboard {
    $callback = [RecoveryTestNative+EnumWindowsProc]{
        param([IntPtr]$hWnd, [IntPtr]$lParam)

        $title = [System.Text.StringBuilder]::new(256)
        [void][RecoveryTestNative]::GetWindowText($hWnd, $title, $title.Capacity)
        if ($title.ToString() -eq 'WSL 运行监控') {
            [void][RecoveryTestNative]::PostMessage($hWnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        }
        return $true
    }
    [void][RecoveryTestNative]::EnumWindows($callback, [IntPtr]::Zero)
}

function Get-AgentStartCount {
    if (-not (Test-Path -LiteralPath $logPath)) { return 0 }
    return @(Select-String -LiteralPath $logPath -SimpleMatch '遥测 agent 已启动').Count
}

function Get-HttpStatus([string]$url) {
    try {
        return [int](Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10).StatusCode
    }
    catch {
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
            return [int]$_.Exception.Response.StatusCode
        }
        return 0
    }
}

$report = [System.Collections.Generic.List[string]]::new()
$failures = 0
$appBefore = Get-Process -Name $appName -ErrorAction Stop | Select-Object -First 1
$startCountBefore = Get-AgentStartCount
Hide-Dashboard
Start-Sleep -Milliseconds 500

$baselineHandles = @{}
foreach ($window in @(Get-VisibleConsoleWindows)) {
    $baselineHandles[[string]$window.Handle] = $true
}
$newVisibleConsoles = [System.Collections.Generic.Dictionary[string, object]]::new()

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'wsl.exe')
$startInfo.Arguments = '--terminate ' + $distro
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$termination = [System.Diagnostics.Process]::Start($startInfo)

$timer = [System.Diagnostics.Stopwatch]::StartNew()
$agentRestartedAt = $null
while ($timer.Elapsed.TotalSeconds -lt 20) {
    foreach ($window in @(Get-VisibleConsoleWindows)) {
        $key = [string]$window.Handle
        if (-not $baselineHandles.ContainsKey($key) -and -not $newVisibleConsoles.ContainsKey($key)) {
            $newVisibleConsoles.Add($key, $window)
        }
    }
    if ($null -eq $agentRestartedAt -and (Get-AgentStartCount) -gt $startCountBefore) {
        $agentRestartedAt = $timer.Elapsed.TotalSeconds
    }
    if ($null -ne $agentRestartedAt -and $timer.Elapsed.TotalSeconds -ge ($agentRestartedAt + 8)) {
        break
    }
    Start-Sleep -Milliseconds 100
}
$timer.Stop()
$termination.WaitForExit(30000) | Out-Null

$appAfter = Get-Process -Name $appName -ErrorAction Stop | Select-Object -First 1
$timerState = (& wsl.exe -d $distro -u root --exec systemctl is-active wsl-tray-watchdog.timer 2>$null).Trim()
$dockerState = (& wsl.exe -d $distro -u root --exec systemctl is-active docker 2>$null).Trim()
$sshState = (& wsl.exe -d $distro -u root --exec systemctl is-active ssh 2>$null).Trim()
$containers = @(& wsl.exe -d $distro --exec docker ps --format '{{.Names}}|{{.Status}}' 2>$null)
$agentProcesses = @(& wsl.exe -d $distro --exec pgrep -af 'wsl-tray-agent' 2>$null)

function Add-Check([bool]$passed, [string]$name, [string]$detail) {
    $prefix = if ($passed) { 'PASS ' } else { 'FAIL ' }
    $script:report.Add($prefix + $name + ' | ' + $detail)
    if (-not $passed) { $script:failures++ }
}

Add-Check ($termination.ExitCode -eq 0) 'terminate command' ('exit=' + $termination.ExitCode)
Add-Check ($null -ne $agentRestartedAt) 'agent auto restart' ('seconds=' + $(if ($null -eq $agentRestartedAt) { 'timeout' } else { $agentRestartedAt.ToString('0.0') }))
Add-Check ($appAfter.Id -eq $appBefore.Id) 'tray process survived' ('pid=' + $appAfter.Id)
Add-Check ($newVisibleConsoles.Count -eq 0) 'no new visible console window' ('count=' + $newVisibleConsoles.Count)
Add-Check ($timerState -eq 'active') 'watchdog timer' $timerState
Add-Check ($dockerState -eq 'active') 'docker service' $dockerState
Add-Check ($sshState -eq 'active') 'ssh service' $sshState
Add-Check ($containers.Count -ge $MinimumContainers) 'docker containers' ("minimum=$MinimumContainers; " + ($containers -join '; '))
Add-Check ($agentProcesses.Count -ge 1) 'telemetry process' (($agentProcesses -join '; '))
foreach ($url in $HealthUrls) {
    $status = Get-HttpStatus $url
    Add-Check ($status -eq 200) 'HTTP health endpoint' ("url=$url; status=$status")
}

foreach ($window in $newVisibleConsoles.Values) {
    $report.Add('VISIBLE_CONSOLE ' + $window.Process + ' | ' + $window.Title + ' | hwnd=' + $window.Handle)
}
$report.Add($(if ($failures -eq 0) { 'RECOVERY_TEST_PASS' } else { 'RECOVERY_TEST_FAIL count=' + $failures }))

$resolvedReport = [System.IO.Path]::GetFullPath($ReportPath)
[System.IO.Directory]::CreateDirectory((Split-Path -Parent $resolvedReport)) | Out-Null
[System.IO.File]::WriteAllLines($resolvedReport, $report, [System.Text.UTF8Encoding]::new($false))
$report
if ($failures -ne 0) { exit 1 }
