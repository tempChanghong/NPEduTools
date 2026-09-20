#requires -Version 7.5
# No server, database or product processes are started. Preserve timestamp strings.
param()
$ErrorActionPreference = 'Stop'
$schema = Get-Content (Join-Path $PSScriptRoot 'n1-wire.schema.json') -Raw | ConvertFrom-Json -AsHashtable
$fixtures = Get-Content (Join-Path $PSScriptRoot 'n1-examples.json') -Raw | ConvertFrom-Json -DateKind String
$passed = 0
foreach ($case in $fixtures.cases) {
    if (-not $schema.definitions.ContainsKey($case.definition)) { throw "Missing definition: $($case.definition)" }
    $schema['$ref'] = '#/definitions/' + $case.definition
    $actual = Test-Json -Json ($case.value | ConvertTo-Json -Depth 64 -Compress) -Schema ($schema | ConvertTo-Json -Depth 64 -Compress) -ErrorAction SilentlyContinue
    if ([bool]$actual -ne [bool]$case.valid) { throw "Unexpected validation result: $($case.name)" }
    $passed++
}
Write-Output "PASS: $passed N1 structural examples; authorization, concurrency and persistence are not tested."
