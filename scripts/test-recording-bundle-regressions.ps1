# Exercise real validators with isolated copies. No App launch or screen/audio capture.
param(
    [Parameter(Mandatory=$true)][string]$PackageRoot,
    [string]$RuntimeRoot = '.tools/recording-bundled'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $projectRoot ('.artifacts/recording-bundle-regressions/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$fixture = Join-Path $scratch 'runtime'
New-Item -ItemType Directory -Path $fixture | Out-Null
$checks = @()
$summary = [ordered]@{status='RUNNING';powershell=$PSVersionTable.PSVersion.ToString();capturesStarted=$false;checks=@();error=$null}
function Save-Summary { $summary.checks = $script:checks; $summary | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $scratch 'result.json') -Encoding utf8 }
function Invoke-Case([string]$Name, [hashtable]$Parameters, [string]$ExpectedStatus, [string]$ExpectedError) {
    $resultDirectory = Join-Path $projectRoot '.artifacts/recording-bundle'
    $before = @(Get-ChildItem -LiteralPath $resultDirectory -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
    $caught = $null
    try { & "$PSScriptRoot/test-recording-bundle.ps1" @Parameters } catch { $caught = $_.Exception.Message }
    $after = @(Get-ChildItem -LiteralPath $resultDirectory -Directory | Where-Object { $_.FullName -notin $before })
    if ($after.Count -ne 1) { throw "Unexpected result directory count: $Name" }
    $result = Get-Content (Join-Path $after[0].FullName 'result.json') -Raw | ConvertFrom-Json
    if ($result.status -ne $ExpectedStatus -or $result.capturesStarted -or $result.deviceAcceptance -ne 'NOT_RUN') { throw "Incorrect result status: $Name" }
    if ($ExpectedStatus -eq 'PASSED' -and $caught) { throw $caught }
    if ($ExpectedStatus -eq 'FAILED' -and (-not $caught -or $result.error -notmatch $ExpectedError)) { throw "Incorrect rejection reason: $Name ($caught)" }
    $script:checks += [ordered]@{name=$Name;passed=$true;result=(Join-Path $after[0].FullName 'result.json')}
    Write-Output "PASS: $Name"
}
Save-Summary
Push-Location $projectRoot
try {
    $cache = [IO.Path]::GetFullPath($RuntimeRoot)
    $package = [IO.Path]::GetFullPath($PackageRoot)
    $missingCache = Join-Path $scratch 'no-developer-cache'
    Invoke-Case 'portable package without developer cache' @{PackageRoot=$package;RuntimeRoot=$missingCache} 'PASSED' ''
    # Run only --probe on an isolated recorder with ffprobe deliberately absent.
    $recorderSource = Join-Path $package 'app/Recorder'
    $probeRoot = Join-Path $scratch 'missing-tool-probe'
    foreach ($file in Get-ChildItem -LiteralPath $recorderSource -File -Recurse) {
        $relative = $file.FullName.Substring($recorderSource.Length + 1)
        if ($relative -match '^Tools[\\/]') { continue }
        $destination = Join-Path $probeRoot $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
    [IO.Directory]::CreateDirectory((Join-Path $probeRoot 'Tools')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $cache 'ffmpeg.exe') -Destination (Join-Path $probeRoot 'Tools/ffmpeg.exe')
    $probeText = & (Join-Path $probeRoot 'NPEduTools.Recorder.exe') --probe
    if ($LASTEXITCODE -ne 0) { throw 'Missing-tool probe failed to return a status.' }
    $probe = $probeText | ConvertFrom-Json
    if ($probe.ready -ne $false -or $probe.error -notmatch 'FFmpeg') { throw 'Missing-tool probe incorrectly reported ready.' }
    $checks += [ordered]@{name='missing ffprobe reports not ready without capture';passed=$true;message=$probe.error}
    Write-Output 'PASS: missing ffprobe reports not ready without capture'
    Invoke-Case 'missing standalone runtime' @{RuntimeRoot=$missingCache} 'FAILED' 'manifest.json'
    Invoke-Case 'missing package' @{PackageRoot=(Join-Path $scratch 'no-package')} 'FAILED' 'package-manifest.json'
    # Only this invocation's isolated copy is modified. Preserve fixtures/results for diagnosis.
    $lock = Get-Content "$PSScriptRoot/recording-bundle.lock.json" -Raw | ConvertFrom-Json
    foreach ($name in @('ffmpeg.exe','LICENSE','README.txt','manifest.json',$lock.sourceBundle.name)) {
        Copy-Item -LiteralPath (Join-Path $cache $name) -Destination $fixture
    }
    Invoke-Case 'missing ffprobe' @{RuntimeRoot=$fixture} 'FAILED' 'ffprobe.exe'
    [IO.File]::WriteAllBytes((Join-Path $fixture 'ffprobe.exe'), [byte[]](0,1,2,3))
    Invoke-Case 'corrupt ffprobe' @{RuntimeRoot=$fixture} 'FAILED' 'Recording runtime mismatch'
    Copy-Item -LiteralPath (Join-Path $cache 'ffprobe.exe') -Destination $fixture -Force
    [IO.File]::WriteAllBytes((Join-Path $fixture $lock.sourceBundle.name), [byte[]](0,1,2,3))
    Invoke-Case 'corrupt corresponding source' @{RuntimeRoot=$fixture} 'FAILED' 'source bundle missing or mismatched'
    Copy-Item -LiteralPath (Join-Path $cache $lock.sourceBundle.name) -Destination $fixture -Force
    Invoke-Case 'missing Python' @{RuntimeRoot=$fixture;PythonExecutable='NPEduTools-Python-Does-Not-Exist'} 'FAILED' 'NPEduTools-Python-Does-Not-Exist'
    $summary.status = 'PASSED'
    Save-Summary
    Write-Output "Recording regression results: $(Join-Path $scratch 'result.json')"
} catch {
    $summary.status = 'FAILED'; $summary.error = $_.Exception.Message
    Save-Summary
    throw
} finally { Pop-Location }
