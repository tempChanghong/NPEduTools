#requires -Version 5.1
[CmdletBinding()]
param([string]$WebRoot, [string]$BackendRoot, [switch]$Database, [switch]$Browser, [switch]$Display, [switch]$Protection, [switch]$Presence, [switch]$Guard)
$ErrorActionPreference = 'Stop'
$desktopRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $WebRoot) { $WebRoot = Join-Path $desktopRoot '../NPClassworks' }
if (-not $BackendRoot) { $BackendRoot = Join-Path $desktopRoot '../NPClassworksKV' }
$WebRoot = (Resolve-Path $WebRoot).Path
$BackendRoot = (Resolve-Path $BackendRoot).Path
$runRoot = Join-Path $desktopRoot ('.artifacts/noise-schedule-tests/run-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $runRoot
$report = [ordered]@{
    status = 'RUNNING'; startedAt = [DateTime]::UtcNow.ToString('o'); finishedAt = $null
    selected = [ordered]@{ Display = [bool]$Display; Protection = [bool]$Protection; Presence = [bool]$Presence; Guard = [bool]$Guard; Database = [bool]$Database; Browser = [bool]$Browser }
    environment = 'isolated-synthetic'; realMicrophone = $false; realScreenAcceptance = $false; deployed = $false
    suites = @(); error = $null
}
function Save-Report {
    $json = $report | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText((Join-Path $runRoot 'result.json'), $json, [Text.UTF8Encoding]::new($false))
}
Save-Report
Write-Host ('Isolated verification results: ' + $runRoot)
function SameFiles([string[]]$Paths) {
    $hashes = @($Paths | ForEach-Object { (Get-FileHash -Algorithm SHA256 -LiteralPath $_).Hash } | Select-Object -Unique)
    if ($hashes.Count -ne 1) { throw ('Schedule copies differ: ' + ($Paths -join ', ')) }
}
function Suite([string]$Directory, [string]$Command, [string[]]$Arguments) {
    $suite = [ordered]@{ directory = $Directory; command = $Command; arguments = $Arguments; status = 'RUNNING'; exitCode = $null; startedAt = [DateTime]::UtcNow.ToString('o'); finishedAt = $null; log = ('suite-{0:D2}.log' -f ($report.suites.Count + 1)) }
    $report.suites += $suite
    Save-Report
    Push-Location -LiteralPath $Directory
    $previousPreference = $ErrorActionPreference
    try {
        # Windows PowerShell 5.1 wraps native stderr as error records. Preserve it
        # in the log and use the process exit code, rather than failing on warnings.
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = $null
        & $Command @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $runRoot $suite.log) -ErrorAction Stop
        $suite.exitCode = $global:LASTEXITCODE
        $ErrorActionPreference = $previousPreference
        if ($null -eq $suite.exitCode) { throw "Suite did not return a process exit code in $Directory." }
        if ($suite.exitCode -ne 0) { throw "Suite failed in $Directory (exit $($suite.exitCode))." }
        $suite.status = 'PASSED'
    } catch { $suite.status = 'FAILED'; throw }
    finally {
        $ErrorActionPreference = $previousPreference
        Pop-Location
        $suite.finishedAt = [DateTime]::UtcNow.ToString('o')
        Save-Report
    }
}
try {
foreach ($command in @('node', 'dotnet')) { $null = Get-Command $command -ErrorAction Stop }
SameFiles @((Join-Path $desktopRoot 'docs/npep/noise-schedule-cases.json'), (Join-Path $BackendRoot 'tests/fixtures/noise-schedule-cases.json'), (Join-Path $WebRoot 'tests/fixtures/noise-schedule-cases.json'))
SameFiles @((Join-Path $BackendRoot 'domain/npep/noiseScheduleRules.js'), (Join-Path $WebRoot 'src/utils/schoolNoiseSchedule.js'))
SameFiles @((Join-Path $BackendRoot 'tests/helpers/noiseScheduleCases.js'), (Join-Path $WebRoot 'tests/helpers/noiseScheduleCases.js'))
SameFiles @((Join-Path $BackendRoot 'domain/npep/noise-schedule-policy.schema.json'), (Join-Path $desktopRoot 'docs/npep/noise-schedule-policy.schema.json'))
SameFiles @((Join-Path $BackendRoot 'domain/npep/noise-schedule-wire.schema.json'), (Join-Path $desktopRoot 'docs/npep/noise-schedule-wire.schema.json'))
SameFiles @((Join-Path $BackendRoot 'domain/npep/noise-schedule-wire-cases.json'), (Join-Path $desktopRoot 'docs/npep/noise-schedule-wire-cases.json'))
$previousDisplayFixtures = $env:NPEP_NOISE_DISPLAY_FIXTURES
if ($Display) {
    # A new run directory prevents old snapshots from masking a missing test.
    $displayFixtures = Join-Path $desktopRoot ('.artifacts/noise-schedule-tests/display-' + [Guid]::NewGuid().ToString('N'))
    $env:NPEP_NOISE_DISPLAY_FIXTURES = $displayFixtures
}
try {
    if ($Guard) { Suite $desktopRoot 'dotnet' @('test', 'tests/NPEduTools.Tests/NPEduTools.Tests.csproj', '--artifacts-path', '.artifacts/noise-schedule-tests', '-m:1', '--filter', 'FullyQualifiedName~GuardTests|FullyQualifiedName~GuardProcessTests|FullyQualifiedName~AppStartupTests|FullyQualifiedName~NoisePipeTests', '--verbosity', 'quiet') }
    Suite $desktopRoot 'dotnet' @('test', 'tests/NPEduTools.Tests/NPEduTools.Tests.csproj', '--artifacts-path', '.artifacts/noise-schedule-tests', '-m:1', '--filter', 'FullyQualifiedName~NoiseScheduleTests|FullyQualifiedName~NoiseScheduleHostTests|FullyQualifiedName~SchoolClockTests|FullyQualifiedName~NoiseTests|FullyQualifiedName~NoiseTransportTests|FullyQualifiedName~NoiseManagementTests|FullyQualifiedName~NoisePipeTests', '--verbosity', 'quiet')
} finally { $env:NPEP_NOISE_DISPLAY_FIXTURES = $previousDisplayFixtures }
Suite $BackendRoot 'node' @('--test', 'tests/noiseScheduleRules.test.js', 'tests/noiseScheduleSchema.test.js', 'tests/npepNoiseSchedules.test.js', 'tests/npepNoiseScheduleRuntime.test.js', 'tests/npepHttpErrors.test.js')
Suite $WebRoot 'node' @('--test', '--test-concurrency=1', 'tests/schoolNoiseSchedule.test.js', 'tests/noiseScheduleLifecycle.test.js', 'tests/noiseScheduleEditorFlows.test.js', 'tests/nativeNoiseSchedule.test.js', 'tests/noiseReportSources.test.js', 'tests/npepAdminClientFlows.test.js')
if ($Display) {
    $previousWebContracts = $env:NPEP_NOISE_CONTRACTS_DIR
    try {
        $env:NPEP_NOISE_CONTRACTS_DIR = $displayFixtures
        Suite $WebRoot 'node' @('--test', 'tests/scheduledNoiseDisplay.test.js')
    } finally { $env:NPEP_NOISE_CONTRACTS_DIR = $previousWebContracts }
    Suite $desktopRoot 'node' @('scripts/check-scheduled-noise-display-contract.mjs', $WebRoot, $displayFixtures)
    Write-Host ('Actual desktop synthetic display snapshots: ' + $displayFixtures)
}
if ($Protection) {
    $previousManagementFixtures = $env:NPEP_NOISE_MANAGEMENT_FIXTURES
    $managementFixtures = Join-Path $desktopRoot ('.artifacts/noise-schedule-tests/management-' + [Guid]::NewGuid().ToString('N'))
    try {
        $env:NPEP_NOISE_MANAGEMENT_FIXTURES = $managementFixtures
        Suite $desktopRoot 'dotnet' @('test', 'tests/NPEduTools.Npep.Tests/NPEduTools.Npep.Tests.csproj', '--artifacts-path', '.artifacts/noise-schedule-tests', '-m:1', '--filter', 'FullyQualifiedName~NoiseManagementProtocolTests|FullyQualifiedName~Noise_cycle|FullyQualifiedName~Legacy_management', '--verbosity', 'quiet')
    } finally { $env:NPEP_NOISE_MANAGEMENT_FIXTURES = $previousManagementFixtures }
    Suite $desktopRoot 'node' @('scripts/check-noise-management-contract.mjs', $BackendRoot, $managementFixtures)
    Suite $BackendRoot 'node' @('--test', 'tests/npepNoiseManagement.test.js')
    $previousProtectionContracts = $env:NPEP_NOISE_CONTRACTS_DIR
    try {
        if ($Display) { $env:NPEP_NOISE_CONTRACTS_DIR = $displayFixtures }
        Suite $WebRoot 'node' @('--test', 'tests/scheduledNoiseDisplay.test.js', 'tests/nativeNoise.test.js', 'tests/nativeNoiseSchedule.test.js')
    } finally { $env:NPEP_NOISE_CONTRACTS_DIR = $previousProtectionContracts }
    Write-Host ('Actual desktop 0.8 parser results: ' + $managementFixtures)
}
if ($Browser) {
    Suite $WebRoot 'node' @('scripts/test-noise-schedules-browser.mjs')
    if ($Display -or $Protection -or $Presence) {
        # The existing Playwright webServer builds fresh app assets against its
        # loopback fixture API; this does not use the developer/production API.
        Suite $WebRoot 'node' @('node_modules/@playwright/test/cli.js', 'test', 'tests/e2e/scheduled-noise-display.spec.js', '--reporter=line')
    }
}
if ($Presence) {
    $previousPresence = $env:NPEP_NOISE_PRESENCE_FIXTURES
    $presenceFixtures = Join-Path $desktopRoot ('.artifacts/noise-schedule-tests/presence-' + [Guid]::NewGuid().ToString('N'))
    try {
        $env:NPEP_NOISE_PRESENCE_FIXTURES = $presenceFixtures
        Suite $desktopRoot 'dotnet' @('test', 'tests/NPEduTools.Npep.Tests/NPEduTools.Npep.Tests.csproj', '--artifacts-path', '.artifacts/noise-schedule-tests', '-m:1', '--filter', 'FullyQualifiedName~NoiseDisplayProtocolTests|FullyQualifiedName~Display_cycle', '--verbosity', 'quiet')
        Suite $desktopRoot 'dotnet' @('test', 'tests/NPEduTools.Tests/NPEduTools.Tests.csproj', '--artifacts-path', '.artifacts/noise-schedule-tests', '-m:1', '--filter', 'FullyQualifiedName~NoiseDisplayTests', '--verbosity', 'quiet')
    } finally { $env:NPEP_NOISE_PRESENCE_FIXTURES = $previousPresence }
    Suite $desktopRoot 'node' @('scripts/check-noise-display-presence-contract.mjs', $BackendRoot, $presenceFixtures)
    Suite $BackendRoot 'node' @('--test', 'tests/npepNoiseDisplayPresence.test.js')
    Write-Host ('Actual desktop 0.9 parser results: ' + $presenceFixtures)
}
if ($Database) { Suite $BackendRoot 'node' @('scripts/run-native-npep-tests.js', '--desktop-root', $desktopRoot) }
Write-Host 'Selected school schedule suites passed. Database/browser switches use isolated fixtures; no real microphone or deployment is triggered.'
$report.status = 'PASSED'
} catch {
    $report.status = 'FAILED'
    $report.error = $_.Exception.Message
    throw
} finally {
    $report.finishedAt = [DateTime]::UtcNow.ToString('o')
    Save-Report
    Write-Host ('Verification status: ' + $report.status + '; report: ' + (Join-Path $runRoot 'result.json'))
}
