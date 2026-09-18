param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'dotnet.ps1') --version
if ($LASTEXITCODE -ne 0) { throw 'SDK unavailable.' }
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing.Common
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AutoPlanCapture {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
'@
$projectRoot = Split-Path $PSScriptRoot -Parent
$runRoot = Join-Path $projectRoot ('.artifacts/auto-recording-ui/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$appExe = Join-Path $projectRoot "src/NPEduTools.App/bin/$Configuration/net10.0-windows/NPEduTools.App.exe"
$peerDll = Join-Path $projectRoot "tests/NPEduTools.ClassIsland.TestPeer/bin/$Configuration/net10.0/NPEduTools.ClassIsland.TestPeer.dll"
$pipe = 'NPEduTools.Test.auto-ui.' + [Guid]::NewGuid().ToString('N')
$upstream = $pipe + '.peer'
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$windowTitle = '自动录课 · 计划与预演'
$statePath = Join-Path $env:LOCALAPPDATA ('NPEduTools/ui/' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($pipe))).Substring(0,24) + '.recording-preview.json')
function Start-App {
    $start = [Diagnostics.ProcessStartInfo]::new($appExe); $start.UseShellExecute = $false
    foreach ($argument in @('--pipe',$pipe,'--classisland-pipe',$upstream)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start); $owned.Add($process); return $process
}
function Window([string]$title) {
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,
        [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id),
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$title)))
}
function Control([string]$id, [string]$title = $windowTitle) {
    $root = Window $title
    if (-not $root) { return $null }
    return $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
}
function Wait-Control([string]$id, [string]$pattern = '', [string]$title = $windowTitle) {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        $item = Control $id $title
        if ($item -and $item.Current.IsEnabled -and (-not $pattern -or $item.Current.Name -match $pattern)) { return $item }
        if ($app.HasExited) { throw "App exited: $($app.ExitCode)" }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Expected $id / $pattern; actual '$($item.Current.Name)'."
}
function Click([string]$id, [string]$title = $windowTitle) {
    (Wait-Control $id '' $title).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Save-Window([string]$filename) {
    Start-Sleep -Milliseconds 250
    $handle = [IntPtr](Window $windowTitle).Current.NativeWindowHandle
    $rect = [AutoPlanCapture+Rect]::new(); $null = [AutoPlanCapture]::GetWindowRect($handle,[ref]$rect)
    $bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap); $hdc = $graphics.GetHdc()
    try { $null = [AutoPlanCapture]::PrintWindow($handle,$hdc,2) } finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    try { $bitmap.Save((Join-Path $runRoot $filename),[Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
}
try {
    $start = [Diagnostics.ProcessStartInfo]::new($env:NPEEDUTOOLS_DOTNET_HOST)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in @($peerDll,$upstream,'schedule')) { $start.ArgumentList.Add($argument) }
    $peer = [Diagnostics.Process]::Start($start); $owned.Add($peer)
    $peerErrors = $peer.StandardError.ReadToEndAsync()
    if ($peer.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult() -ne 'READY') { throw 'Peer did not start.' }
    $app = Start-App
    Click 'OpenRecordingPlan' 'NPEduTools'
    $null = Wait-Control 'AutoSourceStatus' '测试生效课表.*2 节.*已用'
    (Control 'AutoLessonNumbers').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('2')
    Click 'AutoApplyRules'; Click 'AutoTogglePreview'
    $null = Wait-Control 'AutoPreviewStatus' '本日没有待开始'
    (Control 'AutoLessonNumbers').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('1')
    Click 'AutoApplyRules'
    $null = Wait-Control 'AutoPreviewStatus' '模拟录制中：预演数学'
    Save-Window 'preview-running.png'
    $rows = (Control 'AutoPlans').FindAll([Windows.Automation.TreeScope]::Children,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::DataItem))
    if ($rows.Count -ne 2) { throw 'Expected two timetable rows.' }
    $rows[0].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Click 'AutoSkipLesson'
    $null = Wait-Control 'AutoPreviewStatus' '本日没有待开始'
    Click 'AutoRefresh'
    Save-Window 'preview-finished.png'
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if (@($state.Events | Where-Object Action -eq '应开始').Count -ne 1 -or @($state.Events | Where-Object Action -eq '应停止').Count -ne 1) { throw 'Expected one start and one stop rehearsal event.' }
    # Sidebar entry reopens the same manager without enabling real recording.
    Click 'AutoHide'; Click 'OpenQuick' 'NPEduTools'; Click 'QuickRecordingPlan' 'NPEduTools 快捷工具'
    $null = Wait-Control 'AutoSourceStatus' '测试生效课表'
    Click 'AutoHide'; Click 'RailSettings' 'NPEduTools 快捷工具'; Click 'StopHost' 'NPEduTools'
    if (-not $app.WaitForExit(20000)) { throw 'App exit timed out.' }
    $app = Start-App; Click 'OpenRecordingPlan' 'NPEduTools'
    $null = Wait-Control 'AutoSourceStatus' '测试生效课表'
    if ((Control 'AutoLessonNumbers').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne '1') { throw 'Rule did not persist.' }
    Click 'AutoTogglePreview'
    $null = Wait-Control 'AutoPreviewStatus' '本日没有待开始'
    Start-Sleep -Seconds 1
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if (@($state.Events | Where-Object Action -eq '应开始').Count -ne 1) { throw 'Restart duplicated a stopped lesson.' }
    if (Get-CimInstance Win32_Process -Filter "Name='NPEduTools.Recorder.exe'" | Where-Object ParentProcessId -eq $app.Id) { throw 'Rehearsal must never start the recorder.' }
    Save-Window 'preview-restored.png'
    $peer.Kill($true); $null = $peer.WaitForExit(5000)
    Click 'AutoRefresh'
    $null = Wait-Control 'AutoSourceStatus' '日程暂不可用|读取日程超时'
    $null = Wait-Control 'AutoPreviewStatus' '等待新鲜日程'
    Save-Window 'preview-disconnected.png'
    Click 'AutoHide'; Click 'StopHost' 'NPEduTools'
    if (-not $app.WaitForExit(20000)) { throw 'Final shutdown timed out.' }
    Copy-Item -LiteralPath $statePath -Destination (Join-Path $runRoot 'preview-state.json')
    # A valid JSON with an unsupported version must not crash the window or overwrite the file.
    Set-Content -LiteralPath $statePath -Value '{"Version":999}' -Encoding utf8
    $app = Start-App; Click 'OpenRecordingPlan' 'NPEduTools'
    $null = Wait-Control 'AutoError' '原文件已保留'
    if ((Control 'AutoTogglePreview').Current.IsEnabled) { throw 'Invalid records must disable rehearsal.' }
    if ((Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json).Version -ne 999) { throw 'Invalid file was overwritten.' }
    Save-Window 'preview-invalid-config.png'
    Click 'AutoHide'; Click 'StopHost' 'NPEduTools'
    if (-not $app.WaitForExit(20000)) { throw 'Invalid-config shutdown timed out.' }
    @{passed=$true;checks=@('Real WPF daily plan and ordinal filters','Rehearsal emits start and stop without capture','Manual skip survives refresh and full App restart','Sidebar entry and settings persistence','Unavailable upstream keeps App responsive','Invalid configuration is preserved without crashing');completedAt=[DateTimeOffset]::Now} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'summary.json') -Encoding utf8
    Write-Output "PASS: auto recording preview UI. Artifacts: $runRoot"
}
finally {
    foreach ($process in $owned) { if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(5000) }; $process.Dispose() }
    Get-CimInstance Win32_Process -Filter "Name='NPEduTools.Host.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($pipe) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
}
