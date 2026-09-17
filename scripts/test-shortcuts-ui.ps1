param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$runId = [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $projectRoot ".artifacts/shortcuts-ui/$runId"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$pipe = "NPEduTools.Test.shortcuts.$runId"
$appExe = Join-Path $projectRoot "src/NPEduTools.App/bin/$Configuration/net10.0-windows/NPEduTools.App.exe"
$hostExe = Join-Path (Split-Path $appExe -Parent) 'Host/NPEduTools.Host.exe'
$settingsFile = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('NPEduTools/ui/' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($pipe))).Substring(0,24) + '.shortcuts.json')
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$checks = [Collections.Generic.List[string]]::new()
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing.Common
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ShortcutCapture {
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
}
'@

# A private WinExe records shell launch requests and immediately exits. No user application is closed.
@'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>
'@ | Set-Content -LiteralPath (Join-Path $runRoot 'ShortcutFixture.csproj')
@'
File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "receipt.txt"), (args.Length == 0 ? "APP" : args[0]) + Environment.NewLine);
'@ | Set-Content -LiteralPath (Join-Path $runRoot 'Program.cs')
& (Join-Path $PSScriptRoot 'dotnet.ps1') @('build',(Join-Path $runRoot 'ShortcutFixture.csproj'),'--verbosity','quiet')
if ($LASTEXITCODE -ne 0) { throw 'Launch fixture build failed.' }
$fixtureExe = Join-Path $runRoot 'bin/Debug/net10.0/ShortcutFixture.exe'
$receipt = Join-Path (Split-Path $fixtureExe -Parent) 'receipt.txt'
$extension = ".npeutest$runId"
$file = Join-Path $runRoot "lesson & notes$extension"
Set-Content -LiteralPath $file -Value 'Private shortcut file launch test.'
$association = "NPEduTools.Test.$runId"
$registryPaths = @("Software\Classes\$extension","Software\Classes\$association")

function Start-TestApp {
 $start = [Diagnostics.ProcessStartInfo]::new($appExe)
 $start.UseShellExecute = $false
 foreach ($arg in @('--pipe',$pipe,'--classisland-pipe',"$pipe.absent")) { $start.ArgumentList.Add($arg) }
 $p = [Diagnostics.Process]::Start($start); $owned.Add($p); return $p
}
function Window([string]$name) {
 $condition = [Windows.Automation.AndCondition]::new([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id),[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name),[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Window))
 $found = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$condition)
 if ($found -or $name -eq 'NPEduTools') { return $found }
 $main = Window 'NPEduTools'
 if ($main) { return $main.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition) }
}
function Control($root,[string]$id) {
 if (-not $root) { return $null }
 return $root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
}
function MainControl([string]$id) { return Control (Window 'NPEduTools') $id }
function Click([string]$id) { (MainControl $id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Wait-For([scriptblock]$condition,[string]$message) {
 $end = [DateTime]::UtcNow.AddSeconds(20)
 do { if (& $condition) { return }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $end)
 throw $message
}
function Set-Text($root,[string]$id,[string]$text) { (Control $root $id).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($text) }
function Edit-Entry([string]$name,[string]$target,[string]$kind,[switch]$Existing,[switch]$FromHome) {
 if ($FromHome) { Click 'HomeAddShortcut' } elseif ($Existing) { Click 'EditShortcut' } else { Click 'AddShortcut' }
 $title = if ($Existing) { '编辑快捷启动' } else { '添加快捷启动' }
 Wait-For { $null -ne (Window $title) } 'Editor did not open.'
 $editor = Window $title
 $combo = Control $editor 'ShortcutKind'
 $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
 $choice = $combo.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$kind))
 $choice.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Set-Text $editor 'ShortcutName' $name
 Set-Text $editor 'ShortcutTarget' $target
 (Control $editor 'ShortcutSave').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
 Wait-For { $null -eq (Window $title) } 'Editor did not save.'
 Wait-For { (Test-Path -LiteralPath $settingsFile) -and @((Entries) | Where-Object { $_.Name -eq $name -and $_.Target -eq $target }).Count -eq 1 } 'Editor result was not persisted.'
}
function Entries { return @((Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).Items) }
function Select-Entry([string]$name) {
 $list = MainControl 'ShortcutList'
 $item = $list.FindFirst([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name))
 $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Capture($root,[string]$name) {
 $h = [IntPtr]$root.Current.NativeWindowHandle
 $r = [ShortcutCapture+Rect]::new(); $null = [ShortcutCapture]::GetWindowRect($h,[ref]$r)
 $bitmap = [Drawing.Bitmap]::new($r.Right-$r.Left,$r.Bottom-$r.Top)
 $graphics = [Drawing.Graphics]::FromImage($bitmap); $dc = $graphics.GetHdc()
 try { if (-not [ShortcutCapture]::PrintWindow($h,$dc,2)) { throw 'Capture failed.' } }
 finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
 try { $bitmap.Save((Join-Path $runRoot $name),[Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
}
function Stop-App {
 if (-not (Window 'NPEduTools')) { $duplicate = Start-TestApp; $null = $duplicate.WaitForExit(5000) }
 Click 'StopHost'
 if (-not $app.WaitForExit(15000)) { throw 'App failed to exit.' }
}

Write-Output "Shortcut UI artifacts: $runRoot"
try {
 # A unique extension association exercises real shell file opening without changing user associations.
 $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registryPaths[0]); $key.SetValue('',$association); $key.Dispose()
 $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registryPaths[1] + '\shell\open\command'); $key.SetValue('','"' + $fixtureExe + '" "%1"'); $key.Dispose()
 $app = Start-TestApp
 Wait-For { $null -ne (MainControl 'ShortcutsTab') } 'Main window unavailable.'
 Edit-Entry '课堂工具' $fixtureExe '应用' -FromHome
 Edit-Entry '当天课件' $file '文件'
 Edit-Entry '教学平台' 'https://example.com/lesson?q=1&mode=2' '网址'
 if ((Entries).Count -ne 3) { throw 'Three entry types were not saved.' }
 Select-Entry '教学平台'; Click 'MoveShortcutUp'
 Wait-For { (Entries)[1].Name -eq '教学平台' } 'Reordering was not persisted.'
 Edit-Entry '教学平台 · 常用资源' 'https://example.com/resources' '网址' -Existing
 Select-Entry '当天课件'; Click 'RemoveShortcut'
 Wait-For { (Entries).Count -eq 2 } 'Removal was not persisted.'
 if ((Entries).Count -ne 2 -or -not (Test-Path -LiteralPath $file)) { throw 'Removing shortcut affected file or did not persist.' }
 Click 'UndoShortcutDelete'
 Wait-For { (Entries).Count -eq 3 } 'Undo did not restore entries.'
 $checks.Add('Actual editor adds app/file/url, edits, reorders, removes and undoes; original file survives')
 $items = Entries
 $appId = 'ShortcutOpen_' + ([Guid]$items[0].Id).ToString('N')
 Click $appId
 Wait-For { (Test-Path -LiteralPath $receipt) -and ((Get-Content -LiteralPath $receipt) -contains 'APP') } 'Application was not launched.'
 Wait-For { (MainControl $appId).Current.IsEnabled } 'Launch buttons did not recover.'
 $fileId = 'ShortcutOpen_' + ([Guid]$items[2].Id).ToString('N')
 Click $fileId
 Wait-For { (Get-Content -LiteralPath $receipt) -contains $file } 'File association did not receive the exact path.'
 Wait-For { (MainControl $fileId).Current.IsEnabled } 'File launch did not recover.'
 Capture (Window 'NPEduTools') 'manager.png'
 $checks.Add('App launch and default file association reach a real owned executable; spaces and ampersands remain literal')
 Click 'HomeTab'
 Capture (Window 'NPEduTools') 'home-shortcuts.png'
 Click 'OpenQuick'
 $quick = Window 'NPEduTools 快捷工具'
 (Control $quick 'QuickShortcutsTab').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
 Start-Sleep -Milliseconds 200
 if (-not (Control $quick $fileId)) { throw 'Quick panel did not synchronize entries.' }
 Capture $quick 'quick.png'
 $before = @(Get-Content -LiteralPath $receipt).Count
 (Control $quick $appId).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
 Wait-For { @(Get-Content -LiteralPath $receipt).Count -eq $before + 1 } 'Quick app launch failed.'
 Start-Sleep -Milliseconds 800
 # Rename only this test's file to emulate a moved classroom document.
 Move-Item -LiteralPath $file -Destination ($file + '.moved')
 (Control $quick $fileId).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
 Wait-For { (Control $quick 'QuickShortcutMessage').Current.Name -like '*文件不存在*' } 'Missing-file feedback absent.'
 Wait-For { (Control $quick 'QuickRepairShortcut').Current.IsEnabled } 'Repair action unavailable.'
 (Control $quick 'QuickRepairShortcut').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
 Wait-For { $null -ne (Window '编辑快捷启动') } 'Quick repair did not open editor.'
 $editor = Window '编辑快捷启动'
 Set-Text $editor 'ShortcutTarget' ($file + '.moved')
 (Control $editor 'ShortcutSave').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
 Wait-For { $null -eq (Window '编辑快捷启动') } 'Repaired entry did not save.'
 $checks.Add('Quick panel launches shared entries; missing file exposes an editor that repairs the saved target')
 Stop-App
 $app = Start-TestApp
 Wait-For { $null -ne (MainControl 'ShortcutsTab') } 'App restart unavailable.'
 Click 'ShortcutsTab'
 if ((Entries)[1].Name -ne '教学平台 · 常用资源' -or (Entries)[2].Target -ne ($file + '.moved')) { throw 'Restart lost shortcut edits.' }
 if (-not (MainControl $fileId)) { throw 'Restart did not render saved entry.' }
 Stop-App
 $originalJson = Get-Content -LiteralPath $settingsFile -Raw
 Set-Content -LiteralPath $settingsFile -Value '{broken'
 $app = Start-TestApp
 Wait-For { $null -ne (MainControl 'ShortcutsTab') } 'Corrupt-config startup crashed.'
 if ((MainControl 'HomeAddShortcut').Current.IsEnabled) { throw 'Home add action did not reflect the unreadable catalog.' }
 Click 'ShortcutsTab'
 if ((MainControl 'AddShortcut').Current.IsEnabled -or (Get-Content -LiteralPath $settingsFile -Raw).Trim() -ne '{broken') { throw 'Corrupt catalog was silently overwritten.' }
 Set-Content -LiteralPath $settingsFile -Value $originalJson
 Click 'ReloadShortcuts'
 Wait-For { (MainControl 'AddShortcut').Current.IsEnabled } 'Reload did not recover after repair.'
 Stop-App
 $checks.Add('Restart restores order and edits; corrupted configuration stays intact and can be reloaded after repair')
 @{Passed=$true;Checks=@($checks)} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot 'summary.json')
 Write-Output 'PASS: shortcut editing, shell launch, quick panel, missing target, persistence and corrupt configuration.'
} finally {
 foreach ($p in $owned) { if (-not $p.HasExited) { $p.Kill($true); $null = $p.WaitForExit(5000) }; $p.Dispose() }
 $remaining = Get-CimInstance Win32_Process -Filter "Name='NPEduTools.Host.exe'" | Where-Object { $_.ExecutablePath -eq $hostExe -and $_.CommandLine.Contains($pipe) }
 foreach ($p in $remaining) { $ownedHost = [Diagnostics.Process]::GetProcessById($p.ProcessId); $ownedHost.Kill($true); $ownedHost.Dispose() }
 foreach ($path in $registryPaths) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($path,$false) }
 @($checks) | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'checks.json')
}
