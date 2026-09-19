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
$out = Join-Path $root ('.artifacts/examaware-ui/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out -Force | Out-Null
$title = '考试看板 · ExamAware2'
$checks = [Collections.Generic.List[string]]::new()
function Window([string]$name = $title) {
    [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,
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
    Click 'OpenExamAware' 'NPEduTools'
    $null = Wait-Control 'ExamAwareConnection' '尚未连接'
    $null = Wait-Control 'ExamAwareAutoStart' '未知'
    Click 'ExamAwareStart'
    $null = Wait-Control 'ExamAwareMessage' '先选择'
    $checks.Add('Unconfigured start directs user to select a path; disconnected autostart is unknown')
    (Wait-Control 'ExamAwarePath').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('C:\不存在的中文 目录\ExamAware.exe')
    Click 'ExamAwareSave'
    $null = Wait-Control 'ExamAwareMessage' '不存在'
    $checks.Add('Invalid path is rejected without launching another application')
    Snapshot 'management.png'
    (Window).GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    Click 'RailTools' 'NPEduTools 快捷工具'
    Click 'QuickExamAware' 'NPEduTools 快捷工具'
    $null = Wait-Control 'ExamAwareConnection' '尚未连接'
    $checks.Add('Sidebar entry opens setup when no path is configured; management window can reopen')
    (Window).GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    Click 'RailSettings' 'NPEduTools 快捷工具'
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
