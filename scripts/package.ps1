param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\WSL-KeepAlive-Tray')
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$expectedRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'dist'))
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)

if (-not $resolvedOutput.StartsWith($expectedRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -or
    [System.IO.Path]::GetFileName($resolvedOutput) -ine 'WSL-KeepAlive-Tray') {
    throw "Refusing to package into unexpected directory: $resolvedOutput"
}

if (Test-Path -LiteralPath $resolvedOutput) {
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}
[System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null

foreach ($directory in @('assets', 'docs', 'linux', 'scripts', 'src')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $directory) -Destination (Join-Path $resolvedOutput $directory) -Recurse
}
[System.IO.Directory]::CreateDirectory((Join-Path $resolvedOutput 'build')) | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'build\WSLKeepAliveTray.exe') -Destination (Join-Path $resolvedOutput 'build')
Copy-Item -LiteralPath (Join-Path $projectRoot 'build\self-test.txt') -Destination (Join-Path $resolvedOutput 'build')
foreach ($file in @('.gitattributes', '.gitignore', 'app.manifest', 'LICENSE', 'README.md', 'SECURITY.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $resolvedOutput
}

$manifestPath = Join-Path $resolvedOutput 'SHA256SUMS.tsv'
$rows = [System.Collections.Generic.List[string]]::new()
$rows.Add("relative_path`tbytes`tsha256")
foreach ($file in Get-ChildItem -LiteralPath $resolvedOutput -File -Recurse | Where-Object FullName -ne $manifestPath | Sort-Object FullName) {
    $relative = $file.FullName.Substring($resolvedOutput.Length + 1).Replace('\', '/')
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $rows.Add($relative + "`t" + $file.Length + "`t" + $hash)
}
[System.IO.File]::WriteAllLines($manifestPath, $rows, [System.Text.UTF8Encoding]::new($false))

Write-Output "PACKAGE_OK path=$resolvedOutput files=$($rows.Count - 1)"
