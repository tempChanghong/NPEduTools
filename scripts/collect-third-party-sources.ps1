param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$lock = Get-Content -LiteralPath (Join-Path $root 'third-party/sources.lock.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
foreach ($item in $lock) {
    $target = Join-Path $OutputDirectory $item.name
    if (-not (Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target).Hash -ne $item.sha256) {
        Invoke-WebRequest -Uri $item.url -OutFile $target
    }
    if ((Get-Item -LiteralPath $target).Length -ne $item.length -or (Get-FileHash -LiteralPath $target).Hash -ne $item.sha256) { throw "Source archive mismatch: $($item.name)" }
}
Copy-Item -LiteralPath (Join-Path $root 'third-party/sources.lock.json') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $root 'docs/THIRD-PARTY-MATERIALS.md') -Destination (Join-Path $OutputDirectory 'README.md') -Force
