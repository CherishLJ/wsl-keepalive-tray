param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\build\dashboard-screenshot.png'),
    [int]$WaitSeconds = 5,
    [switch]$HideAfterCapture
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WindowCaptureNative
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
'@

# Match the per-monitor-aware application so GetWindowRect and CopyFromScreen
# use the same physical pixel coordinate system on a scaled desktop.
[void][WindowCaptureNative]::SetProcessDpiAwarenessContext([IntPtr](-4))

$appPath = Join-Path $env:LOCALAPPDATA 'Programs\WSLKeepAliveTray\WSLKeepAliveTray.exe'
if (-not (Test-Path -LiteralPath $appPath)) {
    throw "Installed application not found: $appPath"
}

Start-Process -FilePath $appPath -ArgumentList '--show'
$deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
$target = [IntPtr]::Zero
$targetTitle = $null

while ([DateTime]::UtcNow -lt $deadline -and $target -eq [IntPtr]::Zero) {
    $callback = [WindowCaptureNative+EnumWindowsProc]{
        param([IntPtr]$hWnd, [IntPtr]$lParam)

        if (-not [WindowCaptureNative]::IsWindowVisible($hWnd)) {
            return $true
        }

        $builder = New-Object System.Text.StringBuilder 256
        [void][WindowCaptureNative]::GetWindowText($hWnd, $builder, $builder.Capacity)
        if ($builder.ToString() -eq 'WSL 运行监控') {
            $script:target = $hWnd
            $script:targetTitle = $builder.ToString()
            return $false
        }

        return $true
    }

    [void][WindowCaptureNative]::EnumWindows($callback, [IntPtr]::Zero)
    if ($target -eq [IntPtr]::Zero) {
        Start-Sleep -Milliseconds 200
    }
}

if ($target -eq [IntPtr]::Zero) {
    throw 'Dashboard window was not found.'
}

[void][WindowCaptureNative]::SetForegroundWindow($target)
Start-Sleep -Milliseconds 300

$rect = New-Object WindowCaptureNative+RECT
if (-not [WindowCaptureNative]::GetWindowRect($target, [ref]$rect)) {
    throw 'Unable to read dashboard bounds.'
}

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) {
    throw "Invalid dashboard bounds: ${width}x${height}"
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutput
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $hdc = $graphics.GetHdc()
    try {
        $rendered = [WindowCaptureNative]::PrintWindow($target, $hdc, 2)
    }
    finally {
        $graphics.ReleaseHdc($hdc)
    }
    if (-not $rendered) {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    }
    $bitmap.Save($resolvedOutput, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Output "CAPTURE_OK title=$targetTitle size=${width}x${height} path=$resolvedOutput"
if ($HideAfterCapture) {
    [void][WindowCaptureNative]::PostMessage($target, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
}
