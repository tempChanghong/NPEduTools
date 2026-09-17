param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [ValidateRange(5,3600)][int]$DurationSeconds = 30)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'dotnet.ps1') build (Join-Path $projectRoot 'NPEduTools.sln') --configuration $Configuration --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Recording build failed.' }
python (Join-Path $PSScriptRoot 'test-recording.py') --configuration $Configuration --duration $DurationSeconds
exit $LASTEXITCODE
