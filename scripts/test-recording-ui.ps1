param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [string]$AppDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing.Common
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RecordingUiCapture {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
'@
$projectRoot = Split-Path $PSScriptRoot -Parent
$runRoot = Join-Path $projectRoot ('.artifacts/recording-ui/' + [Guid]::NewGuid().ToString('N'))
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
    $rect = [RecordingUiCapture+Rect]::new()
    if (-not [RecordingUiCapture]::GetWindowRect($handle,[ref]$rect)) { throw 'Window bounds unavailable.' }
    $bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap); $hdc = $graphics.GetHdc()
    try { if (-not [RecordingUiCapture]::PrintWindow($handle,$hdc,2)) { throw 'Window capture failed.' } }
    finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    try { $bitmap.Save((Join-Path $runRoot $filename),[Drawing.Imaging.ImageFormat]::Png) }
    finally { $bitmap.Dispose() }
}
try {
    foreach ($path in @($appExe,$fixtureExe,$ffmpeg,$ffprobe)) { if (-not (Test-Path -LiteralPath $path)) { throw "Build Release and bootstrap recording components first: $path" } }
    # Visible owned fixture and WPF windows are intentional UI test surfaces.
    $start = [Diagnostics.ProcessStartInfo]::new($fixtureExe); $start.UseShellExecute = $false
    $start.ArgumentList.Add('title=' + $title)
    $fixture = [Diagnostics.Process]::Start($start); $owned.Add($fixture)
    Start-Sleep -Seconds 1
    $start = [Diagnostics.ProcessStartInfo]::new($appExe); $start.UseShellExecute = $false
    foreach ($argument in @('--pipe',$pipe,'--classisland-pipe',($pipe + '.absent'))) { $start.ArgumentList.Add($argument) }
    $start.Environment['NPEEDUTOOLS_RECORDING_FIXTURE'] = $title
    $app = [Diagnostics.Process]::Start($start); $owned.Add($app)
    Click $mainTitle 'OpenRecording'
    $null = Wait-Control $recordTitle 'RecordingStart'
    foreach ($id in @('RecordingSystemAudio','RecordingMicrophone')) {
        $toggle = (Find-Control $recordTitle $id).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
        if ($toggle.Current.ToggleState -eq [Windows.Automation.ToggleState]::On) { $toggle.Toggle() }
    }
    (Find-Control $recordTitle 'RecordingDirectory').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($runRoot)
    Save-Window $recordTitle 'settings.png'
    Click $recordTitle 'RecordingStart'
    $null = Wait-Control $recordTitle 'RecordingStatus' '^正在录制$'
    $hostProcess = Get-CimInstance Win32_Process -Filter "Name='NPEduTools.Host.exe'" | Where-Object { $_.ParentProcessId -eq $app.Id -and $_.CommandLine.Contains($pipe) } | Select-Object -First 1
    $worker = Get-CimInstance Win32_Process -Filter "Name='NPEduTools.Recorder.exe'" | Where-Object { $_.ParentProcessId -eq $hostProcess.ProcessId } | Select-Object -First 1
    if (-not $worker -or -not $worker.CommandLine.Contains($title)) { throw 'Expected fixture-only recorder child.' }
    Start-Sleep -Seconds 3
    Save-Window $recordTitle 'recording.png'
    # Closing the control window must hide it, leaving the recording running.
    (Find-Window $recordTitle).GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    Start-Sleep -Milliseconds 300
    if (Find-Window $recordTitle) { throw 'Recording window did not hide.' }
    Click $mainTitle 'OpenQuick'
    $null = Wait-Control $quickTitle 'QuickRecordingStatus' '正在录制'
    if (Find-Window $mainTitle) { throw 'Main window did not hide.' }
    Save-Window $quickTitle 'sidebar.png'
    Click $quickTitle 'QuickRecordingPause'
    $null = Wait-Control $quickTitle 'QuickRecordingStatus' '已暂停'
    Start-Sleep -Seconds 2
    Click $quickTitle 'QuickRecordingPause'
    $null = Wait-Control $quickTitle 'QuickRecordingStatus' '正在录制'
    Start-Sleep -Seconds 3
    Click $quickTitle 'QuickRecordingStop'
    $null = Wait-Control $quickTitle 'QuickRecordingStatus' '已保存'
    Click $quickTitle 'QuickOpenRecording'
    $null = Wait-Control $recordTitle 'RecordingOpenVideo'
    Save-Window $recordTitle 'saved.png'
    $checks.Add('Actual WPF start; close control window and hide main; sidebar pause/resume/stop; saved file becomes available')
    # Explicit application exit must save an active second recording first.
    Click $recordTitle 'RecordingStart'
    $null = Wait-Control $recordTitle 'RecordingStatus' '^正在录制$'
    Start-Sleep -Seconds 3
    Click $recordTitle 'RecordingHide'
    Click $quickTitle 'RailSettings'
    Click $mainTitle 'StopHost'
    if (-not $app.WaitForExit(45000)) { throw 'App did not finish saving and exit.' }
    $app.Refresh()
    if ($app.ExitCode -ne 0) { throw 'App exit was unsuccessful.' }
    Start-Sleep -Milliseconds 500
    if (Get-Process -Id $worker.ProcessId -ErrorAction SilentlyContinue) { throw 'Recorder worker survived application exit.' }
    $videos = @(Get-ChildItem -LiteralPath $runRoot -Filter *.mp4)
    if ($videos.Count -ne 2) { throw "Expected two saved videos, got $($videos.Count)." }
    foreach ($video in $videos) {
        $probe = & $ffprobe -v error -show_streams -show_format -of json $video.FullName | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or @($probe.streams | Where-Object { $_.codec_name -eq 'h264' }).Count -ne 1) { throw 'Invalid MP4.' }
        & $ffmpeg -v error -xerror -i $video.FullName -f null -
        if ($LASTEXITCODE -ne 0) { throw 'MP4 failed complete decode.' }
    }
    $checks.Add('Application Exit saves ongoing recording and reaps worker; both MP4s pass complete decode')
    @{ passed = $true; checks = $checks; videos = @($videos.FullName) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'summary.json') -Encoding utf8
    Write-Output "PASS: recording UI. Artifacts: $runRoot"
}
finally {
    foreach ($process in $owned) {
        $process.Refresh()
        if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(10000) }
        $process.Dispose()
    }
    # Only a host launched on this test's unique private pipe is eligible for cleanup.
    Get-CimInstance Win32_Process -Filter "Name='NPEduTools.Host.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains($pipe) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
}
