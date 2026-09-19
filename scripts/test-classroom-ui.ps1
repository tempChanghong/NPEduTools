param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing.Common
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ExamAwareCapture {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
'@
$root = Split-Path $PSScriptRoot -Parent
$pipe = 'NPEduTools.Test.classroom-ui.' + [Guid]::NewGuid().ToString('N')
$out = Join-Path $root ('.artifacts/classroom-ui/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out -Force | Out-Null
$title = '课堂模式 · NPEduTools'
$checks = [Collections.Generic.List[string]]::new()
function Window([string]$name = $title) {
    [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id),
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name)))
}
function Control([string]$id, [string]$name = $title) {
    $window = Window $name
    if ($window) { $window.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)) }
}
function Wait-Control([string]$id, [string]$pattern = '', [string]$name = $title) {
    $end = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $item = Control $id $name
        if ($item -and $item.Current.IsEnabled -and (-not $pattern -or $item.Current.Name -match $pattern)) { return $item }
        if ($app.HasExited) { throw "App exited: $($app.ExitCode)" }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $end)
    throw "Missing $id / $pattern; actual '$($item.Current.Name)'"
}
function Click([string]$id, [string]$name = $title) { (Wait-Control $id '' $name).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Snapshot([string]$file) {
    $hwnd = [IntPtr](Window).Current.NativeWindowHandle
    $rect = [ExamAwareCapture+Rect]::new(); $null = [ExamAwareCapture]::GetWindowRect($hwnd,[ref]$rect)
    $bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap); $hdc = $graphics.GetHdc()
    try { $null = [ExamAwareCapture]::PrintWindow($hwnd,$hdc,2) } finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    try { $bitmap.Save((Join-Path $out $file),[Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
}
$app = $null
try {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $root "src/NPEduTools.App/bin/$Configuration/net10.0-windows/NPEduTools.App.exe"))
    $start.UseShellExecute = $false
    foreach ($arg in @('--pipe',$pipe,'--classisland-pipe',($pipe + '.absent'))) { $start.ArgumentList.Add($arg) }
    $app = [Diagnostics.Process]::Start($start)
    Click 'OobeLater' 'NPEduTools · 初始设置'
    Click 'OpenClassroomMode' 'NPEduTools'
    $null = Wait-Control 'ClassroomModeTitle' '尚未设置'
    $null = Wait-Control 'ClassroomDaily'
    $null = Wait-Control 'ClassroomExam'
    if ((Control 'ClassroomRestore').Current.IsEnabled) { throw 'Recovery must be disabled without a journal.' }
    $checks.Add('Mode management entry shows Daily and Exam actions, with recovery disabled initially')
    Click 'ClassroomExam'
    $end = [DateTime]::UtcNow.AddSeconds(5)
    do { $dialog = Window '切换到考试模式'; if (-not $dialog) { Start-Sleep -Milliseconds 100 } } while (-not $dialog -and [DateTime]::UtcNow -lt $end)
    if (-not $dialog) { throw 'Missing mode confirmation' }
    $dialog.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    $null = Wait-Control 'ClassroomModeTitle' '^尚未设置模式$'
    $checks.Add('Cancellable mode confirmation leaves original mode unchanged')
    Click 'ClassroomRefresh'
    $null = Wait-Control 'ClassroomModeStatus' '检查未通过'
    $null = Wait-Control 'ClassroomDaily'
    $null = Wait-Control 'ClassroomActual' '尚未核实'
    $checks.Add('Read-only refresh rejects missing configuration, does not label unknown startup as off')
    Snapshot 'classroom-modes.png'
    (Window).GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    Click 'OpenClassroomMode' 'NPEduTools'
    $null = Wait-Control 'ClassroomDaily'
    $checks.Add('Management window reopens after closing')
    (Window).GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    Click 'StopHost' 'NPEduTools'
    if (-not $app.WaitForExit(20000)) { throw 'App did not exit.' }
    @{passed=$true;checks=$checks.ToArray();completedAt=[DateTimeOffset]::Now} | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $out 'summary.json') -Encoding utf8
    Write-Output "PASS: Classroom UI ($($checks.Count) scenarios). Artifacts: $out"
} finally {
    if ($app) { if (-not $app.HasExited) { $app.Kill($true); $null = $app.WaitForExit(5000) }; $app.Dispose() }
    Get-CimInstance Win32_Process -Filter "Name='NPEduTools.Host.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($pipe) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
}