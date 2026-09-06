param(
    [ValidateRange(1, 1800)][int]$Seconds = 120,
    [string]$OutputPath,
    [switch]$Probe,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$runner = Join-Path $PSScriptRoot 'dotnet.ps1'
if (-not $NoBuild) {
    & $runner build src/NPEduTools.PowerPoint.Diagnostics --configuration Release --locked-mode
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$diagnosticExe = Join-Path $projectRoot 'src/NPEduTools.PowerPoint.Diagnostics/bin/Release/net10.0/NPEduTools.PowerPoint.Diagnostics.exe'
if ($Probe) {
    & $diagnosticExe --probe
} else {
    if (-not $OutputPath) {
        $OutputPath = Join-Path $projectRoot ('.artifacts/powerpoint-diagnostics/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N') + '.jsonl')
    }
    & $diagnosticExe --seconds $Seconds --output $OutputPath
}
exit $LASTEXITCODE
