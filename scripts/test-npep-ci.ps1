#requires -Version 7.5
# Windows DPAPI/pipe regression only. Does not use school credentials or production services.
param()
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'NPEP device CI requires Windows (DPAPI and named pipes).' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$runDirectory = Join-Path $projectRoot ('.artifacts/npep-ci/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -ErrorAction Stop | Out-Null
function Invoke-Dotnet([string[]]$Arguments) {
    & (Join-Path $PSScriptRoot 'dotnet.ps1') @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed ($LASTEXITCODE)." }
}
Push-Location $projectRoot
try {
    $revision = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Unable to identify source revision.' }
    $changes = @(& git status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read source state.' }
    $metadata = [ordered]@{ commit = $revision; dirty = $changes.Count -gt 0; powershell = $PSVersionTable.PSVersion.ToString(); completed = $false }
    $metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'run.json')

    & (Join-Path $projectRoot 'docs/npep/Test-N1Examples.ps1')
    $builds = @('src/NPEduTools.App', 'tools/NPEduTools.NpepProbe', 'tests/NPEduTools.Npep.Acceptance')
    $tests = @('tests/NPEduTools.Npep.Tests', 'tests/NPEduTools.Tests')
    foreach ($project in ($builds + $tests)) {
        Invoke-Dotnet @('restore', $project, '--locked-mode')
    }
    foreach ($project in $builds) {
        Invoke-Dotnet @('build', $project, '-c', 'Release', '--no-restore')
    }
    foreach ($project in $tests) {
        $name = Split-Path $project -Leaf
        Invoke-Dotnet @('test', $project, '-c', 'Release', '--no-restore', '--logger', "trx;LogFileName=$name.trx", '--results-directory', $runDirectory)
        & (Join-Path $PSScriptRoot 'assert-test-results.ps1') -Path (Join-Path $runDirectory "$name.trx")
    }
    $metadata.completed = $true
    $metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'run.json')
    Write-Output "N1 Windows checks passed. Results: $runDirectory"
} finally { Pop-Location }
