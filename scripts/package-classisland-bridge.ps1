$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '.artifacts/bridge-package'))
$packageOutput = [IO.Path]::GetFullPath((Join-Path $artifactRoot ([guid]::NewGuid().ToString('N'))))
# PluginSdk's packaging target can recursively replace this directory. Check the final path first.
if (-not $packageOutput.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package output.' }
$sdkDefault = [IO.Path]::GetFullPath((Join-Path $projectRoot 'plugins/NPEduTools.ClassIsland.Bridge/cipx'))
if (-not $sdkDefault.StartsWith([IO.Path]::GetFullPath((Join-Path $projectRoot 'plugins/NPEduTools.ClassIsland.Bridge')) + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe SDK default path.' }
$runner = Join-Path $PSScriptRoot 'dotnet.ps1'
& $runner restore plugins/NPEduTools.ClassIsland.Bridge --locked-mode --configfile NuGet.Config
if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
& $runner msbuild plugins/NPEduTools.ClassIsland.Bridge/NPEduTools.ClassIsland.Bridge.csproj '-t:Build' '-p:Configuration=Release' '-p:CreateCipx=true' "-p:OutputPath=$packageOutput/build/"
if ($LASTEXITCODE -ne 0) { throw 'Plugin packaging failed.' }
$package = Join-Path $packageOutput 'NPEduTools.ClassIsland.Bridge.cipx'
# This pinned SDK writes cipx beside the project. Preserve each verified build in artifacts.
Copy-Item -LiteralPath (Join-Path $sdkDefault 'NPEduTools.ClassIsland.Bridge.cipx') -Destination $package
Copy-Item -LiteralPath (Join-Path $sdkDefault 'checksums.md') -Destination (Join-Path $packageOutput 'checksums.md')
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $entries = @($archive.Entries | ForEach-Object FullName)
    foreach ($name in @('manifest.yml','NPEduTools.ClassIsland.Bridge.dll','NPEduTools.ClassIsland.Bridge.Contracts.dll','README.md','LICENSE')) {
        if ($name -notin $entries) { throw "Missing package entry: $name" }
    }
    if ($entries | Where-Object { $_ -match 'TestFixture|^ClassIsland\.|^Avalonia\.|^dotnetCampus\.|^Newtonsoft\.' }) { throw 'Package contains test controls or host-owned runtime assemblies.' }
} finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
@{package=$package;sha256=$hash;entries=$entries;checkedAt=[DateTimeOffset]::Now} | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $packageOutput 'package-verification.json') -Encoding utf8
Write-Output "P0 package: $package"
Write-Output "SHA256: $hash"
