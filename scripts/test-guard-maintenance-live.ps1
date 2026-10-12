# Real Task Scheduler + a private standby guardian. No App/Host or devices are started.
param([Parameter(Mandatory=$true)][string]$GuardBundle)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$source=(Resolve-Path -LiteralPath $GuardBundle).Path
$scratch=Join-Path $projectRoot ('.artifacts/guard-maintenance-live/'+[guid]::NewGuid().ToString('N'))
$root=Join-Path $scratch 'installed';$bundle=Join-Path $root 'app';$guard=Join-Path $bundle 'Guard'
$pipe='NPEduTools.Test.Guard.Maintenance.'+[guid]::NewGuid().ToString('N')
. (Join-Path $projectRoot 'installer/guard-maintenance.ps1')
$identity=[Security.Principal.WindowsIdentity]::GetCurrent();$sid=$identity.User.Value;$identity.Dispose()
$key=(GuardHash ($sid+'|'+$pipe)).Substring(0,24)
$data=GuardData $sid $pipe
$names=@();$process=$null;$result=@{status='RUNNING';checks=@();applicationInstalled=$false;capturesStarted=$false;realDevices='NOT_RUN'}
[IO.Directory]::CreateDirectory($guard) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $bundle 'Host')) | Out-Null
[IO.Directory]::CreateDirectory($data) | Out-Null
# Enumerate only this explicitly selected bundle.
Get-ChildItem -LiteralPath $source | Copy-Item -Destination $guard -Recurse -Force
foreach($path in @('NPEduTools.App.exe','Host\NPEduTools.Host.exe')) { [IO.File]::WriteAllText((Join-Path $bundle $path),'NOT EXECUTABLE: unused fixture') }
$shell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$service=New-Object -ComObject Schedule.Service;$service.Connect();$folder=$service.GetFolder('\')
function RunMaintenance([string]$Action,[string]$Directory) {
    & $shell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $projectRoot 'installer/guard-maintenance.ps1') -Action $Action -InstallRoot $root -TransactionDirectory $Directory
    if ($LASTEXITCODE -ne 0) { throw ('Live maintenance failed: '+$Action) }
}
function Check([string]$Name,[bool]$Value) { if (-not $Value) { throw ('FAIL: '+$Name) };$result.checks+=$Name;Write-Output ('PASS: '+$Name) }
try {
    WriteGuardJson (Join-Path $data 'registration.json') @{Generation=[guid]::NewGuid().ToString();App=@{Id=$PID;StartedUtcTicks=1};Upstream=$null;BundleDirectory=$bundle}
    foreach($role in @('Guard','Login','App','Host')) {
        $name='NPEduTools.Guard.'+$key+'.'+$role
        $exe=switch($role) { 'App' {Join-Path $bundle 'NPEduTools.App.exe'} 'Host' {Join-Path $bundle 'Host\NPEduTools.Host.exe'} default {Join-Path $guard 'svchost.exe'} }
        $arguments='--pipe "'+$pipe+'"';if($role -eq 'App'){$arguments+=' --guard-recovery'};if($role -eq 'Login'){$arguments+=' --at-login'}
        $xml='<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><RegistrationInfo><Source>NPEduTools.Guard.v2</Source></RegistrationInfo><Triggers/><Principals><Principal id="User"><UserId>'+$sid+'</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals><Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings><Actions Context="User"><Exec><Command>'+[Security.SecurityElement]::Escape($exe)+'</Command><Arguments>'+$arguments+'</Arguments><WorkingDirectory>'+[Security.SecurityElement]::Escape((Split-Path $exe))+'</WorkingDirectory></Exec></Actions></Task>'
        $null=$folder.RegisterTask($name,$xml,6,$sid,$null,3,$null);$names+=$name
    }
    $process=Start-Process -FilePath (Join-Path $guard 'svchost.exe') -ArgumentList @('--pipe',$pipe) -WindowStyle Hidden -PassThru
    $deadline=[Diagnostics.Stopwatch]::StartNew()
    while ($null -eq (ReadGuardJson (Join-Path $data 'status.json'))) {
        if ($process.HasExited -or $deadline.Elapsed.TotalSeconds -gt 10) { throw 'Private guardian failed to start.' }
        Start-Sleep -Milliseconds 100
    }
    Check 'private guardian running in standby' (-not $process.HasExited)
    $transaction=Join-Path $scratch 'cancel-transaction';RunMaintenance 'Prepare' $transaction
    Check 'real guardian exits gracefully and owned tasks disappear' ($process.WaitForExit(2000) -and @(GuardPlans (CurrentGuardTasks $folder) $root).Count -eq 0)
    RunMaintenance 'Restore' $transaction
    $deadline.Restart()
    while (@(OwnedGuardProcesses $root).Count -eq 0) { if($deadline.Elapsed.TotalSeconds -gt 10){throw 'Guardian rollback launch missing.'};Start-Sleep -Milliseconds 100 }
    Check 'rollback independently relaunches guardian and four tasks' (@(GuardPlans (CurrentGuardTasks $folder) $root).Count -eq 4)
    $transaction=Join-Path $scratch 'finish-transaction';RunMaintenance 'Prepare' $transaction;RunMaintenance 'Complete' $transaction
    Check 'successful maintenance leaves no guardian or owned tasks' (@(OwnedGuardProcesses $root).Count -eq 0 -and @(GuardPlans (CurrentGuardTasks $folder) $root).Count -eq 0)
    Check 'successful maintenance releases directory marker' (-not [IO.File]::Exists((Join-Path $bundle 'guard-maintenance.json')))
    $result.status='PASSED'
} catch {
    $result.status='FAILED';$result.error=$_.Exception.Message
    throw
} finally {
    # Scope cleanup to the exact random task names and process path created above.
    foreach($name in $names) { try { $folder.DeleteTask($name,0) } catch {} }
    $r=ReadGuardJson (Join-Path $data 'registration.json')
    if($r){WriteGuardJson (Join-Path $data 'stop.json') @{Generation=$r.Generation;Reason='Maintenance'}}
    foreach($p in @(OwnedGuardProcesses $root)) {
        $owned=Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue
        if($owned -and -not $owned.WaitForExit(5000)) { throw 'Private guardian cleanup failed; no unverified process terminated.' }
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $scratch 'result.json') -Encoding utf8
    Write-Output ('GUARD_MAINTENANCE_LIVE_RESULT: '+(Join-Path $scratch 'result.json'))
}
