#requires -Version 5.1
[CmdletBinding()]
param([string]$WebRoot, [string]$BackendRoot, [switch]$Database, [switch]$Browser)
$ErrorActionPreference = 'Stop'
$desktopRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $WebRoot) { $WebRoot = Join-Path $desktopRoot '../NPClassworks' }
if (-not $BackendRoot) { $BackendRoot = Join-Path $desktopRoot '../NPClassworksKV' }
$WebRoot = (Resolve-Path $WebRoot).Path
$BackendRoot = (Resolve-Path $BackendRoot).Path
function SameFiles([string[]]$Paths) {
    $hashes = @($Paths | ForEach-Object { (Get-FileHash -Algorithm SHA256 -LiteralPath $_).Hash } | Select-Object -Unique)
    if ($hashes.Count -ne 1) { throw ('Schedule copies differ: ' + ($Paths -join ', ')) }
}
function Suite([string]$Directory, [string]$Command, [string[]]$Arguments) {
    Push-Location -LiteralPath $Directory
    try { & $Command @Arguments; if ($LASTEXITCODE -ne 0) { throw "Suite failed in $Directory (exit $LASTEXITCODE)." } }
    finally { Pop-Location }
}
SameFiles @((Join-Path $desktopRoot 'docs/npep/noise-schedule-cases.json'), (Join-Path $BackendRoot 'tests/fixtures/noise-schedule-cases.json'), (Join-Path $WebRoot 'tests/fixtures/noise-schedule-cases.json'))
SameFiles @((Join-Path $BackendRoot 'domain/npep/noiseScheduleRules.js'), (Join-Path $WebRoot 'src/utils/schoolNoiseSchedule.js'))
SameFiles @((Join-Path $BackendRoot 'tests/helpers/noiseScheduleCases.js'), (Join-Path $WebRoot 'tests/helpers/noiseScheduleCases.js'))
SameFiles @((Join-Path $BackendRoot 'domain/npep/noise-schedule-policy.schema.json'), (Join-Path $desktopRoot 'docs/npep/noise-schedule-policy.schema.json'))
SameFiles @((Join-Path $BackendRoot 'domain/npep/noise-schedule-wire.schema.json'), (Join-Path $desktopRoot 'docs/npep/noise-schedule-wire.schema.json'))
SameFiles @((Join-Path $BackendRoot 'domain/npep/noise-schedule-wire-cases.json'), (Join-Path $desktopRoot 'docs/npep/noise-schedule-wire-cases.json'))
Suite $desktopRoot 'dotnet' @('test', 'tests/NPEduTools.Tests/NPEduTools.Tests.csproj', '--artifacts-path', '.artifacts/noise-schedule-tests', '-m:1', '--filter', 'FullyQualifiedName~NoiseScheduleTests|FullyQualifiedName~NoiseScheduleHostTests|FullyQualifiedName~SchoolClockTests|FullyQualifiedName~NoiseTests|FullyQualifiedName~NoiseTransportTests', '--verbosity', 'quiet')
Suite $BackendRoot 'node' @('--test', 'tests/noiseScheduleRules.test.js', 'tests/noiseScheduleSchema.test.js', 'tests/npepNoiseSchedules.test.js', 'tests/npepNoiseScheduleRuntime.test.js', 'tests/npepHttpErrors.test.js')
Suite $WebRoot 'node' @('--test', '--test-concurrency=1', 'tests/schoolNoiseSchedule.test.js', 'tests/noiseScheduleLifecycle.test.js', 'tests/noiseScheduleEditorFlows.test.js', 'tests/nativeNoiseSchedule.test.js', 'tests/noiseReportSources.test.js', 'tests/npepAdminClientFlows.test.js')
if ($Browser) { Suite $WebRoot 'node' @('scripts/test-noise-schedules-browser.mjs') }
if ($Database) { Suite $BackendRoot 'node' @('scripts/run-native-npep-tests.js', '--desktop-root', $desktopRoot) }
Write-Host 'Selected school schedule suites passed. Database/browser switches use isolated fixtures; no real microphone or deployment is triggered.'
