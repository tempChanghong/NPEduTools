param([Parameter(ValueFromRemainingArguments = $true)][string[]]$DotnetArguments)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$localSdk = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/cli-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
Push-Location $projectRoot
try {
    $systemSdk = (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    $dotnetCommand = $null
    foreach ($candidate in @($systemSdk, $localSdk) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique) {
        # Resolve global.json from the project root, not the caller's working directory.
        $null = & $candidate --version 2>$null
        if ($LASTEXITCODE -eq 0) { $dotnetCommand = $candidate; break }
    }
    if (-not $dotnetCommand) { throw 'No compatible SDK found. Install the SDK requested by global.json or run scripts/bootstrap-sdk.ps1.' }
    $env:DOTNET_ROOT = Split-Path $dotnetCommand -Parent
    $env:NPEEDUTOOLS_DOTNET_HOST = $dotnetCommand
    & $dotnetCommand @DotnetArguments
    $result = $LASTEXITCODE
} finally { Pop-Location }
exit $result
