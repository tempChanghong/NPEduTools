param(
    [Parameter(Mandatory)][string]$SessionDirectory,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$recorderExe = Join-Path $projectRoot "src/NPEduTools.Recorder/bin/$Configuration/net10.0-windows/NPEduTools.Recorder.exe"
if (-not (Test-Path -LiteralPath $recorderExe)) { throw 'Build the recorder before recovery.' }
# The worker validates its manifest, preserves all originals, and writes a unique file in this directory.
& $recorderExe --recover (Resolve-Path -LiteralPath $SessionDirectory).Path
if ($LASTEXITCODE -ne 0) { throw "Recording recovery failed ($LASTEXITCODE). Original fragments are retained." }
