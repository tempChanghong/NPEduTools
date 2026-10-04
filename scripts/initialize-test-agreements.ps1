# Only creates synthetic consent fixtures for explicitly isolated UI test endpoints.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Pipe)
$ErrorActionPreference = 'Stop'
if ($Pipe -notmatch '^NPEduTools\.Test\.[A-Za-z0-9_.-]+$') { throw 'Agreement fixtures require an isolated NPEduTools.Test endpoint.' }
$root = Split-Path $PSScriptRoot -Parent
$hasher = [Security.Cryptography.SHA256]::Create()
try {
    $prefix = ([BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($Pipe))).Replace('-', '')).Substring(0,24)
    $path = Join-Path $env:LOCALAPPDATA ('NPEduTools/ui/' + $prefix + '.agreements.json')
    $definitions = @(
        @('npedutools-service','NPEduTools-SERVICE-AGREEMENT.md'),
        @('npep-service','NPEP-SERVICE-AGREEMENT.md'),
        @('npep-privacy','NPEP-PRIVACY-AGREEMENT.md'))
    $entries = foreach ($definition in $definitions) {
        $text = [IO.File]::ReadAllText((Join-Path $root ('docs/legal/' + $definition[1])), [Text.Encoding]::UTF8).Replace("`r`n", "`n")
        @{Id=$definition[0];Version='1.0';EffectiveDate='2026-10-04';
            Hash=[BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).Replace('-','');
            AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
    }
    $null = New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force
    [IO.File]::WriteAllText($path, (@{SchemaVersion=1;Agreements=@($entries)} | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
} finally { $hasher.Dispose() }
