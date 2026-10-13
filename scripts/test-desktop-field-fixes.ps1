#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$runRoot = Join-Path $root ('.artifacts/desktop-field-fixes/' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $runRoot
& "$PSScriptRoot/dotnet.ps1" -DotnetArguments @('build', 'tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj', '--artifacts-path', (Join-Path $runRoot 'build'), '-m:1', '-p:RestoreLockedMode=true', '-p:UseSharedCompilation=false', '--verbosity', 'quiet')
if ($LASTEXITCODE -ne 0) { throw 'Desktop field fixture compilation failed.' }
$fixture = Join-Path $runRoot 'build/bin/NPEduTools.Onboarding.UiTests/debug/NPEduTools.Onboarding.UiTests.exe'
foreach ($suite in @('about-version', 'classisland-clock', 'desktop-density', 'desktop-window-bounds', 'study-workspace', 'study-workspace-races', 'study-display', 'study-report', 'quick-boundaries', 'quick-workspace', 'home-workspace', 'recording-workspace', 'manual-recording', 'automatic-recording', 'recording-plan-save', 'automatic-recovery', 'sound-meter-beta', 'usb-reference', 'noise-calibration', 'study-devices', 'touch-status')) {
    $output = Join-Path $runRoot $suite
    $process = Start-Process -FilePath $fixture -ArgumentList @('"' + $output + '"', '--' + $suite) -WindowStyle Hidden -Wait -PassThru
    $resultPath = Join-Path $output 'result.json'
    if (Test-Path -LiteralPath $resultPath) { Get-Content -LiteralPath $resultPath -Raw | Write-Output }
    if ($process.ExitCode -ne 0) { throw "Desktop field checks failed: $suite ($output)" }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($result.status -ne 'PASSED') { throw "Desktop field evidence missing or failed: $suite" }
}
Write-Output "DESKTOP_FIELD_RESULT: $runRoot"
