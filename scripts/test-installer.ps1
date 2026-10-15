# Real Inno install/upgrade/uninstall in a unique per-user fixture, without App launch or capture.
param([string]$IsccPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$compiler = & "$PSScriptRoot/resolve-inno-setup.ps1" -IsccPath $IsccPath
$id = [guid]::NewGuid().ToString('N')
$scratch = Join-Path $projectRoot ".artifacts/installer-tests/$id"
$payload = Join-Path $scratch 'payload'
$install = Join-Path $scratch 'installed'
$output = Join-Path $scratch 'output'
$registry = "HKCU:\Software\NPEduTools\InstallerTest\$id"
$checks = @()
$running = $null
$summary = [ordered]@{status='RUNNING';applicationInstalled=$false;capturesStarted=$false;productionAcceptance='NOT_RUN';checks=@();error=$null}
New-Item -ItemType Directory -Path $payload,$output -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $payload
function Save-Result {
    $summary.checks = $script:checks
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $scratch 'result.json') -Encoding utf8
}
function Check([string]$Name, [bool]$Passed) {
    if (-not $Passed) { throw "FAIL: $Name" }
    $script:checks += $Name
    Write-Output "PASS: $Name"
}
function Compile([string]$Version, [string]$Release) {
    Set-Content -LiteralPath (Join-Path $payload 'version.txt') -Value $Version
    & $compiler '/Q' "/DPackageRoot=$payload" "/DReleaseVersion=$Release" "/DInstallerVersion=$Version" "/DOutputRoot=$output" "/DInstallerTestId=$id" (Join-Path $projectRoot 'installer/NPEduTools.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer fixture compilation failed.' }
    return (Join-Path $output "NPEduTools-$Release-win-x64-setup.exe")
}
function Execute([string]$Exe, [string]$Case, [int]$Expected = 0) {
    $log = Join-Path $scratch "$Case.log"
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',"/LOG=`"$log`"")
    if ($Exe -notlike '*unins*.exe') { $arguments += "/DIR=`"$install`"" }
    $process = Start-Process -FilePath $Exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) {
        # This process belongs to our private fixture, never the product or a user session.
        $process.Kill(); $process.WaitForExit()
        throw "Installer fixture did not exit within 60 seconds: $Case"
    }
    $process.Refresh()
    Check "$Case exit code ($Expected)" ($process.ExitCode -eq $Expected)
}
Save-Result
try {
    # Keep guardian failure/rollback checks in the existing delivery/CI entry point.
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test-guard-maintenance.ps1') | Out-Host
    Check 'guardian maintenance transaction regressions' ($LASTEXITCODE -eq 0)
    # Names and registry roots alone differ from the production .iss; event code is shared.
    $first = Compile '0.2026.1003.0' 'Installer-Test-A'
    $next = Compile '0.2026.1003.1' 'Installer-Test-B'
    Execute $first 'first-install'
    Check 'payload installed' ((Get-Content (Join-Path $install 'version.txt')).Trim() -eq '0.2026.1003.0')
    Check 'maintenance helper installed and initial marker released' ((Test-Path -LiteralPath (Join-Path $install 'maintenance/guard-maintenance.ps1')) -and -not (Test-Path -LiteralPath (Join-Path $install 'app/guard-maintenance.json')))
    Set-Content -LiteralPath (Join-Path $install 'user-file.txt') -Value 'preserve'
    Set-Content -LiteralPath (Join-Path $scratch 'outside-user-data.txt') -Value 'preserve'
    Execute $next 'upgrade'
    Check 'upgrade files and version marker' (((Get-Content (Join-Path $install 'version.txt')).Trim() -eq '0.2026.1003.1') -and (Get-ItemProperty -LiteralPath $registry).InstallerVersion -eq '0.2026.1003.1')
    Check 'successful upgrade releases maintenance marker' (-not (Test-Path -LiteralPath (Join-Path $install 'app/guard-maintenance.json')))
    $marker=Join-Path $install 'app/guard-maintenance.json'
    [IO.File]::WriteAllText($marker,'{"Id":"synthetic-other-transaction"}')
    try {
        Execute $next 'maintenance-failure-rejected' 7
        $failureLog=[IO.File]::ReadAllText((Join-Path $scratch 'maintenance-failure-rejected.log'))
        Check 'maintenance error includes helper failure details in installer log' ($failureLog.Contains('Guardian maintenance failed: Prepare;') -and $failureLog.Contains('guard-maintenance.json'))
        $diagnostic=[regex]::Match($failureLog,'; result: ([^\r\n]+\\Prepare-result\.txt)')
        Check 'failed Prepare keeps its own diagnostic file' ($diagnostic.Success -and (Test-Path -LiteralPath $diagnostic.Groups[1].Value))
        Check 'maintenance failure preserves payload and existing marker' ((Get-Content (Join-Path $install 'version.txt')).Trim() -eq '0.2026.1003.1' -and [IO.File]::ReadAllText($marker).Contains('synthetic-other-transaction'))
    } finally { [IO.File]::Delete($marker) }
    Execute $next 'retry-after-maintenance-failure'
    Execute $first 'downgrade-rejected' 7
    Check 'downgrade left new payload intact' ((Get-Content (Join-Path $install 'version.txt')).Trim() -eq '0.2026.1003.1')
    Set-ItemProperty -LiteralPath $registry -Name InstallerVersion -Value 'damaged'
    Execute $next 'invalid-version-rejected' 7
    Set-ItemProperty -LiteralPath $registry -Name InstallerVersion -Value '0.2026.1003.1'
    $fixtureExe = Join-Path $scratch "NPEduTools.InstallerFixture-$id.exe"
    Copy-Item -LiteralPath "$env:SystemRoot\System32\ping.exe" -Destination $fixtureExe
    $running = Start-Process -FilePath $fixtureExe -ArgumentList @('-t','127.0.0.1') -WindowStyle Hidden -RedirectStandardOutput (Join-Path $scratch 'fixture-process.log') -PassThru
    Start-Sleep -Milliseconds 500
    Check 'isolated busy process started' (-not $running.HasExited)
    Execute $next 'busy-upgrade-rejected' 7
    Check 'installer did not terminate busy process' (-not $running.HasExited)
    $uninstaller = Join-Path $install 'unins000.exe'
    Execute $uninstaller 'busy-uninstall-rejected' 1
    Check 'uninstaller did not terminate busy process' (-not $running.HasExited)
    Stop-Process -Id $running.Id -ErrorAction Stop
    $running.WaitForExit(); $running = $null
    Set-ItemProperty -LiteralPath $registry -Name StartupCommand -Value ('"' + (Join-Path $install 'app/NPEduTools.App.exe') + '" --startup')
    Execute $uninstaller 'startup-uninstall-rejected' 1
    Check 'startup blocker preserved files' (Test-Path -LiteralPath (Join-Path $install 'version.txt'))
    Remove-ItemProperty -LiteralPath $registry -Name StartupCommand
    $helper=Join-Path $install 'maintenance/guard-maintenance.ps1'
    $helperBytes=[IO.File]::ReadAllBytes($helper)
    [IO.File]::AppendAllText($helper,"`r`n# synthetic tampering")
    Execute $uninstaller 'modified-helper-uninstall-rejected' 1
    Check 'modified helper never executed and payload preserved' ((Test-Path -LiteralPath (Join-Path $install 'version.txt')) -and -not (Test-Path -LiteralPath (Join-Path $install 'app/guard-maintenance.json')))
    [IO.File]::WriteAllBytes($helper,$helperBytes)
    Execute $uninstaller 'uninstall'
    Check 'tracked payload removed' (-not (Test-Path -LiteralPath (Join-Path $install 'version.txt')))
    # The first uninstaller process can exit while its second phase completes maintenance.
    $maintenanceDeadline=[DateTime]::UtcNow.AddSeconds(10)
    while (((Test-Path -LiteralPath $helper) -or (Test-Path -LiteralPath (Join-Path $install 'app/guard-maintenance.json'))) -and [DateTime]::UtcNow -lt $maintenanceDeadline) { Start-Sleep -Milliseconds 100 }
    Check 'uninstall removes helper and maintenance marker' (-not (Test-Path -LiteralPath $helper) -and -not (Test-Path -LiteralPath (Join-Path $install 'app/guard-maintenance.json')))
    Check 'untracked and external user files preserved' (((Get-Content (Join-Path $install 'user-file.txt')).Trim() -eq 'preserve') -and ((Get-Content (Join-Path $scratch 'outside-user-data.txt')).Trim() -eq 'preserve'))
    Check 'test version registration removed' (-not (Test-Path -LiteralPath $registry))
    # Inno's self-delete helper outlives the exit code. Reinstalling while it is
    # still running can allocate unins001.exe or let the old helper delete the
    # file the test intends to use. Wait for the private old uninstaller only.
    $deleteDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((Test-Path -LiteralPath $uninstaller) -and [DateTime]::UtcNow -lt $deleteDeadline) {
        Start-Sleep -Milliseconds 100
    }
    Check 'old uninstaller self-delete completed before reinstall' (-not (Test-Path -LiteralPath $uninstaller))
    Execute $next 'reinstall'
    Check 'reinstall retains user file' ((Get-Content (Join-Path $install 'user-file.txt')).Trim() -eq 'preserve')
    $reinstalledUninstallers = @(Get-ChildItem -LiteralPath $install -Filter 'unins*.exe' -File)
    Check 'reinstall has one current uninstaller' ($reinstalledUninstallers.Count -eq 1)
    $uninstaller = $reinstalledUninstallers[0].FullName
    Execute $uninstaller 'final-uninstall'
    $summary.status = 'PASSED'
} catch {
    $summary.status = 'FAILED'; $summary.error = $_.Exception.Message
    throw
} finally {
    if ($running -and -not $running.HasExited) { Stop-Process -Id $running.Id; $running.WaitForExit() }
    # Inno returns before its self-delete helper removes unins000.exe. File existence
    # alone does not mean the fixture is still installed; avoid launching it twice.
    if ((Test-Path -LiteralPath $registry) -and (Test-Path -LiteralPath (Join-Path $install 'unins000.exe'))) {
        if (Test-Path -LiteralPath $registry) { Remove-ItemProperty -LiteralPath $registry -Name StartupCommand -ErrorAction SilentlyContinue }
        $cleanup = Start-Process -FilePath (Join-Path $install 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru
        if (-not $cleanup.WaitForExit(60000)) { $cleanup.Kill(); $cleanup.WaitForExit(); $summary.status='FAILED'; $summary.error += ' Fixture cleanup timed out.' }
        $cleanup.Refresh()
        if ($cleanup.ExitCode -ne 0) { $summary.status = 'FAILED'; $summary.error += ' Fixture uninstall cleanup failed.' }
    }
    Save-Result
    Write-Output "INSTALLER_TEST_RESULT: $(Join-Path $scratch 'result.json')"
    if ($summary.status -eq 'FAILED') { throw $summary.error }
}
