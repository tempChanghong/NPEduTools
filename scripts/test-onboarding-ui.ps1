param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'dotnet.ps1') --version
if ($LASTEXITCODE -ne 0) { throw 'SDK unavailable.' }
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing.Common
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class OobeCapture {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
'@
$projectRoot = Split-Path $PSScriptRoot -Parent
$runRoot = Join-Path $projectRoot ('.artifacts/onboarding-ui/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$appExe = Join-Path $projectRoot "src/NPEduTools.App/bin/$Configuration/net10.0-windows/NPEduTools.App.exe"
$peerDll = Join-Path $projectRoot "tests/NPEduTools.ClassIsland.TestPeer/bin/$Configuration/net10.0/NPEduTools.ClassIsland.TestPeer.dll"
$pipe = 'NPEduTools.Test.oobe.' + [Guid]::NewGuid().ToString('N')
$upstream = $pipe + '.peer'
$prefix = Join-Path $env:LOCALAPPDATA ('NPEduTools/ui/' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($pipe))).Substring(0,24))
$statePath = $prefix + '.onboarding.json'
$title = 'NPEduTools · 初始设置'
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$checks = [Collections.Generic.List[string]]::new()
function Start-App([switch]$AtLogin) {
    $start = [Diagnostics.ProcessStartInfo]::new($appExe); $start.UseShellExecute = $false
    foreach ($argument in @('--pipe',$pipe,'--classisland-pipe',$upstream)) { $start.ArgumentList.Add($argument) }
    if ($AtLogin) { $start.ArgumentList.Add('--startup') }
    $process = [Diagnostics.Process]::Start($start); $owned.Add($process); return $process
}
function Window([string]$name = $title) {
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,
        [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id),
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name)))
}
function Control([string]$id, [string]$name = $title) {
    $root = Window $name
    if (-not $root) { return $null }
    return $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
}
function Wait-Control([string]$id, [string]$pattern = '', [string]$name = $title) {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        $item = Control $id $name
        if ($item -and $item.Current.IsEnabled -and (-not $pattern -or $item.Current.Name -match $pattern)) { return $item }
        if ($app.HasExited) { throw "App exited: $($app.ExitCode)" }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Expected $id / $pattern; actual '$($item.Current.Name)'."
}
function Click([string]$id, [string]$name = $title) {
    if ($id -eq 'OpenOnboarding' -and $name -eq 'NPEduTools') {
        Click 'SettingsTab' $name
        Click 'GeneralSettingsTab' $name
    }
    (Wait-Control $id '' $name).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Toggle([string]$id, [string]$name = $title) { (Wait-Control $id '' $name).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle() }
function Stop-App {
    Click 'RailSettings' 'NPEduTools 快捷工具'
    Click 'StopHost' 'NPEduTools'
    if (-not $app.WaitForExit(20000)) { throw 'App exit timed out.' }
}
function Save-Window([string]$filename) {
    Start-Sleep -Milliseconds 250
    $handle = [IntPtr](Window).Current.NativeWindowHandle
    $rect = [OobeCapture+Rect]::new(); $null = [OobeCapture]::GetWindowRect($handle,[ref]$rect)
    $bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap); $hdc = $graphics.GetHdc()
    try { $null = [OobeCapture]::PrintWindow($handle,$hdc,2) } finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    try { $bitmap.Save((Join-Path $runRoot $filename),[Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
}
try {
    $start = [Diagnostics.ProcessStartInfo]::new($env:NPEEDUTOOLS_DOTNET_HOST)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in @($peerDll,$upstream,'bridge')) { $start.ArgumentList.Add($argument) }
    $peer = [Diagnostics.Process]::Start($start); $owned.Add($peer)
    $peerErrors = $peer.StandardError.ReadToEndAsync()
    if ($peer.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult() -ne 'READY') { throw 'Peer did not start.' }
    $app = Start-App
    $null = Wait-Control 'OobeTitle' '选择用途'
    Save-Window 'welcome.png'
    Toggle 'OobeUseAutomatic'; Click 'OobeNext'
    $null = Wait-Control 'OobeTitle' '使用偏好'
    Click 'OobeLater'; Stop-App
    $saved = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if (-not $saved.Deferred -or $saved.Step -ne 'preferences' -or $saved.Features -ne 9) { throw 'Deferred purpose/progress was lost.' }
    $checks.Add('Fresh manual launch shows welcome and preserves deferred purpose/progress')

    $app = Start-App
    $null = Wait-Control 'SettingsTab' '' 'NPEduTools'
    if (Window) { throw 'Deferred wizard should not interrupt launch.' }
    Click 'OpenOnboarding' 'NPEduTools'
    $null = Wait-Control 'OobeTitle' '使用偏好'
    # Deny replacement temporarily to verify the UI cannot falsely advance.
    $locked = [IO.FileStream]::new($statePath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        Click 'OobeNext'
        $null = Wait-Control 'OobeError' '进度未保存'
        if ((Control 'OobeTitle').Current.Name -ne '使用偏好') { throw 'Failed save advanced the wizard.' }
    } finally { $locked.Dispose() }
    $checks.Add('Save failure leaves the current step unchanged; retry succeeds')
    Click 'OobeNext'
    $null = Wait-Control 'OobeConfirmClock'
    $null = Wait-Control 'OobeClock' '2031-04-07.*'
    $null = Wait-Control 'OobeConnection' '已连接'
    $null = Wait-Control 'OobeBridge' '课表已就绪'
    Click 'OobeNext'
    $null = Wait-Control 'OobeError' '核对学校日期'
    Toggle 'OobeConfirmClock'; Save-Window 'school-clock.png'; Click 'OobeNext'
    $null = Wait-Control 'OobeTitle' '录制准备'
    $checks.Add('Independent ClassIsland/bridge/clock checks use school date and require explicit alignment confirmation')
    Click 'OobeNext'
    $null = Wait-Control 'OobeRecordingStatus' '请先.*保存'
    if ((Control 'OobeTitle').Current.Name -ne '录制准备') { throw 'Unsaved recording settings passed.' }
    Click 'OobeRecordingSettings'
    $recordingTitle = '录制微课 · NPEduTools'
    $null = Wait-Control 'RecordingStart' '' $recordingTitle
    Toggle 'RecordingSystemAudio' $recordingTitle; Toggle 'RecordingMicrophone' $recordingTitle
    (Wait-Control 'RecordingDirectory' '' $recordingTitle).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue((Join-Path $runRoot 'videos'))
    Click 'RecordingSaveSettings' $recordingTitle
    $null = Wait-Control 'RecordingError' '已保存' $recordingTitle
    Click 'RecordingHide' $recordingTitle
    Click 'OobeCheckRecording'; $null = Wait-Control 'OobeRecordingStatus' '检查通过'
    Save-Window 'recording-ready.png'
    Click 'OobeNext'; $null = Wait-Control 'OobeReview' '录制准备：本次已检查'
    if (@(Get-ChildItem -LiteralPath (Join-Path $runRoot 'videos') -Force).Count -ne 0) { throw 'Probe unexpectedly recorded or left a temporary file.' }
    $checks.Add('Unsaved settings rejected; saved screen/audio choices and directory checked without capture')
    Click 'OobeBack'; Click 'OobeSkip'
    $null = Wait-Control 'OobeReview' '录制准备：已跳过'
    Save-Window 'review.png'
    Click 'OobeNext'
    $null = Wait-Control 'AutoRealStatus' '未启用' '自动录课 · 计划与预演'
    $saved = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if (-not $saved.Completed -or 'recording' -notin $saved.Skipped) { throw 'Completion or explicit skip not saved.' }
    $checks.Add('Back/skip/review/completion opens plans without enabling recording')
    Click 'AutoHide' '自动录课 · 计划与预演'; Stop-App

    $app = Start-App
    $null = Wait-Control 'SettingsTab' '' 'NPEduTools'
    if (Window) { throw 'Completed wizard reopened automatically.' }
    Click 'OpenOnboarding' 'NPEduTools'
    $null = Wait-Control 'OobeTitle' '选择用途'
    Toggle 'OobeUseAutomatic'; Click 'OobeNext'; Click 'OobeNext'
    $null = Wait-Control 'OobeTitle' '准备好开始了'
    Click 'OobeNext'
    $null = Wait-Control 'QuickToolsTab' '' 'NPEduTools 快捷工具'
    $checks.Add('Completed users can reopen and finish independent shortcut-only setup without ClassIsland steps')
    Stop-App

    # Retain original artifacts and emulate a previously configured user on the same isolated endpoint.
    Move-Item -LiteralPath $statePath -Destination (Join-Path $runRoot 'completed-state.json')
    $recordingHash = (Get-FileHash -LiteralPath ($prefix + '.recording.json')).Hash
    $app = Start-App
    $null = Wait-Control 'SettingsTab' '' 'NPEduTools'
    if (Window) { throw 'Existing user was forced through OOBE.' }
    if ((Get-FileHash -LiteralPath ($prefix + '.recording.json')).Hash -ne $recordingHash) { throw 'Existing recording settings changed.' }
    $checks.Add('Existing user not interrupted and prior recording configuration retained')
    Stop-App

    # Fresh progress with login launch: the rail appears, neither full window is shown.
    Set-Content -LiteralPath $statePath -Value '{"Version":1}' -Encoding utf8
    $app = Start-App -AtLogin
    $null = Wait-Control 'RailTools' '' 'NPEduTools 快捷工具'
    Start-Sleep -Milliseconds 600
    if ((Window) -or (Window 'NPEduTools')) { throw 'Login launch opened a full window.' }
    Click 'RailTools' 'NPEduTools 快捷工具'; Click 'QuickOnboarding' 'NPEduTools 快捷工具'
    $null = Wait-Control 'OobeTitle' '选择用途'
    $checks.Add('Login remains quiet and sidebar can explicitly open pending setup')
    Click 'OobeLater'; Stop-App

    $peer.Kill($true); $null = $peer.WaitForExit(5000)
    $app = Start-App
    Click 'OpenOnboarding' 'NPEduTools'; Toggle 'OobeUseAutomatic'; Click 'OobeNext'; Click 'OobeNext'
    $null = Wait-Control 'OobeClock' '暂不可用'
    if ((Control 'OobeConfirmClock').Current.IsEnabled) { throw 'Unavailable school clock allowed confirmation.' }
    Click 'OobeNext'; $null = Wait-Control 'OobeError' '等待本体'
    Click 'OobeSkip'; Click 'OobeSkip'
    $null = Wait-Control 'OobeReview' '连接学校时间：已跳过'
    Save-Window 'unavailable-skipped.png'
    Click 'OobeNext'; $null = Wait-Control 'AutoRealStatus' '未启用' '自动录课 · 计划与预演'
    Click 'AutoHide' '自动录课 · 计划与预演'; Stop-App
    $checks.Add('Unavailable school time blocks confirmation but explicit skips can finish without enabling capture')

    Set-Content -LiteralPath $statePath -Value '{"Version":999}' -Encoding utf8
    $original = Get-Content -LiteralPath $statePath -Raw
    $app = Start-App
    $null = Wait-Control 'SettingsTab' '' 'NPEduTools'
    if (Window) { throw 'Corrupt progress should not force a wizard.' }
    Click 'OpenOnboarding' 'NPEduTools'; Click 'OobeNext'
    $null = Wait-Control 'OobeError' '进度未保存'
    if ((Get-Content -LiteralPath $statePath -Raw) -ne $original) { throw 'Unsupported state overwritten.' }
    Click 'OobeLeaveWithoutSave'
    if (Window) { throw 'Explicit close-without-saving failed.' }
    $checks.Add('Unsupported progress version preserved; attempted save does not advance')
    Stop-App
    @{passed=$true;checks=$checks.ToArray();completedAt=[DateTimeOffset]::Now} | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $runRoot 'summary.json') -Encoding utf8
    Write-Output "PASS: onboarding UI ($($checks.Count) scenarios). Artifacts: $runRoot"
}
finally {
    foreach ($process in $owned) { if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(5000) }; $process.Dispose() }
    Get-CimInstance Win32_Process -Filter "Name='NPEduTools.Host.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($pipe) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
}
