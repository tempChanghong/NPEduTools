param([string]$ClassIslandBinary = 'D:/WebstormProjects/ClassIsland/ClassIsland.Desktop/bin/Debug/net8.0-windows10.0.19041.0/ClassIsland.Desktop.exe', [switch]$VerifyHost)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'dotnet.ps1') --version
if ($LASTEXITCODE -ne 0) { throw 'SDK unavailable.' }
if (Get-Process -Name ClassIsland,ClassIsland.Desktop -ErrorAction SilentlyContinue) { throw 'Existing ClassIsland detected; isolated test not started.' }
$mutex = $null
if ([Threading.Mutex]::TryOpenExisting('Global\ClassIsland.Lock',[ref]$mutex)) { $mutex.Dispose(); throw 'ClassIsland mutex is in use.' }
$pluginOutput = Join-Path $projectRoot 'plugins/NPEduTools.ClassIsland.Bridge/bin/Release/net8.0'
$fixtureOutput = Join-Path $projectRoot 'tests/NPEduTools.Bridge.TestFixture/bin/Release/net8.0'
$probeDll = Join-Path $projectRoot 'tools/NPEduTools.BridgeProbe/bin/Release/net10.0/NPEduTools.BridgeProbe.dll'
foreach ($file in @($ClassIslandBinary,$probeDll,(Join-Path $pluginOutput 'NPEduTools.ClassIsland.Bridge.dll'),(Join-Path $fixtureOutput 'NPEduTools.Bridge.TestFixture.dll'))) {
    if (-not (Test-Path -LiteralPath $file)) { throw "Build prerequisite missing: $file" }
}
$runRoot = Join-Path $projectRoot ('.artifacts/classisland-bridge/' + [guid]::NewGuid().ToString('N'))
$appDirectory = Join-Path $runRoot 'app'
$packageRoot = Join-Path $runRoot 'package'
$dataDirectory = Join-Path $packageRoot 'data'
$fixtureDirectory = Join-Path $dataDirectory 'Plugins/npedutools.test.bridgefixture'
New-Item -ItemType Directory -Force $appDirectory,$fixtureDirectory,(Join-Path $dataDirectory 'Profiles') | Out-Null
Copy-Item -Path (Join-Path (Split-Path $ClassIslandBinary) '*') -Destination $appDirectory -Recurse
Copy-Item -Path (Join-Path $fixtureOutput '*') -Destination $fixtureDirectory -Recurse
Set-Content -LiteralPath (Join-Path $packageRoot 'PackageType') -Value 'folder' -NoNewline
Set-Content -LiteralPath (Join-Path $runRoot 'fixture-marker') -Value 'Private disposable ClassIsland bridge test'
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$logs = [Collections.Generic.List[object]]::new()
$samples = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
$bridgeIds = [Collections.Generic.HashSet[int]]::new()
$appExe = Join-Path $appDirectory (Split-Path $ClassIslandBinary -Leaf)
$hostPipe = 'NPEduTools.Test.bridge-host.' + [guid]::NewGuid().ToString('N')
$hostExe = Join-Path $projectRoot 'src/NPEduTools.Host/bin/Release/net10.0/NPEduTools.Host.exe'
$cliExe = Join-Path $projectRoot 'src/NPEduTools.Cli/bin/Release/net10.0/NPEduTools.Cli.exe'
$hostSamples = [Collections.Generic.List[object]]::new()
function Read-HostClock {
    # Preserve the school's comparison carrier; PowerShell's default parser converts +00:00 to local time.
    $response = & $cliExe school-clock --pipe $hostPipe | ConvertFrom-Json -DateKind String
    if ($LASTEXITCODE -ne 0 -or -not $response.schoolClock) { throw 'Host school-clock query failed.' }
    $hostSamples.Add($response.schoolClock)
    return $response.schoolClock
}
function Wait-HostClock([string]$State = 'Advancing') {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        $sample = Read-HostClock
        if ($sample.state -eq $State -and ($State -ne 'Advancing' -or $sample.ageMs -lt 3000)) { return $sample }
        Start-Sleep -Milliseconds 250
    } while ($deadline.Elapsed.TotalSeconds -lt 25)
    throw "Host clock did not reach $State. Last: $($sample | ConvertTo-Json -Compress -Depth 8)"
}

function Write-Fixture {
    $profileId = [guid]::NewGuid().ToString(); $layoutId = [guid]::NewGuid().ToString(); $subjectId = [guid]::NewGuid().ToString()
    $plans = @{}
    foreach ($weekday in 0..6) {
        $plans[[guid]::NewGuid().ToString()] = @{Name="Bridge day $weekday";TimeLayoutId=$layoutId;IsEnabled=$true;
            TimeRule=@{WeekDay=$weekday;WeekCountDiv=0};Classes=@(@{SubjectId=$subjectId},@{SubjectId=$subjectId})}
    }
    $profile = @{Id=$profileId;Subjects=@{$subjectId=@{Name='桥接验证课程'}};ClassPlans=$plans;
        TimeLayouts=@{$layoutId=@{Name='无末节课间';Layouts=@(
            @{StartTime='00:00:00';EndTime='10:00:00';TimeType=0},
            @{StartTime='10:00:00';EndTime='23:59:59';TimeType=0})}}}
    $settings = @{SelectedProfile='Bridge.Live.json';IsWelcomeWindowShowed=$true;LastAppVersion='2.1.0.1';
        IsSplashEnabled=$false;IsMainWindowVisible=$false;IsNotificationEnabled=$false;IsSpeechEnabled=$false;
        IsExactTimeEnabled=$false;TimeOffsetSeconds=0;DebugTimeOffsetSeconds=0;IsTimeAutoAdjustEnabled=$false;
        AutoInstallUpdateNextStartup=$false;IsPluginsAutoUpdateEnabled=$false;TrustedProfileIds=@($profileId)}
    $profile | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $dataDirectory 'Profiles/Bridge.Live.json') -Encoding utf8
    $settings | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $dataDirectory 'Settings.json') -Encoding utf8
}
function Start-Island([bool]$WithBridge = $true) {
    # A new fixture process must not replay the previous process's stop command.
    Remove-Item -LiteralPath (Join-Path $runRoot 'command.json') -ErrorAction SilentlyContinue
    $start = [Diagnostics.ProcessStartInfo]::new($appExe); $start.UseShellExecute=$false
    $start.CreateNoWindow=$true; $start.WindowStyle='Hidden'; $start.WorkingDirectory=$runRoot
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach ($arg in @('--quiet','-dm')) { $start.ArgumentList.Add($arg) }
    if ($WithBridge) { $start.ArgumentList.Add('-epp'); $start.ArgumentList.Add($pluginOutput) }
    $start.Environment['ClassIsland_PackageRoot']=$packageRoot; $start.Environment['ClassIsland_WaitDebuggers']='0'
    $start.Environment['NPEEDUTOOLS_BRIDGE_FIXTURE']=$runRoot
    $process=[Diagnostics.Process]::Start($start); $owned.Add($process)
    if ($WithBridge) { $null=$bridgeIds.Add($process.Id) }
    $logs.Add(@{name="classisland-$($process.Id)";out=$process.StandardOutput.ReadToEndAsync();err=$process.StandardError.ReadToEndAsync()})
    return $process
}
function Query([string]$Mode = '') {
    $start=[Diagnostics.ProcessStartInfo]::new($env:NPEEDUTOOLS_DOTNET_HOST); $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.ArgumentList.Add($probeDll); if ($Mode) { $start.ArgumentList.Add($Mode) }
    $client=[Diagnostics.Process]::Start($start); $out=$client.StandardOutput.ReadToEndAsync(); $err=$client.StandardError.ReadToEndAsync()
    try {
        if (-not $client.WaitForExit(10000)) { $client.Kill($true); throw 'Probe exceeded process deadline.' }
        $text=$out.GetAwaiter().GetResult()
        if (-not $text) { return @{error='NoResponse';exitCode=$client.ExitCode} }
        $result=$text | ConvertFrom-Json; $samples.Add($result)
        return $result
    } finally { $client.Dispose() }
}
function Wait-Snapshot([string]$DayStatus = 'Ready') {
    $deadline=[DateTime]::UtcNow.AddSeconds(25)
    do {
        if ($app.HasExited) { throw "ClassIsland exited: $($app.ExitCode)" }
        $result=Query
        if ($result.clockState -eq 'Advancing' -and $result.day.status -eq $DayStatus -and $result.lifecycle -eq 'Ready') { return $result }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "No healthy snapshot: $($result | ConvertTo-Json -Depth 8 -Compress)"
}
function Command([string]$Action,[hashtable]$Fields = @{}) {
    $id=[guid]::NewGuid().ToString(); $Fields['id']=$id; $Fields['action']=$Action
    $tmp=Join-Path $runRoot 'command.tmp'; $Fields | ConvertTo-Json | Set-Content -LiteralPath $tmp -Encoding utf8
    Move-Item -LiteralPath $tmp -Destination (Join-Path $runRoot 'command.json') -Force
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    do {
        $ackPath=Join-Path $runRoot 'ack.json'
        if (Test-Path -LiteralPath $ackPath) {
            try { $ack=Get-Content -LiteralPath $ackPath -Raw | ConvertFrom-Json; if ($ack.id -eq $id) { return $ack } } catch { }
        }
        Start-Sleep -Milliseconds 75
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Fixture command timed out: $Action"
}
function Passed([string]$Name) { $checks.Add($Name); Write-Output "PASS: $Name" }
function Stop-Island {
    $null=Command 'stop'
    if (-not $app.WaitForExit(10000)) { throw 'Graceful ClassIsland shutdown timed out.' }
    if ($bridgeIds.Contains($app.Id)) {
        $log=$logs | Where-Object name -eq "classisland-$($app.Id)" | Select-Object -First 1
        if ($log.err.GetAwaiter().GetResult() -notmatch 'bridge stopped; subscriptions released') { throw 'Bridge cleanup callback was not observed.' }
    }
}
Write-Output "Bridge live artifacts: $runRoot"
try {
    Write-Fixture; $app=Start-Island
    $first=Wait-Snapshot; $hello=Query 'hello'
    if ($hello.contract -ne 'npedutools.recordingbridge.p0' -or $first.day.lessons.Count -ne 2) { throw 'Handshake/day mapping mismatch.' }
    Passed 'Real plugin load, shared IPC, .NET 8 to .NET 10, and two mapped lessons'
    if ($VerifyHost) {
        $start = [Diagnostics.ProcessStartInfo]::new($hostExe); $start.UseShellExecute = $false
        $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        foreach ($argument in @('--pipe', $hostPipe)) { $start.ArgumentList.Add($argument) }
        $bridgeHost = [Diagnostics.Process]::Start($start); $owned.Add($bridgeHost)
        $logs.Add(@{name='bridge-host';out=$bridgeHost.StandardOutput.ReadToEndAsync();err=$bridgeHost.StandardError.ReadToEndAsync()})
        Start-Sleep -Milliseconds 300
        $hostFirst = Wait-HostClock
        foreach ($i in 1..6) {
            Start-Sleep -Milliseconds 500; $hostNext = Wait-HostClock
            if ($hostNext.connectionId -ne $hostFirst.connectionId -or $hostNext.bridgeInstanceId -ne $first.bridgeInstanceId) { throw 'Healthy connection was replaced or wrong plugin was read.' }
        }
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($bridgeHost.Id)" | Where-Object CommandLine -match '--bridge-worker')
        if ($children.Count -ne 1) { throw 'Expected exactly one persistent bridge worker.' }
        Passed 'Host keeps one leased bridge worker and one healthy IPC connection across repeated reads'
    }
    foreach ($offset in @(120,-120)) {
        $null=Command 'offset' @{seconds=$offset}; Start-Sleep -Milliseconds 400
        $sample=Wait-Snapshot
        $difference=([datetime]$sample.effectiveLocalDateTime - [DateTime]::Now).TotalSeconds
        if ([Math]::Abs($difference-$offset) -gt 2) { throw "Wrong school offset: $difference, expected $offset" }
        if ($sample.clockEpoch -le $first.clockEpoch) { throw 'Clock epoch did not change.' }
        Passed "Live manual time offset $offset seconds"
        if ($VerifyHost) {
            Start-Sleep -Milliseconds 750; $hostOffset = Wait-HostClock
            $hostDifference = (([DateTimeOffset]::Parse($hostOffset.schoolNow)).DateTime - [DateTime]::Now).TotalSeconds
            if ([Math]::Abs($hostDifference-$offset) -gt 3) { throw 'Host applied an extra offset or returned old time.' }
            Passed "Host passes school offset $offset without adding Windows timezone"
        }
    }
    $null=Command 'offset' @{seconds=([DateTime]::Today.AddHours(12)-[DateTime]::Now).TotalSeconds}
    Start-Sleep -Milliseconds 400; $last=Wait-Snapshot
    if (([datetime]$last.effectiveLocalDateTime).Hour -ne 12) { throw 'Last-lesson fixture time mismatch.' }
    Passed 'School time remains available during the last lesson without a following break'
    $null=Command 'enabled' @{value=$false}; Start-Sleep -Milliseconds 400
    $null=Wait-Snapshot 'Disabled'; Passed 'Disabled timetable does not remove clock'
    $null=Command 'timer' @{value=$false}; Start-Sleep -Milliseconds 400
    $null=Wait-Snapshot 'TimerStopped'; Passed 'Stopped lesson timer uses UI clock fallback'
    $null=Command 'timer' @{value=$true}; $null=Command 'enabled' @{value=$true}
    $null=Wait-Snapshot
    $null=Command 'block'; Start-Sleep -Milliseconds 3500
    $stale=Query
    if ($stale.clockState -ne 'Stale' -or $stale.sampleAgeMs -lt 3000) { throw "UI stall not identified: $($stale | ConvertTo-Json -Compress)" }
    $stale2=Query
    if ($stale2.clockState -eq 'Stale' -and $stale2.sequence -ne $stale.sequence) { throw 'Reading stale cache invented a new sample.' }
    Passed 'IPC stays responsive while UI stalls; cache age exposes stale data'
    if ($VerifyHost) {
        $hostStale = Wait-HostClock 'Stale'
        if ($hostStale.ageMs -lt 3000) { throw 'Host discarded source cache age.' }
        Passed 'Host exposes stale clock during real ClassIsland UI blocking'
    }
    $null=Wait-Snapshot
    if ($VerifyHost) {
        $hostRecovered = Wait-HostClock
        if ($hostRecovered.connectionId -ne $hostFirst.connectionId) { throw 'UI stall unnecessarily replaced healthy IPC.' }
        Passed 'Host recovers on the existing IPC connection after UI unblocks'
    }
    $tomorrow=[DateTime]::Today.AddDays(1)
    $null=Command 'offset' @{seconds=($tomorrow.AddSeconds(-5)-[DateTime]::Now).TotalSeconds}
    Start-Sleep -Milliseconds 400; $preMidnight=Wait-Snapshot
    if ($preMidnight.day.date -ne $tomorrow.AddDays(-1).ToString('yyyy-MM-dd')) { throw 'Missed pre-midnight sample; rerun on an idle test machine.' }
    $deadline=[DateTime]::UtcNow.AddSeconds(12)
    do { $post=Query; if ($post.day.date -eq $tomorrow.ToString('yyyy-MM-dd') -and $post.clockState -eq 'Advancing') { break }; Start-Sleep -Milliseconds 300 } while ([DateTime]::UtcNow -lt $deadline)
    if ($post.day.date -ne $tomorrow.ToString('yyyy-MM-dd') -or $post.day.name -ne "Bridge day $([int]$tomorrow.DayOfWeek)") { throw 'School midnight/day plan mismatch.' }
    if ($post.day.planId -eq $preMidnight.day.planId) { throw 'Midnight did not select a new weekday plan.' }
    Passed 'School midnight switches date and weekday timetable without changing Windows time'
    if ($VerifyHost) {
        Start-Sleep -Milliseconds 750; $hostMidnight = Wait-HostClock
        if ($hostMidnight.schedule.date -ne $post.day.date -or $hostMidnight.schedule.name -ne $post.day.name) { throw 'Host school day/plan mismatch.' }
        Passed 'Host school date and effective weekday plan switch together at midnight'
    }
    $null=Command 'no-plan'; Start-Sleep -Milliseconds 400
    $null=Wait-Snapshot 'NoPlan'; Passed 'No class plan still returns advancing school time'
    Stop-Island; $offline=Query
    if (-not $offline.error) { throw 'Offline bridge returned healthy data.' }
    Passed 'Graceful shutdown and bounded offline failure'
    if ($VerifyHost) { $null = Wait-HostClock 'Unavailable'; Passed 'Host marks clock unavailable after graceful plugin shutdown' }
    $app=Start-Island; $restarted=Wait-Snapshot 'NoPlan'
    if ($restarted.bridgeInstanceId -eq $first.bridgeInstanceId) { throw 'Restart reused bridge instance ID.' }
    Passed 'Restart reconnects with a new bridge instance ID'
    if ($VerifyHost) {
        $hostRestart = Wait-HostClock
        if ($hostRestart.bridgeInstanceId -eq $hostFirst.bridgeInstanceId -or $hostRestart.connectionId -eq $hostFirst.connectionId) { throw 'Host reused old lifetime identity.' }
        Passed 'Host automatically reconnects after ClassIsland restart with fresh connection and instance IDs'
    }
    $app.Kill($true); $null=$app.WaitForExit(5000)
    $crashed=Query
    if (-not $crashed.error) { throw 'Crash returned healthy data.' }
    $app=Start-Island; $afterCrash=Wait-Snapshot 'NoPlan'
    if ($afterCrash.bridgeInstanceId -eq $restarted.bridgeInstanceId) { throw 'Crash recovery reused instance ID.' }
    Passed 'Abrupt termination returns bounded failure and a fresh instance after restart'
    if ($VerifyHost) {
        $hostCrash = Wait-HostClock
        if ($hostCrash.bridgeInstanceId -eq $hostRestart.bridgeInstanceId) { throw 'Host failed to reconnect after crash.' }
        Passed 'Host automatically reconnects after forced ClassIsland termination'
    }
    Stop-Island
    $app=Start-Island $false
    # The fixture acknowledgement proves the app has finished startup without the production bridge.
    $null=Command 'offset' @{seconds=0}; $missing=Query 'hello'
    if (-not $missing.error) { throw 'Missing plugin unexpectedly exposed its service.' }
    Passed 'Absent plugin returns bounded failure instead of default time'
    if ($VerifyHost) {
        $null = Wait-HostClock 'Unavailable'
        $null = & $cliExe stop --pipe $hostPipe
        if ($LASTEXITCODE -ne 0 -or -not $bridgeHost.WaitForExit(10000)) { throw 'Host stop failed.' }
        if (Get-CimInstance Win32_Process -Filter "ParentProcessId=$($bridgeHost.Id)" | Where-Object CommandLine -match '--bridge-worker') { throw 'Orphan bridge worker after Host stop.' }
        Passed 'Missing plugin has no clock fallback; Host shutdown cleans the bridge worker'
    }
    Stop-Island
    @{passed=$true;checks=$checks.ToArray();hello=$hello;completedAt=[DateTimeOffset]::Now} | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $runRoot 'summary.json') -Encoding utf8
} finally {
    foreach ($process in $owned) { if (-not $process.HasExited) { $process.Kill($true); $null=$process.WaitForExit(5000) }; $process.Dispose() }
    foreach ($log in $logs) {
        if ($log.out.IsCompleted) { $log.out.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $runRoot ($log.name+'.stdout.log')) }
        if ($log.err.IsCompleted) { $log.err.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $runRoot ($log.name+'.stderr.log')) }
    }
    ConvertTo-Json -InputObject @($samples.ToArray()) -Depth 12 | Set-Content -LiteralPath (Join-Path $runRoot 'responses.json')
    ConvertTo-Json -InputObject @($hostSamples.ToArray()) -Depth 12 | Set-Content -LiteralPath (Join-Path $runRoot 'host-responses.json')
}
