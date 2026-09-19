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
function Set-Text([string]$id, [string]$value) { (Wait-Control $id).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Plan-Rows {
    return (Control 'AutoPlans').FindAll([Windows.Automation.TreeScope]::Children,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::DataItem))
}
function Select-Plan([int]$index) { (Plan-Rows)[$index].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Plan-Text {
    return ((Control 'AutoPlans').FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -join '|'
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
    foreach ($argument in @($peerDll,$upstream,'bridge')) { $start.ArgumentList.Add($argument) }
    $peer = [Diagnostics.Process]::Start($start); $owned.Add($peer)
    $peerErrors = $peer.StandardError.ReadToEndAsync()
    if ($peer.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult() -ne 'READY') { throw 'Peer did not start.' }
    $app = Start-App
    Click 'OpenRecordingPlan' 'NPEduTools'
    $null = Wait-Control 'AutoSourceStatus' '2031-04-07.*测试生效课表.*2 节'
    (Control 'AutoLessonNumbers').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('2')
    Click 'AutoApplyRules'; Click 'AutoTogglePreview'
    $null = Wait-Control 'AutoPreviewStatus' '本日没有待开始'
    (Control 'AutoLessonNumbers').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('1')
    Click 'AutoApplyRules'
    $null = Wait-Control 'AutoPreviewStatus' '模拟录制中：预演数学'
    $null = Wait-Control 'AutoClockStatus' '2031-04-07.*使用 ClassIsland 学校时间'
    Click 'AutoSkipDay'
    $null = Wait-Control 'AutoPreviewStatus' '今天已暂停预演'
    $schoolState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($schoolState.SkipDate -ne '2031-04-07') { throw 'Skip-today used the Windows date.' }
    Click 'AutoSkipDay'
    # The stop above is deliberate: this occurrence must remain deduplicated after resuming today.
    $null = Wait-Control 'AutoPreviewStatus' '本日没有待开始'
    Save-Window 'preview-school-date.png'
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
    $testHost = Get-CimInstance Win32_Process -Filter "Name='NPEduTools.Host.exe'" | Where-Object { $_.ParentProcessId -eq $app.Id -and $_.CommandLine.Contains($pipe) } | Select-Object -First 1
    if (Get-CimInstance Win32_Process -Filter "Name='NPEduTools.Recorder.exe'" | Where-Object { $_.ParentProcessId -eq $app.Id -or $_.ParentProcessId -eq $testHost.ProcessId }) { throw 'Rehearsal must never start the recorder.' }
    Save-Window 'preview-restored.png'
    $bookPath = $statePath.Replace('.recording-preview.json','.recording-plans.json')
    Click 'AutoTomorrow'
    $null = Wait-Control 'AutoDateStatus' '2031-04-08.*预计课表，到当天'
    if ((Plan-Text) -notmatch '科目已排除') { throw 'Default exclusion did not normalize Chinese parentheses.' }
    Select-Plan 0; Click 'AutoInclude'
    if ((Plan-Text) -notmatch '单日明确录制') { throw 'Date include failed to override default exclusion.' }
    $book = Get-Content -LiteralPath $bookPath -Raw | ConvertFrom-Json
    if ($book.Overrides[0].Date -ne '2031-04-08' -or $book.Overrides[0].Mode -ne 'Include') { throw 'Date override was not saved for school tomorrow.' }
    Save-Window 'plans-tomorrow-include.png'
    Select-Plan 0; Click 'AutoInherit'
    if ((Plan-Text) -notmatch '科目已排除') { throw 'Restoring inheritance did not restore default exclusion.' }
    Set-Text 'AutoDatedName' '明天单次验证'; Set-Text 'AutoDatedStart' '12:00'; Set-Text 'AutoDatedEnd' '12:30'; Click 'AutoSaveDated'
    $null = Wait-Control 'AutoError' '指定日期的固定时段已保存'
    $book = Get-Content -LiteralPath $bookPath -Raw | ConvertFrom-Json
    if ($book.Dated.Count -ne 1 -or $book.Dated[0].Date -ne '2031-04-08' -or $book.Dated[0].Start -ne '12:00:00') { throw 'Single date fixed window was not saved.' }
    if ((Plan-Rows).Count -ne 3) { throw 'Fixed window did not appear in forecast.' }
    # Make the fixed window overlap an explicitly included lesson and verify both show a conflict.
    Select-Plan 0; Click 'AutoInclude'; Select-Plan 2
    Set-Text 'AutoDatedStart' '10:10'; Set-Text 'AutoDatedEnd' '10:20'; Click 'AutoSaveDated'
    Start-Sleep -Milliseconds 200
    if ((Plan-Text) -notmatch '固定时段与其他任务重叠') { throw 'Overlapping future tasks were not flagged.' }
    Save-Window 'plans-conflict.png'
    Select-Plan 1; Click 'AutoDeleteDated'; Select-Plan 0; Click 'AutoInherit'
    # Add a disabled weekly fixed rule, then explicitly enable it in the editor.
    Click 'AutoNewRule'; Set-Text 'AutoRuleName' '周期固定验证'
    (Control 'AutoRuleEnabled').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    $kind = Control 'AutoRuleKind'; $kind.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $fixedKind = $kind.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'固定时间段'))
    $fixedKind.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Set-Text 'AutoRuleStart' '13:00'; Set-Text 'AutoRuleEnd' '13:30'; Click 'AutoApplyRules'
    $null = Wait-Control 'AutoError' '规则已应用并保存'
    $book = Get-Content -LiteralPath $bookPath -Raw | ConvertFrom-Json
    $fixedRule = $book.Recurring | Where-Object Name -eq '周期固定验证'
    if (-not $fixedRule.Enabled -or $fixedRule.FixedStart -ne '13:00:00') { throw 'Weekly fixed rule was not saved.' }
    if ((Plan-Text) -notmatch '周期固定验证') { throw 'Weekly fixed rule missing from selected school date.' }
    Save-Window 'plans-weekly-fixed.png'
    # Invalid editing must preserve both the saved configuration and the user's text for correction.
    Set-Text 'AutoRuleEnd' '12:30'; Click 'AutoApplyRules'
    $null = Wait-Control 'AutoError' '未应用修改'
    if ((Control 'AutoRuleEnd').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne '12:30') { throw 'Validation discarded editor input.' }
    Set-Text 'AutoRuleEnd' '13:30'; Click 'AutoApplyRules'
    Click 'AutoAfterTomorrow'; $null = Wait-Control 'AutoDateStatus' '2031-04-09.*预计课表，到当天'
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if (@($state.Events | Where-Object Action -eq '应开始').Count -ne 1) { throw 'Viewing future dates triggered a rehearsal.' }
    Copy-Item -LiteralPath $bookPath -Destination (Join-Path $runRoot 'recording-plans.json')
    Click 'AutoToday'; $null = Wait-Control 'AutoDateStatus' '2031-04-07.*当天生效'
    $peer.Kill($true); $null = $peer.WaitForExit(5000)
    Click 'AutoRefresh'
    $null = Wait-Control 'AutoSourceStatus' '日程暂不可用|读取日程超时'
    $null = Wait-Control 'AutoPreviewStatus' '等待新鲜样本'
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
    # Plan-book corruption is independently rejected; then verify legacy migration and its immutable backup.
    Copy-Item -LiteralPath (Join-Path $runRoot 'preview-state.json') -Destination $statePath -Force
    Set-Content -LiteralPath $bookPath -Value '{"Version":999}' -Encoding utf8
    $app = Start-App; Click 'OpenRecordingPlan' 'NPEduTools'; $null = Wait-Control 'AutoError' '录课计划无法读取'
    if ((Control 'AutoTogglePreview').Current.IsEnabled) { throw 'Invalid plan book must disable rehearsal.' }
    if ((Get-Content -LiteralPath $bookPath -Raw | ConvertFrom-Json).Version -ne 999) { throw 'Invalid plan book was overwritten.' }
    Click 'AutoHide'; Click 'StopHost' 'NPEduTools'; if (-not $app.WaitForExit(20000)) { throw 'Book-validation shutdown timed out.' }
    Move-Item -LiteralPath $bookPath -Destination (Join-Path $runRoot 'invalid-book.json')
    $legacyHash = (Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash
    $app = Start-App; Click 'OpenRecordingPlan' 'NPEduTools'; $null = Wait-Control 'AutoError' '已备份并作为停用草稿'
    $migrated = Get-Content -LiteralPath $bookPath -Raw | ConvertFrom-Json
    if ($migrated.Recurring.Count -ne 1 -or $migrated.Recurring[0].Enabled -or $migrated.Recurring[0].UseDefaultExclusions) { throw 'Migration changed legacy filtering or enabled the draft.' }
    if ((Get-FileHash -LiteralPath ($statePath + '.pre-plan-book.bak') -Algorithm SHA256).Hash -ne $legacyHash) { throw 'Migration backup differs from original preview.' }
    Save-Window 'plans-legacy-migrated.png'
    Click 'AutoHide'; Click 'StopHost' 'NPEduTools'; if (-not $app.WaitForExit(20000)) { throw 'Migration shutdown timed out.' }
    @{passed=$true;checks=@('Real WPF uses ClassIsland 2031-04-07 rather than Windows date','School skip-today and ordinal filters','Rehearsal emits start and stop without capture','Manual skip survives refresh and full App restart','Sidebar entry and settings persistence','Future date queries and default exclusions','Date include and inheritance','Single date fixed window and conflict','Weekly fixed editor and invalid input preservation','Future viewing never triggers current rehearsal','Unavailable upstream keeps App responsive','Invalid configuration is preserved without crashing','Invalid plan book is preserved and rehearsal disabled','Legacy rules migrate disabled with identical original backup');completedAt=[DateTimeOffset]::Now} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'summary.json') -Encoding utf8
    Write-Output "PASS: auto recording preview UI. Artifacts: $runRoot"
}
finally {
    foreach ($process in $owned) { if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(5000) }; $process.Dispose() }
    Get-CimInstance Win32_Process -Filter "Name='NPEduTools.Host.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($pipe) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
}
