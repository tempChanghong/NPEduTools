# Installs only the compiler tool, never NPEduTools. Creates a per-user tool uninstall entry.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$destination = Join-Path $projectRoot '.tools/inno-setup7'
$compiler = Join-Path $destination 'ISCC.exe'
if (Test-Path -LiteralPath $compiler) { & "$PSScriptRoot/resolve-inno-setup.ps1" -IsccPath $compiler; return }
New-Item -ItemType Directory -Path (Join-Path $projectRoot '.tools') -Force | Out-Null
$download = Join-Path $projectRoot '.tools/innosetup-7.1.0-x64.exe'
$sha = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
if (-not (Test-Path -LiteralPath $download) -or (Get-FileHash -LiteralPath $download).Hash -ne $sha) {
    for ($attempt=1; $attempt -le 3; $attempt++) {
        try {
            Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe' -OutFile $download
            break
        } catch {
            if ($attempt -eq 3) { throw }
            Start-Sleep -Seconds 2
        }
    }
}
if ((Get-FileHash -LiteralPath $download).Hash -ne $sha) { throw 'Inno Setup download hash mismatch; not executed.' }
$signature = Get-AuthenticodeSignature -LiteralPath $download
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Pyrsys B\.V\.') {
    throw 'Inno Setup publisher signature not verified; not executed.'
}
$process = Start-Process -FilePath $download -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER','/NOICONS',"/DIR=`"$destination`"") -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Inno Setup tool installation failed: $($process.ExitCode)" }
& "$PSScriptRoot/resolve-inno-setup.ps1" -IsccPath $compiler
