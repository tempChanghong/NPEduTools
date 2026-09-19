param([string]$PackageRoot = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PackageRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$manifest = Get-Content -LiteralPath (Join-Path $root 'package-manifest.json') -Raw | ConvertFrom-Json
$seen = @{}
foreach ($file in $manifest.files) {
    $target = [IO.Path]::GetFullPath((Join-Path $root $file.path))
    if (-not $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($target)) { throw 'Invalid manifest path.' }
    $seen[$target] = $true
    if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-Item -LiteralPath $target).Length -ne $file.length -or
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) { throw "Package file mismatch: $($file.path)" }
}
foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse) {
    if ($file.FullName -ne (Join-Path $root 'package-manifest.json') -and -not $seen.ContainsKey($file.FullName)) { throw "Unlisted file: $($file.FullName)" }
}
foreach ($component in @(@('app','NPEduTools.App'),@('app/Host','NPEduTools.Host'),@('app/Admin','NPEduTools.ClassIsland.Admin'),@('app/Recorder','NPEduTools.Recorder'),@('app/Host/Recorder','NPEduTools.Recorder'))) {
    $directory = Join-Path $root $component[0]
    foreach ($name in @(($component[1]+'.exe'),'coreclr.dll','hostfxr.dll','hostpolicy.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $directory $name))) { throw "Self-contained runtime missing: $directory/$name" }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $directory ($component[1]+'.runtimeconfig.json')) -Raw | ConvertFrom-Json
    if ($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks -or -not $runtime.runtimeOptions.includedFrameworks) { throw "Framework-dependent component: $directory" }
}
foreach ($directory in @('app/Recorder/Tools','app/Host/Recorder/Tools')) {
    $toolsRoot = Join-Path $root $directory
    $toolsManifest = Get-Content -LiteralPath (Join-Path $toolsRoot 'manifest.json') -Raw | ConvertFrom-Json
    foreach ($name in @('ffmpeg.exe','ffprobe.exe')) {
        if ((Get-FileHash -LiteralPath (Join-Path $toolsRoot $name)).Hash -ne $toolsManifest.files.$name) { throw 'Recording tool hash mismatch.' }
    }
}
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $root 'ClassIsland-plugin/NPEduTools.ClassIsland.Bridge.cipx'))
try {
    if (@($zip.Entries | Where-Object { $_.FullName -match 'TestFixture|^ClassIsland\.|^Avalonia\.|^dotnetCampus\.|^Newtonsoft\.' }).Count) { throw 'Bridge includes forbidden test or host assemblies.' }
    if (-not $zip.GetEntry('manifest.yml')) { throw 'Plugin manifest missing.' }
} finally { $zip.Dispose() }
Write-Output "PASS: $($manifest.packageId); $($manifest.files.Count) files; runtime, recording tools and plugin verified."
