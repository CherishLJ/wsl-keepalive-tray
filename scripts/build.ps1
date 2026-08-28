[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildDirectory = Join-Path $projectRoot 'build'
$assetsDirectory = Join-Path $projectRoot 'assets'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

New-Item -ItemType Directory -Force -Path $buildDirectory, $assetsDirectory | Out-Null
& (Join-Path $PSScriptRoot 'generate-icon.ps1') `
    -OutputIco (Join-Path $assetsDirectory 'app.ico') `
    -PreviewPng (Join-Path $assetsDirectory 'icon-preview.png')

$arguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/optimize+',
    '/debug:pdbonly',
    ('/out:' + (Join-Path $buildDirectory 'WSLKeepAliveTray.exe')),
    ('/pdb:' + (Join-Path $buildDirectory 'WSLKeepAliveTray.pdb')),
    ('/win32icon:' + (Join-Path $assetsDirectory 'app.ico')),
    ('/win32manifest:' + (Join-Path $projectRoot 'app.manifest')),
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll',
    '/r:System.Web.Extensions.dll'
)
$sources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName }
& $compiler @arguments @sources
if ($LASTEXITCODE -ne 0) {
    throw "C# compilation failed with exit code $LASTEXITCODE"
}

$selfTestReport = Join-Path $buildDirectory 'self-test.txt'
$process = Start-Process -FilePath (Join-Path $buildDirectory 'WSLKeepAliveTray.exe') `
    -ArgumentList @('--self-test', $selfTestReport) -PassThru -Wait -WindowStyle Hidden
if ($process.ExitCode -ne 0) {
    Get-Content -LiteralPath $selfTestReport -ErrorAction SilentlyContinue
    throw "Self-test failed with exit code $($process.ExitCode)"
}
Get-Content -LiteralPath $selfTestReport

