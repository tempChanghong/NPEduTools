param([string]$IsccPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$candidates = @()
if ($IsccPath) { $candidates = @($IsccPath) }
else {
    $command = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { $candidates += $command.Source }
    $candidates += @( (Join-Path $projectRoot '.tools/inno-setup7/ISCC.exe'),
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe" )
}
foreach ($candidate in $candidates) {
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        $full = (Resolve-Path -LiteralPath $candidate).Path
        # ISCC/ISCmplr version resources are 0.0.0.0 in official distributions.
        # The .iss preprocessor enforces the actual compiler VER instead.
        return $full
    }
}
throw 'Inno Setup compiler not found. Install Inno Setup 7.1.0+ or run scripts/bootstrap-inno-setup.ps1, then supply -IsccPath if needed.'
