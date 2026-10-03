#requires -Version 5.1
# Mock IPC and local Host regression only; does not start SecRandom or draw from a real list.
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'SecRandom local IPC checks require Windows.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$runDirectory = Join-Path $projectRoot ('.artifacts/secrandom-tests/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
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
    $metadata = [ordered]@{ commit = $revision; dirty = $changes.Count -gt 0; configuration = $Configuration;
        powershell = $PSVersionTable.PSVersion.ToString(); completed = $false; liveDraw = $false }
    $metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'run.json') -Encoding UTF8
    Invoke-Dotnet @('restore', 'tests/NPEduTools.Tests', '--locked-mode')
    Invoke-Dotnet @('test', 'tests/NPEduTools.Tests', '-c', $Configuration, '--no-restore', '-p:UseSharedCompilation=false',
        '--filter', 'FullyQualifiedName~SecRandomTests', '--logger', 'trx;LogFileName=secrandom.trx', '--results-directory', $runDirectory)
    & (Join-Path $PSScriptRoot 'assert-test-results.ps1') -Path (Join-Path $runDirectory 'secrandom.trx')
    $metadata.completed = $true
    $metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'run.json') -Encoding UTF8
    Write-Output "SecRandom local checks passed. Results: $runDirectory"
} finally { Pop-Location }
