param(
    [string]$ClassIslandBinary = 'D:/WebstormProjects/ClassIsland/ClassIsland.Desktop/bin/Debug/net8.0-windows10.0.19041.0/ClassIsland.Desktop.exe',
    [switch]$Watch,
    [switch]$Schedule
)
$ErrorActionPreference = 'Stop'
if ($Watch -and $Schedule) { throw 'Choose Watch or Schedule.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$runner = Join-Path $PSScriptRoot 'dotnet.ps1'
& $runner --version
if ($LASTEXITCODE -ne 0) { throw 'NPEduTools SDK is unavailable.' }
if (-not (Test-Path -LiteralPath $ClassIslandBinary)) { throw "Build ClassIsland first: $ClassIslandBinary" }
if (Get-Process -Name 'ClassIsland','ClassIsland.Desktop' -ErrorAction SilentlyContinue) {
    throw 'Close existing ClassIsland instances before running this isolated live test.'
}
$existingMutex = $null
if ([Threading.Mutex]::TryOpenExisting('Global\ClassIsland.Lock', [ref]$existingMutex)) {
    $existingMutex.Dispose()
    throw 'Another ClassIsland instance owns the global mutex. Live test was not started.'
}
$runRoot = Join-Path $projectRoot ('.artifacts/classisland-live/' + [Guid]::NewGuid().ToString('N'))
$appDirectory = Join-Path $runRoot 'app'
$packageRoot = Join-Path $runRoot 'package'
$dataDirectory = Join-Path $packageRoot 'data'
New-Item -ItemType Directory -Force $appDirectory,(Join-Path $dataDirectory 'Profiles') | Out-Null
Copy-Item -Path (Join-Path (Split-Path $ClassIslandBinary -Parent) '*') -Destination $appDirectory -Recurse
Set-Content -LiteralPath (Join-Path $packageRoot 'PackageType') -Value 'folder' -NoNewline

$hostDll = Join-Path $projectRoot 'src/NPEduTools.Host/bin/Release/net10.0/NPEduTools.Host.dll'
$cliDll = Join-Path $projectRoot 'src/NPEduTools.Cli/bin/Release/net10.0/NPEduTools.Cli.dll'
if (-not (Test-Path $hostDll) -or -not (Test-Path $cliDll)) { throw 'Build NPEduTools in Release first.' }
$pipe = 'NPEduTools.Test.live.' + [Guid]::NewGuid().ToString('N')
$dotnetHost = $env:NPEEDUTOOLS_DOTNET_HOST
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$logs = [Collections.Generic.List[object]]::new()
$results = [Collections.Generic.List[object]]::new()

function Start-OwnedProcess([string]$Executable, [string[]]$Arguments, [string]$Name, [hashtable]$Environment = @{}) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.WorkingDirectory = $runRoot
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $info.Environment[$key] = $Environment[$key] }
    $process = [Diagnostics.Process]::Start($info)
    $owned.Add($process)
    $logs.Add(@{ Name=$Name; Out=$process.StandardOutput.ReadToEndAsync(); Err=$process.StandardError.ReadToEndAsync() })
    return $process
}

function Query-Status([int]$ObserveMs = 0, [int]$TimeoutMs = 5000, [string]$Capability = 'status') {
    if ($Watch) {
        $line = $watchProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(6)).GetAwaiter().GetResult()
        if (-not $line) { throw 'Watch stream ended unexpectedly.' }
        $response = $line | ConvertFrom-Json
        $results.Add($response)
        return $response
    }
    $info = [Diagnostics.ProcessStartInfo]::new($dotnetHost)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in @($cliDll,$Capability,'--pipe',$pipe,'--timeout-ms',"$TimeoutMs",'--observe-ms',"$ObserveMs")) {
        $info.ArgumentList.Add($argument)
    }
    $client = [Diagnostics.Process]::Start($info)
    $stdout = $client.StandardOutput.ReadToEndAsync()
    $stderr = $client.StandardError.ReadToEndAsync()
    try {
        if (-not $client.WaitForExit($TimeoutMs + 8000)) { $client.Kill($true); throw 'CLI did not finish within its deadline.' }
        $output = $stdout.GetAwaiter().GetResult()
        if ([string]::IsNullOrWhiteSpace($output)) { throw ('CLI transport failed: ' + $stderr.GetAwaiter().GetResult()) }
        $response = $output | ConvertFrom-Json
        $results.Add($response)
        return $response
    } finally { $client.Dispose() }
}

function Write-Fixture {
    # Actual ClassIsland schedule engine generates transitions; no IPC setters or fake events.
    $now = [DateTime]::Now
    if ($now.TimeOfDay.TotalSeconds -gt 86280) { throw 'Run this test more than two minutes before midnight.' }
    $profileId = [Guid]::NewGuid().ToString()
    $layoutId = [Guid]::NewGuid().ToString()
    $planId = [Guid]::NewGuid().ToString()
    $firstId = [Guid]::NewGuid().ToString()
    $secondId = [Guid]::NewGuid().ToString()
    $boundary = $now.AddSeconds(35).TimeOfDay.ToString('hh\:mm\:ss')
    $resume = $now.AddSeconds(45).TimeOfDay.ToString('hh\:mm\:ss')
    $profile = @{
        Id=$profileId
        Subjects=@{
            $firstId=@{Name='NPEduTools 实机联调 A'}
            $secondId=@{Name='NPEduTools 实机联调 B'}
        }
        TimeLayouts=@{
            $layoutId=@{Name='NPEduTools 联调时间表'; Layouts=@(
                @{StartTime='00:00:00';EndTime=$boundary;TimeType=0},
                @{StartTime=$boundary;EndTime=$resume;TimeType=1;BreakName='联调课间'},
                @{StartTime=$resume;EndTime='23:59:59';TimeType=0}
            )}
        }
        ClassPlans=@{
            $planId=@{Name='NPEduTools 联调课表';TimeLayoutId=$layoutId;IsEnabled=$true;
                TimeRule=@{WeekDay=[int]$now.DayOfWeek;WeekCountDiv=0};
                Classes=@(@{SubjectId=$firstId},@{SubjectId=$secondId})}
        }
    }
    $settings = @{
        SelectedProfile='NPEduTools.Live.json';IsWelcomeWindowShowed=$true;LastAppVersion='2.1.0.1';
        IsSplashEnabled=$false;IsMainWindowVisible=$false;IsNotificationEnabled=$false;IsSpeechEnabled=$false;
        AutoInstallUpdateNextStartup=$false;IsPluginsAutoUpdateEnabled=$false;TrustedProfileIds=@($profileId)
    }
    $profile | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $dataDirectory 'Profiles/NPEduTools.Live.json') -Encoding utf8
    $settings | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $dataDirectory 'Settings.json') -Encoding utf8
}

Write-Output "Live test artifacts: $runRoot"
try {
    Write-Fixture
    $appExe = Join-Path $appDirectory (Split-Path $ClassIslandBinary -Leaf)
    $app = Start-OwnedProcess $appExe @('--quiet','-dm') 'classisland' @{ClassIsland_PackageRoot=$packageRoot;ClassIsland_WaitDebuggers='0'}
    $hostProcess = Start-OwnedProcess $dotnetHost @($hostDll,'--pipe',$pipe) 'host'
    if ($Watch) {
        $watchInfo = [Diagnostics.ProcessStartInfo]::new($dotnetHost)
        $watchInfo.UseShellExecute = $false
        $watchInfo.CreateNoWindow = $true
        $watchInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $watchInfo.RedirectStandardOutput = $true
        $watchInfo.RedirectStandardError = $true
        foreach ($argument in @($cliDll,'watch','--pipe',$pipe)) { $watchInfo.ArgumentList.Add($argument) }
        $watchProcess = [Diagnostics.Process]::Start($watchInfo)
        $owned.Add($watchProcess)
        $logs.Add(@{Name='watch';Out=[Threading.Tasks.Task]::FromResult('');Err=$watchProcess.StandardError.ReadToEndAsync()})
    }
    $startupDeadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        if ($app.HasExited) { throw "ClassIsland exited during startup: $($app.ExitCode)" }
        try { $first = Query-Status } catch { $first = $null }
        if ($first -and $first.outcome -eq 'Succeeded' -and $first.status.isClassPlanLoaded) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $startupDeadline)
    if (-not $first -or $first.outcome -ne 'Succeeded' -or -not $first.status.isClassPlanLoaded) {
        throw 'Real ClassIsland did not load the test profile before the startup deadline.'
    }
    if ($first.status.subject -ne 'NPEduTools 实机联调 A') { throw 'Unexpected initial subject.' }
    Write-Output "Initial: $($first.status.state) / $($first.status.subject)"
    if ($Schedule) {
        $day = Query-Status 0 12000 'schedule'
        if ($day.outcome -ne 'Succeeded' -or $day.schedule.lessons.Count -ne 2 -or
            $day.schedule.lessons[0].subject -ne 'NPEduTools 实机联调 A' -or
            $day.schedule.lessons[1].subject -ne 'NPEduTools 实机联调 B' -or -not $day.schedule.clockVerified) {
            $day | ConvertTo-Json -Depth 10 | Write-Output
            throw 'Real IPC timetable / clock validation failed.'
        }
        @{Passed=$true;Schedule=$day.schedule;CompletedAt=[DateTimeOffset]::Now} | ConvertTo-Json -Depth 10 |
            Set-Content -LiteralPath (Join-Path $runRoot 'summary.json') -Encoding utf8
        Write-Output 'PASS: real ClassIsland profile serialization, effective plan, lesson mapping and clock alignment.'
        return
    }
    $seenStates = [Collections.Generic.HashSet[string]]::new()
    $onClass = 0L
    $onBreak = 0L
    $observeDeadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        $sample = Query-Status 5000 10000
        if ($sample.outcome -ne 'Succeeded') { throw "Live observation failed: $($sample.errorCode)" }
        $null = $seenStates.Add($sample.status.state)
        if ($Watch) {
            if ($sample.connectionId -ne $first.connectionId) { throw 'Healthy monitor unexpectedly replaced its connection.' }
            $onClass = $sample.status.observedEvents.'classisland.lessonsService.onClass'
            $onBreak = $sample.status.observedEvents.'classisland.lessonsService.onBreakingTime'
        } else {
            $onClass += $sample.status.observedEvents.'classisland.lessonsService.onClass'
            $onBreak += $sample.status.observedEvents.'classisland.lessonsService.onBreakingTime'
        }
        Write-Output "Observed: $($sample.status.state) / $($sample.status.subject); class=$onClass break=$onBreak"
        if ($sample.status.subject -eq 'NPEduTools 实机联调 B' -and $onClass -gt 0 -and $onBreak -gt 0) { break }
    } while ([DateTime]::UtcNow -lt $observeDeadline)
    if ($onClass -eq 0 -or $onBreak -eq 0 -or -not $seenStates.Contains('Breaking')) {
        throw 'Expected real schedule events/state transitions were not observed.'
    }

    # Terminate only the disposable app created above to exercise target loss and restart.
    $app.Kill($true)
    if (-not $app.WaitForExit(5000)) { throw 'Test app did not stop.' }
    $offlineDeadline = [DateTime]::UtcNow.AddSeconds(12)
    do { $offline = Query-Status 0 600 }
    while ($Watch -and $offline.outcome -eq 'Succeeded' -and [DateTime]::UtcNow -lt $offlineDeadline)
    if ($Watch) {
        if ($offline.outcome -notin @('Unavailable','TimedOut','Failed') -or $null -ne $offline.status) {
            throw 'Monitor did not clear stale state after target loss.'
        }
    } elseif ($offline.outcome -ne 'TimedOut') { throw 'Expected a bounded timeout while ClassIsland is stopped.' }
    $app = Start-OwnedProcess $appExe @('--quiet','-dm') 'classisland-restarted' @{ClassIsland_PackageRoot=$packageRoot;ClassIsland_WaitDebuggers='0'}
    $restartDeadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        if ($app.HasExited) { throw 'Restarted ClassIsland exited.' }
        $restarted = Query-Status
        if ($restarted.outcome -eq 'Succeeded') { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $restartDeadline)
    if ($restarted.outcome -ne 'Succeeded' -or $restarted.status.subject -ne 'NPEduTools 实机联调 B') {
        throw 'Host did not reconnect to the restarted real ClassIsland.'
    }
    if ($Watch -and ($restarted.streamId -ne $first.streamId -or $restarted.connectionId -eq $first.connectionId)) {
        throw 'Expected the same subscription stream and a fresh target connection after restart.'
    }
    @{Passed=$true;ClassIslandBinary=$ClassIslandBinary;RunRoot=$runRoot;OnClassEvents=$onClass;OnBreakEvents=$onBreak;
      Watch=[bool]$Watch;States=@($seenStates);OfflineOutcome=$offline.outcome;RestartSubject=$restarted.status.subject;CompletedAt=[DateTimeOffset]::Now} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'summary.json') -Encoding utf8
    Write-Output 'PASS: real status, natural schedule events, target loss, and restart.'
} finally {
    foreach ($process in $owned) {
        if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(5000) }
        $process.Dispose()
    }
    foreach ($log in $logs) {
        if ($log.Out.IsCompleted) { $log.Out.GetAwaiter().GetResult() | Set-Content (Join-Path $runRoot ($log.Name + '.stdout.log')) }
        if ($log.Err.IsCompleted) { $log.Err.GetAwaiter().GetResult() | Set-Content (Join-Path $runRoot ($log.Name + '.stderr.log')) }
    }
    ConvertTo-Json -InputObject @($results.ToArray()) -Depth 12 | Set-Content -LiteralPath (Join-Path $runRoot 'responses.json') -Encoding utf8
}
