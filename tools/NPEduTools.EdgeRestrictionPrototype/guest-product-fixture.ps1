param([Parameter(Mandatory)][string]$EvidenceDirectory,
    [ValidateSet('kv','kv-portable')][string]$KvFolder = 'kv',
    [switch]$NeighborOnly)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$output = [IO.Path]::GetFullPath($EvidenceDirectory)
[void][IO.Directory]::CreateDirectory($output)
$temporary = Join-Path $output 'disposable-pg'
[void][IO.Directory]::CreateDirectory($temporary)
$env:TEMP = $temporary
$env:TMP = $temporary
$env:NP_WEBSITE_PRODUCT_EVIDENCE = $output
$env:NP_WEBSITE_PRODUCT_CONSOLE = Join-Path $root 'console/NPEduConsole.ClassCreationDesktop.exe'
if ($NeighborOnly) { $env:NP_WEBSITE_PRODUCT_NEIGHBOR_ONLY = '1' }
$env:PATH = $root + ';' + $env:PATH
& (Join-Path $root 'node.exe') (Join-Path $root 'tests/live-kv.mjs') (Join-Path $root $KvFolder) (Join-Path $root 'pg/bin') '--website-policy-product'
exit $LASTEXITCODE
