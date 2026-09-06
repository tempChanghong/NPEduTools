param([string]$TestPresentation)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$packageId = 'powerpoint-diagnostics-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$packageRoot = Join-Path $projectRoot ('.artifacts/' + $packageId)
$appDirectory = Join-Path $packageRoot 'app'
& (Join-Path $PSScriptRoot 'dotnet.ps1') @('publish', 'src/NPEduTools.PowerPoint.Diagnostics', '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true', '--output', $appDirectory, '-p:PublishTrimmed=false', ('-p:NuGetLockFilePath=' + (Join-Path $packageRoot 'packages.lock.json')))
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
@'
@echo off
cd /d "%~dp0"
app\NPEduTools.PowerPoint.Diagnostics.exe --seconds 180
echo.
echo Diagnostics ended. Logs are in app\diagnostics.
echo A Markdown analysis report is saved next to each completed log.
pause
'@ | Set-Content -LiteralPath (Join-Path $packageRoot 'Start-Diagnostics.cmd') -Encoding ascii
@'
@echo off
if "%~1"=="" (
    echo Drag a diagnostic .jsonl file onto this script to analyze it.
    pause
    exit /b 2
)
"%~dp0app\NPEduTools.PowerPoint.Diagnostics.exe" --analyze "%~f1"
pause
'@ | Set-Content -LiteralPath (Join-Path $packageRoot 'Analyze-Log.cmd') -Encoding ascii
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/POWERPOINT-DIAGNOSTICS.md') -Destination (Join-Path $packageRoot 'README.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/POWERPOINT-TOUCH-ASSIST-PLAN.md') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $packageRoot
if ($TestPresentation) {
    $sourcePresentation = Get-Item -LiteralPath $TestPresentation
    if ($sourcePresentation.Extension -ne '.pptx') { throw 'TestPresentation must be a .pptx file.' }
    Copy-Item -LiteralPath $sourcePresentation.FullName -Destination (Join-Path $packageRoot 'diagnostic-slides.pptx')
}
$zipPath = Join-Path $projectRoot ('.artifacts/' + $packageId + '-win-x64.zip')
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
Get-FileHash -LiteralPath $zipPath -Algorithm SHA256 | Select-Object Path,Hash
