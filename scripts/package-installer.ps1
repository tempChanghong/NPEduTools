param(
    [Parameter(Mandatory=$true)][string]$PackageRoot,
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$InstallerVersion,
    [string]$OutputRoot = '.artifacts/installers',
    [string]$IsccPath,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
foreach ($part in $InstallerVersion.Split('.')) { if ([long]$part -gt 65535) { throw 'Each installer version component must be 0..65535.' } }
$root = (Resolve-Path -LiteralPath $PackageRoot).Path
$manifestPath = Join-Path $root 'package-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$release = [string]$manifest.version
if ($release -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]*$' -or $manifest.packageId -ne "NPEduTools-$release") { throw 'Invalid package identity.' }
if ($release -in (Get-Content "$PSScriptRoot/published-desktop-versions.json" -Raw | ConvertFrom-Json)) { throw 'Published release is frozen; use a new candidate version.' }
$existingTag = @(& git -C $projectRoot tag --list $release)
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect local release tags.' }
if ($existingTag.Count) { throw 'Release tag already exists; use a new candidate version.' }
if ($manifest.rid -ne 'win-x64' -or $manifest.selfContained -ne $true -or $manifest.recordingToolsBundled -ne $true) { throw 'Installer requires a self-contained win-x64 package with bundled recording tools.' }
# Verification includes the original license/source archives and every packaged file hash.
& "$PSScriptRoot/verify-portable-package.ps1" -PackageRoot $root | Write-Host
foreach ($item in @('LICENSE','NPEduTools-source.zip','third-party/inventory.json','third-party/inno-setup/LICENSE.txt','ClassIsland-plugin/NPEduTools.ClassIsland.Bridge.cipx')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $item) -PathType Leaf)) { throw "Installer material missing: $item" }
}
if (-not $manifest.examAwareBridgeVersion) { throw 'ExamAware bridge package metadata missing.' }
if ($ValidateOnly) { Write-Output "PASS: installer input $release ($InstallerVersion)"; return }
$compiler = & "$PSScriptRoot/resolve-inno-setup.ps1" -IsccPath $IsccPath
$output = if ([IO.Path]::IsPathRooted($OutputRoot)) { [IO.Path]::GetFullPath($OutputRoot) }
    else { [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputRoot)) }
# Prevent compiling into the payload and then recursively embedding the output.
if ($output.Equals($root,[StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($root + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Installer output must be outside PackageRoot.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe = Join-Path $output "NPEduTools-$release-win-x64-setup.exe"
if (Test-Path -LiteralPath $exe) { throw "Installer already exists; use a new OutputRoot: $exe" }
& $compiler '/Qp' "/DPackageRoot=$root" "/DReleaseVersion=$release" "/DInstallerVersion=$InstallerVersion" "/DOutputRoot=$output" (Join-Path $projectRoot 'installer/NPEduTools.iss')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $exe)) { throw "Installer compilation failed: $LASTEXITCODE" }
$sha = (Get-FileHash -LiteralPath $exe).Hash
"$sha  $([IO.Path]::GetFileName($exe))" | Set-Content -LiteralPath ($exe + '.sha256') -Encoding ascii
@{installer=$exe;sha256=$sha;version=$release;installerVersion=$InstallerVersion;packageManifestSha256=(Get-FileHash -LiteralPath $manifestPath).Hash;
  compilerSha256=(Get-FileHash -LiteralPath $compiler).Hash;signed=$false;installed=$false} |
    ConvertTo-Json | Set-Content -LiteralPath ($exe + '.json') -Encoding utf8
Write-Output "INSTALLER_RESULT: $exe"
