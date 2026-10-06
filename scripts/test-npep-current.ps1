#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$DesktopRoot, [string]$WebRoot, [string]$BackendRoot,
    [string]$DesktopCommit, [string]$WebCommit, [string]$BackendCommit,
    [string]$SourceManifest, [string]$ResultsRoot, [switch]$RequireClean, [switch]$Browser, [switch]$Database
)
$ErrorActionPreference = 'Stop'
$driverRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $DesktopRoot) { $DesktopRoot = $driverRoot }
if (-not $WebRoot) { $WebRoot = Join-Path $DesktopRoot '../NPClassworks' }
if (-not $BackendRoot) { $BackendRoot = Join-Path $DesktopRoot '../NPClassworksKV' }
if (-not $ResultsRoot) { $ResultsRoot = Join-Path $driverRoot '.artifacts/npep-current/runs' }
$ResultsRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ResultsRoot)
$runRoot = Join-Path $ResultsRoot ([Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $runRoot -Force
$record = [ordered]@{
    schemaVersion = 1; category = 'CURRENT'; status = 'RUNNING'
    scope = $(if ($Browser -and $Database) { 'FULL_AUTOMATED' } else { 'SELECTED_AUTOMATED' })
    startedAt = [DateTime]::UtcNow.ToString('o'); finishedAt = $null
    selected = @{ browser = [bool]$Browser; database = [bool]$Database; requireClean = [bool]$RequireClean }
    sources = $null; resolution = $null; driverCommit = $null; driverLocalChanges = $null; runtimes = $null; stages = @(); error = $null
    deviceAcceptance = 'NOT_RUN'; realMicrophone = $false; deployment = 'NOT_RUN'
}
function Save-Record {
    [IO.File]::WriteAllText((Join-Path $runRoot 'result.json'), ($record | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
}
function Suite([string]$Name, [string]$Directory, [string]$Command, [string[]]$Arguments) {
    $stage = [ordered]@{ name = $Name; directory = $Directory; command = $Command; arguments = $Arguments
        status = 'RUNNING'; exitCode = $null; startedAt = [DateTime]::UtcNow.ToString('o'); finishedAt = $null
        log = ('stage-{0:D2}.log' -f ($record.stages.Count + 1)) }
    $record.stages += $stage
    Save-Record
    Write-Host ('Current combination: ' + $Name)
    Push-Location -LiteralPath $Directory
    $previousPreference = $ErrorActionPreference
    try {
        # PowerShell 5.1 native stderr can contain harmless build warnings.
        # Capture it, but decide success by the native process exit code.
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = $null
        & $Command @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $runRoot $stage.log) -ErrorAction Stop
        $stage.exitCode = $global:LASTEXITCODE
        $ErrorActionPreference = $previousPreference
        if ($null -eq $stage.exitCode -or $stage.exitCode -ne 0) { throw "$Name failed (exit $($stage.exitCode))." }
        $stage.status = 'PASSED'
    } catch { $stage.status = 'FAILED'; throw }
    finally {
        $ErrorActionPreference = $previousPreference
        Pop-Location
        $stage.finishedAt = [DateTime]::UtcNow.ToString('o')
        Save-Record
    }
}
function Skip([string]$Name) { $record.stages += @{ name = $Name; status = 'SKIPPED'; reason = 'Not selected' }; Save-Record }
Save-Record
Write-Host ('Current combination report: ' + (Join-Path $runRoot 'result.json'))
try {
    $DesktopRoot = (Resolve-Path -LiteralPath $DesktopRoot).Path
    $WebRoot = (Resolve-Path -LiteralPath $WebRoot).Path
    $BackendRoot = (Resolve-Path -LiteralPath $BackendRoot).Path
    $record.driverCommit = (& git -C $driverRoot rev-parse HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read CI driver commit.' }
    $record.driverLocalChanges = [bool](& git -C $driverRoot status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect CI driver changes.' }
    $sourceArgs = @((Join-Path $PSScriptRoot 'check-npep-pairing-sources.mjs'), '--desktop-root', $DesktopRoot,
        '--web-root', $WebRoot, '--backend-root', $BackendRoot, '--output', (Join-Path $runRoot 'sources.json'))
    foreach ($entry in @(@('--desktop-commit', $DesktopCommit), @('--web-commit', $WebCommit), @('--backend-commit', $BackendCommit))) {
        if ($entry[1]) { $sourceArgs += @($entry[0], $entry[1]) }
    }
    if ($RequireClean) { $sourceArgs += '--require-clean' }
    Suite 'Source identity before testing' $driverRoot 'node' $sourceArgs
    $record.sources = Get-Content (Join-Path $runRoot 'sources.json') -Raw | ConvertFrom-Json
    if ($SourceManifest) {
        $record.resolution = Get-Content -LiteralPath $SourceManifest -Raw | ConvertFrom-Json
        if ($record.resolution.schemaVersion -ne 1 -or $record.resolution.category -ne 'CURRENT') { throw 'Unsupported source manifest.' }
        foreach ($key in @('desktop', 'web', 'backend')) {
            if ($record.resolution.sources.$key.commit -cne $record.sources.sources.$key.commit) { throw "Resolved $key does not match checked-out source." }
        }
    }
    $nodeVersion = (& node --version)
    if ($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^v(\d+)\.(\d+)\.\d+$') { throw 'Unable to read Node.js version.' }
    $nodeMajor = [int]$Matches[1]; $nodeMinor = [int]$Matches[2]
    if (-not (($nodeMajor -eq 22 -and $nodeMinor -ge 18) -or $nodeMajor -ge 24)) {
        throw 'Use Node.js 22.18+ (22 LTS) or 24+: the generated Prisma client requires native TypeScript support.'
    }
    Push-Location -LiteralPath $DesktopRoot
    try {
        $dotnetVersion = (& dotnet --version)
        if ($LASTEXITCODE -ne 0) { throw 'Unable to read .NET SDK version.' }
    } finally { Pop-Location }
    $record.runtimes = @{ node = $nodeVersion; dotnet = $dotnetVersion; powershell = $PSVersionTable.PSVersion.ToString() }
    Save-Record
    Suite 'CI source selection and failure record regression' $driverRoot 'node' @('--test', 'tests/currentCiSources.test.mjs', 'tests/currentCiRunner.test.mjs', 'tests/pairingSources.test.mjs')
    Suite 'Pairing contract' $DesktopRoot 'node' @('scripts/check-npep-pairing-contract.mjs', $DesktopRoot, $BackendRoot)
    Suite 'Exam contract and actual web requests' $DesktopRoot 'node' @('scripts/check-npep-n3-contract.mjs', $DesktopRoot, $WebRoot, $BackendRoot)
    Suite 'Exam plan contract and web file input' $DesktopRoot 'node' @('scripts/check-npep-exam-plan-contract.mjs', $DesktopRoot, $WebRoot, $BackendRoot)
    if ((Get-FileHash (Join-Path $DesktopRoot 'docs/npep/noise.schema.json')).Hash -ne
        (Get-FileHash (Join-Path $BackendRoot 'domain/npep/noise.schema.json')).Hash) { throw 'Desktop/backend noise schemas differ.' }
    Suite 'Web pairing, exam and noise recovery' $WebRoot 'node' @('--test', '--test-concurrency=1',
        'tests/npepScreenPairingFlows.test.js', 'tests/npepAdminClientFlows.test.js', 'tests/npepAcceptance.test.js',
        'tests/npepPresentation.test.js', 'tests/npepRuntime.test.js', 'tests/nativeNoise.test.js',
        'tests/nativeNoisePresentation.test.js', 'tests/nativeNoiseTakeover.test.js', 'tests/noiseMonitoringController.test.js',
        'tests/manualNoiseLifecycle.test.js')
    Suite 'Backend pairing, exam, noise and error protocol' $BackendRoot 'node' @('--test',
        'tests/npepWire.test.js', 'tests/npepAdmissionSecurity.test.js', 'tests/npepHttpErrors.test.js',
        'tests/npepRuntime.test.js', 'tests/npepNoise.test.js', 'tests/debugNpep.test.js')
    $previousBackendRoot = $env:CLASSWORKS_BACKEND_ROOT
    try {
        $env:CLASSWORKS_BACKEND_ROOT = $BackendRoot
        Suite 'Actual web/backend API producers and consumers' $WebRoot 'node' @('--test', 'tests/contracts/core.test.js')
    } finally { $env:CLASSWORKS_BACKEND_ROOT = $previousBackendRoot }
    Suite 'Desktop transport, persisted retries and report recovery' $DesktopRoot 'dotnet' @('test',
        'tests/NPEduTools.Npep.Tests/NPEduTools.Npep.Tests.csproj', '--artifacts-path', $runRoot, '-m:1',
        '--verbosity', 'quiet', '--logger', 'trx;LogFileName=transport.trx', '--results-directory', $runRoot)
    Suite 'Desktop exam adapters and onboarding' $DesktopRoot 'dotnet' @('test',
        'tests/NPEduTools.Tests/NPEduTools.Tests.csproj', '--artifacts-path', $runRoot, '-m:1',
        '--filter', 'FullyQualifiedName~RemoteExam|FullyQualifiedName~RuntimeAdapter|FullyQualifiedName~RuntimeReservation|FullyQualifiedName~ClassroomMode|FullyQualifiedName~AdminStartupTests|FullyQualifiedName~OnboardingTests|FullyQualifiedName~NpepConnectionSessionTests',
        '--verbosity', 'quiet', '--logger', 'trx;LogFileName=exam-onboarding.trx', '--results-directory', $runRoot)
    # Reuse the existing schedule suite: it exports fresh C# fixtures for web/KV,
    # verifies shared rules, and uses test peers rather than the installed Host.
    $shell = (Get-Process -Id $PID).Path
    $scheduleArgs = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
        (Join-Path $DesktopRoot 'scripts/test-npep-noise-schedules.ps1'), '-WebRoot', $WebRoot, '-BackendRoot', $BackendRoot,
        '-Display', '-Protection', '-Presence', '-Guard')
    if ($Browser) { $scheduleArgs += '-Browser' }
    Suite 'Schedules, display presence and protected recovery' $DesktopRoot $shell $scheduleArgs
    if ($Browser) {
        Suite 'Actual pairing browser' $WebRoot 'node' @('scripts/test-npep-pairing-browser.mjs', '--backend-root', $BackendRoot)
        Suite 'Actual native noise browser' $WebRoot 'node' @('scripts/test-native-noise-browser.mjs')
        Suite 'Actual browser stale state and error recovery' $WebRoot 'node' @('node_modules/@playwright/test/cli.js',
            'test', 'tests/e2e/npep-recovery.spec.js', '--reporter=line')
    } else { Skip 'Pairing, noise and recovery browser checks' }
    if ($Database) {
        Suite 'Native isolated database, upgrade and real HTTP/.NET' $BackendRoot 'node' @('scripts/run-native-npep-tests.js', '--desktop-root', $DesktopRoot)
    } else { Skip 'Native isolated database and HTTP/.NET checks' }
    $afterArgs = @($sourceArgs)
    $afterArgs[$afterArgs.IndexOf('--output') + 1] = Join-Path $runRoot 'sources-after.json'
    # Also detect an unexpected checkout change or dirty source created by a test.
    foreach ($key in @('desktop', 'web', 'backend')) {
        if (-not ($afterArgs -contains "--$key-commit")) { $afterArgs += @("--$key-commit", $record.sources.sources.$key.commit) }
    }
    Suite 'Source identity after testing' $driverRoot 'node' $afterArgs
    $record.status = 'PASSED'
} catch {
    $record.status = 'FAILED'
    $record.error = $_.Exception.Message
    throw
} finally {
    $record.finishedAt = [DateTime]::UtcNow.ToString('o')
    Save-Record
    Write-Host ('Current combination: ' + $record.status + ' / ' + $record.scope + '; ' + (Join-Path $runRoot 'result.json'))
}
