[CmdletBinding()]
param(
    [string]$Distro = 'Ubuntu-24.04',
    [string]$InstallDirectory = "$env:LOCALAPPDATA\Programs\WSLKeepAliveTray"
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildDirectory = Join-Path $projectRoot 'build'
$executableSource = Join-Path $buildDirectory 'WSLKeepAliveTray.exe'
$executableTarget = Join-Path $InstallDirectory 'WSLKeepAliveTray.exe'
$backupDirectory = Join-Path $InstallDirectory 'backup'
$taskName = "WSL-$Distro-KeepAlive"
$statePath = Join-Path $InstallDirectory 'install-state.json'
$previousState = if (Test-Path -LiteralPath $statePath) {
    Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
} else {
    $null
}

if (-not (Test-Path -LiteralPath $executableSource)) {
    & (Join-Path $PSScriptRoot 'build.ps1')
}

function Convert-ToWslPath {
    param([Parameter(Mandatory = $true)][string]$WindowsPath)
    $fullPath = [System.IO.Path]::GetFullPath($WindowsPath)
    if ($fullPath -notmatch '^(?<drive>[A-Za-z]):\\(?<tail>.*)$') {
        throw "Only fixed-drive paths can be converted for WSL: $fullPath"
    }
    $drive = $Matches.drive.ToLowerInvariant()
    $tail = $Matches.tail.Replace('\', '/')
    return "/mnt/$drive/$tail"
}

New-Item -ItemType Directory -Force -Path $InstallDirectory, $backupDirectory | Out-Null

$existingTask = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
$taskExisted = if ($previousState) { [bool]$previousState.taskExisted } else { [bool]$existingTask }
$taskWasEnabled = if ($previousState) { [bool]$previousState.taskWasEnabled } else { $existingTask -and $existingTask.State -ne 'Disabled' }
$taskBackupPath = Join-Path $backupDirectory "$taskName.xml"
if ($existingTask -and -not (Test-Path -LiteralPath $taskBackupPath)) {
    Export-ScheduledTask -TaskName $taskName -TaskPath '\' | Set-Content -LiteralPath (Join-Path $backupDirectory "$taskName.xml") -Encoding Unicode
}

$state = [ordered]@{
    installedAt = (Get-Date).ToString('o')
    distro = $Distro
    taskName = $taskName
    taskExisted = $taskExisted
    taskWasEnabled = $taskWasEnabled
    installDirectory = $InstallDirectory
}
$state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8

$agentSource = Convert-ToWslPath (Join-Path $projectRoot 'linux\wsl-tray-agent')
$watchdogSource = Convert-ToWslPath (Join-Path $projectRoot 'linux\wsl-tray-watchdog')
$watchdogConfigSource = Convert-ToWslPath (Join-Path $projectRoot 'linux\wsl-keepalive-tray.default')
$serviceSource = Convert-ToWslPath (Join-Path $projectRoot 'linux\wsl-tray-watchdog.service')
$timerSource = Convert-ToWslPath (Join-Path $projectRoot 'linux\wsl-tray-watchdog.timer')
$binfmtOverrideSource = Convert-ToWslPath (Join-Path $projectRoot 'linux\systemd-binfmt-wsl.conf')

wsl.exe -d $Distro -u root -- sh -lc 'if [ -e /usr/local/sbin/wsl-tray-agent ]; then cp -a /usr/local/sbin/wsl-tray-agent /usr/local/sbin/wsl-tray-agent.pre-tray-backup; fi'
if ($LASTEXITCODE -ne 0) { throw 'Failed to back up the previous telemetry agent.' }
wsl.exe -d $Distro -u root -- install -o root -g root -m 0755 $agentSource /usr/local/sbin/wsl-tray-agent
if ($LASTEXITCODE -ne 0) { throw 'Failed to install the telemetry agent.' }
wsl.exe -d $Distro -u root -- sh -lc 'target=/usr/local/sbin/wsl-tray-watchdog; marker=/usr/local/sbin/.wsl-tray-watchdog-owned; if [ -e "$target" ] && [ ! -e "$marker" ]; then cp -a "$target" "$target.pre-tray-backup"; fi'
if ($LASTEXITCODE -ne 0) { throw 'Failed to back up the previous watchdog.' }
wsl.exe -d $Distro -u root -- install -o root -g root -m 0755 $watchdogSource /usr/local/sbin/wsl-tray-watchdog
if ($LASTEXITCODE -ne 0) { throw 'Failed to install the watchdog.' }
wsl.exe -d $Distro -u root -- touch /usr/local/sbin/.wsl-tray-watchdog-owned
wsl.exe -d $Distro -u root -- test -e /etc/default/wsl-keepalive-tray
if ($LASTEXITCODE -ne 0) {
    wsl.exe -d $Distro -u root -- install -o root -g root -m 0644 $watchdogConfigSource /etc/default/wsl-keepalive-tray
    if ($LASTEXITCODE -ne 0) { throw 'Failed to install the watchdog configuration.' }
}
wsl.exe -d $Distro -u root -- install -o root -g root -m 0644 $serviceSource /etc/systemd/system/wsl-tray-watchdog.service
if ($LASTEXITCODE -ne 0) { throw 'Failed to install the watchdog service.' }
wsl.exe -d $Distro -u root -- install -o root -g root -m 0644 $timerSource /etc/systemd/system/wsl-tray-watchdog.timer
if ($LASTEXITCODE -ne 0) { throw 'Failed to install the watchdog timer.' }
wsl.exe -d $Distro -u root -- mkdir -p /etc/systemd/system/systemd-binfmt.service.d
wsl.exe -d $Distro -u root -- sh -lc 'dir=/etc/systemd/system/systemd-binfmt.service.d; target="$dir/wsl-keepalive-tray.conf"; marker="$dir/.wsl-keepalive-tray-owned"; if [ -e "$target" ] && [ ! -e "$marker" ]; then cp -a "$target" "$target.pre-tray-backup"; fi'
if ($LASTEXITCODE -ne 0) { throw 'Failed to back up the systemd-binfmt compatibility override.' }
wsl.exe -d $Distro -u root -- install -o root -g root -m 0644 $binfmtOverrideSource /etc/systemd/system/systemd-binfmt.service.d/wsl-keepalive-tray.conf
if ($LASTEXITCODE -ne 0) { throw 'Failed to install the systemd-binfmt WSL compatibility override.' }
wsl.exe -d $Distro -u root -- touch /etc/systemd/system/systemd-binfmt.service.d/.wsl-keepalive-tray-owned
wsl.exe -d $Distro -u root -- systemctl daemon-reload
wsl.exe -d $Distro -u root -- systemctl reset-failed systemd-binfmt.service
wsl.exe -d $Distro -u root -- systemctl start systemd-binfmt.service
if ($LASTEXITCODE -ne 0) { throw 'The systemd-binfmt WSL compatibility override failed validation.' }
wsl.exe -d $Distro -u root -- systemctl enable --now wsl-tray-watchdog.timer
wsl.exe -d $Distro -u root -- systemctl start wsl-tray-watchdog.service
if ($LASTEXITCODE -ne 0) { throw 'The WSL watchdog health check failed.' }

$running = Get-Process -Name 'WSLKeepAliveTray' -ErrorAction SilentlyContinue
if ($running) {
    $currentExecutable = $running | Select-Object -First 1 -ExpandProperty Path
    if ($currentExecutable -and (Test-Path -LiteralPath $currentExecutable)) {
        Start-Process -FilePath $currentExecutable -ArgumentList '--quit' -WindowStyle Hidden
    }
    for ($attempt = 0; $attempt -lt 20 -and (Get-Process -Name 'WSLKeepAliveTray' -ErrorAction SilentlyContinue); $attempt++) {
        Start-Sleep -Milliseconds 250
    }
    Get-Process -Name 'WSLKeepAliveTray' -ErrorAction SilentlyContinue | Stop-Process -Force
}

Copy-Item -LiteralPath $executableSource -Destination $executableTarget -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\app.ico') -Destination (Join-Path $InstallDirectory 'app.ico') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $InstallDirectory 'README.md') -Force

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-Item -Path $runKey -Force | Out-Null
Set-ItemProperty -Path $runKey -Name 'WSLKeepAliveTray' -Value ('"' + $executableTarget + '" --distro "' + $Distro.Replace('"', '') + '"')
Start-Process -FilePath $executableTarget -ArgumentList @('--distro', $Distro) -WindowStyle Hidden

$appReady = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    if (Get-Process -Name 'WSLKeepAliveTray' -ErrorAction SilentlyContinue) {
        $agentProcess = wsl.exe -d $Distro -- pgrep -af '/usr/local/sbin/wsl-tray-agent' 2>$null
        if ($LASTEXITCODE -eq 0 -and $agentProcess) {
            $appReady = $true
            break
        }
    }
    Start-Sleep -Seconds 1
}
if (-not $appReady) { throw 'The tray application did not establish its telemetry keepalive session.' }

$timerState = wsl.exe -d $Distro -- systemctl is-active wsl-tray-watchdog.timer
if (($timerState | Select-Object -First 1).Trim() -ne 'active') { throw 'The systemd watchdog timer is not active.' }
$containerRows = wsl.exe -d $Distro -- docker ps --format '{{.Names}}={{.Status}}'
if ($LASTEXITCODE -ne 0) { throw 'Docker verification failed.' }

if ($existingTask) {
    Disable-ScheduledTask -TaskName $taskName -TaskPath '\' | Out-Null
}

Write-Output "INSTALL_OK executable=$executableTarget"
Write-Output "TIMER=$timerState"
$containerRows
if ($existingTask) { Write-Output "LEGACY_TASK=Disabled (wasEnabled=$taskWasEnabled)" }
