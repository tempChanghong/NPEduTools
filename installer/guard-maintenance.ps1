# Windows PowerShell 5.1; invoked only by the installer, never by the client UI.
param(
    [ValidateSet('Prepare','Restore','Complete')][string]$Action,
    [string]$InstallRoot,
    [string]$TransactionDirectory
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    # A setup launched from PowerShell 7 inherits its incompatible module search path.
    $env:PSModulePath=$PSHOME+'\Modules;'+$env:ProgramFiles+'\WindowsPowerShell\Modules'
}
function GuardHash([string]$Text) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)))).Replace('-','') }
    finally { $sha.Dispose() }
}
function GuardPath([string]$Path) { return [IO.Path]::GetFullPath($Path).TrimEnd('\') }
function ReadGuardJson([string]$Path) {
    if (-not [IO.File]::Exists($Path)) { return $null }
    if ((Get-Item -LiteralPath $Path).Length -gt 1048576) { throw 'Maintenance record too large.' }
    return ([IO.File]::ReadAllText($Path) | ConvertFrom-Json)
}
function WriteGuardJson([string]$Path, $Value) {
    $temporary=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        $bytes=[Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 16))
        $stream=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary,$Path,[System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($temporary,$Path) }
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}
function GuardTaskInfo([string]$Name,[string]$Xml,[string]$Root) {
    $document=[xml]$Xml
    $ns=[Xml.XmlNamespaceManager]::new($document.NameTable)
    $ns.AddNamespace('t','http://schemas.microsoft.com/windows/2004/02/mit/task')
    $task=$document.DocumentElement
    $actions=@($task.SelectNodes('t:Actions/*',$ns))
    $principals=@($task.SelectNodes('t:Principals/t:Principal',$ns))
    $source=$task.SelectSingleNode('t:RegistrationInfo/t:Source',$ns)
    if ($source.InnerText -ne 'NPEduTools.Guard.v2' -or $actions.Count -ne 1 -or $actions[0].LocalName -ne 'Exec' -or $principals.Count -ne 1) { throw 'Guardian task ownership mismatch.' }
    $sid=$principals[0].SelectSingleNode('t:UserId',$ns).InnerText
    if ($sid -notmatch '^S-1-[0-9-]+$' -or $principals[0].SelectSingleNode('t:LogonType',$ns).InnerText -ne 'InteractiveToken') { throw 'Guardian principal mismatch.' }
    $match=[regex]::Match($Name,'^NPEduTools\.Guard\.([A-F0-9]{24})\.(Guard|Login|App|Host)$')
    if (-not $match.Success) { throw 'Guardian task name mismatch.' }
    $role=$match.Groups[2].Value
    $exe=switch($role) { 'App' {Join-Path $Root 'app\NPEduTools.App.exe'} 'Host' {Join-Path $Root 'app\Host\NPEduTools.Host.exe'} default {Join-Path $Root 'app\Guard\svchost.exe'} }
    if ($actions[0].SelectSingleNode('t:Command',$ns).InnerText -ine $exe -or $actions[0].SelectSingleNode('t:WorkingDirectory',$ns).InnerText -ine (Split-Path $exe)) { throw 'Guardian task path mismatch.' }
    # Task Scheduler drops an empty Arguments element when persisting the default Host.
    # Treat absence as empty, as native GuardTasks.OwnedDefinition does; required args
    # for every other role still have to match exactly below.
    $arguments=[string]$actions[0].SelectSingleNode('t:Arguments',$ns).InnerText
    $endpoint='default';$prefix='';$upstream=$null
    if ($arguments -match '^--pipe "([^"\r\n\\/:]{1,200})"(?: |$)') {
        $endpoint=$Matches[1];if ($endpoint.StartsWith('--') -or $endpoint -eq 'default') { throw 'Invalid or ambiguous guardian pipe.' }
        $prefix='--pipe "'+$endpoint+'"'
    } elseif ($role -in @('Guard','Login')) { $prefix='--default-pipe' }
    if ($arguments -match '(?:^| )--classisland-pipe "([^"\r\n\\/:]{1,200})"(?: |$)') {
        $upstream=$Matches[1];if ($upstream.StartsWith('--')) { throw 'Invalid upstream pipe.' }
    }
    $expected=$prefix
    if ($role -in @('App','Host') -and $upstream) { $expected+=' --classisland-pipe "'+$upstream+'"' }
    if ($role -eq 'App') { $expected+=' --guard-recovery' }
    if ($role -eq 'Login') { $expected+=' --at-login' }
    # Task checks may precede journal creation: identify the task without logging raw
    # command-line values or account identifiers in the only available result file.
    if ($arguments -cne $expected.Trim()) { throw ('Guardian task arguments or identity mismatch. Task: '+$Name+'; reason: ARGUMENTS_MISMATCH.') }
    if ($match.Groups[1].Value -cne (GuardHash ($sid+'|'+$endpoint)).Substring(0,24)) { throw ('Guardian task arguments or identity mismatch. Task: '+$Name+'; reason: IDENTITY_MISMATCH.') }
    return [pscustomobject]@{Name=$Name;Xml=$Xml;Sid=$sid;Endpoint=$endpoint;Role=$role;Upstream=$upstream;Key=$match.Groups[1].Value}
}
function GuardPlans($Tasks,[string]$Root) {
    # Find groups touching this installation, then inspect every member before any mutation.
    $selected=@{}
    foreach($task in $Tasks) {
        if ($task.Name -notlike 'NPEduTools.Guard.*') { continue }
        $xml=[xml]$task.Xml
        foreach($command in $xml.GetElementsByTagName('Command')) {
            if ($command.InnerText -in @((Join-Path $Root 'app\Guard\svchost.exe'),(Join-Path $Root 'app\NPEduTools.App.exe'),(Join-Path $Root 'app\Host\NPEduTools.Host.exe'))) {
                if ($task.Name -notmatch '^NPEduTools\.Guard\.([A-F0-9]{24})\.(Guard|Login|App|Host)$') { throw 'Unexpected guardian task name.' }
                $selected[$Matches[1]]=$true
            }
        }
    }
    $members=@()
    foreach($task in $Tasks) {
        if ($task.Name -match '^NPEduTools\.Guard\.([A-F0-9]{24})\.(Guard|Login|App|Host)$' -and $selected.ContainsKey($Matches[1])) {
            $members+=GuardTaskInfo $task.Name $task.Xml $Root
        }
    }
    foreach($group in @($members | Group-Object Key)) {
        $upstreams=@($group.Group | Where-Object Role -in @('App','Host') | ForEach-Object { [string]$_.Upstream } | Select-Object -Unique)
        if ($upstreams.Count -gt 1) { throw 'Guardian upstream mismatch.' }
    }
    return $members
}
function GuardData([string]$Sid,[string]$Endpoint) {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try { $currentSid=$identity.User.Value } finally { $identity.Dispose() }
    if ($Endpoint -eq 'default') {
        if ($currentSid -eq $Sid) { return Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'NPEduTools\config\guard' }
        $profile=Get-ItemProperty -LiteralPath ('HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\'+$Sid)
        return Join-Path ([Environment]::ExpandEnvironmentVariables($profile.ProfileImagePath)) 'AppData\Local\NPEduTools\config\guard'
    }
    if ($currentSid -ne $Sid) { throw 'Custom-pipe maintenance requires the owning Windows account.' }
    return Join-Path ([IO.Path]::GetTempPath()) ('NPEduTools\instances\'+(GuardHash $Endpoint).Substring(0,24)+'\guard')
}
function CurrentGuardTasks($Folder) {
    return @($Folder.GetTasks(1) | ForEach-Object { [pscustomobject]@{Name=[string]$_.Name;Xml=[string]$_.Xml} })
}
function OwnedGuardProcesses([string]$Root) {
    $path=Join-Path $Root 'app\Guard\svchost.exe'
    return @(Get-CimInstance Win32_Process -Filter "Name = 'svchost.exe'" | Where-Object { $_.ExecutablePath -ieq $path })
}
function GuardProcessInstance($Process) {
    $path=[string]$Process.ExecutablePath;$line=[string]$Process.CommandLine
    if ([string]::IsNullOrWhiteSpace($path) -or [string]::IsNullOrWhiteSpace($line)) { throw 'Cannot inspect guardian invocation; use manual maintenance.' }
    # Guard endpoints cannot contain quotes or backslashes. Recognize its supported
    # launch forms, including quoted paths/pipe names, without guessing on extra args.
    $programs=@([regex]::Escape('"'+$path+'"'))
    if ($path -notmatch '\s') { $programs+=[regex]::Escape($path) }
    $prefix='^\s*(?i:' + ($programs -join '|') + ')\s+'
    $arguments='(?:(?<Default>--default-pipe|"--default-pipe")|(?:--pipe|"--pipe")\s+(?:"(?<Pipe>[^"\r\n\\/:]{1,200})"|(?<Pipe>[^\s"\\/:]{1,200})))(?:\s+(?<Action>--at-login|--maintenance|"--at-login"|"--maintenance"))?\s*$'
    $match=[regex]::Match($line,$prefix+$arguments)
    if (-not $match.Success -or $match.Groups['Pipe'].Value.StartsWith('--')) { throw 'Cannot verify guardian invocation; use manual maintenance.' }
    $owner=Invoke-CimMethod -InputObject $Process -MethodName GetOwnerSid
    if ($owner.ReturnValue -ne 0 -or [string]$owner.Sid -notmatch '^S-1-[0-9-]+$') { throw 'Cannot inspect guardian owner.' }
    $endpoint=$match.Groups['Pipe'].Value
    if ($match.Groups['Default'].Success -or $endpoint -ceq ('NPEduTools.v1.'+$owner.Sid+'.'+$Process.SessionId)) { $endpoint='default' }
    elseif ($endpoint -ieq 'default') { throw 'Ambiguous guardian endpoint; use manual maintenance.' }
    return [pscustomobject]@{Sid=[string]$owner.Sid;Endpoint=$endpoint;Resident=(-not $match.Groups['Action'].Success)}
}
function GuardFileSnapshot([string]$Path) {
    if ([IO.File]::Exists($Path)) {
        if ((Get-Item -LiteralPath $Path).Length -gt 32768) { throw 'Guardian state record too large.' }
        return [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path))
    }
    return $null
}
function RestoreGuardFile([string]$Path,$Bytes) {
    if ($null -eq $Bytes) { if ([IO.File]::Exists($Path)) { [IO.File]::Delete($Path) } }
    else {
        $temporary=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
        try {
            $content=[Convert]::FromBase64String($Bytes)
            $stream=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try { $stream.Write($content,0,$content.Length);$stream.Flush($true) } finally { $stream.Dispose() }
            if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary,$Path,[System.Management.Automation.Language.NullString]::Value) }
            else { [IO.File]::Move($temporary,$Path) }
        } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
    }
}
function CheckGuardMarker($Journal) {
    $marker=ReadGuardJson $Journal.Marker
    if ($null -eq $marker -or $marker.Id -ne $Journal.Id) { throw 'Installation maintenance ownership changed; manual recovery required.' }
}
function RestoreGuardMaintenance($Journal,$Folder,[string]$JournalPath) {
    if (-not $JournalPath) { throw 'Missing rollback journal path.' }
    if ($Journal.Phase -eq 'Restored') {
        # Completion can survive a crash before marker deletion. Finalize only
        # this transaction's marker, without registering or launching tasks again.
        if ([IO.File]::Exists($Journal.Marker)) { CheckGuardMarker $Journal;[IO.File]::Delete($Journal.Marker) }
        return
    }
    CheckGuardMarker $Journal
    foreach($task in $Journal.Tasks) {
        $expected=GuardTaskInfo $task.Name $task.Xml $Journal.Root
        foreach($field in @('Sid','Key','Role','Endpoint','Upstream')) {
            if ([string]$task.$field -cne [string]$expected.$field) { throw 'Guardian journal task identity mismatch.' }
        }
    }
    foreach($instance in $Journal.Instances) {
        $member=@($Journal.Tasks | Where-Object Key -eq $instance.Key)
        if ($member.Count -eq 0 -or $instance.Data -ine (GuardData $member[0].Sid $member[0].Endpoint)) { throw 'Guardian journal data directory mismatch.' }
    }
    # Do not run the old guardian against an incomplete upgrade or a newer client generation.
    foreach($file in $Journal.Files) {
        if ((GuardFileFingerprint $file.Path) -cne $file.Hash) { throw 'Application files changed; automatic rollback cannot restart this bundle.' }
    }
    $currentFiles=@(Get-ChildItem -LiteralPath (Join-Path $Journal.Root 'app') -File -Recurse -Force | Where-Object FullName -ne $Journal.Marker | ForEach-Object FullName)
    $beforeNames=(@($Journal.Files | ForEach-Object Path | Sort-Object) -join "`n")
    if ($beforeNames -cne (@($currentFiles | Sort-Object) -join "`n")) { throw 'Application file set changed; automatic rollback refused.' }
    foreach($instance in $Journal.Instances) {
        $r=ReadGuardJson (Join-Path $instance.Data 'registration.json')
        if ($null -ne $instance.Generation -and ($null -eq $r -or $r.Generation -ne $instance.Generation)) { throw 'Guardian generation changed; automatic rollback refused.' }
        $stop=ReadGuardJson (Join-Path $instance.Data 'stop.json')
        if ($instance.Written -and (GuardFileSnapshot (Join-Path $instance.Data 'stop.json')) -cne $instance.PreviousStop -and ($null -eq $stop -or $stop.InstallerTransaction -ne $Journal.Id)) { throw 'Guardian maintenance stop changed; automatic rollback refused.' }
    }
    $existing=CurrentGuardTasks $Folder
    foreach($task in $Journal.Tasks) {
        $found=@($existing | Where-Object Name -eq $task.Name)
        if ($found.Count -gt 0) {
            $null=GuardTaskInfo $found[0].Name $found[0].Xml $Journal.Root
            if ($found[0].Xml -cne $task.Xml) { throw 'Task changed while in maintenance; automatic rollback refused.' }
        }
    }
    # Restore XML while the durable stop is still present: no task may wake App/Host yet.
    foreach($task in $Journal.Tasks) { $null=$Folder.RegisterTask($task.Name,$task.Xml,6,$task.Sid,$null,3,$null) }
    foreach($instance in $Journal.Instances) {
        if ($instance.Written) { RestoreGuardFile (Join-Path $instance.Data 'stop.json') $instance.PreviousStop }
    }
    # Restart only guardians that were running before maintenance. Never launch App/Host here.
    foreach($instance in $Journal.Instances) {
        $previous=if ($instance.PreviousStop) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($instance.PreviousStop)) | ConvertFrom-Json } else { $null }
        if ($instance.WasRunning -and ($null -eq $previous -or $previous.Generation -ne $instance.Generation)) {
            $guard=@($Journal.Tasks | Where-Object { $_.Key -eq $instance.Key -and $_.Role -eq 'Guard' })
            if ($guard.Count -ne 1) { throw 'Guardian task absent; manual restart required.' }
            $null=$Folder.GetTask($guard[0].Name).Run($null)
        }
    }
    # Keep ownership until launches are accepted AND completion is durable.
    # Use the caller's transaction path, never a serialized write destination.
    $previousPhase=$Journal.Phase
    $Journal.Phase='Restored'
    try { WriteGuardJson $JournalPath $Journal }
    catch { $Journal.Phase=$previousPhase;throw }
    [IO.File]::Delete($Journal.Marker)
}
function PrepareGuardMaintenance([string]$Root,[string]$JournalPath,$Folder) {
    $members=@(GuardPlans (CurrentGuardTasks $Folder) $Root)
    $processes=@(OwnedGuardProcesses $Root)
    $instances=@($processes | ForEach-Object { GuardProcessInstance $_ })
    foreach($instance in $instances) {
        if ($instance.Resident -and @($members | Where-Object { $_.Sid -eq $instance.Sid -and $_.Endpoint -ceq $instance.Endpoint }).Count -eq 0) {
            throw 'Running guardian has no matching owned task group; use manual maintenance.'
        }
    }
    $journal=[pscustomobject]@{Id=[guid]::NewGuid().ToString('N');Root=$Root;Marker=(Join-Path $Root 'app\guard-maintenance.json');JournalPath=$JournalPath;Phase='Prepared';Tasks=$members;Instances=@();Files=@()}
    if ([IO.Directory]::Exists((Join-Path $Root 'app'))) {
        foreach($file in @(Get-ChildItem -LiteralPath (Join-Path $Root 'app') -File -Recurse -Force)) {
            $journal.Files+= [pscustomobject]@{Path=$file.FullName;Hash=(GuardFileFingerprint $file.FullName)}
        }
    }
    foreach($group in @($members | Group-Object Key)) {
        $first=$group.Group[0];$data=GuardData $first.Sid $first.Endpoint
        $registration=ReadGuardJson (Join-Path $data 'registration.json')
        $generation=$null
        if ($registration) {
            $guid=[guid]::Empty
            if (-not [guid]::TryParse([string]$registration.Generation,[ref]$guid) -or $guid -eq [guid]::Empty -or $registration.BundleDirectory -ine (Join-Path $Root 'app')) { throw 'Guardian registration mismatch.' }
            $generation=[string]$registration.Generation
        }
        $running=@($instances | Where-Object { $_.Resident -and $_.Sid -eq $first.Sid -and $_.Endpoint -ceq $first.Endpoint }).Count -gt 0
        if ($running -and $null -eq $generation) { throw 'Running guardian has no readable registration.' }
        $null=ReadGuardJson (Join-Path $data 'stop.json') # Fail on damaged state before deleting tasks, not during rollback.
        $journal.Instances+=[pscustomobject]@{Key=$first.Key;Data=$data;Generation=$generation;PreviousStop=(GuardFileSnapshot (Join-Path $data 'stop.json'));Written=$false;WasRunning=$running}
    }
    if ($processes.Count -gt 0 -and $members.Count -eq 0) { throw 'Running guardian has no owned tasks; use manual maintenance.' }
    # CreateNew never takes over an earlier interrupted installer transaction.
    $parent=Split-Path $journal.Marker
    if (-not [IO.Directory]::Exists($parent)) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
    # An abrupt exit must not leave a launch-blocking marker without its recovery
    # record. Persist the checkpoint before making maintenance visible to clients.
    WriteGuardJson $JournalPath $journal
    try { $marker=[IO.File]::Open($journal.Marker,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None) }
    catch { [IO.File]::Delete($JournalPath);throw } # No marker or runtime state was taken over.
    try { $bytes=[Text.Encoding]::UTF8.GetBytes(($journal | Select-Object Id,Root,JournalPath | ConvertTo-Json));$marker.Write($bytes,0,$bytes.Length);$marker.Flush($true) } finally { $marker.Dispose() }
    try {
        foreach($instance in $journal.Instances) {
            if ($null -ne $instance.Generation) {
                $instance.Written=$true;WriteGuardJson $JournalPath $journal
                WriteGuardJson (Join-Path $instance.Data 'stop.json') @{Generation=$instance.Generation;Reason='Maintenance';InstallerTransaction=$journal.Id}
            }
        }
        foreach($task in $members) { $Folder.DeleteTask($task.Name,0) }
        $deadline=[Diagnostics.Stopwatch]::StartNew()
        while (@(OwnedGuardProcesses $Root).Count -gt 0) {
            if ($deadline.Elapsed.TotalSeconds -ge 10) { throw 'Guardian did not stop within ten seconds; no process was force-killed.' }
            Start-Sleep -Milliseconds 100
        }
        if (@(GuardPlans (CurrentGuardTasks $Folder) $Root).Count -gt 0) { throw 'Guardian tasks reappeared during maintenance.' }
        WriteGuardJson $JournalPath $journal
    } catch {
        $failure=$_.Exception.Message
        try { RestoreGuardMaintenance $journal $Folder $JournalPath }
        catch { throw ($failure+' Rollback failed: '+$_.Exception.Message+' Journal: '+$JournalPath) }
        throw $failure
    }
}
function GuardFileFingerprint([string]$Path) {
    if (-not [IO.File]::Exists($Path)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}
function WriteGuardResult([string]$Directory,[string]$Operation,[string]$Message) {
    # Keep Prepare's failure even if the installer's later Restore succeeds.
    if ($Operation -in @('Prepare','Restore','Complete')) {
        [IO.File]::WriteAllText((Join-Path $Directory ($Operation+'-result.txt')),$Message,[Text.Encoding]::UTF8)
    }
    [IO.File]::WriteAllText((Join-Path $Directory 'result.txt'),$Message,[Text.Encoding]::UTF8)
}
# Tests dot-source the same ownership and transaction code with private scheduler fixtures.
if ($MyInvocation.InvocationName -eq '.') { return }
try {
    if (-not $Action -or -not $InstallRoot -or -not $TransactionDirectory) { throw 'Missing maintenance arguments.' }
    $root=GuardPath $InstallRoot;$directory=GuardPath $TransactionDirectory
    if ($directory -ieq $root -or $directory.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Maintenance journal must be outside installation.' }
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    # Never allow an unprivileged account to replace XML that an elevated restore executes.
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal=[Security.Principal.WindowsPrincipal]::new($identity)
        $acl=[IO.Directory]::GetAccessControl($directory,[Security.AccessControl.AccessControlSections]::Access)
        $acl.SetAccessRuleProtection($true,$false)
        foreach($rule in @($acl.Access)) { $acl.RemoveAccessRuleSpecific($rule) }
        $sids=@('S-1-5-18','S-1-5-32-544')
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { $sids+=$identity.User.Value }
        foreach($sid in $sids) { $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow')) }
        [IO.Directory]::SetAccessControl($directory,$acl)
    } finally { $identity.Dispose() }
    $journalPath=Join-Path $directory 'journal.json'
    if ($Action -eq 'Prepare') {
        if ([IO.File]::Exists($journalPath)) { throw 'Maintenance transaction already exists.' }
        $service=New-Object -ComObject Schedule.Service;$service.Connect();$folder=$service.GetFolder('\')
        PrepareGuardMaintenance $root $journalPath $folder
    } else {
        $journal=ReadGuardJson $journalPath
        if ($null -eq $journal -or $journal.Root -ine $root -or $journal.Marker -ine (Join-Path $root 'app\guard-maintenance.json')) { throw 'Maintenance transaction mismatch.' }
        if ($Action -eq 'Restore') {
            # A durable Restored checkpoint needs only local marker cleanup.
            # Do not block it (or Complete below) on an unrelated COM service.
            $folder=$null
            if ($journal.Phase -ne 'Restored') {
                $service=New-Object -ComObject Schedule.Service;$service.Connect();$folder=$service.GetFolder('\')
            }
            RestoreGuardMaintenance $journal $folder $journalPath
        } else {
            if ($journal.Phase -ne 'Completed' -or [IO.File]::Exists($journal.Marker)) { CheckGuardMarker $journal }
            $journal.Phase='Completed';WriteGuardJson $journalPath $journal
            [IO.File]::Delete($journal.Marker)
        }
    }
    WriteGuardResult $directory $Action 'OK'
    exit 0
} catch {
    if ($TransactionDirectory -and [IO.Directory]::Exists($TransactionDirectory)) {
        WriteGuardResult $TransactionDirectory $Action $_.Exception.Message
    }
    Write-Error $_ -ErrorAction Continue
    exit 1
}
