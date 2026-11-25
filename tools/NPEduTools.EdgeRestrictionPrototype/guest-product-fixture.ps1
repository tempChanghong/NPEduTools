param([Parameter(Mandatory)][string]$EvidenceDirectory,
    [ValidateSet('kv','kv-portable','compact-kv')][string]$KvFolder = 'kv',
    [switch]$NeighborOnly, [switch]$Timed, [switch]$NaturalClassIsland, [switch]$TimingHold)
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
if ($Timed) { $env:NP_WEBSITE_PRODUCT_TIMING = '1' }
if ($TimingHold) {
    $env:NP_WEBSITE_PRODUCT_TIMING = '1'
    $env:NP_WEBSITE_PRODUCT_TIMING_HOLD = '1'
}
if ($NaturalClassIsland) {
    $env:NP_WEBSITE_PRODUCT_NATURAL = '1'
    $env:ACCESS_TOKEN_EXPIRES_IN = '45m'
}
$env:PATH = $root + ';' + $env:PATH
& (Join-Path $root 'node.exe') (Join-Path $root 'tests/live-kv.mjs') (Join-Path $root $KvFolder) (Join-Path $root 'pg/bin') '--website-policy-product'
exit $LASTEXITCODE
