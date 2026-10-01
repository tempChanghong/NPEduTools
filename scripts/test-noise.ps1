#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location -LiteralPath $root
try {
    # Isolated outputs; synthetic PCM/fake capture only. No microphone, server or desktop mutations.
    & dotnet test tests/NPEduTools.Tests/NPEduTools.Tests.csproj --artifacts-path .artifacts/noise-tests -m:1 --verbosity quiet --filter 'FullyQualifiedName~Noise|FullyQualifiedName~TestProcessPath|FullyQualifiedName~ProtocolTests|FullyQualifiedName~RemoteExam|FullyQualifiedName~ClassroomMode|FullyQualifiedName~Recording' --logger 'trx;LogFileName=noise-regression.trx' --results-directory .artifacts/noise-results
    if ($LASTEXITCODE -ne 0) { throw "Noise regression failed (exit $LASTEXITCODE)." }
    Write-Host 'Passed: synthetic PCM, lifecycle, real local pipe, recording and exam regression. Real microphone acceptance remains separate.'
} finally { Pop-Location }
