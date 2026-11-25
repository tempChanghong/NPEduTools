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
$pipe = 'NPEduTools.Test.examaware-ui.' + [Guid]::NewGuid().ToString('N')
& (Join-Path $PSScriptRoot 'initialize-test-agreements.ps1') -Pipe $pipe
$out = Join-Path $root ('.artifacts/examaware-ui/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out -Force | Out-Null
$title = 'NPEduTools'
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
    $mainHandle = (Window).Current.NativeWindowHandle
    Click 'OpenExamAware' 'NPEduTools'
    $null = Wait-Control 'ExamAwareBridgeState' '待登记程序'
    if ((Window).Current.NativeWindowHandle -ne $mainHandle) { throw 'ExamAware management did not stay in the original window.' }
    $plan = Wait-Control 'ExamAwarePlanSection'
    if ($plan.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState -ne 'Collapsed') {
        throw 'Local playback tools must start folded.'
    }
    $quit = Control 'ExamAwareQuit'
    if ($quit -and -not $quit.Current.IsOffscreen -and $quit.Current.IsEnabled) { throw 'Quit must be unavailable before setup.' }
    $checks.Add('First setup stays in the original window with playback folded and no runnable quit action')
    (Wait-Control 'ExamAwareAutoStartConfiguration').GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $null = Wait-Control 'ExamAwareAutoStart' '未知'
    foreach ($id in @('ExamAwareAutoStartEnable','ExamAwareAutoStartDisable')) {
        $item = Control $id
        if (-not $item -or $item.Current.IsEnabled) { throw 'Auto-start controls must be disabled while disconnected.' }
    }
    $null = Wait-Control 'ExamAwareAutoStartMessage' '连接桥接'
    $checks.Add('Explicit enable/disable controls stay disabled while disconnected; unknown is not off')
    $startExamAware = Control 'ExamAwareStart'
    if ($startExamAware -and -not $startExamAware.Current.IsOffscreen -and $startExamAware.Current.IsEnabled) { throw 'Start must stay unavailable until an executable is registered.' }
    Click 'ExamAwareStepPath'
    $null = Wait-Control 'ExamAwarePath'
    $checks.Add('The initial primary action opens path registration; disconnected auto-start remains unknown')
    (Wait-Control 'ExamAwarePath').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('C:\不存在的中文 目录\ExamAware.exe')
    Click 'ExamAwareSave'
    $null = Wait-Control 'ExamAwareMessage' '不存在'
    $checks.Add('Invalid path is rejected without launching another application')
    Snapshot 'management.png'
    Click 'ExamAwareBack'
    Click 'OpenQuick'
    Click 'RailTools' 'NPEduTools 快捷工具'
    Click 'QuickExamAware' 'NPEduTools 快捷工具'
    $null = Wait-Control 'ExamAwareBridgeState' '待登记程序'
    $draft = (Wait-Control 'ExamAwarePath').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($draft -notlike '*不存在的中文*' -or (Window).Current.NativeWindowHandle -ne $mainHandle) { throw 'Sidebar fallback lost the draft or opened another window.' }
    $checks.Add('Sidebar launch fallback restores the original management page and its path draft')
    Click 'ExamAwareBack'
    Click 'StopHost' 'NPEduTools'
    if (-not $app.WaitForExit(20000)) { throw 'App did not exit.' }
    @{passed=$true;checks=$checks.ToArray();completedAt=[DateTimeOffset]::Now} | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $out 'summary.json') -Encoding utf8
    Write-Output "PASS: ExamAware UI ($($checks.Count) scenarios). Artifacts: $out"
} finally {
    if ($app) { if (-not $app.HasExited) { $app.Kill($true); $null = $app.WaitForExit(5000) }; $app.Dispose() }
    Get-CimInstance Win32_Process -Filter "Name='NPEduTools.Host.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($pipe) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
}
