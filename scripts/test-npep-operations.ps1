#requires -Version 5.1
[CmdletBinding()]
param([string]$WebRoot, [string]$BackendRoot, [switch]$CheckOnly, [switch]$Browser, [switch]$Database)
$ErrorActionPreference = 'Stop'
$arguments = @((Join-Path $PSScriptRoot 'test-npep-operations.mjs'), '--powershell-version', $PSVersionTable.PSVersion.ToString())
if ($WebRoot) { $arguments += @('--web-root', $WebRoot) }
if ($BackendRoot) { $arguments += @('--backend-root', $BackendRoot) }
if ($CheckOnly) { $arguments += '--check' }
if ($Browser) { $arguments += '--browser' }
if ($Database) { $arguments += '--database' }
& node @arguments
if ($LASTEXITCODE -ne 0) { throw "NPEP operations acceptance failed (exit $LASTEXITCODE). Inspect the reported result.json and logs." }
