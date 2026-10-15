# Same production transaction code, synthetic tasks/processes, private files only.
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
. (Join-Path $projectRoot 'installer/guard-maintenance.ps1')
$scratch=Join-Path $projectRoot ('.artifacts/guard-maintenance-tests/'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$sid=$identity.User.Value;$identity.Dispose()
$self=[Diagnostics.Process]::GetCurrentProcess();$testSession=$self.SessionId;$self.Dispose()
$defaultData=GuardData $sid 'default'
if ($defaultData -ne (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'NPEduTools\config\guard')) { throw 'Default data path does not match native KnownFolder.' }
$script:checks=@();$script:runs=0
function Check([string]$Name,[bool]$Value) {
    if (-not $Value) { throw ('FAIL: '+$Name) }
    $script:checks+=$Name;Write-Output ('PASS: '+$Name)
}
function Throws([string]$Name,[scriptblock]$Work,[string]$Pattern) {
    $errorMessage=$null
    try { & $Work | Out-Null } catch { $errorMessage=$_.Exception.Message }
    Check $Name ($null -ne $errorMessage -and $errorMessage -match $Pattern)
}
function TaskXml([string]$Role,[string]$Root,[string]$Pipe,[string]$Sid) {
    $exe=switch($Role) { 'App' {Join-Path $Root 'app\NPEduTools.App.exe'} 'Host' {Join-Path $Root 'app\Host\NPEduTools.Host.exe'} default {Join-Path $Root 'app\Guard\svchost.exe'} }
    $args=if ($Pipe -eq 'default') { if ($Role -in @('Guard','Login')) { '--default-pipe' } else { '' } } else { '--pipe "'+$Pipe+'"' }
    if ($Role -eq 'App') { $args+=' --guard-recovery' }
    if ($Role -eq 'Login') { $args+=' --at-login' }
    $args=$args.Trim()
    $command=[Security.SecurityElement]::Escape($exe);$wd=[Security.SecurityElement]::Escape((Split-Path $exe))
    return '<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><RegistrationInfo><Source>NPEduTools.Guard.v2</Source></RegistrationInfo><Principals><Principal id="User"><UserId>'+ $Sid+'</UserId><LogonType>InteractiveToken</LogonType></Principal></Principals><Actions Context="User"><Exec><Command>'+$command+'</Command><Arguments>'+$args+'</Arguments><WorkingDirectory>'+$wd+'</WorkingDirectory></Exec></Actions></Task>'
}
function Fixture([string]$Name,[switch]$Default) {
    $root=Join-Path $scratch ($Name+'\installed');$data=Join-Path $scratch ($Name+'\data');$transaction=Join-Path $scratch ($Name+'\transaction')
    foreach($directory in @($data,$transaction,(Join-Path $root 'app\Host'),(Join-Path $root 'app\Guard'))) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
    foreach($file in @('app\NPEduTools.App.exe','app\Host\NPEduTools.Host.exe','app\Guard\svchost.exe')) { [IO.File]::WriteAllText((Join-Path $root $file),'NOT EXECUTABLE fixture') }
    $pipe=if ($Default) { 'default' } else { 'NPEduTools.Test.Maintenance.'+[guid]::NewGuid().ToString('N') };$key=(GuardHash ($sid+'|'+$pipe)).Substring(0,24)
    $registration=@{Generation=[guid]::NewGuid().ToString();App=@{Id=$PID;StartedUtcTicks=1};Upstream=$null;BundleDirectory=(Join-Path $root 'app')}
    WriteGuardJson (Join-Path $data 'registration.json') $registration
    $folder=[pscustomobject]@{Rows=@{};Deleted=@();Registered=@();Launched=@();FailDelete=$null;FailRun=$false}
    $folder | Add-Member ScriptMethod GetTasks { param($flags) return @($this.Rows.Values) }
    $folder | Add-Member ScriptMethod DeleteTask { param($name,$flags) if ($this.FailDelete -eq $name) { $this.FailDelete=$null;throw 'Synthetic deletion failure' };$this.Rows.Remove($name);$this.Deleted+=$name }
    $folder | Add-Member ScriptMethod RegisterTask { param($name,$xml,$flags,$user,$password,$logon,$sddl) $this.Rows[$name]=[pscustomobject]@{Name=$name;Xml=$xml};$this.Registered+=$name;return $this.Rows[$name] }
    $folder | Add-Member ScriptMethod GetTask { param($name) $task=[pscustomobject]@{Name=$name;Folder=$this};$task | Add-Member ScriptMethod Run {param($value) if($this.Folder.FailRun){$this.Folder.FailRun=$false;throw 'Synthetic guardian restart failure'};$script:runs++;$this.Folder.Launched+=$this.Name };return $task }
    foreach($role in @('Guard','Login','App','Host')) {
        $name='NPEduTools.Guard.'+$key+'.'+$role
        $folder.Rows[$name]=[pscustomobject]@{Name=$name;Xml=(TaskXml $role $root $pipe $sid)}
    }
    $script:active=[pscustomobject]@{Root=$root;Data=$data;DataByEndpoint=@{$pipe=$data};Transaction=$transaction;Journal=(Join-Path $transaction 'journal.json');Folder=$folder;Generation=$registration.Generation;Running=$true;NeverStops=$false;Pipe=$pipe;Key=$key;CommandLine=$null}
    return $script:active
}
# Substitute only environmental adapters. No production scheduler/process is touched.
function GuardData([string]$Sid,[string]$Endpoint) {
    if ($Sid -ne $sid -or -not $script:active.DataByEndpoint.ContainsKey($Endpoint)) { throw 'Fixture data ownership mismatch' }
    return $script:active.DataByEndpoint[$Endpoint]
}
function OwnedGuardProcesses([string]$Root) {
    if ($Root -ne $script:active.Root) { throw 'Fixture process scope mismatch' }
    $stop=ReadGuardJson (Join-Path $script:active.Data 'stop.json')
    if ($script:active.Running -and ($script:active.NeverStops -or $null -eq $stop -or $stop.Generation -ne $script:active.Generation)) {
        $path=Join-Path $Root 'app\Guard\svchost.exe'
        $arguments=if($script:active.Pipe -eq 'default'){'--default-pipe'}else{'--pipe "'+$script:active.Pipe+'"'}
        $line=if($null -ne $script:active.CommandLine){$script:active.CommandLine}else{'"'+$path+'" '+$arguments}
        return @([pscustomobject]@{ProcessId=12345;ExecutablePath=$path;CommandLine=$line;SessionId=$testSession})
    }
    return @()
}
function Invoke-CimMethod($InputObject,[string]$MethodName) { return [pscustomobject]@{ReturnValue=0;Sid=$sid} }
function MaintenanceWithoutScheduler([string]$Action,$Fixture) {
    # Exercise the real helper entry point, including its journal/ACL/results logic.
    # Only COM creation is faulted in a private child process; no Windows service is stopped.
    $runner=Join-Path $scratch 'scheduler-unavailable.ps1'
    if (-not [IO.File]::Exists($runner)) {
        [IO.File]::WriteAllText($runner,@'
param([string]$Helper,[string]$Action,[string]$Root,[string]$Transaction,[string]$Calls)
function New-Object {
    param([string]$ComObject)
    [IO.File]::WriteAllText($Calls,$ComObject)
    throw 'Injected Task Scheduler unavailable'
}
& $Helper -Action $Action -InstallRoot $Root -TransactionDirectory $Transaction
exit $LASTEXITCODE
'@)
    }
    $prefix=Join-Path $Fixture.Transaction ($Action+'-outage-'+[guid]::NewGuid().ToString('N'))
    $calls=$prefix+'-calls.txt'
    $arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$runner+'" -Helper "'+(Join-Path $projectRoot 'installer/guard-maintenance.ps1')+'" -Action '+$Action+' -Root "'+$Fixture.Root+'" -Transaction "'+$Fixture.Transaction+'" -Calls "'+$calls+'"'
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName="$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe";$start.Arguments=$arguments
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    # Retain the process handle from creation, even when local cleanup exits quickly.
    $child=[Diagnostics.Process]::new();$child.StartInfo=$start
    $null=$child.Start()
    $stdout=$child.StandardOutput.ReadToEndAsync();$stderr=$child.StandardError.ReadToEndAsync()
    try {
        if (-not $child.WaitForExit(15000)) {
            $child.Kill();$child.WaitForExit()
            throw 'Private maintenance helper timed out.'
        }
        [IO.File]::WriteAllText(($prefix+'-stdout.txt'),$stdout.Result)
        [IO.File]::WriteAllText(($prefix+'-stderr.txt'),$stderr.Result)
        return [pscustomobject]@{ExitCode=$child.ExitCode;SchedulerCalled=[IO.File]::Exists($calls)}
    } finally { $child.Dispose() }
}
function InterruptInitialCheckpoint($Fixture,[int]$WriteNumber=1) {
    # The helper owns no real tasks/processes. Exit before the selected journal
    # write, skipping normal catch/finally recovery as an abrupt process exit would.
    $runner=Join-Path $scratch 'interrupt-initial-checkpoint.ps1'
    if (-not [IO.File]::Exists($runner)) {
        [IO.File]::WriteAllText($runner,@'
param([string]$Helper,[string]$InputPath)
$ErrorActionPreference='Stop'
. $Helper
$script:testState=ReadGuardJson $InputPath
function CurrentGuardTasks($Folder) { return @($script:testState.Tasks) }
function OwnedGuardProcesses([string]$Root) {
    if ($Root -cne $script:testState.Root) { throw 'Private process boundary mismatch.' }
    return @()
}
function GuardData([string]$Sid,[string]$Endpoint) {
    if ($Sid -cne $script:testState.Sid -or $Endpoint -cne $script:testState.Pipe) { throw 'Private task boundary mismatch.' }
    return $script:testState.Data
}
$script:writer=${function:WriteGuardJson}
$script:journalWrites=0
function WriteGuardJson([string]$Path,$Value) {
    if ($Path -ceq $script:testState.Journal) {
        $script:journalWrites++
        if ($script:journalWrites -eq $script:testState.WriteNumber) {
            [IO.File]::WriteAllText($script:testState.Reached,('before journal persistence '+$script:journalWrites))
            [Environment]::Exit(91)
        }
    }
    & $script:writer $Path $Value
}
PrepareGuardMaintenance $script:testState.Root $script:testState.Journal $null
throw 'Expected checkpoint was not reached.'
'@)
    }
    $inputPath=Join-Path $Fixture.Transaction 'interruption-input.json'
    $reached=Join-Path $Fixture.Transaction 'interruption-reached.txt'
    WriteGuardJson $inputPath @{Root=$Fixture.Root;Data=$Fixture.Data;Sid=$sid;Pipe=$Fixture.Pipe;Journal=$Fixture.Journal;Tasks=@($Fixture.Folder.Rows.Values);Reached=$reached;WriteNumber=$WriteNumber}
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName="$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $start.Arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$runner+'" -Helper "'+(Join-Path $projectRoot 'installer/guard-maintenance.ps1')+'" -InputPath "'+$inputPath+'"'
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $child=[Diagnostics.Process]::new();$child.StartInfo=$start
    try {
        $null=$child.Start()
        $stdout=$child.StandardOutput.ReadToEndAsync();$stderr=$child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(15000)) { $child.Kill();$child.WaitForExit();throw 'Private checkpoint fixture timed out.' }
        [IO.File]::WriteAllText((Join-Path $Fixture.Transaction 'interruption-stdout.txt'),$stdout.Result)
        [IO.File]::WriteAllText((Join-Path $Fixture.Transaction 'interruption-stderr.txt'),$stderr.Result)
        Check ('private helper exits abruptly before journal write '+$WriteNumber) ($child.ExitCode -eq 91 -and [IO.File]::Exists($reached))
    } finally { $child.Dispose() }
}
try {
    Check 'default instance uses current account LocalAppData KnownFolder' ($null -ne $defaultData)
    WriteGuardResult $scratch 'Prepare' 'Synthetic prepare failure'
    WriteGuardResult $scratch 'Restore' 'OK'
    Check 'successful rollback preserves the original Prepare diagnostic' ([IO.File]::ReadAllText((Join-Path $scratch 'Prepare-result.txt')) -ceq 'Synthetic prepare failure')
    Check 'rollback and legacy last-result files still report their own success' ([IO.File]::ReadAllText((Join-Path $scratch 'Restore-result.txt')) -ceq 'OK' -and [IO.File]::ReadAllText((Join-Path $scratch 'result.txt')) -ceq 'OK')
    $f=Fixture 'default-host-empty-arguments' -Default
    $name='NPEduTools.Guard.'+$f.Key+'.Host'
    $xml=$f.Folder.Rows[$name].Xml
    Check 'explicit empty default Host arguments accepted' ((GuardTaskInfo $name $xml $f.Root).Endpoint -eq 'default')
    # Windows Task Scheduler omits an empty Arguments element on XML round-trip.
    $f.Folder.Rows[$name].Xml=$xml.Replace('<Arguments></Arguments>','')
    $f.Running=$false
    PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
    Check 'omitted default Host arguments do not block maintenance' ($f.Folder.Rows.Count -eq 0)
    $journal=ReadGuardJson $f.Journal
    RestoreGuardMaintenance $journal $f.Folder $f.Journal
    Check 'default Host rollback preserves scheduler-normalized XML' ($f.Folder.Rows[$name].Xml -ceq $xml.Replace('<Arguments></Arguments>',''))
    foreach($role in @('Guard','Login','App')) {
        $name='NPEduTools.Guard.'+$f.Key+'.'+$role
        $xml=$f.Folder.Rows[$name].Xml -replace '<Arguments>[^<]*</Arguments>',''
        Throws ('omitted required '+$role+' arguments still refused') { GuardTaskInfo $name $xml $f.Root } 'mismatch'
    }

    $f=Fixture 'normal'
    $original=GuardFileSnapshot (Join-Path $f.Data 'registration.json')
    PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
    $journal=ReadGuardJson $f.Journal
    Check 'all four owned tasks removed' ($f.Folder.Rows.Count -eq 0)
    Check 'generation-scoped stop persisted before completion' ((ReadGuardJson (Join-Path $f.Data 'stop.json')).Generation -eq $f.Generation)
    Check 'maintenance marker present and registration preserved' ([IO.File]::Exists($journal.Marker) -and (GuardFileSnapshot (Join-Path $f.Data 'registration.json')) -eq $original)
    RestoreGuardMaintenance $journal $f.Folder $f.Journal
    Check 'cancel restores tasks, original stop and only guardian launch' ($f.Folder.Rows.Count -eq 4 -and -not [IO.File]::Exists((Join-Path $f.Data 'stop.json')) -and $script:runs -eq 1)
    Check 'cancel releases installation marker' (-not [IO.File]::Exists($journal.Marker))
    RestoreGuardMaintenance $journal $f.Folder $f.Journal
    Check 'repeated rollback does not launch twice' ($script:runs -eq 1)

    $f=Fixture 'stale-stop'
    WriteGuardJson (Join-Path $f.Data 'stop.json') @{Generation=[guid]::NewGuid().ToString();Reason='WindowsSessionEnding'}
    $previous=GuardFileSnapshot (Join-Path $f.Data 'stop.json')
    PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
    RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder $f.Journal
    Check 'prior stale stop restored byte-for-byte and guardian resumed' ((GuardFileSnapshot (Join-Path $f.Data 'stop.json')) -eq $previous -and $script:runs -eq 2)

    $f=Fixture 'partial-failure';$f.Folder.FailDelete='NPEduTools.Guard.'+$f.Key+'.Host'
    Throws 'partial deletion failure rolls back' {PrepareGuardMaintenance $f.Root $f.Journal $f.Folder} 'Synthetic deletion failure'
    Check 'partial rollback restores all tasks and clears maintenance' ($f.Folder.Rows.Count -eq 4 -and -not [IO.File]::Exists((Join-Path $f.Root 'app\guard-maintenance.json')))

    $f=Fixture 'mismatch';$name='NPEduTools.Guard.'+$f.Key+'.Host';$f.Folder.Rows[$name].Xml=$f.Folder.Rows[$name].Xml.Replace('NPEduTools.Guard.v2','Other.Product')
    Throws 'all ownership checked before touching any member' {PrepareGuardMaintenance $f.Root $f.Journal $f.Folder} 'ownership mismatch'
    Check 'ownership mismatch preserves all tasks and configuration' ($f.Folder.Deleted.Count -eq 0 -and -not [IO.File]::Exists((Join-Path $f.Data 'stop.json')))

    $f=Fixture 'other-installation';$other=TaskXml 'Guard' (Join-Path $scratch 'another') $f.Pipe $sid
    Check 'other installation tasks ignored' (@(GuardPlans @([pscustomobject]@{Name=('NPEduTools.Guard.'+$f.Key+'.Guard');Xml=$other}) $f.Root).Count -eq 0)
    foreach($change in @('path','arguments','principal','source')) {
        $xml=TaskXml 'Guard' $f.Root $f.Pipe $sid
        switch($change) {
            'path' {$xml=$xml.Replace('svchost.exe','foreign.exe')}
            'arguments' {$xml=$xml.Replace('</Arguments>',' --unexpected</Arguments>')}
            'principal' {$xml=$xml.Replace($sid,'S-1-5-18')}
            'source' {$xml=$xml.Replace('NPEduTools.Guard.v2','Other')}
        }
        Throws ('changed '+$change+' refused') { GuardTaskInfo ('NPEduTools.Guard.'+$f.Key+'.Guard') $xml $f.Root } 'mismatch'
    }

    $f=Fixture 'missing-guardian';$f.Running=$false
    [IO.File]::Delete((Join-Path $f.Root 'app\Guard\svchost.exe'))
    PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
    Check 'missing guardian executable does not block owned task cleanup' ($f.Folder.Rows.Count -eq 0)
    RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder $f.Journal
    Check 'idle missing-guardian rollback never starts components' ($script:runs -eq 3)

    $f=Fixture 'changed-bundle';PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
    [IO.File]::WriteAllText((Join-Path $f.Root 'app\Host\NPEduTools.Host.exe'),'PARTIAL UPGRADE')
    Throws 'partial upgrade cannot restart mixed bundle' {RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder $f.Journal} 'Application files changed'
    Check 'mixed bundle retains stop and recovery journal' ([IO.File]::Exists((Join-Path $f.Root 'app\guard-maintenance.json')) -and $f.Folder.Rows.Count -eq 0)

    $f=Fixture 'changed-generation';PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
    $r=ReadGuardJson (Join-Path $f.Data 'registration.json');$r.Generation=[guid]::NewGuid().ToString();WriteGuardJson (Join-Path $f.Data 'registration.json') $r
    Throws 'new client generation cannot be overwritten by rollback' {RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder $f.Journal} 'generation changed'

    $f=Fixture 'timeout';$f.NeverStops=$true
    Throws 'nonresponsive guard times out without force-killing any process' {PrepareGuardMaintenance $f.Root $f.Journal $f.Folder} 'ten seconds'
    Check 'timeout restores task registrations' ($f.Folder.Rows.Count -eq 4 -and (ReadGuardJson $f.Journal).Phase -eq 'Restored')

    $f=Fixture 'rollback-launch-failure'
    PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
    $journal=ReadGuardJson $f.Journal
    $f.Folder.FailRun=$true
    Throws 'rollback guardian launch failure is reported' {RestoreGuardMaintenance $journal $f.Folder $f.Journal} 'Synthetic guardian restart failure'
    Check 'failed rollback launch retains transaction ownership for retry' ([IO.File]::Exists($journal.Marker) -and (ReadGuardJson $f.Journal).Phase -eq 'Prepared')
    $beforeRetry=$script:runs
    $retryJournal=ReadGuardJson $f.Journal
    RestoreGuardMaintenance $retryJournal $f.Folder $f.Journal
    Check 'same rollback transaction can be retried after transient launch failure' ($script:runs -eq ($beforeRetry+1) -and -not [IO.File]::Exists($journal.Marker) -and $f.Folder.Rows.Count -eq 4 -and -not [IO.File]::Exists((Join-Path $f.Data 'stop.json')))
    RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder $f.Journal
    Check 'successful rollback retry is durable and does not relaunch twice' ((ReadGuardJson $f.Journal).Phase -eq 'Restored' -and $script:runs -eq ($beforeRetry+1))

    foreach($change in @('bundle','generation','task')) {
        $f=Fixture ('rollback-retry-changed-'+$change)
        PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
        $f.Folder.FailRun=$true
        Throws ('initial rollback '+$change+' fixture launch failure') {RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder $f.Journal} 'Synthetic guardian restart failure'
        switch($change) {
            'bundle' {[IO.File]::WriteAllText((Join-Path $f.Root 'app\Host\NPEduTools.Host.exe'),'CHANGED AFTER FAILED ROLLBACK');$pattern='Application files changed'}
            'generation' {$r=ReadGuardJson (Join-Path $f.Data 'registration.json');$r.Generation=[guid]::NewGuid().ToString();WriteGuardJson (Join-Path $f.Data 'registration.json') $r;$pattern='generation changed'}
            'task' {$name='NPEduTools.Guard.'+$f.Key+'.Guard';$f.Folder.Rows[$name].Xml=$f.Folder.Rows[$name].Xml.Replace('NPEduTools.Guard.v2','Other.Product');$pattern='ownership mismatch'}
        }
        $beforeRetry=$script:runs
        Throws ('rollback retry rejects changed '+$change) {RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder $f.Journal} $pattern
        Check ('changed '+$change+' preserves rollback marker without launching') ([IO.File]::Exists((Join-Path $f.Root 'app\guard-maintenance.json')) -and $script:runs -eq $beforeRetry)
    }

    $f=Fixture 'rollback-checkpoint-write-failure'
    PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
    $journal=ReadGuardJson $f.Journal
    # Real Windows sharing failure: readers are allowed, replacement is blocked.
    $journalLock=[IO.File]::Open($f.Journal,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        # Older callers wrote completion only after Restore had released the marker.
        Throws 'rollback completion write failure is reported' {RestoreGuardMaintenance $journal $f.Folder $f.Journal;WriteGuardJson $f.Journal $journal} 'Replace'
        Check 'rollback checkpoint failure keeps marker and retryable in-memory state' ([IO.File]::Exists($journal.Marker) -and $journal.Phase -eq 'Prepared' -and (ReadGuardJson $f.Journal).Phase -eq 'Prepared')
    } finally { $journalLock.Dispose() }
    RestoreGuardMaintenance $journal $f.Folder $f.Journal
    Check 'rollback persists completion before releasing ownership' ((ReadGuardJson $f.Journal).Phase -eq 'Restored' -and -not [IO.File]::Exists($journal.Marker))

    foreach($owner in @('same','foreign')) {
        $f=Fixture ('rollback-marker-cleanup-'+$owner)
        PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
        $journal=ReadGuardJson $f.Journal
        $markerLock=[IO.File]::Open($journal.Marker,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        try {
            Throws ('rollback marker cleanup '+$owner+' failure is reported') {RestoreGuardMaintenance $journal $f.Folder $f.Journal} 'Delete'
            Check ('completed rollback '+$owner+' remains durable despite locked marker') ((ReadGuardJson $f.Journal).Phase -eq 'Restored' -and [IO.File]::Exists($journal.Marker))
        } finally { $markerLock.Dispose() }
        $beforeRetry=$script:runs;$beforeRegisters=$f.Folder.Registered.Count
        if($owner -eq 'foreign') {
            $foreignId=[guid]::NewGuid().ToString('N')
            WriteGuardJson $journal.Marker @{Id=$foreignId}
            Throws 'completed rollback cannot remove another transaction marker' {RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder $f.Journal} 'ownership changed'
            Check 'foreign marker remains untouched without restarting or registering' ((ReadGuardJson $journal.Marker).Id -eq $foreignId -and $script:runs -eq $beforeRetry -and $f.Folder.Registered.Count -eq $beforeRegisters)
        } else {
            RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder $f.Journal
            Check 'completed rollback retries marker cleanup without duplicate actions' (-not [IO.File]::Exists($journal.Marker) -and $script:runs -eq $beforeRetry -and $f.Folder.Registered.Count -eq $beforeRegisters)
        }
    }

    $f=Fixture 'rollback-explicit-checkpoint-path'
    PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
    $journal=ReadGuardJson $f.Journal
    $otherPath=Join-Path $scratch 'unrelated-checkpoint.json'
    [IO.File]::WriteAllText($otherPath,'preserve unrelated file')
    $journal.JournalPath=$otherPath
    RestoreGuardMaintenance $journal $f.Folder $f.Journal
    Check 'rollback checkpoint uses caller transaction path instead of record data' ([IO.File]::ReadAllText($otherPath) -ceq 'preserve unrelated file' -and (ReadGuardJson $f.Journal).Phase -eq 'Restored')

    foreach($registered in @($true,$false)) {
        $f=Fixture ('multiple-endpoints-'+$registered)
        $idlePipe='NPEduTools.Test.Idle.'+[guid]::NewGuid().ToString('N')
        $idleKey=(GuardHash ($sid+'|'+$idlePipe)).Substring(0,24)
        $idleData=Join-Path $scratch ('idle-data-'+[guid]::NewGuid().ToString('N'))
        $f.DataByEndpoint[$idlePipe]=$idleData
        if($registered) {
            [IO.Directory]::CreateDirectory($idleData) | Out-Null
            WriteGuardJson (Join-Path $idleData 'registration.json') @{Generation=[guid]::NewGuid().ToString();BundleDirectory=(Join-Path $f.Root 'app')}
        }
        $idleRegistration=GuardFileSnapshot (Join-Path $idleData 'registration.json')
        foreach($role in @('Guard','Login','App','Host')) {
            $name='NPEduTools.Guard.'+$idleKey+'.'+$role
            $f.Folder.Rows[$name]=[pscustomobject]@{Name=$name;Xml=(TaskXml $role $f.Root $idlePipe $sid)}
        }
        PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
        $journal=ReadGuardJson $f.Journal
        Check ('multiple endpoints '+$registered+' record only the actual resident') (@($journal.Instances | Where-Object WasRunning).Count -eq 1 -and ($journal.Instances | Where-Object Key -eq $f.Key).WasRunning)
        RestoreGuardMaintenance $journal $f.Folder $f.Journal
        Check ('multiple endpoints '+$registered+' never launch the idle guardian') ($f.Folder.Rows.Count -eq 8 -and $f.Folder.Launched.Count -eq 1 -and $f.Folder.Launched[0] -ceq ('NPEduTools.Guard.'+$f.Key+'.Guard'))
        Check ('idle endpoint '+$registered+' state is preserved') ((GuardFileSnapshot (Join-Path $idleData 'registration.json')) -ceq $idleRegistration -and -not [IO.File]::Exists((Join-Path $idleData 'stop.json')) -and ($registered -or -not [IO.Directory]::Exists($idleData)))
    }

    $probePath='C:\NPEduTools test & fixture\app\Guard\svchost.exe'
    $defaultPipe='NPEduTools.v1.'+$sid+'.'+$testSession
    $forms=@(
        @{Arguments='--pipe NPEduTools.Test.Exact';Endpoint='NPEduTools.Test.Exact';Resident=$true},
        @{Arguments='--pipe "NPEduTools.Test.Exact [A] with spaces"';Endpoint='NPEduTools.Test.Exact [A] with spaces';Resident=$true},
        @{Arguments='--default-pipe';Endpoint='default';Resident=$true},
        @{Arguments='"--default-pipe"';Endpoint='default';Resident=$true},
        @{Arguments=('--pipe "'+$defaultPipe+'"');Endpoint='default';Resident=$true},
        @{Arguments='--default-pipe --at-login';Endpoint='default';Resident=$false},
        @{Arguments='--pipe NPEduTools.Test.Exact --maintenance';Endpoint='NPEduTools.Test.Exact';Resident=$false}
    )
    foreach($form in $forms) {
        $probe=[pscustomobject]@{ExecutablePath=$probePath;CommandLine=('"'+$probePath+'" '+$form.Arguments);SessionId=$testSession}
        $parsed=GuardProcessInstance $probe
        Check ('guardian invocation '+$form.Arguments+' maps to its own instance') ($parsed.Sid -eq $sid -and $parsed.Endpoint -ceq $form.Endpoint -and $parsed.Resident -eq $form.Resident)
    }
    foreach($arguments in @('','--pipe','--pipe default','--pipe NPEduTools.Test.Exact --unexpected')) {
        $probe=[pscustomobject]@{ExecutablePath=$probePath;CommandLine=('"'+$probePath+'" '+$arguments);SessionId=$testSession}
        Throws ('ambiguous guardian invocation rejected: '+$arguments) {GuardProcessInstance $probe} 'invocation|endpoint'
    }

    $f=Fixture 'unknown-resident-endpoint'
    $f.CommandLine='"'+(Join-Path $f.Root 'app\Guard\svchost.exe')+'" --pipe NPEduTools.Test.Unknown'
    Throws 'unmatched resident endpoint is rejected before any mutation' {PrepareGuardMaintenance $f.Root $f.Journal $f.Folder} 'no matching owned task group'
    Check 'unmatched endpoint preserves tasks and state' ($f.Folder.Deleted.Count -eq 0 -and -not [IO.File]::Exists((Join-Path $f.Data 'stop.json')) -and -not [IO.File]::Exists((Join-Path $f.Root 'app\guard-maintenance.json')))

    $f=Fixture 'bootstrap-is-not-resident'
    $f.CommandLine='"'+(Join-Path $f.Root 'app\Guard\svchost.exe')+'" --pipe "'+$f.Pipe+'" --at-login'
    PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
    $journal=ReadGuardJson $f.Journal
    RestoreGuardMaintenance $journal $f.Folder $f.Journal
    Check 'a bootstrap process alone does not cause a guardian restart on rollback' ($f.Folder.Launched.Count -eq 0)

    $f=Fixture 'scheduler-outage-complete'
    $registration=GuardFileSnapshot (Join-Path $f.Data 'registration.json')
    PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
    $journal=ReadGuardJson $f.Journal
    $result=MaintenanceWithoutScheduler 'Complete' $f
    Check 'completion succeeds without accessing an unavailable scheduler' ($result.ExitCode -eq 0 -and -not $result.SchedulerCalled)
    Check 'completion persists its phase and releases only its marker' ((ReadGuardJson $f.Journal).Phase -eq 'Completed' -and -not [IO.File]::Exists($journal.Marker))
    Check 'completion preserves registration and generation-scoped maintenance stop' ((GuardFileSnapshot (Join-Path $f.Data 'registration.json')) -ceq $registration -and (ReadGuardJson (Join-Path $f.Data 'stop.json')).InstallerTransaction -eq $journal.Id)
    Check 'completion reports its own successful result' ([IO.File]::ReadAllText((Join-Path $f.Transaction 'Complete-result.txt')) -ceq 'OK')
    $result=MaintenanceWithoutScheduler 'Complete' $f
    Check 'completed transaction retry needs no scheduler' ($result.ExitCode -eq 0 -and -not $result.SchedulerCalled -and -not [IO.File]::Exists($journal.Marker))

    # A completed checkpoint can outlive a failed marker deletion. Cleanup must
    # remain retryable without reintroducing scheduler availability as a gate.
    WriteGuardJson $journal.Marker @{Id=$journal.Id}
    $markerLock=[IO.File]::Open($journal.Marker,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        $result=MaintenanceWithoutScheduler 'Complete' $f
        Check 'locked completion marker reports a local failure without scheduler access' ($result.ExitCode -eq 1 -and -not $result.SchedulerCalled -and [IO.File]::Exists($journal.Marker) -and (ReadGuardJson $f.Journal).Phase -eq 'Completed')
    } finally { $markerLock.Dispose() }
    $result=MaintenanceWithoutScheduler 'Complete' $f
    Check 'completion cleanup retries after the local file lock is released' ($result.ExitCode -eq 0 -and -not $result.SchedulerCalled -and -not [IO.File]::Exists($journal.Marker))
    $foreignId=[guid]::NewGuid().ToString('N');WriteGuardJson $journal.Marker @{Id=$foreignId}
    $result=MaintenanceWithoutScheduler 'Complete' $f
    Check 'scheduler-free completion still refuses a foreign transaction marker' ($result.ExitCode -eq 1 -and -not $result.SchedulerCalled -and (ReadGuardJson $journal.Marker).Id -eq $foreignId)

    $f=Fixture 'scheduler-outage-prepare'
    $result=MaintenanceWithoutScheduler 'Prepare' $f
    Check 'preparation still requires a working scheduler' ($result.ExitCode -eq 1 -and $result.SchedulerCalled)
    Check 'failed scheduler access leaves preparation state untouched' (-not [IO.File]::Exists($f.Journal) -and -not [IO.File]::Exists((Join-Path $f.Root 'app\guard-maintenance.json')) -and -not [IO.File]::Exists((Join-Path $f.Data 'stop.json')))
    Check 'scheduler preparation failure retains an actionable diagnostic' ([IO.File]::ReadAllText((Join-Path $f.Transaction 'Prepare-result.txt')).Contains('Injected Task Scheduler unavailable'))

    $f=Fixture 'scheduler-outage-restore'
    PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
    $journal=ReadGuardJson $f.Journal
    $stop=GuardFileSnapshot (Join-Path $f.Data 'stop.json')
    $result=MaintenanceWithoutScheduler 'Restore' $f
    Check 'pending task restoration still requires a working scheduler' ($result.ExitCode -eq 1 -and $result.SchedulerCalled)
    Check 'unavailable scheduler retains pending rollback ownership and stop' ([IO.File]::Exists($journal.Marker) -and (ReadGuardJson $f.Journal).Phase -eq 'Prepared' -and (GuardFileSnapshot (Join-Path $f.Data 'stop.json')) -ceq $stop)
    RestoreGuardMaintenance $journal $f.Folder $f.Journal
    WriteGuardJson $journal.Marker @{Id=$journal.Id}
    $beforeLaunch=$f.Folder.Launched.Count
    $result=MaintenanceWithoutScheduler 'Restore' $f
    Check 'already-restored transaction cleans up its marker without scheduler access' ($result.ExitCode -eq 0 -and -not $result.SchedulerCalled -and -not [IO.File]::Exists($journal.Marker) -and (ReadGuardJson $f.Journal).Phase -eq 'Restored')
    Check 'restored cleanup does not change task launches or the prior stop' ($f.Folder.Launched.Count -eq $beforeLaunch -and -not [IO.File]::Exists((Join-Path $f.Data 'stop.json')))
    $foreignId=[guid]::NewGuid().ToString('N');WriteGuardJson $journal.Marker @{Id=$foreignId}
    $result=MaintenanceWithoutScheduler 'Restore' $f
    Check 'scheduler-free restored cleanup still refuses a foreign marker' ($result.ExitCode -eq 1 -and -not $result.SchedulerCalled -and (ReadGuardJson $journal.Marker).Id -eq $foreignId)

    $f=Fixture 'interrupted-initial-checkpoint';$f.Running=$false
    $registration=GuardFileSnapshot (Join-Path $f.Data 'registration.json')
    InterruptInitialCheckpoint $f
    Check 'interrupted initial checkpoint never leaves an installation blocker without a journal' (-not [IO.File]::Exists((Join-Path $f.Root 'app\guard-maintenance.json')) -and -not [IO.File]::Exists($f.Journal))
    Check 'initial checkpoint interruption preserves tasks and instance state' ($f.Folder.Rows.Count -eq 4 -and $f.Folder.Deleted.Count -eq 0 -and (GuardFileSnapshot (Join-Path $f.Data 'registration.json')) -ceq $registration -and -not [IO.File]::Exists((Join-Path $f.Data 'stop.json')))
    PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
    RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder $f.Journal
    Check 'maintenance can be retried after an interrupted initial checkpoint' ($f.Folder.Rows.Count -eq 4 -and $f.Folder.Launched.Count -eq 0 -and (ReadGuardJson $f.Journal).Phase -eq 'Restored' -and -not [IO.File]::Exists((Join-Path $f.Root 'app\guard-maintenance.json')))

    $f=Fixture 'interrupted-after-marker';$f.Running=$false
    $registration=GuardFileSnapshot (Join-Path $f.Data 'registration.json')
    InterruptInitialCheckpoint $f 2
    $journal=ReadGuardJson $f.Journal
    Check 'visible maintenance marker already has a durable matching checkpoint' ($null -ne $journal -and (ReadGuardJson $journal.Marker).Id -ceq $journal.Id -and $journal.Phase -eq 'Prepared')
    Check 'post-marker interruption precedes task or stop mutation' ($f.Folder.Deleted.Count -eq 0 -and -not [IO.File]::Exists((Join-Path $f.Data 'stop.json')) -and -not $journal.Instances[0].Written)
    RestoreGuardMaintenance $journal $f.Folder $f.Journal
    Check 'durable initial checkpoint can restore an interrupted visible transaction' ((ReadGuardJson $f.Journal).Phase -eq 'Restored' -and -not [IO.File]::Exists($journal.Marker) -and (GuardFileSnapshot (Join-Path $f.Data 'registration.json')) -ceq $registration -and $f.Folder.Rows.Count -eq 4 -and $f.Folder.Launched.Count -eq 0)

    $f=Fixture 'initial-foreign-marker'
    $marker=Join-Path $f.Root 'app\guard-maintenance.json'
    WriteGuardJson $marker @{Id='existing-transaction'}
    $markerBytes=GuardFileSnapshot $marker
    Throws 'initial marker collision is reported' {PrepareGuardMaintenance $f.Root $f.Journal $f.Folder} 'Open'
    Check 'initial marker collision preserves its owner without a misleading new journal' ((GuardFileSnapshot $marker) -ceq $markerBytes -and -not [IO.File]::Exists($f.Journal) -and $f.Folder.Deleted.Count -eq 0 -and -not [IO.File]::Exists((Join-Path $f.Data 'stop.json')))

    foreach($hiddenKind in @('file','directory')) {
        $f=Fixture ('changed-hidden-'+$hiddenKind)
        $component=Join-Path $f.Root 'app\Host\NPEduTools.Host.exe'
        $hiddenPath=if($hiddenKind -eq 'directory'){Split-Path $component}else{$component}
        [IO.File]::SetAttributes($hiddenPath,([IO.File]::GetAttributes($hiddenPath) -bor [IO.FileAttributes]::Hidden))
        PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
        # Opening the existing file preserves Hidden, unlike a replacement that
        # might accidentally turn the case into an ordinary visible-file check.
        $stream=[IO.File]::Open($component,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try {
            $bytes=[Text.Encoding]::UTF8.GetBytes('PARTIAL UPGRADE inside hidden '+$hiddenKind)
            $stream.SetLength(0);$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)
        } finally { $stream.Dispose() }
        Throws ('changed hidden '+$hiddenKind+' component refuses rollback') {RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder $f.Journal} 'Application files changed'
        Check ('changed hidden '+$hiddenKind+' retains maintenance without restoring or launching tasks') ([IO.File]::Exists((Join-Path $f.Root 'app\guard-maintenance.json')) -and (ReadGuardJson (Join-Path $f.Data 'stop.json')).InstallerTransaction -eq (ReadGuardJson $f.Journal).Id -and $f.Folder.Registered.Count -eq 0 -and $f.Folder.Launched.Count -eq 0)

        $f=Fixture ('unchanged-hidden-'+$hiddenKind)
        $component=Join-Path $f.Root 'app\Host\NPEduTools.Host.exe'
        $hiddenPath=if($hiddenKind -eq 'directory'){Split-Path $component}else{$component}
        [IO.File]::SetAttributes($hiddenPath,([IO.File]::GetAttributes($hiddenPath) -bor [IO.FileAttributes]::Hidden))
        $before=GuardFileFingerprint $component
        PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
        $journal=ReadGuardJson $f.Journal
        Check ('hidden '+$hiddenKind+' component is included in the original snapshot') (@($journal.Files | Where-Object Path -eq $component).Count -eq 1)
        RestoreGuardMaintenance $journal $f.Folder $f.Journal
        Check ('unchanged hidden '+$hiddenKind+' allows normal cancellation rollback') ((GuardFileFingerprint $component) -ceq $before -and (ReadGuardJson $f.Journal).Phase -eq 'Restored' -and -not [IO.File]::Exists($journal.Marker) -and $f.Folder.Rows.Count -eq 4 -and $f.Folder.Launched.Count -eq 1)
    }

    $f=Fixture 'added-hidden-file'
    PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
    $extra=Join-Path $f.Root 'app\extra-hidden-library.dll'
    [IO.File]::WriteAllText($extra,'NOT EXECUTABLE partial upgrade fixture')
    [IO.File]::SetAttributes($extra,[IO.FileAttributes]::Hidden)
    Throws 'new hidden file refuses rollback of a changed file set' {RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder $f.Journal} 'file set changed'
    Check 'added hidden file leaves recovery ownership intact without task mutations' ([IO.File]::Exists((Join-Path $f.Root 'app\guard-maintenance.json')) -and $f.Folder.Registered.Count -eq 0 -and $f.Folder.Launched.Count -eq 0)

    foreach($diagnostic in @('arguments','identity')) {
        $f=Fixture ('task-diagnostic-'+$diagnostic)
        $name='NPEduTools.Guard.'+$f.Key+'.Guard'
        $xml=$f.Folder.Rows[$name].Xml
        if($diagnostic -eq 'arguments') {
            $xml=$xml.Replace('</Arguments>',' --private-argument-canary</Arguments>')
            $reason='ARGUMENTS_MISMATCH'
        } else {
            $changedKey=if($f.Key[0] -eq '0'){'1'+$f.Key.Substring(1)}else{'0'+$f.Key.Substring(1)}
            $name='NPEduTools.Guard.'+$changedKey+'.Guard';$reason='IDENTITY_MISMATCH'
        }
        $message=$null
        try { GuardTaskInfo $name $xml $f.Root | Out-Null } catch { $message=$_.Exception.Message }
        Check ('task '+$diagnostic+' diagnostic identifies the failing task and reason') ($null -ne $message -and $message.Contains($name) -and $message.Contains($reason))
        Check ('task '+$diagnostic+' diagnostic does not expose raw arguments or account identity') (-not $message.Contains('private-argument-canary') -and -not $message.Contains($f.Pipe) -and -not $message.Contains($sid))
    }
    @{status='PASSED';checks=$script:checks;scheduler='SIMULATED';processes='SIMULATED';realDevices='NOT_RUN'} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $scratch 'result.json') -Encoding utf8
    Write-Output ('GUARD_MAINTENANCE_RESULT: '+(Join-Path $scratch 'result.json'))
} catch {
    @{status='FAILED';checks=$script:checks;error=$_.Exception.Message} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $scratch 'result.json') -Encoding utf8
    throw
}
