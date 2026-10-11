#requires -Version 5.1
[CmdletBinding()]
param([string]$WebRoot, [string]$BackendRoot, [switch]$CheckOnly, [switch]$Browser, [switch]$Database,
    [switch]$RequireClean, [string]$DesktopCommit, [string]$WebCommit, [string]$BackendCommit)
$ErrorActionPreference = 'Stop'
$arguments = @((Join-Path $PSScriptRoot 'test-npep-operations.mjs'), '--powershell-version', $PSVersionTable.PSVersion.ToString())
if ($WebRoot) { $arguments += @('--web-root', $WebRoot) }
if ($BackendRoot) { $arguments += @('--backend-root', $BackendRoot) }
if ($CheckOnly) { $arguments += '--check' }
if ($Browser) { $arguments += '--browser' }
if ($Database) { $arguments += '--database' }
if ($RequireClean) { $arguments += '--require-clean' }
if ($DesktopCommit) { $arguments += @('--desktop-commit', $DesktopCommit) }
if ($WebCommit) { $arguments += @('--web-commit', $WebCommit) }
if ($BackendCommit) { $arguments += @('--backend-commit', $BackendCommit) }
& node @arguments
if ($LASTEXITCODE -ne 0) { throw "NPEP operations acceptance failed (exit $LASTEXITCODE). Inspect the reported result.json and logs." }
