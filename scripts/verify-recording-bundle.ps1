param([Parameter(Mandatory=$true)][string]$RuntimeRoot, [string]$SourceBundlePath)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RuntimeRoot)
$expected = Get-Content (Join-Path $PSScriptRoot 'recording-bundle.lock.json') -Raw | ConvertFrom-Json
$actual = Get-Content (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json
if ($actual.profile -ne 'npedutools-recording-v1' -or $actual.profile -ne $expected.profile -or $actual.version -ne $expected.version) { throw 'Unexpected recording runtime profile.' }
if ((Get-FileHash (Join-Path $PSScriptRoot 'build-recording-tools.sh')).Hash -ne $expected.recipeSha256 -or $actual.recipeSha256 -ne $expected.recipeSha256) { throw 'Recording recipe changed; rebuild and review the bundle lock.' }
foreach ($name in @('ffmpeg.exe','ffprobe.exe','LICENSE','README.txt')) {
    if ($actual.files.$name -ne $expected.files.$name -or (Get-FileHash (Join-Path $root $name)).Hash -ne $expected.files.$name) { throw "Recording runtime mismatch: $name" }
}
$bundle = if ($SourceBundlePath) { [IO.Path]::GetFullPath($SourceBundlePath) } else { Join-Path $root $expected.sourceBundle.name }
if ($actual.sourceBundle.sha256 -ne $expected.sourceBundle.sha256 -or (Get-Item $bundle).Length -ne $expected.sourceBundle.length -or (Get-FileHash $bundle).Hash -ne $expected.sourceBundle.sha256) { throw 'Corresponding recording source bundle missing or mismatched.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($bundle)
try {
    $prefix = 'NPEduTools-recording-tools-source/'
    foreach ($name in @('build-recording-tools.sh','build-recording-tools.ps1','recording-sources.lock.json','licenses/COPYING.GPLv3','licenses/x264-COPYING','configuration/ffmpeg-config.mak','configuration/x264-config.mak','configuration/toolchain-packages.txt')) {
        if (-not $zip.GetEntry($prefix + $name)) { throw "Recording source material missing: $name" }
    }
    # PowerShell 5.1 emits a JSON array as one object; do not wrap it in another array.
    $sources = Get-Content (Join-Path $PSScriptRoot 'recording-sources.lock.json') -Raw | ConvertFrom-Json
    foreach ($source in $sources) {
        $entry = $zip.GetEntry($prefix + 'sources/' + $source.name)
        if (-not $entry -or $entry.Length -ne $source.length) { throw 'Corresponding upstream source missing.' }
        $stream = $entry.Open(); $hasher = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-','') } finally { $stream.Dispose(); $hasher.Dispose() }
        if ($hash -ne $source.sha256) { throw 'Corresponding upstream source hash mismatch.' }
    }
} finally { $zip.Dispose() }
Write-Output 'PASS: recording binaries, exact upstream sources, licenses and build material verified.'
