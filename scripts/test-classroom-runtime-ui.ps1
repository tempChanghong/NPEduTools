param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [ValidateSet('Unconfigured','Exam','Incomplete')][string]$Scenario = 'Unconfigured')
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
$pipe = 'NPEduTools.Test.classroom-runtime-ui.' + [Guid]::NewGuid().ToString('N')
$out = Join-Path $root ('.artifacts/classroom-runtime-ui/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out -Force | Out-Null
$title = '课堂模式 · NPEduTools'
$checks = [Collections.Generic.List[string]]::new()
function Window([string]$name = $title) {
    [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id),
            [Windows.Automation.AndCondition]::new([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name), [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Window))))
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
function Snapshot([string]$file, [string]$name = $title) {
    $hwnd = [IntPtr](Window $name).Current.NativeWindowHandle
    $rect = [ExamAwareCapture+Rect]::new(); $null = [ExamAwareCapture]::GetWindowRect($hwnd,[ref]$rect)
    $bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap); $hdc = $graphics.GetHdc()
    try { $null = [ExamAwareCapture]::PrintWindow($hwnd,$hdc,2) } finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    try { $bitmap.Save((Join-Path $out $file),[Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
}
$app = $null
try {
    if ($Scenario -ne 'Unconfigured') {
        $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($pipe))).Substring(0,24)
        $data=Join-Path ([IO.Path]::GetTempPath()) ('NPEduTools/instances/'+$hash)
        New-Item -ItemType Directory -Path $data -Force | Out-Null
        $startup=@{classIslandPath='C:\ModeFixture\ClassIsland.exe';classIslandRevision=1;classIslandEnabled=$false;examAwarePath='C:\ModeFixture\ExamAware.exe';examAwareRevision=1;examAwareEnabled=$true}
        $state=@{revision=1;mode='Exam';phase='Idle';automaticPaused=$true;message='考试模式：自动录课暂停，原计划保留。';recentRequests=@()}
        if ($Scenario -eq 'Incomplete') {
            $state.mode='Daily'; $state.phase='Incomplete';$state.message='ClassIsland 拒绝退出，请处理弹窗后重试。'
            $previous=$startup.Clone();$previous.classIslandEnabled=$true;$previous.examAwareEnabled=$false
            $state.recovery=@{previousMode='Daily';previousPause=$false;startup=$previous}
            $state.runtime=@{target='Exam';startup=$startup;startupCheckedAt=[DateTimeOffset]::UtcNow.ToString('O');step='CloseClassIsland'}
        }
        $state|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $data 'classroom-mode.json') -Encoding utf8NoBOM
    }
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $root "src/NPEduTools.App/bin/$Configuration/net10.0-windows/NPEduTools.App.exe"))
    $start.UseShellExecute=$false
    foreach($arg in @('--pipe',$pipe,'--classisland-pipe',($pipe+'.absent'))){$start.ArgumentList.Add($arg)}
    $app=[Diagnostics.Process]::Start($start)
    Click 'OobeLater' 'NPEduTools · 初始设置'
    $expected=switch($Scenario){'Exam'{'考试模式'} 'Incomplete'{'切换未完成'} default{'尚未设置模式'}}
    $rail=switch($Scenario){'Exam'{'考试'} 'Incomplete'{'待处理'} default{'未设'}}
    $null=Wait-Control 'HomeClassroomModeTitle' $expected 'NPEduTools'
    $null=Wait-Control 'RailClassroomMode' $rail 'NPEduTools 快捷工具'
    Snapshot 'home.png' 'NPEduTools'
    Click 'RailTools' 'NPEduTools 快捷工具'
    $null=Wait-Control 'QuickClassroomModeTitle' $expected 'NPEduTools 快捷工具'
    if($Scenario -ne 'Unconfigured'){
        $null=Wait-Control 'HomeClassroomModeDetail' '暂停' 'NPEduTools'
        $null=Wait-Control 'QuickClassroomModeDetail' '暂停' 'NPEduTools 快捷工具'
    }
    Snapshot 'sidebar.png' 'NPEduTools 快捷工具'
    $checks.Add('Main window, expanded sidebar and collapsed rail show consistent mode and pause state: '+$Scenario)
    Click 'QuickClassroomMode' 'NPEduTools 快捷工具'
    $null=Wait-Control 'ClassroomModeTitle' $expected
    $checks.Add('Sidebar mode entry opens the existing management page')
    if($Scenario -eq 'Incomplete'){
        $null=Wait-Control 'ClassroomRetry'
        $null=Wait-Control 'ClassroomRestore'
        if((Control 'ClassroomDaily').Current.IsEnabled){throw 'New mode must not overwrite recovery'}
        Click 'ClassroomRetry'
        $end=[DateTime]::UtcNow.AddSeconds(5)
        do{$dialog=Window '重试即时切换';if(-not $dialog){Start-Sleep -Milliseconds 100}}while(-not $dialog -and [DateTime]::UtcNow -lt $end)
        if(-not $dialog){throw 'Retry confirmation missing'}
        $dialog.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
        $null=Wait-Control 'ClassroomRetry'
        $checks.Add('Incomplete runtime exposes retry and restoration, blocks new mode and supports cancelling retry')
    }else{
        $toggle=(Wait-Control 'ClassroomSwitchRunning').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
        if($toggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off){throw 'Runtime switch must default off'}
        $toggle.Toggle()
        Click 'ClassroomDaily'
        $null=Wait-Control 'ClassroomSetupSummary' '还有配置需要处理'
        $null=Wait-Control 'ClassroomDaily'
        if(Window '切换到日常模式'){throw 'Missing setup should stop before confirmation'}
        $null=Wait-Control 'ClassroomModeTitle' $expected
        $checks.Add('Runtime toggle defaults off; missing setup stops Daily switch before confirmation and preserves mode')
    }
    Snapshot 'mode-management.png'
    (Window).GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    Click 'StopHost' 'NPEduTools'
    if(-not $app.WaitForExit(20000)){throw 'App did not exit'}
    @{passed=$true;scenario=$Scenario;checks=$checks.ToArray();completedAt=[DateTimeOffset]::Now}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $out 'summary.json') -Encoding utf8
    Write-Output "PASS: Runtime modes UI [$Scenario] ($($checks.Count) scenarios). Artifacts: $out"
}finally{
    if($app){if(-not $app.HasExited){$app.Kill($true);$null=$app.WaitForExit(5000)};$app.Dispose()}
    Get-CimInstance Win32_Process -Filter "Name='NPEduTools.Host.exe'"|Where-Object{$_.CommandLine -and $_.CommandLine.Contains($pipe)}|ForEach-Object{Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue}
}
