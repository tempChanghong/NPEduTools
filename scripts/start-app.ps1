$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$app = Join-Path $projectRoot 'src/NPEduTools.App/bin/Release/net10.0-windows/NPEduTools.App.exe'
if (-not (Test-Path -LiteralPath $app)) { throw 'Run scripts/verify.ps1 to build the desktop app first.' }
# This is the interactive window the user requested to open.
Start-Process -FilePath $app -WorkingDirectory (Split-Path $app -Parent)
