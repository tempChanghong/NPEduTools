# Compatible with Windows PowerShell 5.1 and PowerShell 7. No elevation required.
param(
    [string]$PackageRoot = $PSScriptRoot,
    [string]$ArchivePath
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\','/')
$manifestPath = Join-Path $root 'recording-tools.json'
if (-not (Test-Path -LiteralPath (Join-Path $root 'app/NPEduTools.App.exe')) -or -not (Test-Path -LiteralPath $manifestPath)) {
    throw 'Run this script from an extracted NPEduTools release package.'
}
$spec = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$destinations = @('app/Recorder/Tools','app/Host/Recorder/Tools') | ForEach-Object { Join-Path $root $_ }
# Never replace media tools while this copy of the recorder is running.
$running = @(Get-Process -Name 'NPEduTools.Recorder','ffmpeg','ffprobe' -ErrorAction SilentlyContinue)
foreach ($process in $running) {
    try { $processPath = $process.Path } catch { throw 'Unable to verify running media processes; close them before installing.' }
    if (-not $processPath -or $processPath.StartsWith($root + '\',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Close NPEduTools and its recording processes before installing recording tools.'
    }
}
$stageRoot = Join-Path ([IO.Path]::GetTempPath()) 'NPEduTools-Recording-Install'
$stage = [IO.Path]::GetFullPath((Join-Path $stageRoot ([guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    if ($ArchivePath) {
        $archive = [IO.Path]::GetFullPath($ArchivePath)
        if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw 'The supplied FFmpeg ZIP does not exist.' }
    } else {
        $archive = Join-Path $stage 'ffmpeg.zip'
        Write-Output "Downloading FFmpeg $($spec.version) directly from its upstream distributor: $($spec.downloadUrl)"
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $spec.downloadUrl -OutFile $archive -UseBasicParsing
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $spec.archiveSha256) {
        throw 'FFmpeg ZIP checksum mismatch. No installed recording tools have been changed.'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($name in @('ffmpeg.exe','ffprobe.exe','LICENSE','README.txt')) {
            $entries = @($zip.Entries | Where-Object { $_.Name -ceq $name })
            if ($entries.Count -ne 1) { throw "Expected exactly one $name in the verified ZIP." }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0],(Join-Path $stage $name),$false)
        }
    } finally { $zip.Dispose() }
    foreach ($name in @('ffmpeg.exe','ffprobe.exe')) {
        if ((Get-FileHash -LiteralPath (Join-Path $stage $name)).Hash -ne $spec.files.$name) { throw "Recording tool checksum mismatch: $name" }
    }
    foreach ($destination in $destinations) {
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        foreach ($name in @('ffmpeg.exe','ffprobe.exe','LICENSE','README.txt')) {
            Copy-Item -LiteralPath (Join-Path $stage $name) -Destination $destination -Force
        }
    }
    foreach ($destination in $destinations) {
        foreach ($name in @('ffmpeg.exe','ffprobe.exe')) {
            if ((Get-FileHash -LiteralPath (Join-Path $destination $name)).Hash -ne $spec.files.$name) { throw "Installed tool verification failed: $destination/$name" }
        }
    }
    Write-Output 'Recording tools installed and verified. Reopen NPEduTools and refresh recording devices before a short test recording.'
} finally {
    # This is a fresh GUID directory under our own temporary root, never the supplied ZIP.
    $safeRoot = [IO.Path]::GetFullPath($stageRoot).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (-not $stage.StartsWith($safeRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe temporary directory.' }
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
