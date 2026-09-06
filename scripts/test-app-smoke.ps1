param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
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
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
'@
$appExe = Join-Path $projectRoot "src/NPEduTools.App/bin/$Configuration/net10.0-windows/NPEduTools.App.exe"
$hostExe = Join-Path (Split-Path $appExe -Parent) 'Host/NPEduTools.Host.exe'
$peerDll = Join-Path $projectRoot "tests/NPEduTools.ClassIsland.TestPeer/bin/$Configuration/net10.0/NPEduTools.ClassIsland.TestPeer.dll"
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

function Start-App([switch]$AtLogin) {
    $start = [Diagnostics.ProcessStartInfo]::new($appExe)
    $start.UseShellExecute = $false
    # The WPF window is deliberately visible for actual layout / accessibility verification.
    foreach ($arg in @('--pipe',$pipe,'--classisland-pipe',$upstream)) { $start.ArgumentList.Add($arg) }
    if ($AtLogin) { $start.ArgumentList.Add('--startup') }
    $process = [Diagnostics.Process]::Start($start)
    $owned.Add($process)
    return $process
}

function Find-Control([Diagnostics.Process]$process, [string]$id) {
    $process.Refresh()
    if ($process.HasExited) { return $null }
    $root = Find-Main $process
    if (-not $root) { return $null }
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
    return $root.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
}

function Find-Main([Diagnostics.Process]$process) {
    $condition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'NPEduTools'))
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $condition)
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

function Select-Page([Diagnostics.Process]$process, [string]$id) {
    (Find-Control $process $id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 150
    if ($id -eq 'SettingsTab') {
        (Find-Control $process 'ClassIslandDetails').GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    }
}

function Find-OwnedHost {
    # This random private pipe belongs to this test; match the bundle path as well.
    return Get-CimInstance Win32_Process -Filter "Name='NPEduTools.Host.exe'" |
        Where-Object { $_.ExecutablePath -eq $hostExe -and $_.CommandLine.Contains($pipe) } |
        Select-Object -First 1
}

function Save-Window([Diagnostics.Process]$process, [string]$name) {
    $process.Refresh()
    $hwnd = [IntPtr](Find-Main $process).Current.NativeWindowHandle
    $rect = [SmokeWindowCapture+Rect]::new()
    if (-not [SmokeWindowCapture]::GetWindowRect($hwnd,[ref]$rect)) { throw 'Cannot read window bounds.' }
    $bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        if (-not [SmokeWindowCapture]::PrintWindow($hwnd,$hdc,2)) { throw 'Cannot capture test window.' }
    } finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    try { $bitmap.Save((Join-Path $runRoot $name),[Drawing.Imaging.ImageFormat]::Png) }
    finally { $bitmap.Dispose() }
}

function Find-Quick([Diagnostics.Process]$process, [string]$id) {
    $condition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'NPEduTools 快捷工具'))
    $quick = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $condition)
    if (-not $id) { return $quick }
    $idCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $quick.FindFirst([Windows.Automation.TreeScope]::Descendants,$idCondition)
}

function Click-Quick([Diagnostics.Process]$process, [string]$id) {
    (Find-Quick $process $id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500
}

function Save-Quick([Diagnostics.Process]$process, [string]$name) {
    $quick = Find-Quick $process ''
    $hwnd = [IntPtr]$quick.Current.NativeWindowHandle
    $rect = [SmokeWindowCapture+Rect]::new()
    $null = [SmokeWindowCapture]::GetWindowRect($hwnd,[ref]$rect)
    $bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try { $null = [SmokeWindowCapture]::PrintWindow($hwnd,$hdc,2) }
    finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    try { $bitmap.Save((Join-Path $runRoot $name),[Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
}

function Exercise-EdgePointer([Diagnostics.Process]$process) {
    $original = [SmokeWindowCapture+Point]::new()
    $null = [SmokeWindowCapture]::GetCursorPos([ref]$original)
    try {
        $quick = Find-Quick $process ''
        $hwnd = [IntPtr]$quick.Current.NativeWindowHandle
        $rect = [SmokeWindowCapture+Rect]::new()
        $null = [SmokeWindowCapture]::GetWindowRect($hwnd,[ref]$rect)
        $startY = $rect.Top
        $point = [SmokeWindowCapture+Point]::new()
        $point.X = [int](($rect.Left+$rect.Right)/2); $point.Y = [int](($rect.Top+$rect.Bottom)/2)
        $null = [SmokeWindowCapture]::SetCursorPos($point.X,$point.Y)
        if ([SmokeWindowCapture]::WindowFromPoint($point) -ne $hwnd) { throw 'Test handle is obscured; no pointer input sent.' }
        [SmokeWindowCapture]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
        Start-Sleep -Milliseconds 80
        for ($offset=12; $offset -le 120; $offset+=12) {
            $null = [SmokeWindowCapture]::SetCursorPos($point.X,$point.Y-$offset)
            Start-Sleep -Milliseconds 35
        }
        [SmokeWindowCapture]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
        Start-Sleep -Milliseconds 250
        $null = [SmokeWindowCapture]::GetWindowRect($hwnd,[ref]$rect)
        if ($rect.Top -ge $startY-50 -or $rect.Bottom-$rect.Top -gt 200) { throw 'Edge drag did not move the collapsed handle.' }
        $point.X = [int](($rect.Left+$rect.Right)/2); $point.Y = [int](($rect.Top+$rect.Bottom)/2)
        $null = [SmokeWindowCapture]::SetCursorPos($point.X,$point.Y)
        if ([SmokeWindowCapture]::WindowFromPoint($point) -ne $hwnd) { throw 'Moved handle is obscured.' }
        [SmokeWindowCapture]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
        Start-Sleep -Milliseconds 80
        [SmokeWindowCapture]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
        Start-Sleep -Milliseconds 400
        $null = [SmokeWindowCapture]::GetWindowRect($hwnd,[ref]$rect)
        if ($rect.Bottom-$rect.Top -lt 300) { throw 'A physical pointer click did not expand the panel.' }
    }
    finally {
        [SmokeWindowCapture]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
        $null = [SmokeWindowCapture]::SetCursorPos($original.X,$original.Y)
    }
}

Write-Output "UI smoke artifacts: $runRoot"
try {
    $peer = Start-Peer 'healthy'
    $app = Start-App
    Start-Sleep -Milliseconds 1000
    Wait-Text $app 'Connection' '已连接 ClassIsland'
    Wait-Text $app 'TouchStatus' '已关闭'
    Save-Window $app 'home.png'
    Click-Control $app 'CloseWindow'
    Start-Sleep -Milliseconds 300
    Save-Quick $app 'edge.png'
    Exercise-EdgePointer $app
    $checks.Add('Physical pointer drag repositions the edge handle; a short click opens the panel')
    if ((Find-Quick $app 'QuickTouchPower').Current.Name -ne '开启辅助') { throw 'Quick panel is not ready.' }
    Save-Quick $app 'quick.png'
    Click-Quick $app 'QuickTouchPower'
    if ((Find-Quick $app 'QuickTouchPower').Current.Name -ne '停止辅助') { throw 'Quick enable did not reach Host.' }
    Click-Quick $app 'QuickTouchPause'
    if ((Find-Quick $app 'QuickTouchPause').Current.Name -ne '继续') { throw 'Quick pause failed.' }
    Click-Quick $app 'QuickTouchPower'
    $quickHwnd = [IntPtr](Find-Quick $app '').Current.NativeWindowHandle
    $null = [SmokeWindowCapture]::PostMessage($quickHwnd,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
    Start-Sleep -Milliseconds 200
    if (-not (Find-Quick $app 'EdgeHandle')) { throw 'Closing the quick panel removed the edge entry.' }
    Click-Quick $app 'EdgeHandle'
    Click-Quick $app 'QuickOpenSettings'
    if (-not (Find-Control $app 'TouchCompatibility')) { throw 'Quick settings did not open the main window.' }
    Select-Page $app 'HomeTab'
    $checks.Add('Edge handle opens controls without tray; quick enable/pause/stop and settings work')
    Click-Control $app 'TouchPower'
    Wait-Text $app 'TouchPower' '停止辅助'
    Click-Control $app 'TouchPause'
    Wait-Text $app 'TouchStatus' '已暂停'
    Click-Control $app 'TouchPause'
    Wait-Text $app 'TouchPause' '暂停辅助'
    $checks.Add('PowerPoint assist enable, pause, resume use actual Host state')
    $element = Find-Main $app
    $element.GetCurrentPattern([Windows.Automation.TransformPattern]::Pattern).Resize(620,600)
    Start-Sleep -Milliseconds 300
    Save-Window $app 'compact.png'
    $checks.Add('App automatically starts Host and renders compact home')
    $firstHost = Find-OwnedHost
    if (-not $firstHost) { throw 'Auto-started Host was not found.' }

    Click-Control $app 'CloseWindow'
    Start-Sleep -Milliseconds 300
    if ($app.HasExited) { throw 'Hide to edge unexpectedly exited App.' }
    if (-not (Find-OwnedHost)) { throw 'Hiding App unexpectedly stopped Host.' }
    $duplicate = Start-App
    if (-not $duplicate.WaitForExit(5000)) { throw 'Duplicate App did not exit.' }
    Wait-Text $app 'TouchPower' '停止辅助'
    if ((Find-OwnedHost).ProcessId -ne $firstHost.ProcessId) { throw 'App reopen replaced the Host.' }
    $checks.Add('Hide to edge and single-instance activation retain running assist and Host')
    Click-Control $app 'TouchPower'
    Wait-Text $app 'TouchStatus' '已关闭'
    Select-Page $app 'SettingsTab'
    Wait-Text $app 'Subject' '数学'
    Save-Window $app 'settings.png'

    $peer.Kill($true)
    $null = $peer.WaitForExit(5000)
    Wait-Text $app 'Subject' '—'
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
    Select-Page $app 'HomeTab'
    Wait-Text $app 'Connection' '已连接 ClassIsland'
    Wait-Text $app 'TouchStatus' '已关闭'
    Click-Control $app 'TouchPower'
    Wait-Text $app 'TouchPower' '停止辅助'
    $checks.Add('App reconnects and resynchronizes after Host termination')

    $savedEdgeTop = (Find-Quick $app '').Current.BoundingRectangle.Top
    Click-Control $app 'StopHost'
    if (-not $app.WaitForExit(15000)) { throw 'Stop background did not exit App.' }
    if (Find-OwnedHost) { throw 'Stop background left Host running.' }
    $checks.Add('Stop background and exit waits for Host termination')
    $app = Start-App
    Wait-Text $app 'TouchStatus' '已关闭'
    $restoredEdgeTop = (Find-Quick $app '').Current.BoundingRectangle.Top
    if ([Math]::Abs($restoredEdgeTop-$savedEdgeTop) -gt 2) { throw 'Edge position was not restored after a full restart.' }
    Select-Page $app 'SettingsTab'
    if ((Find-Control $app 'LoginStartup').Current.IsEnabled) { throw 'Private instance can modify real login startup.' }
    (Find-Control $app 'AutoTouchAtLaunch').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    Wait-Text $app 'StartupPreferencesMessage' '已保存，下次启动时生效。'
    Select-Page $app 'HomeTab'
    Wait-Text $app 'TouchStatus' '已关闭'
    Click-Control $app 'StopHost'
    if (-not $app.WaitForExit(15000) -or (Find-OwnedHost)) { throw 'Restarted App did not shut down cleanly.' }
    $checks.Add('Edge position survives a full App restart; closing the panel preserves its handle')

    $app = Start-App -AtLogin
    $end = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 150
        if ($app.HasExited) { throw 'Quiet login exited unexpectedly.' }
        if (Find-Main $app) { throw 'Quiet login displayed the main window.' }
        $edge = Find-Quick $app ''
    } while (-not $edge -and [DateTime]::UtcNow -lt $end)
    if (-not $edge) { throw 'Quiet login did not create an edge entry.' }
    $duplicate = Start-App -AtLogin
    if (-not $duplicate.WaitForExit(5000)) { throw 'Duplicate login did not exit.' }
    Start-Sleep -Milliseconds 400
    if (Find-Main $app) { throw 'Duplicate login unexpectedly opened the main window.' }
    $duplicate = Start-App
    if (-not $duplicate.WaitForExit(5000)) { throw 'Manual reopen did not reuse quiet instance.' }
    Wait-Text $app 'TouchPower' '停止辅助'
    $checks.Add('Quiet login shows only edge; manual reopen reveals the same instance with automatically enabled assist')
    Click-Control $app 'TouchPause'
    Wait-Text $app 'TouchStatus' '已暂停'
    $duplicate = Start-App -AtLogin
    if (-not $duplicate.WaitForExit(5000)) { throw 'Duplicate login did not exit.' }
    Wait-Text $app 'TouchStatus' '已暂停'
    Click-Control $app 'TouchPower'
    Wait-Text $app 'TouchStatus' '已关闭'
    $oldHost = Find-OwnedHost
    $hostProcess = [Diagnostics.Process]::GetProcessById([int]$oldHost.ProcessId)
    $hostProcess.Kill()
    $null = $hostProcess.WaitForExit(5000)
    $hostProcess.Dispose()
    $end = [DateTime]::UtcNow.AddSeconds(25)
    do {
        Start-Sleep -Milliseconds 200
        $newHost = Find-OwnedHost
    } while ((-not $newHost -or $newHost.ProcessId -eq $oldHost.ProcessId) -and [DateTime]::UtcNow -lt $end)
    if (-not $newHost -or $newHost.ProcessId -eq $oldHost.ProcessId) { throw 'Host did not recover during startup test.' }
    Wait-Text $app 'TouchStatus' '已关闭'
    Start-Sleep -Milliseconds 1500
    Wait-Text $app 'TouchStatus' '已关闭'
    $checks.Add('Duplicate login preserves pause; Host recovery does not replay startup enable after manual stop')
    Select-Page $app 'SettingsTab'
    if ((Find-Control $app 'AutoTouchAtLaunch').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne [Windows.Automation.ToggleState]::On) { throw 'Auto touch preference was not persisted.' }
    Save-Window $app 'startup-settings.png'
    (Find-Control $app 'AutoTouchAtLaunch').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    (Find-Control $app 'EdgeOnlyAtLogin').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    Click-Control $app 'StopHost'
    if (-not $app.WaitForExit(15000)) { throw 'Quiet-start instance did not exit.' }
    $app = Start-App -AtLogin
    Wait-Text $app 'TouchStatus' '已关闭'
    Click-Control $app 'StopHost'
    if (-not $app.WaitForExit(15000) -or (Find-OwnedHost)) { throw 'Login with main window did not exit cleanly.' }
    $checks.Add('Disabling edge-only login reveals main window; disabling automatic assist takes effect next launch')
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
