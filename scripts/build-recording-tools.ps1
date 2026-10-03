param(
    [Parameter(Mandatory=$true)][string]$MsysRoot,
    [string]$BuildRoot = (Join-Path $env:LOCALAPPDATA 'NPEduTools/Build/recording-9.0.1'),
    [ValidateRange(1,32)][int]$Jobs = 8,
    [switch]$SkipCompile
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$build = [IO.Path]::GetFullPath($BuildRoot)
$runtime = Join-Path $projectRoot '.tools/recording-bundled'
$bash = Join-Path ([IO.Path]::GetFullPath($MsysRoot)) 'usr/bin/bash.exe'
if (-not (Test-Path -LiteralPath $bash -PathType Leaf)) { throw 'MSYS2 UCRT64 is required; see docs/FFMPEG-BUNDLED-BUILD.md.' }
$sources = @(Get-Content (Join-Path $PSScriptRoot 'recording-sources.lock.json') -Raw | ConvertFrom-Json)
$archives = Join-Path $build 'sources'
$src = Join-Path $build 'src'
New-Item -ItemType Directory -Path $archives,$src -Force | Out-Null
foreach ($source in $sources) {
    $archive = Join-Path $archives $source.name
    if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $source.url -OutFile $archive -UseBasicParsing }
    if ((Get-Item -LiteralPath $archive).Length -ne $source.length -or (Get-FileHash -LiteralPath $archive).Hash -ne $source.sha256) { throw "Source archive mismatch: $($source.project)" }
    $sourceDirectory = if ($source.project -eq 'FFmpeg') { 'FFmpeg-' + $source.commit } else { 'x264-' + $source.commit }
    if (-not (Test-Path -LiteralPath (Join-Path $src $sourceDirectory))) {
        & tar -xf $archive -C $src
        if ($LASTEXITCODE) { throw "Source extraction failed: $($source.project)" }
    }
}
$previousMsystem = $env:MSYSTEM
$previousChere = $env:CHERE_INVOKING
try {
    $env:MSYSTEM = 'UCRT64'; $env:CHERE_INVOKING = 'yes'
    if (-not $SkipCompile) {
        $recipePath = (& $bash -c 'cygpath -u "$1"' '_' (Join-Path $PSScriptRoot 'build-recording-tools.sh')).Trim()
        $buildPath = (& $bash -c 'cygpath -u "$1"' '_' $build).Trim()
        & $bash -lc 'bash "$1" "$2" "$3"' '_' $recipePath $buildPath $Jobs *> (Join-Path $build 'build.log')
        if ($LASTEXITCODE) { throw "Recording tools compilation failed; see $build/build.log" }
    }
} finally { $env:MSYSTEM = $previousMsystem; $env:CHERE_INVOKING = $previousChere }
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
foreach ($name in @('ffmpeg.exe','ffprobe.exe')) {
    Copy-Item -LiteralPath (Join-Path $build "prefix/bin/$name") -Destination $runtime -Force
}
$ffSource = Join-Path $src ('FFmpeg-' + $sources[0].commit)
$x264Source = Join-Path $src ('x264-' + $sources[1].commit)
Copy-Item -LiteralPath (Join-Path $ffSource 'COPYING.GPLv3') -Destination (Join-Path $runtime 'LICENSE') -Force
@'
NPEduTools recording tools: FFmpeg 9.0.1 + x264 (8-bit), Windows x64.
GPL-3.0-or-later FFmpeg build with libx264. No enable-nonfree components.
This is a focused build, not the general-purpose Gyan essentials distribution.
Corresponding source, licenses, configuration and build scripts are provided in
third-party/ffmpeg/NPEduTools-recording-tools-source.zip in the application package.
No network support. Windows system DLLs are still required.
No changes to upstream implementation source; configuration is in build-recording-tools.sh.
'@ | Set-Content -LiteralPath (Join-Path $runtime 'README.txt') -Encoding utf8
$materialParent = Join-Path $build ('distribution-source-' + [guid]::NewGuid().ToString('N'))
$materialRoot = Join-Path $materialParent 'NPEduTools-recording-tools-source'
New-Item -ItemType Directory -Path $materialRoot,(Join-Path $materialRoot 'sources'),(Join-Path $materialRoot 'licenses'),(Join-Path $materialRoot 'configuration') | Out-Null
foreach ($source in $sources) { Copy-Item -LiteralPath (Join-Path $archives $source.name) -Destination (Join-Path $materialRoot 'sources') }
foreach ($name in @('build-recording-tools.sh','build-recording-tools.ps1','recording-sources.lock.json')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $materialRoot }
Copy-Item -Path (Join-Path $ffSource 'COPYING*') -Destination (Join-Path $materialRoot 'licenses')
Copy-Item -LiteralPath (Join-Path $ffSource 'LICENSE.md') -Destination (Join-Path $materialRoot 'licenses/FFmpeg-LICENSE.md')
Copy-Item -LiteralPath (Join-Path $x264Source 'COPYING') -Destination (Join-Path $materialRoot 'licenses/x264-COPYING')
foreach ($component in @('gcc','libgcc','crt','headers')) {
    $licenseDirectory = Join-Path $MsysRoot ('ucrt64/share/licenses/' + $component)
    Copy-Item -LiteralPath $licenseDirectory -Destination (Join-Path $materialRoot ('licenses/' + $component)) -Recurse
}
foreach ($name in @('toolchain-packages.txt','compiler-version.txt','assembler-version.txt','ffmpeg-pe.txt','ffprobe-pe.txt')) { Copy-Item -LiteralPath (Join-Path $build $name) -Destination (Join-Path $materialRoot 'configuration') }
foreach ($pair in @(@('build-ffmpeg/config.h','ffmpeg-config.h'),@('build-ffmpeg/ffbuild/config.mak','ffmpeg-config.mak'),@('build-x264/config.mak','x264-config.mak'),@('build-x264/x264_config.h','x264_config.h'))) {
    Copy-Item -LiteralPath (Join-Path $build $pair[0]) -Destination (Join-Path $materialRoot ('configuration/' + $pair[1]))
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/FFMPEG-BUNDLED-BUILD.md') -Destination (Join-Path $materialRoot 'README.md')
$sourceBundle = Join-Path $runtime 'NPEduTools-recording-tools-source.zip'
if (Test-Path -LiteralPath $sourceBundle) { Remove-Item -LiteralPath $sourceBundle -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($materialRoot,$sourceBundle,[IO.Compression.CompressionLevel]::Optimal,$true)
$files = @{}
foreach ($name in @('ffmpeg.exe','ffprobe.exe','LICENSE','README.txt')) { $files[$name] = (Get-FileHash -LiteralPath (Join-Path $runtime $name)).Hash }
$manifest = @{profile='npedutools-recording-v1';version='9.0.1';license='GPL-3.0-or-later';files=$files;sources=$sources;
    recipeSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'build-recording-tools.sh')).Hash;
    sourceBundle=@{name=[IO.Path]::GetFileName($sourceBundle);sha256=(Get-FileHash -LiteralPath $sourceBundle).Hash;length=(Get-Item -LiteralPath $sourceBundle).Length}}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runtime 'manifest.json') -Encoding utf8
Write-Output "Recording bundle prepared: $runtime"
