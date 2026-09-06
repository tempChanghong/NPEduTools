param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot 'dotnet.ps1') @('restore', 'src/NPEduTools.PowerPoint.Assist', '--locked-mode')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & (Join-Path $PSScriptRoot 'dotnet.ps1') @('build', 'src/NPEduTools.PowerPoint.Assist', '--configuration', 'Release', '--no-restore')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
# This is the interactive window explicitly requested by this launcher.
Start-Process -FilePath (Join-Path $projectRoot 'src/NPEduTools.PowerPoint.Assist/bin/Release/net10.0-windows/NPEduTools.PowerPoint.Assist.exe')
