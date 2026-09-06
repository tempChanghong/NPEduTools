param([string]$DiagnosticExe, [string]$AssistExe)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (Get-Process POWERPNT -ErrorAction SilentlyContinue) { throw 'Close PowerPoint before this isolated test. Existing presentations will not be touched.' }
if (-not $DiagnosticExe) { $DiagnosticExe = Join-Path $projectRoot 'src/NPEduTools.PowerPoint.Diagnostics/bin/Debug/net10.0/NPEduTools.PowerPoint.Diagnostics.exe' }
if (-not $AssistExe) { $AssistExe = Join-Path $projectRoot 'src/NPEduTools.PowerPoint.Assist/bin/Debug/net10.0-windows/NPEduTools.PowerPoint.Assist.exe' }
$testDirectory = Join-Path $projectRoot ('.artifacts/powerpoint-assist-live/' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testDirectory
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AssistTestInput {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint source, uint target, bool attach);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    public static void Activate(IntPtr hwnd) {
        uint pid;
        uint foreground = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        uint current = GetCurrentThreadId();
        bool attached = foreground != current && AttachThreadInput(current, foreground, true);
        try { ShowWindow(hwnd, 5); SetForegroundWindow(hwnd); }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
    }
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    public struct Point { public int X, Y; }
}
'@
$dpiContext = [AssistTestInput]::SetThreadDpiAwarenessContext([IntPtr](-4))
$ppt = $null; $presentation = $null; $show = $null; $assist = $null; $worker = $null
$cursor = [AssistTestInput+Point]::new()
$null = [AssistTestInput]::GetCursorPos([ref]$cursor)
function Read-Show {
    $json = & $DiagnosticExe --probe
    if ($LASTEXITCODE -ne 0) { throw "Probe failed: $json" }
    $snapshot = $json | ConvertFrom-Json
    if ($snapshot.Status -ne 'Showing') { throw "Expected Showing: $json" }
    return $snapshot.Targets[0]
}
function Touch-Tap($target, [uint64]$extra = 4283520896, [switch]$AdvanceDuringTouch) {
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        $show.Activate()
        [AssistTestInput]::Activate([IntPtr]$target.Hwnd)
        Start-Sleep -Milliseconds 200
        if ([AssistTestInput]::GetForegroundWindow().ToInt64() -eq $target.Hwnd) { break }
    }
    $null = [AssistTestInput]::SetCursorPos([int](($target.Left + $target.Right)/2), [int](($target.Top + $target.Bottom)/2))
    Start-Sleep -Milliseconds 200
    if ([AssistTestInput]::GetForegroundWindow().ToInt64() -ne $target.Hwnd) { throw "Test show could not become foreground (actual=$([AssistTestInput]::GetForegroundWindow()), expected=$($target.Hwnd)); no input sent." }
    [AssistTestInput]::mouse_event(2, 0, 0, 0, [UIntPtr]$extra)
    if ($AdvanceDuringTouch) { $show.View.GotoClick(1) }
    Start-Sleep -Milliseconds 70
    [AssistTestInput]::mouse_event(4, 0, 0, 0, [UIntPtr]$extra)
    Start-Sleep -Milliseconds 1000
}
try {
    $ppt = New-Object -ComObject PowerPoint.Application
    $presentation = $ppt.Presentations.Add()
    $slide1 = $presentation.Slides.Add(1, 12)
    $shape = $slide1.Shapes.AddTextbox(1, 80, 80, 500, 100)
    $shape.TextFrame.TextRange.Text = 'Touch once: fade in. Touch again: next slide.'
    $effect = $slide1.TimeLine.MainSequence.AddEffect($shape, 10, 0, 1)
    $effect.Timing.Duration = 0.2
    $slide2 = $presentation.Slides.Add(2, 12)
    $slide2.Shapes.AddTextbox(1, 80, 80, 500, 100).TextFrame.TextRange.Text = 'Touch assist: second slide.'
    # Disable native mouse-click progression so tagged injected input exercises the supplement path.
    $slide1.SlideShowTransition.AdvanceOnClick = 0
    $slide2.SlideShowTransition.AdvanceOnClick = 0
    $presentation.SaveAs((Join-Path $testDirectory 'touch-assist-slides.pptx'), 24)
    $show = $presentation.SlideShowSettings.Run()
    $show.View.PointerType = 1
    $before = Read-Show

    # Verify the WPF executable itself supports the isolated COM worker protocol.
    $start = [Diagnostics.ProcessStartInfo]::new($AssistExe)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true
    $start.ArgumentList.Add('--probe-worker')
    $worker = [Diagnostics.Process]::Start($start)
    $worker.StandardInput.WriteLine('lease'); $worker.StandardInput.Flush()
    $line = $worker.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(7)).GetAwaiter().GetResult()
    if (($line | ConvertFrom-Json).Status -ne 'Showing') { throw "WPF worker failed: $line" }
    $worker.StandardInput.Close()
    if (-not $worker.WaitForExit(3000)) { throw 'WPF worker did not exit after lease closed.' }

    $start = [Diagnostics.ProcessStartInfo]::new($DiagnosticExe)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.ArgumentList.Add('--assist-seconds'); $start.ArgumentList.Add('20')
    $assist = [Diagnostics.Process]::Start($start)
    $output = $assist.StandardOutput.ReadToEndAsync()
    $errors = $assist.StandardError.ReadToEndAsync()
    Start-Sleep -Milliseconds 1500
    Touch-Tap $before
    $animation = Read-Show
    if ($animation.SlideIndex -ne 1 -or $animation.ClickIndex -ne 1) { throw "Touch did not advance exactly one animation: $($animation | ConvertTo-Json -Compress)" }
    Touch-Tap $animation
    $next = Read-Show
    if ($next.SlideIndex -ne 2) { throw 'Second touch did not advance to slide 2.' }
    $show.View.PointerType = 2
    Start-Sleep -Milliseconds 500
    Touch-Tap $next
    $pen = Read-Show
    if ($pen.SlideIndex -ne 2) { throw 'Pen mode unexpectedly advanced.' }
    $show.View.PointerType = 1
    $show.View.GotoSlide(1)
    Start-Sleep -Milliseconds 500
    # Deliberately advance through COM while touch is down, simulating a step already handled by PowerPoint.
    Touch-Tap (Read-Show) -AdvanceDuringTouch
    $native = Read-Show
    if ($native.SlideIndex -ne 1 -or $native.ClickIndex -ne 1) { throw 'An already handled step was duplicated.' }
    if (-not $assist.WaitForExit(15000)) { throw 'Assist did not stop at its deadline.' }
    $statusText = $output.GetAwaiter().GetResult()
    $statusText | Set-Content -LiteralPath (Join-Path $testDirectory 'status.jsonl') -Encoding utf8
    $errors.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $testDirectory 'stderr.txt') -Encoding utf8
    $statuses = @($statusText -split '\r?\n' | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    if ($assist.ExitCode -ne 0 -or $statuses[-1].Sent -ne 2 -or $statuses[-1].NativeAdvances -ne 1) { throw "Expected two supplements and one native advance: $statusText" }
    @{ before=$before; animation=$animation; next=$next; pen=$pen; native=$native; statuses=$statuses; input='Synthetic touch-marked mouse events; not physical touchscreen validation'; wpfWorker='Passed' } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $testDirectory 'summary.json') -Encoding utf8
    Write-Output "PASS: automatic touch -> Fade animation -> next slide; pen ignored; COM advance during touch not duplicated; WPF worker. Evidence: $testDirectory"
}
finally {
    foreach ($child in @($assist, $worker)) { if ($child) { if (-not $child.HasExited) { $child.Kill() }; $child.Dispose() } }
    if ($output) { $output.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $testDirectory 'status.jsonl') -Encoding utf8 }
    if ($errors) { $errors.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $testDirectory 'stderr.txt') -Encoding utf8 }
    if ($show) { try { $show.View.Exit() } catch { } }
    if ($presentation) { try { $presentation.Saved = -1; $presentation.Close() } catch { } }
    if ($ppt) { try { if ($ppt.Presentations.Count -eq 0) { $ppt.Quit() } } catch { } }
    $null = [AssistTestInput]::SetCursorPos($cursor.X, $cursor.Y)
    $null = [AssistTestInput]::SetThreadDpiAwarenessContext($dpiContext)
    foreach ($item in @($show, $presentation, $ppt)) { if ($item -and [Runtime.InteropServices.Marshal]::IsComObject($item)) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) } }
}
