param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$runner = Join-Path $PSScriptRoot 'dotnet.ps1'
if (-not $NoBuild) {
    & $runner restore src/NPEduTools.PowerPoint.Diagnostics --locked-mode
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $runner build src/NPEduTools.PowerPoint.Diagnostics --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$experimentExe = Join-Path $projectRoot 'src/NPEduTools.PowerPoint.Diagnostics/bin/Release/net10.0/NPEduTools.PowerPoint.Diagnostics.exe'
& $experimentExe --step-once-experiment
exit $LASTEXITCODE
