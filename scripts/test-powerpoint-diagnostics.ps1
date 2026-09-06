param([switch]$NoBuild, [string]$DiagnosticExe)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (Get-Process POWERPNT -ErrorAction SilentlyContinue) {
    throw 'PowerPoint is already running. Close it before this isolated test; existing presentations will not be touched.'
}
if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot 'dotnet.ps1') build src/NPEduTools.PowerPoint.Diagnostics --configuration Release --locked-mode
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not $DiagnosticExe) { $DiagnosticExe = Join-Path $projectRoot 'src/NPEduTools.PowerPoint.Diagnostics/bin/Release/net10.0/NPEduTools.PowerPoint.Diagnostics.exe' }
$diagnosticExe = (Get-Item -LiteralPath $DiagnosticExe).FullName
$testDirectory = Join-Path $projectRoot ('.artifacts/powerpoint-live/' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testDirectory
$evidence = [System.Collections.Generic.List[object]]::new()
$pptApp = $null
$presentation = $null
$show = $null
$observer = $null
function Read-Probe([string]$ExpectedStatus) {
    $json = & $diagnosticExe --probe
    if ($LASTEXITCODE -ne 0) { throw "Probe failed: $json" }
    $snapshot = $json | ConvertFrom-Json
    $evidence.Add($snapshot)
    if ($snapshot.Status -ne $ExpectedStatus) { throw "Expected $ExpectedStatus; got $($snapshot.Status)." }
    return $snapshot
}
try {
    $null = Read-Probe 'NotRunning'
    $pptApp = New-Object -ComObject PowerPoint.Application
    $presentation = $pptApp.Presentations.Add()
    $null = Read-Probe 'NoSlideShow'
    $slide1 = $presentation.Slides.Add(1, 12)
    $shape1 = $slide1.Shapes.AddTextbox(1, 80, 80, 500, 100)
    $shape1.TextFrame.TextRange.Text = 'Diagnostic slide 1 - first click reveals this text'
    $null = $slide1.TimeLine.MainSequence.AddEffect($shape1, 1, 0, 1)
    $slide2 = $presentation.Slides.Add(2, 12)
    $shape2 = $slide2.Shapes.AddTextbox(1, 80, 80, 500, 100)
    $shape2.TextFrame.TextRange.Text = 'Diagnostic slide 2 - no click animation'
    $presentation.SaveAs((Join-Path $testDirectory 'diagnostic-slides.pptx'), 24)
    $show = $presentation.SlideShowSettings.Run()
    $first = Read-Probe 'Showing'
    if ($first.Targets.Count -ne 1 -or $first.Targets[0].SlideIndex -ne 1 -or $first.Targets[0].ClickCount -ne 1) {
        throw 'Initial slide or animation count mismatch.'
    }
    $start = [System.Diagnostics.ProcessStartInfo]::new($diagnosticExe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('--seconds', '4', '--output', (Join-Path $testDirectory 'observation.jsonl'))) {
        $start.ArgumentList.Add($argument)
    }
    $observer = [System.Diagnostics.Process]::Start($start)
    $stdout = $observer.StandardOutput.ReadToEndAsync()
    $stderr = $observer.StandardError.ReadToEndAsync()
    # COM is used only on this test-owned presentation. No synthetic mouse/touch input is emitted.
    $show.View.GotoClick(1)
    $animated = Read-Probe 'Showing'
    if ($animated.Targets[0].SlideIndex -ne 1 -or $animated.Targets[0].ClickIndex -ne 1) { throw 'Animation state mismatch.' }
    $show.View.Next()
    $second = Read-Probe 'Showing'
    if ($second.Targets[0].SlideIndex -ne 2) { throw 'Second slide was not observed.' }
    if (-not $observer.WaitForExit(10000)) { throw 'Diagnostic observer did not terminate.' }
    $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $testDirectory 'observer.stdout.txt')
    $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $testDirectory 'observer.stderr.txt')
    if ($observer.ExitCode -ne 0) { throw 'Diagnostic observer failed.' }
    $frames = @(Get-Content -LiteralPath (Join-Path $testDirectory 'observation.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
    if ($frames[-1].type -ne 'summary' -or -not ($frames | Where-Object { $_.type -eq 'show' -and $_.data.Status -eq 'Showing' })) {
        throw 'Observation trace is missing show frames or final summary.'
    }
    $show.View.Exit()
    $show = $null
    $null = Read-Probe 'NoSlideShow'
    [ordered]@{ passed = $true; syntheticTouchUsed = $false; targetTouchValidationPassed = $false; snapshots = $evidence } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $testDirectory 'summary.json')
    Write-Output "PowerPoint COM and observer smoke test passed: $testDirectory"
} finally {
    if ($observer) {
        if (-not $observer.HasExited) { $observer.Kill() }
        $observer.Dispose()
    }
    if ($show) { try { $show.View.Exit() } catch { Write-Warning 'Could not exit test slide show.' } }
    if ($presentation) { $presentation.Saved = -1; $presentation.Close() }
    if ($pptApp -and $pptApp.Presentations.Count -eq 0) { $pptApp.Quit() }
    foreach ($pptObject in @($show, $shape2, $slide2, $shape1, $slide1, $presentation, $pptApp)) {
        if ($pptObject -and [System.Runtime.InteropServices.Marshal]::IsComObject($pptObject)) {
            $null = [System.Runtime.InteropServices.Marshal]::ReleaseComObject($pptObject)
        }
    }
}
