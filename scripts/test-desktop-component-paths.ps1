#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PackageResultPath, [string]$HostAssemblyPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$candidate = (Resolve-Path -LiteralPath $PackageResultPath).Path
$fixture = Join-Path $projectRoot ('.artifacts/component-paths/' + [guid]::NewGuid().ToString('N'))
$hostDirectory = Join-Path $fixture 'app/Host'
$project = Join-Path $projectRoot 'tests/NPEduTools.ComponentPaths/NPEduTools.ComponentPaths.csproj'
& (Join-Path $PSScriptRoot 'dotnet.ps1') @('build', $project, '--configuration', 'Release', '--output', $hostDirectory, '--nologo', '--verbosity', 'quiet', '-p:RestoreLockedMode=true')
if ($LASTEXITCODE -ne 0) { throw 'Component path probe build failed.' }
$arguments = @((Join-Path $hostDirectory 'NPEduTools.ComponentPaths.dll'), $candidate)
if ($HostAssemblyPath) { $arguments += (Resolve-Path -LiteralPath $HostAssemblyPath).Path }
& (Join-Path $PSScriptRoot 'dotnet.ps1') @arguments
exit $LASTEXITCODE
