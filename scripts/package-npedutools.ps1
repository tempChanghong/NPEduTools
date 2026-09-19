param([string]$OutputRoot = '.artifacts/releases')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputRoot))
$id = 'NPEduTools-P4-preview-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$work = Join-Path $output $id
$package = Join-Path $work $id
$app = Join-Path $package 'app'
$locks = Join-Path $work 'publish-locks'
New-Item -ItemType Directory -Path $package,$locks -Force | Out-Null
$runner = Join-Path $PSScriptRoot 'dotnet.ps1'
Push-Location $projectRoot
try {
    & $runner restore NPEduTools.sln --locked-mode --configfile NuGet.Config
    if ($LASTEXITCODE) { throw 'Locked restore failed.' }
    & (Join-Path $PSScriptRoot 'bootstrap-recorder.ps1')
    if ($LASTEXITCODE) { throw 'Recording runtime verification failed.' }
    # Independent self-contained publications: Build-only copy targets are not a publish manifest.
    $components = @(
        @{Project='NPEduTools.App';Directory=$app},
        @{Project='NPEduTools.Host';Directory=(Join-Path $app 'Host')},
        @{Project='NPEduTools.ClassIsland.Admin';Directory=(Join-Path $app 'Admin')},
        @{Project='NPEduTools.Recorder';Directory=(Join-Path $work 'recorder')})
    foreach ($component in $components) {
        & $runner @('publish',("src/" + $component.Project),'--configuration','Release','--runtime','win-x64','--self-contained','true','--output',$component.Directory,
            '-m:1','-p:PublishTrimmed=false','-p:PublishSingleFile=false',"-p:PackageLockDirectory=$locks")
        if ($LASTEXITCODE) { throw "Publish failed: $($component.Project)" }
    }
    $recorder = Join-Path $work 'recorder'
    $toolsDirectory = Join-Path $recorder 'Tools'
    New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
    foreach ($name in @('ffmpeg.exe','ffprobe.exe','LICENSE','README.txt','manifest.json')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot ".tools/recording/$name") -Destination $toolsDirectory
    }
    foreach ($destination in @((Join-Path $app 'Recorder'),(Join-Path $app 'Host/Recorder'))) {
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -Path (Join-Path $recorder '*') -Destination $destination -Recurse -Force
    }
    # Obtain the exact path emitted by the SDK package verifier; never pick a stale newest package.
    $bridgeOutput = @(& (Join-Path $PSScriptRoot 'package-classisland-bridge.ps1'))
    $bridgeLine = @($bridgeOutput | Where-Object { $_ -is [string] -and $_.StartsWith('P0 package: ') })
    if ($bridgeLine.Count -ne 1) { throw 'Verified bridge package not returned.' }
    $bridgePath = $bridgeLine[0].Substring('P0 package: '.Length)
    $pluginDirectory = Join-Path $package 'ClassIsland-plugin'
    New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
    Copy-Item -LiteralPath $bridgePath -Destination $pluginDirectory
    Copy-Item -LiteralPath (Join-Path (Split-Path $bridgePath) 'checksums.md') -Destination $pluginDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $package
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/PORTABLE-CLASSROOM-GUIDE.md') -Destination (Join-Path $package 'README.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/CLASSROOM-ACCEPTANCE.md') -Destination $package
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/DEPENDENCIES.md') -Destination $package
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'verify-portable-package.ps1') -Destination $package
    @'
@echo off
start "" "%~dp0app\NPEduTools.App.exe"
'@ | Set-Content -LiteralPath (Join-Path $package 'Start-NPEduTools.cmd') -Encoding ascii
    # Preserve actual runtime package metadata and available notices. No test libraries are published.
    $notices = Join-Path $package 'third-party'
    New-Item -ItemType Directory -Path $notices -Force | Out-Null
    $dependencies = @{}
    foreach ($deps in Get-ChildItem -LiteralPath $app -Filter '*.deps.json' -Recurse) {
        $json = Get-Content -LiteralPath $deps.FullName -Raw | ConvertFrom-Json -AsHashtable
        foreach ($key in $json.libraries.Keys) { if ($json.libraries[$key].type -eq 'package') { $dependencies[$key] = $true } }
    }
    $inventory = foreach ($key in $dependencies.Keys | Sort-Object) {
        $cache = Join-Path $env:NUGET_PACKAGES $key.ToLowerInvariant()
        $nuspec = Get-ChildItem -LiteralPath $cache -Filter '*.nuspec' | Select-Object -First 1
        if (-not $nuspec) { throw "Dependency metadata missing: $key" }
        [xml]$meta = Get-Content -LiteralPath $nuspec.FullName -Raw
        $directory = Join-Path $notices ($key.Replace('/','-'))
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        Copy-Item -LiteralPath $nuspec.FullName -Destination $directory
        foreach ($file in Get-ChildItem -LiteralPath $cache -File -Recurse | Where-Object { $_.Name -match '^(LICENSE|COPYING|THIRD.PARTY.NOTICES|NOTICE)(\.|$)' }) {
            $relative = [IO.Path]::GetRelativePath($cache,$file.FullName)
            $target = Join-Path $directory $relative
            New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
        @{package=$key;license=[string]$meta.package.metadata.license.InnerText;licenseUrl=[string]$meta.package.metadata.licenseUrl;projectUrl=[string]$meta.package.metadata.projectUrl}
    }
    @($inventory) | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $notices 'inventory.json') -Encoding utf8
    # Include the precise working source, including uncommitted implementation, without local data/binaries.
    $sourceZip = Join-Path $package 'NPEduTools-source.zip'
    $archive = [IO.Compression.ZipFile]::Open($sourceZip,[IO.Compression.ZipArchiveMode]::Create)
    try {
        $files = @(git -c core.quotepath=false ls-files --cached --others --exclude-standard -- src plugins scripts tests tools images '*.sln' '*.props' global.json NuGet.Config LICENSE .gitignore .editorconfig README.md 'docs/*.md') | Sort-Object -Unique
        foreach ($file in $files) {
            if ((Test-Path -LiteralPath $file -PathType Leaf) -and $file -notmatch '(^|/)(bin|obj|cipx)/') {
                $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $projectRoot $file),$file,[IO.Compression.CompressionLevel]::Optimal)
            }
        }
    } finally { $archive.Dispose() }
    Copy-Item -LiteralPath $locks -Destination (Join-Path $package 'build-locks') -Recurse
    $manifest = @{packageId=$id;channel='classroom-preview';rid='win-x64';selfContained=$true;createdAt=[DateTimeOffset]::Now;
        sourceCommit=(git rev-parse HEAD);workingTreeDirty=([bool](git status --porcelain));sourceArchiveSha256=(Get-FileHash $sourceZip).Hash;
        sdk=(& $env:NPEEDUTOOLS_DOTNET_HOST --version);classIslandValidated='2.1.0.1 local build';bridgeVersion='0.2.0.0';files=@()}
    $manifest.files = @(Get-ChildItem -LiteralPath $package -File -Recurse | Sort-Object FullName | ForEach-Object {
        @{path=[IO.Path]::GetRelativePath($package,$_.FullName).Replace('\','/');length=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Encoding utf8
    & (Join-Path $PSScriptRoot 'verify-portable-package.ps1') -PackageRoot $package
    $zip = Join-Path $output ($id + '-win-x64.zip')
    [IO.Compression.ZipFile]::CreateFromDirectory($package,$zip,[IO.Compression.CompressionLevel]::Optimal,$true)
    $sha = (Get-FileHash -LiteralPath $zip).Hash
    "$sha  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ascii
    @{packageRoot=$package;zip=$zip;sha256=$sha} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'result.json') -Encoding utf8
    Write-Output "PACKAGE_RESULT: $(Join-Path $work 'result.json')"
} finally { Pop-Location }
