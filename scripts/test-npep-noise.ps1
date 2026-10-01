#requires -Version 5.1
[CmdletBinding()]
param([string]$WebRoot, [string]$BackendRoot, [switch]$Database, [switch]$Browser)
$ErrorActionPreference = 'Stop'
$desktopRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $WebRoot) { $WebRoot = Join-Path $desktopRoot '../NPClassworks' }
if (-not $BackendRoot) { $BackendRoot = Join-Path $desktopRoot '../NPClassworksKV' }
$WebRoot = (Resolve-Path $WebRoot).Path
$BackendRoot = (Resolve-Path $BackendRoot).Path
function Suite([string]$Directory, [string]$Command, [string[]]$Arguments) {
    Push-Location -LiteralPath $Directory
    try { & $Command @Arguments; if ($LASTEXITCODE -ne 0) { throw "Suite failed in $Directory (exit $LASTEXITCODE)." } }
    finally { Pop-Location }
}
$left = Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $desktopRoot 'docs/npep/noise.schema.json')
$right = Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $BackendRoot 'domain/npep/noise.schema.json')
if ($left.Hash -ne $right.Hash) { throw 'Desktop/backend noise contracts differ.' }
& (Join-Path $PSScriptRoot 'test-noise.ps1')
Suite $desktopRoot 'dotnet' @('test', 'tests/NPEduTools.Npep.Tests/NPEduTools.Npep.Tests.csproj', '--artifacts-path', '.artifacts/noise-tests', '-m:1', '--verbosity', 'quiet')
Suite $BackendRoot 'node' @('--test', 'tests/npepNoise.test.js', 'tests/npepRuntime.test.js', 'tests/npepHttpErrors.test.js')
Suite $WebRoot 'node' @('--test', '--test-concurrency=1', 'tests/nativeNoise.test.js', 'tests/nativeNoisePresentation.test.js', 'tests/nativeNoiseTakeover.test.js', 'tests/noiseMonitoringController.test.js', 'tests/noiseScheduleLifecycle.test.js', 'tests/manualNoiseLifecycle.test.js', 'tests/npepAdminClientFlows.test.js')
if ($Browser) { Suite $WebRoot 'node' @('scripts/test-native-noise-browser.mjs') }
if ($Database) { Suite $BackendRoot 'node' @('scripts/run-native-npep-tests.js', '--desktop-root', $desktopRoot) }
Write-Host 'Selected noise suites passed. Native PostgreSQL is disposable; audio is synthetic. Real microphone acceptance and deployment remain separate.'
