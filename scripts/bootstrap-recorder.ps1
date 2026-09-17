$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$version = '9.0.1'
$archiveHash = 'FEC81AE03971D9DD4BE3EBE02E263BD2EC1D789483F931BDBA5F5715E65DA2E9'
$toolRoot = Join-Path $projectRoot '.tools/recording'
$manifestPath = Join-Path $toolRoot 'manifest.json'
if (Test-Path -LiteralPath $manifestPath) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $valid = $manifest.version -eq $version -and $manifest.archiveSha256 -eq $archiveHash
    foreach ($file in @('ffmpeg.exe','ffprobe.exe')) {
        $path = Join-Path $toolRoot $file
        if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $manifest.files.$file) { $valid = $false }
    }
    if ($valid) { Write-Output "Recording runtime $version is verified: $toolRoot"; exit 0 }
}
New-Item -ItemType Directory -Force -Path $toolRoot | Out-Null
$archive = Join-Path $toolRoot "ffmpeg-$version.zip"
if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveHash) {
    # This alias is accepted only with the pinned hash; a future alias change fails closed.
    Invoke-WebRequest -Uri 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveHash) {
    throw 'FFmpeg archive does not match the pinned release. Review the upstream version and checksum before updating this script.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($name in @('ffmpeg.exe','ffprobe.exe','LICENSE','README.txt')) {
        $entries = @($zip.Entries | Where-Object { $_.Name -eq $name })
        if ($entries.Count -ne 1) { throw "Expected one $name in the verified archive." }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0],(Join-Path $toolRoot $name),$true)
    }
} finally { $zip.Dispose() }
$files = @{}
foreach ($name in @('ffmpeg.exe','ffprobe.exe')) { $files[$name] = (Get-FileHash -LiteralPath (Join-Path $toolRoot $name) -Algorithm SHA256).Hash }
@{version=$version;archiveSha256=$archiveHash;source='https://www.gyan.dev/ffmpeg/builds/';files=$files} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Output "Verified recording runtime ready: $toolRoot"
