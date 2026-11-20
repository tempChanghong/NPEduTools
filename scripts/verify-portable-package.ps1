param([string]$PackageRoot = $PSScriptRoot, [switch]$AllowInstalledRecordingTools)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = [IO.Path]::GetFullPath($PackageRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$manifest = Get-Content -LiteralPath (Join-Path $root 'package-manifest.json') -Raw | ConvertFrom-Json
$seen = @{}
$installed = @{}
if ($manifest.recordingToolsBundled -eq $false -and $AllowInstalledRecordingTools) {
    $spec = Get-Content -LiteralPath (Join-Path $root 'recording-tools.json') -Raw | ConvertFrom-Json
    foreach ($directory in @('app/Recorder/Tools','app/Host/Recorder/Tools')) {
        foreach ($name in @('ffmpeg.exe','ffprobe.exe','LICENSE','README.txt')) {
            $path = Join-Path $root "$directory/$name"
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Installed recording file missing: $path" }
            if ($spec.files.$name -and (Get-FileHash -LiteralPath $path).Hash -ne $spec.files.$name) { throw "Installed recording tool hash mismatch: $path" }
            $installed[[IO.Path]::GetFullPath($path)] = $true
        }
    }
}
foreach ($file in $manifest.files) {
    $target = [IO.Path]::GetFullPath((Join-Path $root $file.path))
    if (-not $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($target)) { throw 'Invalid manifest path.' }
    $seen[$target] = $true
    if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-Item -LiteralPath $target).Length -ne $file.length -or
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) { throw "Package file mismatch: $($file.path)" }
}
foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse) {
    if ($manifest.recordingToolsBundled -eq $false -and $file.Name -match '^ff(mpeg|probe)\.exe$' -and -not $installed.ContainsKey($file.FullName)) { throw "FFmpeg must not be bundled: $($file.FullName)" }
    if ($file.FullName -ne (Join-Path $root 'package-manifest.json') -and -not $seen.ContainsKey($file.FullName) -and -not $installed.ContainsKey($file.FullName)) { throw "Unlisted file: $($file.FullName)" }
}
foreach ($component in @(@('app','NPEduTools.App'),@('app/Host','NPEduTools.Host'),@('app/Guard','svchost'),@('app/Admin','NPEduTools.ClassIsland.Admin'),@('app/Recorder','NPEduTools.Recorder'),@('app/Host/Recorder','NPEduTools.Recorder'),@('app/Host/Camera','NPEduTools.Camera'))) {
    $directory = Join-Path $root $component[0]
    foreach ($name in @(($component[1]+'.exe'),'coreclr.dll','hostfxr.dll','hostpolicy.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $directory $name))) { throw "Self-contained runtime missing: $directory/$name" }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $directory ($component[1]+'.runtimeconfig.json')) -Raw | ConvertFrom-Json
    if ($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks -or -not $runtime.runtimeOptions.includedFrameworks) { throw "Framework-dependent component: $directory" }
}
# Older packages do not declare exact runtime notice coverage.
if ($manifest.PSObject.Properties.Name -contains 'runtimePacks') {
    $runtimePacks = @{}
    foreach ($config in Get-ChildItem -LiteralPath (Join-Path $root 'app') -Filter '*.runtimeconfig.json' -Recurse -File) {
        $runtime = Get-Content -LiteralPath $config.FullName -Raw | ConvertFrom-Json
        foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
            if ($framework.name -notmatch '^Microsoft\.[A-Za-z0-9.]+$' -or $framework.version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') {
                throw "Invalid bundled runtime identity: $($config.FullName)"
            }
            $id = "$($framework.name.ToLowerInvariant()).runtime.win-x64"
            $version = $framework.version.ToLowerInvariant()
            $runtimePacks["$id/$version"] = @($id, $version)
        }
    }
    if (@($manifest.runtimePacks).Count -ne $runtimePacks.Count) { throw 'Declared runtime packs do not match the published components.' }
    foreach ($pack in $runtimePacks.Keys) {
        if ($pack -notin $manifest.runtimePacks) { throw "Declared runtime packs do not include $pack." }
        $id, $version = $runtimePacks[$pack]
        $directory = Join-Path $root "third-party/$id-$version"
        $nuspec = Join-Path $directory "$id.nuspec"
        if (-not $seen.ContainsKey($nuspec) -or
            -not @(Get-ChildItem -LiteralPath $directory -File | Where-Object Name -match '^LICENSE(\.|$)').Count) {
            throw "Runtime license or package metadata missing: $pack"
        }
        [xml]$metadata = Get-Content -LiteralPath $nuspec -Raw
        if ($metadata.package.metadata.id -ne $id -or $metadata.package.metadata.version -ne $version) {
            throw "Runtime notice metadata mismatch: $pack"
        }
    }
}
if ($manifest.recordingToolsBundled -ne $false) {
    $mediaLock = Get-Content -LiteralPath (Join-Path $root 'recording-bundle.lock.json') -Raw | ConvertFrom-Json
    if ($mediaLock.profile -ne 'npedutools-recording-v1') { throw 'Unknown bundled recording profile.' }
    $sourceMaterial = Join-Path $root ('third-party/ffmpeg/' + $mediaLock.sourceBundle.name)
    if ((Get-Item -LiteralPath $sourceMaterial).Length -ne $mediaLock.sourceBundle.length -or (Get-FileHash -LiteralPath $sourceMaterial).Hash -ne $mediaLock.sourceBundle.sha256) { throw 'Bundled FFmpeg corresponding source mismatch.' }
    foreach ($directory in @('app/Recorder/Tools','app/Host/Recorder/Tools')) {
    $toolsRoot = Join-Path $root $directory
    $toolsManifest = Get-Content -LiteralPath (Join-Path $toolsRoot 'manifest.json') -Raw | ConvertFrom-Json
    if ($toolsManifest.profile -ne $mediaLock.profile -or $toolsManifest.version -ne $mediaLock.version -or $toolsManifest.sourceBundle.sha256 -ne $mediaLock.sourceBundle.sha256) { throw 'Bundled recording metadata mismatch.' }
    foreach ($name in @('ffmpeg.exe','ffprobe.exe','LICENSE','README.txt')) {
        if ($toolsManifest.files.$name -ne $mediaLock.files.$name -or (Get-FileHash -LiteralPath (Join-Path $toolsRoot $name)).Hash -ne $mediaLock.files.$name) { throw 'Recording tool hash mismatch.' }
    }
}
}
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $root 'ClassIsland-plugin/NPEduTools.ClassIsland.Bridge.cipx'))
try {
    if (@($zip.Entries | Where-Object { $_.FullName -match 'TestFixture|^ClassIsland\.|^Avalonia\.|^dotnetCampus\.|^Newtonsoft\.' }).Count) { throw 'Bridge includes forbidden test or host assemblies.' }
    if (-not $zip.GetEntry('manifest.yml')) { throw 'Plugin manifest missing.' }
} finally { $zip.Dispose() }
if ($manifest.examAwareBridgeVersion) {
    $examZip = [IO.Compression.ZipFile]::OpenRead((Join-Path $root "ExamAware2-plugin/npedutools-examaware-bridge-$($manifest.examAwareBridgeVersion).ea2x"))
    try {
        foreach ($entry in @('package.json','dist/main/index.cjs','dist/renderer/index.mjs','LICENSE','THIRD-PARTY-NOTICES.md')) {
            if (-not @($examZip.Entries | Where-Object { $_.FullName.Replace('\','/') -eq $entry }).Count) { throw "ExamAware plugin entry missing: $entry" }
        }
        $examEntry = $examZip.Entries | Where-Object { $_.FullName.Replace('\','/') -eq 'package.json' }
        $reader = [IO.StreamReader]::new($examEntry.Open())
        try { $examManifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($examManifest.version -ne $manifest.examAwareBridgeVersion -or $examManifest.examaware.apiVersion -ne 2) { throw 'ExamAware plugin version mismatch.' }
    } finally { $examZip.Dispose() }
}
Write-Output "PASS: $($manifest.packageId); $($manifest.files.Count) files; runtime, recording tools and plugin verified."
