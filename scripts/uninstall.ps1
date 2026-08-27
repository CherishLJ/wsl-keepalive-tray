[CmdletBinding()]
param(
    [string]$InstallDirectory = "$env:LOCALAPPDATA\Programs\WSLKeepAliveTray"
)

$ErrorActionPreference = 'Stop'
$expectedDirectory = [System.IO.Path]::GetFullPath("$env:LOCALAPPDATA\Programs\WSLKeepAliveTray")
$resolvedDirectory = [System.IO.Path]::GetFullPath($InstallDirectory)
if ($resolvedDirectory.TrimEnd('\') -ine $expectedDirectory.TrimEnd('\')) {
    throw "Refusing to uninstall from unexpected directory: $resolvedDirectory"
}

$statePath = Join-Path $resolvedDirectory 'install-state.json'
$state = if (Test-Path -LiteralPath $statePath) { Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json } else { $null }
$distro = if ($state -and $state.distro) { [string]$state.distro } else { 'Ubuntu-24.04' }
$executable = Join-Path $resolvedDirectory 'WSLKeepAliveTray.exe'

if (Test-Path -LiteralPath $executable) {
    Start-Process -FilePath $executable -ArgumentList '--quit' -WindowStyle Hidden
    for ($attempt = 0; $attempt -lt 20 -and (Get-Process -Name 'WSLKeepAliveTray' -ErrorAction SilentlyContinue); $attempt++) {
        Start-Sleep -Milliseconds 250
    }
}
Get-Process -Name 'WSLKeepAliveTray' -ErrorAction SilentlyContinue | Stop-Process -Force
Unregister-ScheduledTask -TaskName 'WSLKeepAliveTray-Startup' -Confirm:$false -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'WSLKeepAliveTray' -ErrorAction SilentlyContinue

wsl.exe -d $distro -u root -- systemctl disable --now wsl-tray-watchdog.timer 2>$null
wsl.exe -d $distro -u root -- rm -f /etc/systemd/system/wsl-tray-watchdog.timer /etc/systemd/system/wsl-tray-watchdog.service /usr/local/sbin/wsl-tray-agent
wsl.exe -d $distro -u root -- sh -lc 'if [ -e /usr/local/sbin/wsl-tray-agent.pre-tray-backup ]; then mv /usr/local/sbin/wsl-tray-agent.pre-tray-backup /usr/local/sbin/wsl-tray-agent; fi'
wsl.exe -d $distro -u root -- sh -lc 'target=/usr/local/sbin/wsl-tray-watchdog; rm -f "$target" /usr/local/sbin/.wsl-tray-watchdog-owned; if [ -e "$target.pre-tray-backup" ]; then mv "$target.pre-tray-backup" "$target"; fi'
wsl.exe -d $distro -u root -- sh -lc 'dir=/etc/systemd/system/systemd-binfmt.service.d; target="$dir/wsl-keepalive-tray.conf"; rm -f "$target" "$dir/.wsl-keepalive-tray-owned"; if [ -e "$target.pre-tray-backup" ]; then mv "$target.pre-tray-backup" "$target"; fi; rmdir "$dir" 2>/dev/null || true'
wsl.exe -d $distro -u root -- systemctl daemon-reload

if ($state -and $state.taskExisted -and $state.taskWasEnabled) {
    Enable-ScheduledTask -TaskName ([string]$state.taskName) -TaskPath '\' | Out-Null
}

if (Test-Path -LiteralPath $resolvedDirectory) {
    Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
}
Write-Output 'UNINSTALL_OK'
