#requires -Version 5.1
[CmdletBinding()]
param([string]$WebRoot, [string]$BackendRoot, [switch]$Database, [switch]$Browser)
$ErrorActionPreference = 'Stop'
$desktopRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $WebRoot) { $WebRoot = Join-Path $desktopRoot '../NPClassworks' }
if (-not $BackendRoot) { $BackendRoot = Join-Path $desktopRoot '../NPClassworksKV' }
$WebRoot = (Resolve-Path $WebRoot).Path
$BackendRoot = (Resolve-Path $BackendRoot).Path
$results = Join-Path $desktopRoot '.artifacts/n4-tests'
function Invoke-Suite([string]$Name, [string]$Directory, [string]$Command, [string[]]$Arguments) {
    Write-Host "Exam plans: $Name" -ForegroundColor Cyan
    Push-Location -LiteralPath $Directory
    try { & $Command @Arguments; if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit $LASTEXITCODE)." } }
    finally { Pop-Location }
}
Invoke-Suite 'Cross-repository contract' $desktopRoot 'node' @('scripts/check-npep-exam-plan-contract.mjs', $desktopRoot, $WebRoot, $BackendRoot)
Invoke-Suite 'Exam and Daily mode contract' $desktopRoot 'node' @('scripts/check-npep-n3-contract.mjs', $desktopRoot, $WebRoot, $BackendRoot)
Invoke-Suite 'Web file, state and API regression' $WebRoot 'node' @('--test', '--test-concurrency=1', 'tests/npepExamPlans.test.js', 'tests/npepExamPlanFlows.test.js', 'tests/npepAdminClientFlows.test.js', 'tests/npepRuntime.test.js')
Invoke-Suite 'Backend state machine' $BackendRoot 'node' @('--test', 'tests/npepExamPlans.test.js', 'tests/npepRuntime.test.js', 'tests/npepHttpErrors.test.js')
Invoke-Suite 'Host bridge and mode regression' $desktopRoot 'dotnet' @('test', 'tests/NPEduTools.Tests/NPEduTools.Tests.csproj', '--artifacts-path', $results, '-m:1', '--verbosity', 'quiet', '--filter', 'FullyQualifiedName~ExamAware|FullyQualifiedName~RemoteExam|FullyQualifiedName~ClassroomMode|FullyQualifiedName~AdminRuntimeReservation')
Invoke-Suite 'Transport and interruption regression' $desktopRoot 'dotnet' @('test', 'tests/NPEduTools.Npep.Tests/NPEduTools.Npep.Tests.csproj', '--artifacts-path', $results, '-m:1', '--verbosity', 'quiet')
if ($Browser) { Invoke-Suite 'Isolated browser with fixture API' $WebRoot 'node' @('scripts/test-npep-exam-plans-browser.mjs') }
if ($Database) { Invoke-Suite 'Real HTTP and disposable native PostgreSQL' $BackendRoot 'node' @('scripts/run-native-npep-tests.js', '--desktop-root', $desktopRoot) }
Write-Host 'Selected suites passed. No deployment, production changes or real player startup.' -ForegroundColor Green
