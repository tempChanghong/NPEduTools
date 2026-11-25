[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RunDirectory, [ValidateRange(30,600)][int]$TimeoutSeconds=300)
$ErrorActionPreference='Stop'
$guest=Get-CimInstance Win32_ComputerSystem
$os=Get-CimInstance Win32_OperatingSystem
if($guest.Model -notmatch 'VMware|Virtual Machine|VirtualBox' -or $os.Caption -notmatch 'Windows 10' -or $os.OSArchitecture -notmatch '64' -or -not [Environment]::Is64BitProcess){throw 'This policy transaction requires a Windows 10 x64 VM and 64-bit PowerShell.'}
$canonical=[IO.Path]::GetFullPath($RunDirectory).TrimEnd('\')
if($canonical -cnotmatch '^C:\\NpepEdgePolicyEvidence\\edge-policy-([a-f0-9]{32})$'){throw 'Unexpected isolated run directory.'}
$runId=$Matches[1]
if(-not (Test-Path -LiteralPath $canonical -PathType Container)){throw 'Run directory is missing.'}
for($directory=Get-Item -LiteralPath $canonical;$directory;$directory=$directory.Parent){
    if($directory.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse points are not accepted.'}
}
$principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Existing authorized administrator token is required.'}
$requestPath=Join-Path $canonical 'policy-request.json'
$request=Get-Content -LiteralPath $requestPath -Raw|ConvertFrom-Json
if($request.runId -cne $runId){throw 'Request runId mismatch.'}
if($request.policyHive -cne 'HKEY_LOCAL_MACHINE'){throw 'Only this guest HKLM policy transaction is accepted.'}
$port=[int](Get-Content -LiteralPath (Join-Path $canonical 'loopback-port.txt') -Raw)
if($port -lt 1024 -or $port -gt 65535){throw 'Invalid loopback port.'}
if($request.loopbackPort -ne $port){throw 'Request loopback port mismatch.'}
$expected=@("https://.douyin.com/__npeptest_$runId","http://127.0.0.1:$port/__npeptest_$runId")
$patterns=@($request.patterns)
if($patterns.Count -ne 2 -or $patterns[0] -cne $expected[0] -or $patterns[1] -cne $expected[1]){throw 'Request is outside this isolated run.'}
$hive=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
$policyPath='Software\Policies\Microsoft\Edge\URLBlocklist'
$ancestors=@('Software\Policies','Software\Policies\Microsoft','Software\Policies\Microsoft\Edge',$policyPath)
$before=@(foreach($path in $ancestors){
    $key=$hive.OpenSubKey($path)
    try{[pscustomobject]@{Path=$path;Exists=($null -ne $key)}}finally{if($key){$key.Dispose()}}
})
function Read-PolicyValues {
    $key=$hive.OpenSubKey($policyPath)
    try{
        if(-not $key){return @()}
        foreach($name in @($key.GetValueNames()|Sort-Object)){
            [pscustomobject]@{Name=$name;Kind=$key.GetValueKind($name).ToString();Value=$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}
        }
    }finally{if($key){$key.Dispose()}}
}
$baseline=@(Read-PolicyValues)
$baselineText=ConvertTo-Json -InputObject $baseline -Depth 8 -Compress
[pscustomobject]@{runId=$runId;hive='HKEY_LOCAL_MACHINE';view='Registry64';keys=$before;values=$baseline}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $canonical 'policy-admin-baseline.json') -Encoding UTF8
$status=[ordered]@{runId=$runId;ready=$false;restored=$false;started=(Get-Date -Format o);sessionId=(Get-Process -Id $PID).SessionId;error=$null;ownedValues=@();cleanup=@()}
$owned=[ordered]@{}
$policy=$null
function Write-AtomicSignal($Value,[string]$Path){
    if(Test-Path -LiteralPath $Path){throw 'Existing signal must not be overwritten.'}
    $temporary=$Path+'.tmp'
    $stream=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{
        $writer=[IO.StreamWriter]::new($stream,[Text.UTF8Encoding]::new($false))
        try{$writer.Write(($Value|ConvertTo-Json -Depth 8));$writer.Flush()}finally{$writer.Dispose()}
    }finally{$stream.Dispose()}
    Move-Item -LiteralPath $temporary -Destination $Path
}
try{
    $policy=$hive.CreateSubKey($policyPath)
    foreach($pattern in $expected){
        $slot=1
        while($slot -le 1000 -and $policy.GetValueNames() -contains [string]$slot){$slot++}
        if($slot -gt 1000){throw 'No unused URLBlocklist slot.'}
        $name=[string]$slot
        $policy.SetValue($name,$pattern,[Microsoft.Win32.RegistryValueKind]::String)
        $owned[$name]=$pattern
    }
    $policy.Flush()
    $status.ownedValues=@(foreach($name in $owned.Keys){[pscustomobject]@{name=$name;value=$owned[$name]}})
    $status.ready=$true
    Write-AtomicSignal ([pscustomobject]@{runId=$runId;ready=$true;hive='HKEY_LOCAL_MACHINE';slots=@($owned.Keys);patterns=$expected}) (Join-Path $canonical 'policy-ready.json')
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $finished=$false
    while($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds){
        $finishedPath=Join-Path $canonical 'policy-finished.json'
        if(Test-Path -LiteralPath $finishedPath){
            try{$reply=Get-Content -LiteralPath $finishedPath -Raw|ConvertFrom-Json}catch{Start-Sleep -Milliseconds 200;continue}
            if($reply.runId -cne $runId){throw 'Finished handshake runId mismatch.'}
            if($reply.finished -ne $true){throw 'Finished handshake did not confirm completion.'}
            $finished=$true
            break
        }
        Start-Sleep -Milliseconds 200
    }
    if(-not $finished){throw 'Timed out waiting for the interactive test; restoring only this transaction.'}
}catch{
    $status.error=$_.Exception.Message
}finally{
    $allOwned=$true
    try{
        if($policy){
            foreach($name in $owned.Keys){
                if($policy.GetValueNames() -contains $name -and $policy.GetValueKind($name) -eq [Microsoft.Win32.RegistryValueKind]::String -and $policy.GetValue($name) -ceq $owned[$name]){$policy.DeleteValue($name,$false)}else{$allOwned=$false}
            }
            $policy.Flush()
            $policy.Dispose()
            $policy=$null
        }
        $cleanup=@(for($i=$before.Count-1;$i -ge 0;$i--){
            $entry=$before[$i]
            if($entry.Exists){continue}
            $key=$hive.OpenSubKey($entry.Path)
            $empty=$false
            if($key){try{$empty=($key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0)}finally{$key.Dispose()}}
            if($empty){$hive.DeleteSubKey($entry.Path,$false)}
            $check=$hive.OpenSubKey($entry.Path)
            try{[pscustomobject]@{path=$entry.Path;removedEmpty=$empty;absentAfter=($null -eq $check)}}finally{if($check){$check.Dispose()}}
        })
        $status.cleanup=$cleanup
        $afterText=ConvertTo-Json -InputObject @(Read-PolicyValues) -Depth 8 -Compress
        $keysMatch=$true
        foreach($entry in $before){$key=$hive.OpenSubKey($entry.Path);try{if(($null -ne $key) -ne $entry.Exists){$keysMatch=$false}}finally{if($key){$key.Dispose()}}}
        $status.restored=$allOwned -and $keysMatch -and ($afterText -ceq $baselineText)
    }catch{$status.error=($status.error+'; cleanup: '+$_.Exception.Message).Trim('; ')}finally{
        if($policy){$policy.Dispose()}
        $hive.Dispose()
        $status.finished=Get-Date -Format o
        Write-AtomicSignal $status (Join-Path $canonical 'policy-restored.json')
    }
}
if(-not $status.restored){exit 1}
