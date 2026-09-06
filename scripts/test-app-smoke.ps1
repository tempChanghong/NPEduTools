$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'dotnet.ps1') --version
if ($LASTEXITCODE -ne 0) { throw 'SDK unavailable.' }
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type -AssemblyName System.Drawing.Common
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SmokeWindowCapture {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
'@
$appExe = Join-Path $projectRoot 'src/NPEduTools.App/bin/Release/net10.0-windows/NPEduTools.App.exe'
$hostExe = Join-Path (Split-Path $appExe -Parent) 'Host/NPEduTools.Host.exe'
$peerDll = Join-Path $projectRoot 'tests/NPEduTools.ClassIsland.TestPeer/bin/Release/net10.0/NPEduTools.ClassIsland.TestPeer.dll'
if (-not (Test-Path -LiteralPath $appExe)) { throw 'Build Release before running the UI smoke test.' }
$pipe = 'NPEduTools.Test.ui.' + [Guid]::NewGuid().ToString('N')
$upstream = 'NPEduTools.Test.ui.' + [Guid]::NewGuid().ToString('N')
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$checks = [Collections.Generic.List[string]]::new()
$runRoot = Join-Path $projectRoot ('.artifacts/app-smoke/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $runRoot | Out-Null

function Start-Peer([string]$mode) {
    $start = [Diagnostics.ProcessStartInfo]::new($env:NPEEDUTOOLS_DOTNET_HOST)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($arg in @($peerDll,$upstream,$mode)) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($start)
    $owned.Add($process)
    if ($process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult() -ne 'READY') {
        throw 'Test peer was not ready.'
    }
    return $process
}

function Start-App {
    $start = [Diagnostics.ProcessStartInfo]::new($appExe)
    $start.UseShellExecute = $false
    # The WPF window is deliberately visible for actual layout / accessibility verification.
    foreach ($arg in @('--pipe',$pipe,'--classisland-pipe',$upstream)) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($start)
    $owned.Add($process)
    return $process
}

function Find-Control([Diagnostics.Process]$process, [string]$id) {
    $process.Refresh()
    if ($process.HasExited -or $process.MainWindowHandle -eq [IntPtr]::Zero) { return $null }
    $root = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
    return $root.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
}

function Wait-Text([Diagnostics.Process]$process, [string]$id, [string]$expected) {
    $end = [DateTime]::UtcNow.AddSeconds(25)
    do {
        if ($process.HasExited) { throw "App exited unexpectedly: $($process.ExitCode)" }
        $control = Find-Control $process $id
        if ($control -and $control.Current.Name -eq $expected) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $end)
    throw "Expected UI $id = '$expected'; actual = '$($control.Current.Name)'"
}

function Click-Control([Diagnostics.Process]$process, [string]$id) {
    $control = Find-Control $process $id
    if (-not $control) { throw "Missing button: $id" }
    $control.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Find-OwnedHost {
    # This random private pipe belongs to this test; match the bundle path as well.
    return Get-CimInstance Win32_Process -Filter "Name='NPEduTools.Host.exe'" |
        Where-Object { $_.ExecutablePath -eq $hostExe -and $_.CommandLine.Contains($pipe) } |
        Select-Object -First 1
}

function Save-Window([Diagnostics.Process]$process, [string]$name) {
    $process.Refresh()
    $rect = [SmokeWindowCapture+Rect]::new()
    if (-not [SmokeWindowCapture]::GetWindowRect($process.MainWindowHandle,[ref]$rect)) { throw 'Cannot read window bounds.' }
    $bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        if (-not [SmokeWindowCapture]::PrintWindow($process.MainWindowHandle,$hdc,2)) { throw 'Cannot capture test window.' }
    } finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    try { $bitmap.Save((Join-Path $runRoot $name),[Drawing.Imaging.ImageFormat]::Png) }
    finally { $bitmap.Dispose() }
}

Write-Output "UI smoke artifacts: $runRoot"
try {
    $peer = Start-Peer 'healthy'
    $app = Start-App
    Wait-Text $app 'Subject' '数学'
    Wait-Text $app 'Connection' '已连接 ClassIsland'
    Save-Window $app 'connected.png'
    $element = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    $element.GetCurrentPattern([Windows.Automation.TransformPattern]::Pattern).Resize(620,600)
    Start-Sleep -Milliseconds 300
    Save-Window $app 'compact.png'
    $checks.Add('App automatically starts Host and renders live course data')
    $firstHost = Find-OwnedHost
    if (-not $firstHost) { throw 'Auto-started Host was not found.' }

    Click-Control $app 'CloseWindow'
    if (-not $app.WaitForExit(5000)) { throw 'Close window did not exit App.' }
    if (-not (Find-OwnedHost)) { throw 'Closing App unexpectedly stopped Host.' }
    $app = Start-App
    Wait-Text $app 'Subject' '数学'
    if ((Find-OwnedHost).ProcessId -ne $firstHost.ProcessId) { throw 'App reopen replaced the Host.' }
    $checks.Add('Closing and reopening App retains the same Host')

    $peer.Kill($true)
    $null = $peer.WaitForExit(5000)
    Wait-Text $app 'Subject' '—'
    Wait-Text $app 'Connection' 'ClassIsland 暂不可用'
    $peer = Start-Peer 'empty'
    Wait-Text $app 'Subject' '暂无科目'
    Wait-Text $app 'LessonState' '当前无课程'
    $checks.Add('Target loss clears old course; target restart resynchronizes empty timetable')

    # Kill only this test's Host, leaving its worker to exercise the lost-parent lease.
    $hostProcess = [Diagnostics.Process]::GetProcessById([int]$firstHost.ProcessId)
    $hostProcess.Kill()
    $null = $hostProcess.WaitForExit(5000)
    $hostProcess.Dispose()
    $end = [DateTime]::UtcNow.AddSeconds(25)
    do {
        Start-Sleep -Milliseconds 200
        $newHost = Find-OwnedHost
    } while ((-not $newHost -or $newHost.ProcessId -eq $firstHost.ProcessId) -and [DateTime]::UtcNow -lt $end)
    if (-not $newHost -or $newHost.ProcessId -eq $firstHost.ProcessId) { throw 'App did not restart lost Host.' }
    Wait-Text $app 'Subject' '暂无科目'
    Wait-Text $app 'Connection' '已连接 ClassIsland'
    $checks.Add('App reconnects and resynchronizes after Host termination')

    Click-Control $app 'StopHost'
    if (-not $app.WaitForExit(8000)) { throw 'Stop background did not exit App.' }
    if (Find-OwnedHost) { throw 'Stop background left Host running.' }
    $checks.Add('Stop background and exit waits for Host termination')
    @{Passed=$true;Checks=@($checks);CompletedAt=[DateTimeOffset]::Now} | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $runRoot 'summary.json') -Encoding utf8
    Write-Output 'PASS: actual WPF controls, App reopen, target restart, Host restart, and shutdown.'
} finally {
    # The random endpoint and verified path ensure that cleanup cannot target a user's Host.
    foreach ($process in $owned) {
        if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(5000) }
        $process.Dispose()
    }
    $remainingHost = Find-OwnedHost
    if ($remainingHost) {
        $process = [Diagnostics.Process]::GetProcessById([int]$remainingHost.ProcessId)
        $process.Kill($true)
        $null = $process.WaitForExit(5000)
        $process.Dispose()
    }
    @($checks) | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'checks.json') -Encoding utf8
}
