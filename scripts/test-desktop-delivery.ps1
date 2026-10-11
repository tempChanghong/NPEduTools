#requires -Version 5.1
[CmdletBinding()]
param([string]$PackageResultPath, [string]$IsccPath, [string]$PythonExecutable = 'python')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$runRoot = Join-Path $projectRoot ('.artifacts/desktop-delivery/' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $runRoot
$shell = (Get-Process -Id $PID).Path
$report = [ordered]@{status='RUNNING';startedAt=[DateTime]::UtcNow.ToString('o');finishedAt=$null;
    powershell=$PSVersionTable.PSVersion.ToString();packageResult=$PackageResultPath;
    candidateChecked=[bool]$PackageResultPath;applicationInstalled=$false;capturesStarted=$false;
    humanAcceptance='NOT_RUN';classroomAcceptance='NOT_RUN';published=$false;suites=@();error=$null}
function Save-Report {
    [IO.File]::WriteAllText((Join-Path $runRoot 'result.json'), ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}
function Suite([string]$Name, [string]$Script, [string[]]$Arguments) {
    $suite = [ordered]@{name=$Name;status='RUNNING';exitCode=$null;log=($Name + '.log');childReport=$null}
    $report.suites += $suite; Save-Report
    $previousPreference = $ErrorActionPreference
    $previousExit = $global:LASTEXITCODE
    try {
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = $null
        & $shell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $Script) @Arguments 2>&1 |
            Tee-Object -FilePath (Join-Path $runRoot $suite.log) -ErrorAction Stop | Out-Host
        $suite.exitCode = $global:LASTEXITCODE
        $ErrorActionPreference = $previousPreference
        if ($null -eq $suite.exitCode -or $suite.exitCode -ne 0) { throw "Delivery suite failed: $Name (exit $($suite.exitCode))." }
        if ($Script -ne 'verify-desktop-delivery.ps1') {
            $lines = @(Get-Content -LiteralPath (Join-Path $runRoot $suite.log) |
                Where-Object { $_ -match '^(INSTALLER_(PACKAGE_)?TEST_RESULT: |Recording checks passed: )(.+)$' })
            if ($lines.Count -ne 1) { throw "Missing or ambiguous child report: $Name" }
            $null = $lines[0] -match '^(INSTALLER_(PACKAGE_)?TEST_RESULT: |Recording checks passed: )(.+)$'
            $suite.childReport = $Matches[3]
            $child = Get-Content -LiteralPath $suite.childReport -Raw | ConvertFrom-Json
            if ($child.status -ne 'PASSED' -or $child.capturesStarted -ne $false -or $child.applicationInstalled -eq $true) {
                throw "Child evidence is not a synthetic pass: $Name"
            }
        }
        $suite.status = 'PASSED'
    } catch { $suite.status = 'FAILED'; throw }
    finally { $ErrorActionPreference = $previousPreference; $global:LASTEXITCODE = $previousExit; Save-Report }
}
Save-Report
Push-Location $projectRoot
try {
    $compiler = & "$PSScriptRoot/resolve-inno-setup.ps1" -IsccPath $IsccPath
    $mediaArgs = @('-PythonExecutable', $PythonExecutable)
    if ($PackageResultPath) {
        $PackageResultPath = (Resolve-Path -LiteralPath $PackageResultPath).Path
        $report.packageResult = $PackageResultPath
        Suite 'candidate-pair' 'verify-desktop-delivery.ps1' @('-PackageResultPath', $PackageResultPath)
        $package = Get-Content -LiteralPath $PackageResultPath -Raw | ConvertFrom-Json
        $mediaArgs += @('-PackageRoot', $package.packageRoot)
    }
    Suite 'package-inputs' 'test-installer-package.ps1' @('-IsccPath', $compiler)
    Suite 'installation-lifecycle' 'test-installer.ps1' @('-IsccPath', $compiler)
    Suite 'bundled-media' 'test-recording-bundle.ps1' $mediaArgs
    $report.status = 'PASSED'
} catch { $report.status = 'FAILED'; $report.error = $_.Exception.Message; throw }
finally {
    Pop-Location
    $report.finishedAt = [DateTime]::UtcNow.ToString('o'); Save-Report
    Write-Output "DESKTOP_DELIVERY_RESULT: $(Join-Path $runRoot 'result.json')"
}
