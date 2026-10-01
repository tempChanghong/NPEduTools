#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$WebRoot,
    [string]$BackendRoot,
    [switch]$Database
)
$ErrorActionPreference = 'Stop'
if (-not $WebRoot) { $WebRoot = Join-Path $PSScriptRoot '../../NPClassworks' }
if (-not $BackendRoot) { $BackendRoot = Join-Path $PSScriptRoot '../../NPClassworksKV' }
$desktopRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$WebRoot = (Resolve-Path $WebRoot).Path
$BackendRoot = (Resolve-Path $BackendRoot).Path
$results = Join-Path $desktopRoot '.artifacts/n3-tests'
foreach ($command in @('node', 'dotnet')) { $null = Get-Command $command -ErrorAction Stop }
function Invoke-Suite([string]$Name, [string]$Directory, [string]$Command, [string[]]$Arguments) {
    Write-Host "N3: $Name" -ForegroundColor Cyan
    Push-Location -LiteralPath $Directory
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit $LASTEXITCODE)." }
    } finally { Pop-Location }
}
Invoke-Suite 'Shared contract and actual web request' $desktopRoot 'node' @('scripts/check-npep-n3-contract.mjs', $desktopRoot, $WebRoot, $BackendRoot)
Invoke-Suite 'Web API, state and presentation' $WebRoot 'node' @('--test', '--test-concurrency=1', 'tests/npepRuntime.test.js', 'tests/npepAdminClientFlows.test.js', 'tests/npepPresentation.test.js')
Invoke-Suite 'Backend N3 state machine and protocol errors' $BackendRoot 'node' @('--test', 'tests/npepRuntime.test.js', 'tests/npepHttpErrors.test.js', 'tests/debugNpep.test.js')
Invoke-Suite 'Desktop execution kernel and local adapters' $desktopRoot 'dotnet' @('test', 'tests/NPEduTools.Tests/NPEduTools.Tests.csproj', '--artifacts-path', $results, '--filter', 'FullyQualifiedName~RemoteExam|FullyQualifiedName~RuntimeAdapter|FullyQualifiedName~RuntimeReservation|FullyQualifiedName~ClassroomMode|FullyQualifiedName~ClassroomRuntime|FullyQualifiedName~AdminStartupTests', '--verbosity', 'quiet')
Invoke-Suite 'Desktop N1/N2/N3 transport regression' $desktopRoot 'dotnet' @('test', 'tests/NPEduTools.Npep.Tests/NPEduTools.Npep.Tests.csproj', '--artifacts-path', $results, '--verbosity', 'quiet')
if ($Database) {
    Invoke-Suite 'Native isolated HTTP/PostgreSQL N1/N2/N3' $BackendRoot 'node' @('scripts/run-native-npep-tests.js', '--desktop-root', $desktopRoot)
} else {
    Write-Host 'Database integration not selected. Use -Database with native PostgreSQL installed (no Docker; temporary cluster only).'
}
Write-Host 'Selected N3 suites passed. No production deployment or real desktop software switch was performed.' -ForegroundColor Green
