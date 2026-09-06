param([switch]$NoBuild, [string]$DiagnosticExe)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (Get-Process POWERPNT -ErrorAction SilentlyContinue) {
    throw 'PowerPoint is already running. Close it before this isolated test; existing presentations will not be touched.'
}
if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot 'dotnet.ps1') restore src/NPEduTools.PowerPoint.Diagnostics --locked-mode
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & (Join-Path $PSScriptRoot 'dotnet.ps1') build src/NPEduTools.PowerPoint.Diagnostics --configuration Release --no-restore
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
    $slide3 = $presentation.Slides.Add(3, 12)
    $label3 = $slide3.Shapes.AddTextbox(1, 60, 40, 600, 80)
    $label3.TextFrame.TextRange.Text = 'Links and actions: use the blue button to go to slide 2. Tapping it must not also advance.'
    $linkButton = $slide3.Shapes.AddShape(1, 100, 180, 280, 100)
    $linkButton.TextFrame.TextRange.Text = 'Go to slide 2'
    $linkButton.Fill.ForeColor.RGB = 0xCC6633
    $linkButton.ActionSettings.Item(1).Action = 7 # ppActionHyperlink; internal target only.
    $linkButton.ActionSettings.Item(1).Hyperlink.SubAddress = "$($slide2.SlideID),2,Diagnostic slide 2"
    $slide4 = $presentation.Slides.Add(4, 12)
    $label4 = $slide4.Shapes.AddTextbox(1, 60, 40, 600, 80)
    $label4.TextFrame.TextRange.Text = 'Trigger animation: tap Reveal. The answer should appear without changing the slide.'
    $triggerButton = $slide4.Shapes.AddShape(1, 100, 180, 220, 80)
    $triggerButton.TextFrame.TextRange.Text = 'Reveal'
    $answer = $slide4.Shapes.AddTextbox(1, 100, 310, 500, 70)
    $answer.TextFrame.TextRange.Text = 'Trigger result: stay on slide 4.'
    $interactiveSequence = $slide4.TimeLine.InteractiveSequences.Add(1)
    $effect = $interactiveSequence.AddEffect($answer, 1, 0, 4) # msoAnimTriggerOnShapeClick
    $effect.Timing.TriggerShape = $triggerButton
    $slide5 = $presentation.Slides.Add(5, 12)
    $label5 = $slide5.Shapes.AddTextbox(1, 60, 40, 600, 110)
    $label5.TextFrame.TextRange.Text = 'Menus, pen and gestures: open/close the context menu; choose Pen and write below; test long press and drag away then back. Record any unintended advances.'
    $writingArea = $slide5.Shapes.AddShape(1, 80, 190, 540, 260)
    $writingArea.Fill.ForeColor.RGB = 0xF0F0F0
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
    foreach ($argument in @('--seconds', '9', '--output', (Join-Path $testDirectory 'observation.jsonl'))) {
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
    $show.View.GotoSlide(3)
    $links = Read-Probe 'Showing'
    if ($links.Targets[0].Features.ActionShapes -lt 1 -or $links.Targets[0].Features.Hyperlinks -lt 1) {
        throw 'Internal hyperlink/action inventory mismatch.'
    }
    $show.View.GotoSlide(4)
    $triggers = Read-Probe 'Showing'
    if ($triggers.Targets[0].Features.InteractiveSequences -lt 1) { throw 'Trigger sequence was not detected.' }
    $show.View.GotoSlide(5)
    $show.View.PointerType = 2 # ppSlideShowPointerPen; affects only this test-owned show.
    $pen = Read-Probe 'Showing'
    if ($pen.Targets[0].PointerType -ne 2) { throw 'Pen state was not observed.' }
    $show.View.PointerType = 1
    if (-not $observer.WaitForExit(15000)) { throw 'Diagnostic observer did not terminate.' }
    $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $testDirectory 'observer.stdout.txt')
    $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $testDirectory 'observer.stderr.txt')
    if ($observer.ExitCode -ne 0) { throw 'Diagnostic observer failed.' }
    $frames = @(Get-Content -LiteralPath (Join-Path $testDirectory 'observation.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
    if ($frames[-1].type -ne 'summary' -or -not ($frames | Where-Object { $_.type -eq 'show' -and $_.data.Status -eq 'Showing' })) {
        throw 'Observation trace is missing show frames or final summary.'
    }
    $reports = @(Get-ChildItem -LiteralPath $testDirectory -Filter '*.analysis-*.md')
    if ($reports.Count -ne 1 -or -not (Select-String -LiteralPath $reports[0].FullName -SimpleMatch 'PowerPoint 诊断分析' -Quiet)) {
        throw 'Automatic analysis report was not generated.'
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
    foreach ($pptObject in @($show, $writingArea, $label5, $slide5, $effect, $interactiveSequence, $answer, $triggerButton, $label4, $slide4,
        $linkButton, $label3, $slide3, $shape2, $slide2, $shape1, $slide1, $presentation, $pptApp)) {
        if ($pptObject -and [System.Runtime.InteropServices.Marshal]::IsComObject($pptObject)) {
            $null = [System.Runtime.InteropServices.Marshal]::ReleaseComObject($pptObject)
        }
    }
}
