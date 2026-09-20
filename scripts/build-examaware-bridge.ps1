param([switch]$SkipTests, [switch]$PackageOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$plugin = Join-Path $root 'plugins/npedutools-examaware-bridge'
Push-Location $plugin
try {
    if (-not $PackageOnly) {
        & npm ci --ignore-scripts --prefer-offline --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'Plugin dependencies failed.' }
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
    }
    if (-not $SkipTests -and -not $PackageOnly) {
        & (Join-Path $root 'scripts/dotnet.ps1') build (Join-Path $root 'src/NPEduTools.Host') --configuration Release '-m:1' --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Host build failed.' }
        & npm test
        if ($LASTEXITCODE -ne 0) { throw 'Plugin integration checks failed.' }
    }
    $manifest = Get-Content package.json -Raw | ConvertFrom-Json
    foreach ($entry in @('dist/main/index.cjs','dist/renderer/index.mjs')) {
        if (-not (Test-Path -LiteralPath $entry -PathType Leaf)) { throw "Build first: $entry missing." }
    }
    $out = Join-Path $root '.artifacts/examaware-bridge'
    $stage = Join-Path $out ('stage-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    foreach ($entry in @('package.json','package-lock.json','tsconfig.json','build.mjs','README.md','THIRD-PARTY-NOTICES.md','dist','src','test')) {
        Copy-Item -LiteralPath (Join-Path $plugin $entry) -Destination $stage -Recurse
    }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $stage 'LICENSE')
    Copy-Item -LiteralPath (Join-Path $root 'third-party/licenses/ExamAware-plugin-sdk-1.5.2.txt') -Destination (Join-Path $stage 'UPSTREAM-LICENSE.txt')
    $package = Join-Path $out ("npedutools-examaware-bridge-$($manifest.version).ea2x")
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $temporary = Join-Path $out ([Guid]::NewGuid().ToString('N') + '.zip')
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $temporary)
    Move-Item -LiteralPath $temporary -Destination $package -Force
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        foreach ($entry in @('package.json','dist/main/index.cjs','dist/renderer/index.mjs')) {
            if (-not ($archive.Entries | Where-Object { $_.FullName.Replace('\','/') -eq $entry })) { throw "Missing archive entry: $entry" }
        }
    } finally { $archive.Dispose() }
    Get-FileHash -LiteralPath $package -Algorithm SHA256 | Format-List
    Write-Output "Plugin: $package"
} finally { Pop-Location }
