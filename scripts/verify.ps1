$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'dotnet.ps1'
& $runner restore NPEduTools.sln --configfile NuGet.Config --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $runner build NPEduTools.sln --no-restore --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $runner test NPEduTools.sln --no-build --no-restore --configuration Release --logger 'trx;LogFileName=prototype.trx' --results-directory .artifacts/test-results
exit $LASTEXITCODE
