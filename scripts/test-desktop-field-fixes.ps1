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
foreach ($suite in @('classisland-clock', 'desktop-density', 'sound-meter-beta', 'usb-reference')) {
    $output = Join-Path $runRoot $suite
    $process = Start-Process -FilePath $fixture -ArgumentList @('"' + $output + '"', '--' + $suite) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Desktop field checks failed: $suite ($output)" }
    $result = Get-Content -LiteralPath (Join-Path $output 'result.json') -Raw | ConvertFrom-Json
    if ($result.status -ne 'PASSED') { throw "Desktop field evidence missing or failed: $suite" }
}
Write-Output "DESKTOP_FIELD_RESULT: $runRoot"
