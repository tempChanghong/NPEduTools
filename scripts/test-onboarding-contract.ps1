#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$runRoot = Join-Path $root ('.artifacts/onboarding-contract/' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $runRoot
& "$PSScriptRoot/dotnet.ps1" -DotnetArguments @('test','tests/NPEduTools.Tests/NPEduTools.Tests.csproj','--artifacts-path',(Join-Path $runRoot 'unit-build'),'-m:1','-p:RestoreLockedMode=true','-p:UseSharedCompilation=false','--filter','FullyQualifiedName~AgreementAcceptanceTests|FullyQualifiedName~OnboardingTests|FullyQualifiedName~AppStartupTests','--logger','trx;LogFileName=onboarding.trx','--results-directory',(Join-Path $runRoot 'unit-results'),'--verbosity','quiet')
if ($LASTEXITCODE -ne 0) { throw 'Onboarding state/consent regression failed.' }
& "$PSScriptRoot/dotnet.ps1" -DotnetArguments @('build','tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj','--artifacts-path',(Join-Path $runRoot 'ui-build'),'-m:1','-p:RestoreLockedMode=true','-p:UseSharedCompilation=false','--verbosity','quiet')
if ($LASTEXITCODE -ne 0) { throw 'Isolated WPF fixture compilation failed.' }
$fixture = Join-Path $runRoot 'ui-build/bin/NPEduTools.Onboarding.UiTests/debug/NPEduTools.Onboarding.UiTests.exe'
$fixtureProcess = Start-Process -FilePath $fixture -ArgumentList @('"' + (Join-Path $runRoot 'ui-results') + '"') -WindowStyle Hidden -Wait -PassThru
if ($fixtureProcess.ExitCode -ne 0) { throw "Isolated WPF checks failed: $runRoot" }
$fixtureResult = Get-Content -LiteralPath (Join-Path $runRoot 'ui-results/result.json') -Raw | ConvertFrom-Json
if ($fixtureResult.status -ne 'PASSED') { throw "Isolated WPF evidence missing or failed: $runRoot" }
Write-Output "ONBOARDING_RESULT: $(Join-Path $runRoot 'ui-results/result.json')"
