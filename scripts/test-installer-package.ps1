# Exercise real package verification and compiler with non-executable fixture files.
param([string]$IsccPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$projectRoot = Split-Path $PSScriptRoot -Parent
$id = [guid]::NewGuid().ToString('N')
$scratch = Join-Path $projectRoot ".artifacts/installer-package-tests/$id"
$payload = Join-Path $scratch 'payload'
$output = Join-Path $scratch 'output'
$checks = @()
$summary = [ordered]@{status='RUNNING';fixtureOnly=$true;applicationInstalled=$false;capturesStarted=$false;longPathsCovered=($PSVersionTable.PSVersion.Major -ge 7);checks=@();error=$null}
New-Item -ItemType Directory -Path $payload -Force | Out-Null
function Put([string]$Path, [string]$Text) {
    $target = Join-Path $payload $Path
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    [IO.File]::WriteAllText($target,$Text,[Text.UTF8Encoding]::new($false))
}
function Zip([string]$Path, [hashtable]$Entries) {
    $target = Join-Path $payload $Path
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    $archive = [IO.Compression.ZipFile]::Open($target,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Entries.Keys) {
            $entry = $archive.CreateEntry($name)
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write([string]$Entries[$name]) } finally { $writer.Dispose() }
        }
    } finally { $archive.Dispose() }
}
function Save-Manifest {
    $manifest.files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Where-Object Name -ne 'package-manifest.json' | ForEach-Object {
        @{path=$_.FullName.Substring($payload.Length + 1).Replace('\','/');length=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $payload 'package-manifest.json') -Encoding utf8
}
function Case([string]$Name, [scriptblock]$Action, [string]$ErrorPattern = '') {
    $caught = $null
    try { & $Action | Out-Host } catch { $caught = $_.Exception.Message }
    if (($ErrorPattern -and (-not $caught -or $caught -notmatch $ErrorPattern)) -or (-not $ErrorPattern -and $caught)) { throw "FAIL: $Name ($caught)" }
    $script:checks += $Name
    Write-Output "PASS: $Name"
}
try {
    foreach ($component in @(@('app','NPEduTools.App'),@('app/Host','NPEduTools.Host'),@('app/Guard','NPEduTools.Guard'),@('app/Admin','NPEduTools.ClassIsland.Admin'),@('app/Recorder','NPEduTools.Recorder'),@('app/Host/Recorder','NPEduTools.Recorder'))) {
        foreach ($file in @(($component[1] + '.exe'),'coreclr.dll','hostfxr.dll','hostpolicy.dll')) { Put ($component[0] + '/' + $file) 'NOT EXECUTABLE: installer test fixture' }
        Put ($component[0] + '/' + $component[1] + '.runtimeconfig.json') '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.11"}]}}'
    }
    Zip 'ClassIsland-plugin/NPEduTools.ClassIsland.Bridge.cipx' @{'manifest.yml'='fixture'}
    Zip 'ExamAware2-plugin/npedutools-examaware-bridge-0.0.0.ea2x' @{'package.json'='{"version":"0.0.0","examaware":{"apiVersion":2}}';'dist/main/index.cjs'='fixture';'dist/renderer/index.mjs'='fixture';'LICENSE'='fixture';'THIRD-PARTY-NOTICES.md'='fixture'}
    Zip 'NPEduTools-source.zip' @{'README.md'='Fixture only; not application source.'}
    Zip 'third-party/ffmpeg/fixture-source.zip' @{'README.md'='Fixture source material.'}
    $source = Get-Item -LiteralPath (Join-Path $payload 'third-party/ffmpeg/fixture-source.zip')
    $media = @{profile='npedutools-recording-v1';version='fixture';sourceBundle=@{name='fixture-source.zip';length=$source.Length;sha256=(Get-FileHash -LiteralPath $source.FullName).Hash};files=@{}}
    foreach ($name in @('ffmpeg.exe','ffprobe.exe','LICENSE','README.txt')) {
        Put "app/Recorder/Tools/$name" 'NOT EXECUTABLE: installer media fixture'
        Put "app/Host/Recorder/Tools/$name" 'NOT EXECUTABLE: installer media fixture'
        $media.files[$name] = (Get-FileHash -LiteralPath (Join-Path $payload "app/Recorder/Tools/$name")).Hash
    }
    $mediaJson = $media | ConvertTo-Json -Depth 5
    foreach ($file in @('recording-bundle.lock.json','app/Recorder/Tools/manifest.json','app/Host/Recorder/Tools/manifest.json')) { Put $file $mediaJson }
    Put 'LICENSE' (Get-Content (Join-Path $projectRoot 'LICENSE') -Raw)
    Put 'third-party/inno-setup/LICENSE.txt' (Get-Content (Join-Path $projectRoot 'installer/Languages/INNO-LICENSE.txt') -Raw)
    Put 'third-party/inventory.json' '[]'
    if ($summary.longPathsCovered) {
        # Exercise the actual Inno 6 failure: no library/source notices may be dropped
        # merely because a maintainer's staging directory exceeds MAX_PATH.
        Put ('third-party/' + ('x' * 100) + '/' + ('y' * 100) + '.txt') 'long-path license fixture'
    }
    $release = "Installer-Fixture-$id"
    $manifest = @{version=$release;packageId="NPEduTools-$release";rid='win-x64';selfContained=$true;recordingToolsBundled=$true;examAwareBridgeVersion='0.0.0';files=@()}
    Save-Manifest
    $parameters = @{PackageRoot=$payload;InstallerVersion='0.2026.1003.0';OutputRoot=$output;IsccPath=$IsccPath}
    Case 'verified complete fixture accepted' { & "$PSScriptRoot/package-installer.ps1" @parameters -ValidateOnly }
    $manifest.recordingToolsBundled = $false; Save-Manifest
    Case 'external FFmpeg package rejected' { & "$PSScriptRoot/package-installer.ps1" @parameters -ValidateOnly } 'bundled recording tools'
    $manifest.recordingToolsBundled = $true
    $manifest.version = 'InDev-20261002'; $manifest.packageId = 'NPEduTools-InDev-20261002'; Save-Manifest
    Case 'published version rejected' { & "$PSScriptRoot/package-installer.ps1" @parameters -ValidateOnly } 'frozen'
    $manifest.version = $release; $manifest.packageId = "NPEduTools-$release"; Save-Manifest
    Put 'app/NPEduTools.App.exe' 'tampered'
    Case 'tampered payload rejected' { & "$PSScriptRoot/package-installer.ps1" @parameters -ValidateOnly } 'Package file mismatch'
    Put 'app/NPEduTools.App.exe' 'NOT EXECUTABLE: installer test fixture'; Save-Manifest
    $material = Join-Path $payload 'third-party/inno-setup/LICENSE.txt'
    $originalLicense = [IO.File]::ReadAllText($material)
    Remove-Item -LiteralPath $material; Save-Manifest
    Case 'missing installer license rejected' { & "$PSScriptRoot/package-installer.ps1" @parameters -ValidateOnly } 'Installer material missing'
    Put 'third-party/inno-setup/LICENSE.txt' $originalLicense; Save-Manifest
    $parameters.OutputRoot = Join-Path $payload 'output'
    Case 'output inside payload rejected' { & "$PSScriptRoot/package-installer.ps1" @parameters } 'outside PackageRoot'
    $parameters.OutputRoot = $output
    Case 'production installer script compiled' { & "$PSScriptRoot/package-installer.ps1" @parameters }
    $exe = Join-Path $output "NPEduTools-$release-win-x64-setup.exe"
    $before = (Get-FileHash -LiteralPath $exe).Hash
    Case 'existing installer rejected' { & "$PSScriptRoot/package-installer.ps1" @parameters } 'already exists'
    if ((Get-FileHash -LiteralPath $exe).Hash -ne $before) { throw 'Existing installer was modified.' }
    # Production ZIPs have a packageId root, so construct that exact layout.
    $rootedZip = Join-Path $output 'rooted-delivery.zip'
    $archive = [IO.Compression.ZipFile]::Open($rootedZip,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse) {
            $entry = $manifest.packageId + '/' + $file.FullName.Substring($payload.Length + 1).Replace('\','/')
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$entry)
        }
    } finally { $archive.Dispose() }
    $zipHash = (Get-FileHash -LiteralPath $rootedZip).Hash
    "$zipHash  $([IO.Path]::GetFileName($rootedZip))" | Set-Content -LiteralPath ($rootedZip + '.sha256') -Encoding ascii
    $delivery = @{status='PACKAGED';installerRequested=$true;packageRoot=$payload;zip=$rootedZip;sha256=$zipHash;installer=$exe;installerSha256=$before}
    $deliveryPath = Join-Path $output 'delivery.json'
    function Save-Delivery { $delivery | ConvertTo-Json | Set-Content -LiteralPath $deliveryPath -Encoding utf8 }
    Save-Delivery
    Case 'matching ZIP and setup accepted' { & "$PSScriptRoot/verify-desktop-delivery.ps1" -PackageResultPath $deliveryPath }
    $delivery.installerRequested = $false; Save-Delivery
    Case 'incomplete setup delivery rejected' { & "$PSScriptRoot/verify-desktop-delivery.ps1" -PackageResultPath $deliveryPath } 'completed ZIP and installer'
    $delivery.installerRequested = $true; $delivery.sha256 = ('0' * 64); Save-Delivery
    Case 'artifact hash mismatch rejected' { & "$PSScriptRoot/verify-desktop-delivery.ps1" -PackageResultPath $deliveryPath } 'artifact hash mismatch'
    $delivery.sha256 = $zipHash; Save-Delivery
    $archive = [IO.Compression.ZipFile]::Open($rootedZip,[IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.GetEntry($manifest.packageId + '/LICENSE'); $entry.Delete()
        $entry = $archive.CreateEntry($manifest.packageId + '/LICENSE')
        $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.Write('different ZIP build') } finally { $writer.Dispose() }
    } finally { $archive.Dispose() }
    $delivery.sha256 = (Get-FileHash -LiteralPath $rootedZip).Hash; Save-Delivery
    "$($delivery.sha256)  $([IO.Path]::GetFileName($rootedZip))" | Set-Content -LiteralPath ($rootedZip + '.sha256') -Encoding ascii
    Case 'valid hashes cannot disguise mismatched ZIP content' { & "$PSScriptRoot/verify-desktop-delivery.ps1" -PackageResultPath $deliveryPath } 'ZIP content differs'
    $summary.status='PASSED'
} catch { $summary.status='FAILED'; $summary.error=$_.Exception.Message; throw }
finally {
    $summary.checks=$checks
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $scratch 'result.json') -Encoding utf8
    Write-Output "INSTALLER_PACKAGE_TEST_RESULT: $(Join-Path $scratch 'result.json')"
}
