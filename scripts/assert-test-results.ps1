# Reject empty/skipped runs rather than accepting dotnet's exit code alone.
param([Parameter(Mandatory = $true)][string]$Path)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing test results: $Path" }
[xml]$report = Get-Content -LiteralPath $Path -Raw
$counters = $report.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
if ($null -eq $counters) { throw 'Test result counters are missing.' }
foreach ($field in @('total', 'executed', 'passed', 'failed')) {
    if ($counters.GetAttribute($field) -notmatch '^\d+$') { throw "Invalid test result counter: $field" }
}
$total = [int]$counters.total
if ($total -le 0 -or [int]$counters.executed -ne $total -or [int]$counters.passed -ne $total -or [int]$counters.failed -ne 0) {
    throw 'Tests must all execute and pass; empty, failed or skipped suites cannot pass this gate.'
}
$results = @($report.SelectNodes("/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult']"))
if ($results.Count -ne $total -or @($results | Where-Object { $_.outcome -ne 'Passed' }).Count -ne 0) {
    throw 'Individual test results do not match the passing counters.'
}
Write-Output "PASS: $total/$total executed test results; none skipped."
