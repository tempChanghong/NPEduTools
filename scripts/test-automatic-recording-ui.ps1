param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [string]$AppDirectory, [string]$ClassIslandPipe)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing.Common
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AutomaticUiCapture {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
'@
$projectRoot = Split-Path $PSScriptRoot -Parent
$runRoot = Join-Path $projectRoot ('.artifacts/automatic-recording-ui/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$appExe = Join-Path $projectRoot "src/NPEduTools.App/bin/$Configuration/net10.0-windows/NPEduTools.App.exe"
if ($AppDirectory) { $appExe = Join-Path ([IO.Path]::GetFullPath($AppDirectory)) 'NPEduTools.App.exe' }
$fixtureExe = Join-Path $projectRoot "tests/NPEduTools.Recording.TestFixture/bin/$Configuration/net10.0-windows/NPEduTools.Recording.TestFixture.exe"
$ffmpeg = Join-Path (Split-Path $appExe -Parent) 'Recorder/Tools/ffmpeg.exe'
$ffprobe = Join-Path (Split-Path $appExe -Parent) 'Recorder/Tools/ffprobe.exe'
$pipe = 'NPEduTools.Test.recordui.' + [Guid]::NewGuid().ToString('N')
$title = 'Recording UI fixture ' + [Guid]::NewGuid().ToString('N')
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$checks = [Collections.Generic.List[string]]::new()
$mainTitle = 'NPEduTools'; $recordTitle = '录制微课 · NPEduTools'; $quickTitle = 'NPEduTools 快捷工具'
$app = $null
function Find-Window([string]$name) {
    $condition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name))
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$condition)
}
function Find-Control([string]$window, [string]$id) {
    $root = Find-Window $window
    if (-not $root) { return $null }
    return $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
}
function Wait-Control([string]$window, [string]$id, [string]$pattern = '', [int]$seconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        if ($app.HasExited) { throw "App exited unexpectedly ($($app.ExitCode))." }
        $control = Find-Control $window $id
        if ($control -and $control.Current.IsEnabled -and (-not $pattern -or $control.Current.Name -match $pattern)) { return $control }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Expected $id / $pattern; actual '$($control.Current.Name)'."
}
function Click([string]$window, [string]$id) {
    (Wait-Control $window $id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Save-Window([string]$title, [string]$filename) {
    Start-Sleep -Milliseconds 250 # Allow the sidebar's 140 ms opening animation to finish.
    $handle = [IntPtr](Find-Window $title).Current.NativeWindowHandle
    $rect = [AutomaticUiCapture+Rect]::new()
    if (-not [AutomaticUiCapture]::GetWindowRect($handle,[ref]$rect)) { throw 'Window bounds unavailable.' }
    $bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap); $hdc = $graphics.GetHdc()
    try { if (-not [AutomaticUiCapture]::PrintWindow($handle,$hdc,2)) { throw 'Window capture failed.' } }
    finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    try { $bitmap.Save((Join-Path $runRoot $filename),[Drawing.Imaging.ImageFormat]::Png) }
    finally { $bitmap.Dispose() }
}

$planTitle = '自动录课 · 计划与预演'
$peerExe = Join-Path $projectRoot "tests/NPEduTools.ClassIsland.TestPeer/bin/$Configuration/net10.0/NPEduTools.ClassIsland.TestPeer.exe"
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($pipe))).Substring(0,24)
$bookPath = Join-Path $env:LOCALAPPDATA "NPEduTools/ui/$hash.recording-plans.json"
$clockFile = Join-Path $runRoot 'school-clock.json'
function Rpc([string]$capability) {
    $stream = [IO.Pipes.NamedPipeClientStream]::new('.', $pipe, [IO.Pipes.PipeDirection]::InOut)
    try {
        $stream.Connect(5000)
        $payload = [Text.Encoding]::UTF8.GetBytes((@{version=1;requestId=[Guid]::NewGuid();capability=$capability;timeoutMs=5000} | ConvertTo-Json -Compress))
        $stream.Write([BitConverter]::GetBytes([int]$payload.Length)); $stream.Write($payload); $stream.Flush()
        $reader = [IO.BinaryReader]::new($stream); $length = $reader.ReadInt32()
        if ($length -lt 0 -or $length -gt 65536) { throw 'Invalid frame' }
        return [Text.Encoding]::UTF8.GetString($reader.ReadBytes($length)) | ConvertFrom-Json -DateKind String
    } finally { $stream.Dispose() }
}
try {
    '{"noPlan":true}' | Set-Content -LiteralPath $clockFile -Encoding utf8NoBOM
    $start = [Diagnostics.ProcessStartInfo]::new($peerExe); $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.Environment['NPEEDUTOOLS_TEST_BRIDGE_CONTROL'] = $clockFile
    foreach ($a in @(($pipe + '.peer'),'bridge')) { $start.ArgumentList.Add($a) }
    if (-not $ClassIslandPipe) { $peer = [Diagnostics.Process]::Start($start); $owned.Add($peer) }
    $start = [Diagnostics.ProcessStartInfo]::new($fixtureExe); $start.UseShellExecute = $false; $start.ArgumentList.Add('title=' + $title)
    $fixture = [Diagnostics.Process]::Start($start); $owned.Add($fixture)
    $start = [Diagnostics.ProcessStartInfo]::new($appExe); $start.UseShellExecute = $false
    $upstream = if ($ClassIslandPipe) { $ClassIslandPipe } else { $pipe + '.peer' }
    foreach ($a in @('--pipe',$pipe,'--classisland-pipe',$upstream)) { $start.ArgumentList.Add($a) }
    $start.Environment['NPEEDUTOOLS_RECORDING_FIXTURE'] = $title
    $app = [Diagnostics.Process]::Start($start); $owned.Add($app)
    Click $mainTitle 'OpenRecording'
    $null = Wait-Control $recordTitle 'RecordingStart'
    foreach ($id in @('RecordingSystemAudio','RecordingMicrophone')) {
        $toggle = (Find-Control $recordTitle $id).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
        if ($toggle.Current.ToggleState -eq [Windows.Automation.ToggleState]::On) { $toggle.Toggle() }
    }
    (Find-Control $recordTitle 'RecordingDirectory').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($runRoot)
    Click $recordTitle 'RecordingSaveSettings'
    if ((Rpc 'recording.status').recording.active) { throw 'Saving preferences must not start recording.' }
    Save-Window $recordTitle 'saved-settings.png'
    Click $recordTitle 'RecordingHide'
    $school = [DateTimeOffset]::Parse((Rpc 'classisland.school-clock').schoolClock.schoolNow)
    if (-not $ClassIslandPipe -and $school.Year -ne 2031) { throw 'Expected private school date.' }
    $schoolDate = $school.ToString('yyyy-MM-dd')
    $startAt = $school.AddSeconds(8); $endAt = $startAt.AddSeconds(20)
    @{Version=1;Recurring=@();Dated=@(@{Id=[Guid]::NewGuid();Name='界面自动短课';Date=$school.ToString('yyyy-MM-dd');Start=$startAt.ToString('HH:mm:ss');End=$endAt.ToString('HH:mm:ss');Enabled=$true});Overrides=@();Days=@();ExcludedNames=@();Subjects=@()} |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $bookPath -Encoding utf8NoBOM
    Click $mainTitle 'OpenRecordingPlan'
    $null = Wait-Control $planTitle 'AutoClockStatus' ($schoolDate + '.*使用 ClassIsland 学校时间')
    Click $planTitle 'AutoToggleReal'
    $null = Wait-Control $planTitle 'AutoRealStatus' '正在自动录制' 35
    Save-Window $planTitle 'automatic-recording.png'
    $state = Rpc 'recording.status'
    if ($state.recording.control.owner -ne 'Automatic') { throw 'Expected automatic owner.' }
    if ($AppDirectory) {
        $packageApp = [IO.Path]::GetFullPath($AppDirectory).TrimEnd('\') + '\'
        $hostProcess = Get-CimInstance Win32_Process -Filter "Name='NPEduTools.Host.exe'" | Where-Object { $_.ParentProcessId -eq $app.Id } | Select-Object -First 1
        $recProcess = Get-CimInstance Win32_Process -Filter "Name='NPEduTools.Recorder.exe'" | Where-Object { $_.ParentProcessId -eq $hostProcess.ProcessId } | Select-Object -First 1
        foreach ($pidToInspect in @($app.Id,$hostProcess.ProcessId,$recProcess.ProcessId)) {
            $runtime = (Get-Process -Id $pidToInspect).Modules | Where-Object ModuleName -eq 'coreclr.dll' | Select-Object -First 1
            if (-not $runtime -or -not $runtime.FileName.StartsWith($packageApp,[StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime was loaded outside extracted package.' }
        }
    }
    Click $planTitle 'AutoHide'; Click $mainTitle 'OpenQuick'
    $null = Wait-Control $quickTitle 'QuickRecordingStatus' '自动.*正在录制.*至多剩余'
    Save-Window $quickTitle 'automatic-sidebar.png'
    Click $quickTitle 'QuickRecordingPause'
    $null = Wait-Control $quickTitle 'QuickRecordingStatus' '已暂停'
    $null = Wait-Control $quickTitle 'QuickRecordingStatus' '已保存' 35
    # Worker completion can reach the UI one scheduler tick before the durable ledger is updated.
    $savedWait = [Diagnostics.Stopwatch]::StartNew()
    do {
        $state = Rpc 'recording.status'
        if ($state.automatic.recent[0].phase -eq 'Recorded') { break }
        Start-Sleep -Milliseconds 150
    } while ($savedWait.Elapsed.TotalSeconds -lt 5)
    if ($state.automatic.recent[0].phase -ne 'Recorded' -or $state.automatic.recent[0].date -ne $schoolDate) { throw 'Automatic ledger did not confirm school-day completion.' }
    Click $quickTitle 'QuickRecordingPlan'
    $tab = (Find-Window $planTitle).FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'实际录制记录'))
    $tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Save-Window $planTitle 'actual-records.png'
    Click $planTitle 'AutoRealSkipDay'
    $null = Wait-Control $planTitle 'AutoRealStatus' '学校今天不再自动录制'
    if ((Rpc 'recording.status').automatic.skipDate -ne $schoolDate) { throw 'Skip-day used system date.' }
    Click $planTitle 'AutoHide'; Click $quickTitle 'RailSettings'; Click $mainTitle 'StopHost'
    if (-not $app.WaitForExit(30000)) { throw 'App shutdown timed out.' }
    $videos = @(Get-ChildItem -LiteralPath $runRoot -Filter *.mp4)
    if ($videos.Count -ne 1) { throw 'Expected one automatic recording.' }
    & $ffmpeg -v error -xerror -i $videos[0].FullName -f null -
    if ($LASTEXITCODE -ne 0) { throw 'Full MP4 decode failed.' }
    @{passed=$true;checks=@('Save preferences without starting capture','Enable actual fixed-date recording from WPF using school time','Sidebar shows automatic ownership and remaining limit','Pause does not extend end; verified MP4 saved','Actual history and skip-day use school date');state=$state;videos=@($videos.FullName)} |
        ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $runRoot 'summary.json') -Encoding utf8
    Write-Output "PASS: automatic recording UI. Artifacts: $runRoot"
}
finally {
    foreach ($process in $owned) { if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(10000) }; $process.Dispose() }
    Get-CimInstance Win32_Process -Filter "Name='NPEduTools.Host.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($pipe) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
}

