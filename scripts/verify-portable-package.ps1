param([string]$PackageRoot = $PSScriptRoot, [switch]$AllowInstalledRecordingTools)
$ErrorActionPreference = 'Stop'
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
foreach ($component in @(@('app','NPEduTools.App'),@('app/Host','NPEduTools.Host'),@('app/Admin','NPEduTools.ClassIsland.Admin'),@('app/Recorder','NPEduTools.Recorder'),@('app/Host/Recorder','NPEduTools.Recorder'))) {
    $directory = Join-Path $root $component[0]
    foreach ($name in @(($component[1]+'.exe'),'coreclr.dll','hostfxr.dll','hostpolicy.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $directory $name))) { throw "Self-contained runtime missing: $directory/$name" }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $directory ($component[1]+'.runtimeconfig.json')) -Raw | ConvertFrom-Json
    if ($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks -or -not $runtime.runtimeOptions.includedFrameworks) { throw "Framework-dependent component: $directory" }
}
if ($manifest.recordingToolsBundled -ne $false) { foreach ($directory in @('app/Recorder/Tools','app/Host/Recorder/Tools')) {
    $toolsRoot = Join-Path $root $directory
    $toolsManifest = Get-Content -LiteralPath (Join-Path $toolsRoot 'manifest.json') -Raw | ConvertFrom-Json
    foreach ($name in @('ffmpeg.exe','ffprobe.exe')) {
        if ((Get-FileHash -LiteralPath (Join-Path $toolsRoot $name)).Hash -ne $toolsManifest.files.$name) { throw 'Recording tool hash mismatch.' }
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
