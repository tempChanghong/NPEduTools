$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$destination = Join-Path $projectRoot '.tools/dotnet'
$executable = Join-Path $destination 'dotnet.exe'
$version = (Get-Content (Join-Path $projectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
if ($version -ne '10.0.400') { throw 'Update this bootstrap script and checksum when changing global.json.' }
if (Test-Path -LiteralPath $executable) {
    & $executable --version
    if ($LASTEXITCODE -ne 0) { throw 'Existing local SDK is incomplete; inspect .tools/dotnet before retrying.' }
    return
}
New-Item -ItemType Directory -Force (Join-Path $projectRoot '.tools') | Out-Null
$archive = Join-Path $projectRoot '.tools/dotnet-sdk.zip'
$url = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.400/dotnet-sdk-10.0.400-win-x64.zip'
$expected = '9b8b88590e4da131bfd0da7aa089d0fc04d5418d5f8607ec13d55dc5a17b4399afd54d496c12657fa05c6c6546dc5eab930f26ac6c50f2d3a7712c0fb378c366'
Invoke-WebRequest -Uri $url -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $expected) { throw 'SDK SHA-512 mismatch. Archive was not extracted.' }
Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
& $executable --version
if ($LASTEXITCODE -ne 0) { throw 'SDK verification failed.' }
