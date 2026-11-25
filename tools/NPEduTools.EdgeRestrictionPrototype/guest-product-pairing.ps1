param(
    [Parameter(Mandatory)][ValidateSet('Begin','Confirm','Unpair','Status')][string]$Action,
    [Parameter(Mandatory)][ValidatePattern('^NPEduTools\.EdgeProduct\.[a-zA-Z0-9.-]+$')][string]$PipeName,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
$fixture = Get-Content -LiteralPath (Join-Path $EvidenceDirectory 'fixture.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$uri = [uri]$fixture.origin
if (-not $uri.IsLoopback -or $uri.Scheme -ne 'http') { throw 'Use only the local disposable fixture.' }

function Invoke-Pipe($capability, $command) {
    $stream = New-Object System.IO.Pipes.NamedPipeClientStream('.', $PipeName, [IO.Pipes.PipeDirection]::InOut)
    try {
        $stream.Connect(3000)
        $request = @{version=1; requestId=[guid]::NewGuid().ToString(); capability=$capability}
        if ($command) { $request.npep = $command }
        $bytes = [Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Depth 8 -Compress))
        $writer = New-Object IO.BinaryWriter($stream, [Text.Encoding]::UTF8, $true)
        $writer.Write([int]$bytes.Length); $writer.Write($bytes); $writer.Flush()
        $reader = New-Object IO.BinaryReader($stream, [Text.Encoding]::UTF8, $true)
        $size = $reader.ReadInt32()
        if ($size -lt 1 -or $size -gt 65536) { throw 'Invalid Host response frame.' }
        $reply = [Text.Encoding]::UTF8.GetString($reader.ReadBytes($size)) | ConvertFrom-Json
        if ($reply.requestId -ne $request.requestId -or $reply.outcome -notin @('Accepted','Succeeded')) {
            throw "Host rejected ${capability}: $($reply.error)."
        }
        $reply.npep
    } finally { $stream.Dispose() }
}

function Wait-State([scriptblock]$accept) {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $state = Invoke-Pipe 'npep.status'
        if (-not $state.busy) {
            if (& $accept $state) { return $state }
            if ($state.error) { throw "Host: $($state.state) / $($state.error)." }
        }
        Start-Sleep -Milliseconds 350
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Host state timed out: $($state.state) / $($state.connection)."
}

function Send-Command($command) {
    $command.revision = (Invoke-Pipe 'npep.status').revision
    Invoke-Pipe 'npep.command' $command | Out-Null
}

function Write-Marker($name, $value) {
    $path = Join-Path $EvidenceDirectory ($name + '.json')
    [IO.File]::WriteAllText($path + '.tmp', ($value | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding($false)))
    [IO.File]::Move($path + '.tmp', $path)
}

switch ($Action) {
    'Begin' {
        if ($fixture.stage -notmatch '^pair-([01])$') { throw 'Wrong pairing stage.' }
        $index = [int]$Matches[1]
        Send-Command @{action='inspect'; origin=$fixture.origin}
        $verified = Wait-State { param($state) $state.connection -eq 'VERIFIED' }
        if ($verified.server.serverInstanceId -ne $fixture.serverInstanceId -or
            $verified.server.deploymentEpoch -ne $fixture.deploymentEpoch) { throw 'Fixture changed.' }
        Send-Command @{action='pair'; origin=$fixture.origin; serverInstanceId=$fixture.serverInstanceId;
            deploymentEpoch=$fixture.deploymentEpoch; deviceName=$fixture.names[$index]}
        $pending = Wait-State { param($state) $state.state -eq 'PENDING' -and $state.pairing.pairingId }
        Write-Marker ('pair-request-' + $index) @{pairingId=$pending.pairing.pairingId}
    }
    'Confirm' {
        if ($fixture.stage -notmatch '^finish-pair-([01])$') { throw 'Wrong approval stage.' }
        $index = [int]$Matches[1]
        if ((Invoke-Pipe 'npep.status').state -ne 'APPROVED') { Send-Command @{action='poll'} }
        $approved = Wait-State { param($state) $state.state -eq 'APPROVED' -and $state.approval.approvalId }
        if ($approved.approval.screenBindingId -ne $fixture.bindings[$index]) { throw 'The approval is for another device binding.' }
        Send-Command @{action='confirm'; approvalId=$approved.approval.approvalId}
        Wait-State { param($state) $state.state -eq 'ACTIVE' } | Out-Null
    }
    'Unpair' {
        if ($fixture.stage -notmatch '^unpair-([01])$') { throw 'Wrong unpair stage.' }
        $index = [int]$Matches[1]
        Send-Command @{action='unpair'}
        Wait-State { param($state) $state.state -eq 'UNPAIRED' } | Out-Null
        Write-Marker ('unpair-' + $index) @{unpaired=$true}
    }
    'Status' {
        $state = Invoke-Pipe 'npep.status'
        @{state=$state.state; connection=$state.connection; busy=$state.busy; error=$state.error} | ConvertTo-Json -Compress
    }
}
