# Same production transaction code, synthetic tasks/processes, private files only.
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
. (Join-Path $projectRoot 'installer/guard-maintenance.ps1')
$scratch=Join-Path $projectRoot ('.artifacts/guard-maintenance-tests/'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$sid=$identity.User.Value;$identity.Dispose()
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
    $folder=[pscustomobject]@{Rows=@{};Deleted=@();Registered=@();FailDelete=$null;FailRun=$false}
    $folder | Add-Member ScriptMethod GetTasks { param($flags) return @($this.Rows.Values) }
    $folder | Add-Member ScriptMethod DeleteTask { param($name,$flags) if ($this.FailDelete -eq $name) { $this.FailDelete=$null;throw 'Synthetic deletion failure' };$this.Rows.Remove($name);$this.Deleted+=$name }
    $folder | Add-Member ScriptMethod RegisterTask { param($name,$xml,$flags,$user,$password,$logon,$sddl) $this.Rows[$name]=[pscustomobject]@{Name=$name;Xml=$xml};$this.Registered+=$name;return $this.Rows[$name] }
    $folder | Add-Member ScriptMethod GetTask { param($name) $task=[pscustomobject]@{Name=$name;Folder=$this};$task | Add-Member ScriptMethod Run {param($value) if($this.Folder.FailRun){$this.Folder.FailRun=$false;throw 'Synthetic guardian restart failure'};$script:runs++ };return $task }
    foreach($role in @('Guard','Login','App','Host')) {
        $name='NPEduTools.Guard.'+$key+'.'+$role
        $folder.Rows[$name]=[pscustomobject]@{Name=$name;Xml=(TaskXml $role $root $pipe $sid)}
    }
    $script:active=[pscustomobject]@{Root=$root;Data=$data;Transaction=$transaction;Journal=(Join-Path $transaction 'journal.json');Folder=$folder;Generation=$registration.Generation;Running=$true;NeverStops=$false;Pipe=$pipe;Key=$key}
    return $script:active
}
# Substitute only environmental adapters. No production scheduler/process is touched.
function GuardData([string]$Sid,[string]$Endpoint) {
    if ($Sid -ne $sid -or $Endpoint -ne $script:active.Pipe) { throw 'Fixture data ownership mismatch' }
    return $script:active.Data
}
function OwnedGuardProcesses([string]$Root) {
    if ($Root -ne $script:active.Root) { throw 'Fixture process scope mismatch' }
    $stop=ReadGuardJson (Join-Path $script:active.Data 'stop.json')
    if ($script:active.Running -and ($script:active.NeverStops -or $null -eq $stop -or $stop.Generation -ne $script:active.Generation)) { return @([pscustomobject]@{ProcessId=12345}) }
    return @()
}
function Invoke-CimMethod($InputObject,[string]$MethodName) { return [pscustomobject]@{ReturnValue=0;Sid=$sid} }
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
    RestoreGuardMaintenance $journal $f.Folder
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
    RestoreGuardMaintenance $journal $f.Folder
    Check 'cancel restores tasks, original stop and only guardian launch' ($f.Folder.Rows.Count -eq 4 -and -not [IO.File]::Exists((Join-Path $f.Data 'stop.json')) -and $script:runs -eq 1)
    Check 'cancel releases installation marker' (-not [IO.File]::Exists($journal.Marker))
    RestoreGuardMaintenance $journal $f.Folder
    Check 'repeated rollback does not launch twice' ($script:runs -eq 1)

    $f=Fixture 'stale-stop'
    WriteGuardJson (Join-Path $f.Data 'stop.json') @{Generation=[guid]::NewGuid().ToString();Reason='WindowsSessionEnding'}
    $previous=GuardFileSnapshot (Join-Path $f.Data 'stop.json')
    PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
    RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder
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
    RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder
    Check 'idle missing-guardian rollback never starts components' ($script:runs -eq 3)

    $f=Fixture 'changed-bundle';PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
    [IO.File]::WriteAllText((Join-Path $f.Root 'app\Host\NPEduTools.Host.exe'),'PARTIAL UPGRADE')
    Throws 'partial upgrade cannot restart mixed bundle' {RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder} 'Application files changed'
    Check 'mixed bundle retains stop and recovery journal' ([IO.File]::Exists((Join-Path $f.Root 'app\guard-maintenance.json')) -and $f.Folder.Rows.Count -eq 0)

    $f=Fixture 'changed-generation';PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
    $r=ReadGuardJson (Join-Path $f.Data 'registration.json');$r.Generation=[guid]::NewGuid().ToString();WriteGuardJson (Join-Path $f.Data 'registration.json') $r
    Throws 'new client generation cannot be overwritten by rollback' {RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder} 'generation changed'

    $f=Fixture 'timeout';$f.NeverStops=$true
    Throws 'nonresponsive guard times out without force-killing any process' {PrepareGuardMaintenance $f.Root $f.Journal $f.Folder} 'ten seconds'
    Check 'timeout restores task registrations' ($f.Folder.Rows.Count -eq 4 -and (ReadGuardJson $f.Journal).Phase -eq 'Restored')

    $f=Fixture 'rollback-launch-failure'
    PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
    $journal=ReadGuardJson $f.Journal
    $f.Folder.FailRun=$true
    Throws 'rollback guardian launch failure is reported' {RestoreGuardMaintenance $journal $f.Folder} 'Synthetic guardian restart failure'
    Check 'failed rollback launch retains transaction ownership for retry' ([IO.File]::Exists($journal.Marker) -and (ReadGuardJson $f.Journal).Phase -eq 'Prepared')
    $beforeRetry=$script:runs
    $retryJournal=ReadGuardJson $f.Journal
    RestoreGuardMaintenance $retryJournal $f.Folder
    WriteGuardJson $f.Journal $retryJournal
    Check 'same rollback transaction can be retried after transient launch failure' ($script:runs -eq ($beforeRetry+1) -and -not [IO.File]::Exists($journal.Marker) -and $f.Folder.Rows.Count -eq 4 -and -not [IO.File]::Exists((Join-Path $f.Data 'stop.json')))
    RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder
    Check 'successful rollback retry is durable and does not relaunch twice' ((ReadGuardJson $f.Journal).Phase -eq 'Restored' -and $script:runs -eq ($beforeRetry+1))

    foreach($change in @('bundle','generation','task')) {
        $f=Fixture ('rollback-retry-changed-'+$change)
        PrepareGuardMaintenance $f.Root $f.Journal $f.Folder
        $f.Folder.FailRun=$true
        Throws ('initial rollback '+$change+' fixture launch failure') {RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder} 'Synthetic guardian restart failure'
        switch($change) {
            'bundle' {[IO.File]::WriteAllText((Join-Path $f.Root 'app\Host\NPEduTools.Host.exe'),'CHANGED AFTER FAILED ROLLBACK');$pattern='Application files changed'}
            'generation' {$r=ReadGuardJson (Join-Path $f.Data 'registration.json');$r.Generation=[guid]::NewGuid().ToString();WriteGuardJson (Join-Path $f.Data 'registration.json') $r;$pattern='generation changed'}
            'task' {$name='NPEduTools.Guard.'+$f.Key+'.Guard';$f.Folder.Rows[$name].Xml=$f.Folder.Rows[$name].Xml.Replace('NPEduTools.Guard.v2','Other.Product');$pattern='ownership mismatch'}
        }
        $beforeRetry=$script:runs
        Throws ('rollback retry rejects changed '+$change) {RestoreGuardMaintenance (ReadGuardJson $f.Journal) $f.Folder} $pattern
        Check ('changed '+$change+' preserves rollback marker without launching') ([IO.File]::Exists((Join-Path $f.Root 'app\guard-maintenance.json')) -and $script:runs -eq $beforeRetry)
    }
    @{status='PASSED';checks=$script:checks;scheduler='SIMULATED';processes='SIMULATED';realDevices='NOT_RUN'} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $scratch 'result.json') -Encoding utf8
    Write-Output ('GUARD_MAINTENANCE_RESULT: '+(Join-Path $scratch 'result.json'))
} catch {
    @{status='FAILED';checks=$script:checks;error=$_.Exception.Message} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $scratch 'result.json') -Encoding utf8
    throw
}
