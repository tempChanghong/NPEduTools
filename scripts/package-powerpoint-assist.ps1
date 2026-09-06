param([string]$TestPresentation)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$packageId = 'powerpoint-touch-assist-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$packageRoot = Join-Path $projectRoot ('.artifacts/' + $packageId)
$appDirectory = Join-Path $packageRoot 'app'
$lockDirectory = Join-Path $projectRoot ('.artifacts/package-locks/' + $packageId)
$null = New-Item -ItemType Directory -Path $lockDirectory -Force
& (Join-Path $PSScriptRoot 'dotnet.ps1') @('publish', 'src/NPEduTools.PowerPoint.Assist', '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true', '--output', $appDirectory, '-p:PublishTrimmed=false', ('-p:PackageLockDirectory=' + $lockDirectory))
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
@'
@echo off
start "" "%~dp0app\NPEduTools.PowerPoint.Assist.exe"
'@ | Set-Content -LiteralPath (Join-Path $packageRoot 'Start-Touch-Assist.cmd') -Encoding ascii
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/POWERPOINT-TOUCH-ASSIST.md') -Destination (Join-Path $packageRoot 'README.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/PowerPoint-Touch-Assist/LICENSE') -Destination (Join-Path $packageRoot 'REFERENCE-CC0-LICENSE')
if ($TestPresentation) {
    $source = Get-Item -LiteralPath $TestPresentation
    if ($source.Extension -ne '.pptx') { throw 'TestPresentation must be a .pptx file.' }
    Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $packageRoot 'touch-assist-slides.pptx')
}
$zipPath = Join-Path $projectRoot ('.artifacts/' + $packageId + '-win-x64.zip')
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
Get-FileHash -LiteralPath $zipPath -Algorithm SHA256 | Select-Object Path, Hash
