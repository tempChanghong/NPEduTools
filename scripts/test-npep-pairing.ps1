#requires -Version 5.1
[CmdletBinding()]
param([string]$WebRoot, [string]$BackendRoot, [switch]$Database, [switch]$Browser,
    [string]$DesktopCommit, [string]$WebCommit, [string]$BackendCommit, [switch]$RequireClean)
$ErrorActionPreference = 'Stop'
$desktopRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $WebRoot) { $WebRoot = Join-Path $desktopRoot '../NPClassworks' }
if (-not $BackendRoot) { $BackendRoot = Join-Path $desktopRoot '../NPClassworksKV' }
$WebRoot = (Resolve-Path $WebRoot).Path
$BackendRoot = (Resolve-Path $BackendRoot).Path
$results = Join-Path $desktopRoot ('.artifacts/screen-pairing/runs/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results -Force | Out-Null
$record = [ordered]@{ schemaVersion = 1; status = 'RUNNING'; startedAt = [DateTime]::UtcNow.ToString('o'); stages = @(); browserRequested = [bool]$Browser; databaseRequested = [bool]$Database; deviceAcceptance = 'NOT_RUN'; deployment = 'NOT_RUN' }
function Save-Record {
    [System.IO.File]::WriteAllText((Join-Path $results 'result.json'), ($record | ConvertTo-Json -Depth 10), [System.Text.UTF8Encoding]::new($false))
}
Save-Record
function Invoke-Suite([string]$Name, [string]$Directory, [string]$Command, [string[]]$Arguments) {
    $stage = [ordered]@{ name = $Name; status = 'RUNNING' }
    $record.stages += $stage
    Save-Record
    Write-Host "Pairing: $Name" -ForegroundColor Cyan
    Push-Location -LiteralPath $Directory
    try { & $Command @Arguments; if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit $LASTEXITCODE)." }; $stage.status = 'PASSED' }
    catch { $stage.status = 'FAILED'; $record.status = 'FAILED'; $record.failedStage = $Name; Save-Record; throw }
    finally { Pop-Location; Save-Record }
}
$sourceArguments = @('scripts/check-npep-pairing-sources.mjs', '--desktop-root', $desktopRoot, '--web-root', $WebRoot, '--backend-root', $BackendRoot, '--output', (Join-Path $results 'sources.json'))
foreach ($entry in @(@('--desktop-commit', $DesktopCommit), @('--web-commit', $WebCommit), @('--backend-commit', $BackendCommit))) { if ($entry[1]) { $sourceArguments += @($entry[0], $entry[1]) } }
if ($RequireClean) { $sourceArguments += '--require-clean' }
Invoke-Suite 'Three repository source identity' $desktopRoot 'node' $sourceArguments
Invoke-Suite 'Source gate regression' $desktopRoot 'node' @('--test','tests/pairingSources.test.mjs')
Invoke-Suite 'Shared contract' $desktopRoot 'node' @('scripts/check-npep-pairing-contract.mjs', $desktopRoot, $BackendRoot)
Invoke-Suite 'Web credentials, UI state and administrator regression' $WebRoot 'node' @('--test','--test-concurrency=1','tests/npepScreenPairingFlows.test.js','tests/npepAdminClientFlows.test.js','tests/npepAcceptance.test.js','tests/npepPresentation.test.js')
Invoke-Suite 'Backend protocol and admission' $BackendRoot 'node' @('--test','tests/npepWire.test.js','tests/npepAdmissionSecurity.test.js','tests/npepHttpErrors.test.js')
Invoke-Suite 'Desktop transport and recovery' $desktopRoot 'dotnet' @('test','tests/NPEduTools.Npep.Tests/NPEduTools.Npep.Tests.csproj','--artifacts-path',$results,'--verbosity','quiet','--logger','trx;LogFileName=transport.trx','--results-directory',$results)
Invoke-Suite 'Onboarding and shared settings session' $desktopRoot 'dotnet' @('test','tests/NPEduTools.Tests/NPEduTools.Tests.csproj','--artifacts-path',$results,'--filter','FullyQualifiedName~OnboardingTests|FullyQualifiedName~NpepConnectionSessionTests','--verbosity','quiet','--logger','trx;LogFileName=onboarding.trx','--results-directory',$results)
if ($Browser) { Invoke-Suite 'Actual Vue/Vuetify browser pairing' $WebRoot 'node' @('scripts/test-npep-pairing-browser.mjs','--backend-root',$BackendRoot) }
if ($Database) { Invoke-Suite 'Disposable native PostgreSQL and real HTTP/.NET' $BackendRoot 'node' @('scripts/run-native-npep-tests.js','--desktop-root',$desktopRoot) }
else { Write-Host 'Database acceptance not selected; -Database uses an isolated temporary cluster, never the debug database.' }
Write-Host 'Selected pairing checks passed. No real school pairing or production deployment performed.' -ForegroundColor Green
$record.status = 'PASSED'
$record.finishedAt = [DateTime]::UtcNow.ToString('o')
Save-Record
Write-Host "Pairing result: $results"
