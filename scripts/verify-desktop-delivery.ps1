#requires -Version 5.1
# Verify a matching ZIP/setup pair without installing or starting the application.
param([Parameter(Mandatory=$true)][string]$PackageResultPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$result = Get-Content -LiteralPath $PackageResultPath -Raw | ConvertFrom-Json
if ($result.status -ne 'PACKAGED' -or $result.installerRequested -ne $true -or -not $result.installer) {
    throw 'Delivery requires a completed ZIP and installer build.'
}
$root = (Resolve-Path -LiteralPath $result.packageRoot).Path.TrimEnd('\')
& "$PSScriptRoot/verify-portable-package.ps1" -PackageRoot $root | Write-Host
$manifestPath = Join-Path $root 'package-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$installer = Get-Content -LiteralPath ($result.installer + '.json') -Raw | ConvertFrom-Json
if ($installer.version -ne $manifest.version -or $installer.installed -ne $false -or
    $installer.packageManifestSha256 -ne (Get-FileHash -LiteralPath $manifestPath).Hash) {
    throw 'Installer metadata does not match the verified package.'
}
foreach ($artifact in @(@{path=$result.zip;hash=$result.sha256},
                       @{path=$result.installer;hash=$result.installerSha256})) {
    $hash = (Get-FileHash -LiteralPath $artifact.path -Algorithm SHA256).Hash
    if ($hash -ne $artifact.hash -or ($artifact.path -eq $result.installer -and $hash -ne $installer.sha256)) {
        throw 'Delivery artifact hash mismatch.'
    }
    $line = (Get-Content -LiteralPath ($artifact.path + '.sha256') -Raw).Trim()
    if ($line -ne "$hash  $([IO.Path]::GetFileName($artifact.path))") { throw 'Artifact checksum sidecar mismatch.' }
}
# A valid loose directory and valid ZIP must also be the same build. Reject extra,
# missing or duplicate archive entries instead of accepting a stale ZIP beside it.
$expected = @{}
foreach ($file in $manifest.files) { $expected[$file.path] = $file.sha256 }
$expected['package-manifest.json'] = (Get-FileHash -LiteralPath $manifestPath).Hash
$archive = [IO.Compression.ZipFile]::OpenRead($result.zip)
$seen = @{}
try {
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        $prefix = $manifest.packageId + '/'
        if (-not $entry.FullName.StartsWith($prefix, [StringComparison]::Ordinal)) { throw 'Unexpected ZIP root.' }
        $path = $entry.FullName.Substring($prefix.Length)
        if (-not $expected.ContainsKey($path) -or $seen.ContainsKey($path)) { throw 'Unexpected or duplicate ZIP entry.' }
        $stream = $entry.Open(); $hasher = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-','') }
        finally { $stream.Dispose(); $hasher.Dispose() }
        if ($hash -ne $expected[$path]) { throw "ZIP content differs from package: $path" }
        $seen[$path] = $true
    }
    if ($seen.Count -ne $expected.Count) { throw 'ZIP is missing package entries.' }
} finally { $archive.Dispose() }
Write-Output "PASS: matching ZIP/setup delivery $($manifest.packageId); $($seen.Count) archive files; no installation or capture."
