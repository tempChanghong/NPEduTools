# Synthetic media and package checks only; does not start screen/audio capture or the App.
param(
    [string]$RuntimeRoot = '.tools/recording-bundled',
    [string]$PackageRoot,
    [string]$PythonExecutable = 'python'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$run = Join-Path $projectRoot ('.artifacts/recording-bundle/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$resultPath = Join-Path $run 'result.json'
$result = [ordered]@{status='RUNNING';runtime=$null;package=$PackageRoot;mode=$(if ($PackageRoot) { 'package' } else { 'runtime' });capturesStarted=$false;deviceAcceptance='NOT_RUN';checks=@();error=$null}
function Save-Result { $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding utf8 }
Save-Result
Push-Location $projectRoot
try {
    if ($PackageRoot) {
        $package = [IO.Path]::GetFullPath($PackageRoot)
        $manifest = Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw | ConvertFrom-Json
        if ($manifest.recordingToolsBundled -ne $true) { throw 'This entry requires a package with bundled recording tools.' }
        & "$PSScriptRoot/verify-portable-package.ps1" -PackageRoot $package
        if (-not $?) { throw 'Portable package verification failed.' }
        $mediaRuntime = Join-Path $package 'app/Recorder/Tools'
        $mediaLock = Get-Content -LiteralPath (Join-Path $package 'recording-bundle.lock.json') -Raw | ConvertFrom-Json
        $sourceBundle = Join-Path $package ('third-party/ffmpeg/' + $mediaLock.sourceBundle.name)
        & "$PSScriptRoot/verify-recording-bundle.ps1" -RuntimeRoot $mediaRuntime -SourceBundlePath $sourceBundle
        if (-not $?) { throw 'Bundled recording source verification failed.' }
        foreach ($component in @('app/Recorder','app/Host/Recorder')) {
            $probe = & (Join-Path $package "$component/NPEduTools.Recorder.exe") --probe
            if ($LASTEXITCODE -ne 0 -or ($probe | ConvertFrom-Json).ready -ne $true) { throw "Recorder not ready: $component" }
        }
        $result.checks += 'package integrity and both recorder probes'
    } else {
        $mediaRuntime = [IO.Path]::GetFullPath($RuntimeRoot)
        & "$PSScriptRoot/verify-recording-bundle.ps1" -RuntimeRoot $mediaRuntime
        if (-not $?) { throw 'Recording bundle verification failed.' }
    }
    $result.runtime = $mediaRuntime
    $result.checks += 'exact binaries and corresponding sources'
    & $PythonExecutable "$PSScriptRoot/test-bundled-recording-tools.py" --runtime $mediaRuntime --report (Join-Path $run 'media.json')
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic media tests failed.' }
    $media = Get-Content -LiteralPath (Join-Path $run 'media.json') -Raw | ConvertFrom-Json
    if ($media.passed -ne $true) { throw 'Synthetic media result is not a pass.' }
    $result.checks += $media.checks
    $result.status = 'PASSED'
    Save-Result
    Write-Output "Recording checks passed: $resultPath"
} catch {
    $result.status = 'FAILED'
    $result.error = $_.Exception.Message
    Save-Result
    throw
} finally { Pop-Location }
