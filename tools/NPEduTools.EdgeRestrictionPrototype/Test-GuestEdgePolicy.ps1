# Run with Windows PowerShell 5.1 inside the authorized guest.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$AppDirectory = (Join-Path $PSScriptRoot 'app'),
    [switch]$MachinePolicy,
    [switch]$DiagnosticAccessibility,
    [switch]$LaunchUrlDirectly,
    [switch]$ExternalPolicyHandshake,
    [switch]$ControlOnly
)
$ErrorActionPreference = 'Stop'
if ($MachinePolicy -and $ExternalPolicyHandshake) { throw 'Select one policy owner.' }
$guest = Get-CimInstance Win32_ComputerSystem
$os = Get-CimInstance Win32_OperatingSystem
if ($guest.Model -notmatch 'VMware|Virtual Machine|VirtualBox' -or $os.Caption -notmatch 'Windows 10' -or
    $os.OSArchitecture -notmatch '64' -or -not [Environment]::UserInteractive -or (Get-Process -Id $PID).SessionId -eq 0) {
    throw 'This runner requires a signed-in interactive Windows 10 x64 VM.'
}
$program = (Resolve-Path -LiteralPath (Join-Path $AppDirectory 'NPEduTools.EdgeRestrictionPrototype.exe')).Path
$edge = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
if (-not (Test-Path -LiteralPath $edge -PathType Leaf)) {
    $edge = Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe'
}
if (-not (Test-Path -LiteralPath $edge -PathType Leaf)) { throw 'Edge Stable is not installed.' }
$runId = [Guid]::NewGuid().ToString('N')
$out = Join-Path ([IO.Path]::GetFullPath($Output)) "edge-policy-$runId"
[void](New-Item -ItemType Directory -Path $out)
$testPath = "/__npeptest_$runId"
$douyin = "https://douyin.com$testPath"
$watchedSite = 'https://douyin.com/'
$portFile = Join-Path $out 'loopback-port.txt'
$traffic = Join-Path $out 'loopback-requests.txt'
$policyPath = 'Software\Policies\Microsoft\Edge\URLBlocklist'
$hive = [Microsoft.Win32.Registry]::CurrentUser
if ($MachinePolicy) { $hive = [Microsoft.Win32.Registry]::LocalMachine }
$results = [ordered]@{ windows=$os.Caption; build=$os.BuildNumber; guest=$guest.Model
    edge=(Get-Item -LiteralPath $edge).VersionInfo.ProductVersion; policyHive=$hive.Name
    diagnosticAccessibility=[bool]$DiagnosticAccessibility; launchUrlDirectly=[bool]$LaunchUrlDirectly
    controlOnly=[bool]$ControlOnly; policyApplied=$false; target=$douyin; watchedSite=$watchedSite }
$policyValues = [ordered]@{}
$job = $policy = $null
$hasPolicyKey = $policyRestored = $false
$externalRequested = $externalReady = $false
Write-Host "Evidence: $out"

function Write-Signal([string]$Name, $Value) {
    $target = Join-Path $out $Name
    $temp = $target + '.tmp'
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temp -Encoding UTF8
    Move-Item -LiteralPath $temp -Destination $target
}

function Wait-Signal([string]$Name, [int]$Seconds) {
    $signalPath = Join-Path $out $Name
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt $Seconds) {
        if (Test-Path -LiteralPath $signalPath -PathType Leaf) {
            $signal = Get-Content -LiteralPath $signalPath -Raw | ConvertFrom-Json
            if ($signal.runId -cne $runId) { throw "Wrong run ID in $Name." }
            return $signal
        }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for $Name."
}

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class GuestEdgeProof {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int process);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public static int ForegroundProcess() {
        int id;
        GetWindowThreadProcessId(GetForegroundWindow(), out id);
        return id;
    }
}
'@

function Save-TestBrowser([int]$BrowserId, [string]$Path) {
    if ([GuestEdgeProof]::ForegroundProcess() -ne $BrowserId) { return $false }
    $previousDpi = [GuestEdgeProof]::SetThreadDpiAwarenessContext([IntPtr](-4))
    if ($previousDpi -eq [IntPtr]::Zero) { return $false }
    try { Save-TestBrowserPixels $BrowserId $Path }
    finally { [void][GuestEdgeProof]::SetThreadDpiAwarenessContext($previousDpi) }
}

function Save-TestBrowserPixels([int]$BrowserId, [string]$Path) {
    if ([GuestEdgeProof]::ForegroundProcess() -ne $BrowserId) { return $false }
    $window = [GuestEdgeProof]::GetForegroundWindow()
    $rect = New-Object 'GuestEdgeProof+RECT'
    if (-not [GuestEdgeProof]::GetWindowRect($window, [ref]$rect)) { return $false }
    $bitmap = New-Object Drawing.Bitmap (($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $dc = $graphics.GetHdc()
        try { $printed = [GuestEdgeProof]::PrintWindow($window, $dc, 2) }
        finally { $graphics.ReleaseHdc($dc) }
        if (-not $printed) { return $false }
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
        return $true
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}

function Start-Case([string]$Name, [string]$Page, [string]$Expected, [int]$Seconds) {
    $case = Join-Path $out $Name
    [void](New-Item -ItemType Directory -Path $case)
    $profile = Join-Path $case 'edge-profile'
    $edgeArguments = @(('--user-data-dir="{0}"' -f $profile), '--guest', '--disable-sync', '--disable-extensions',
        '--no-first-run', '--no-default-browser-check', '--disable-background-networking')
    if ($DiagnosticAccessibility) { $edgeArguments += '--force-renderer-accessibility' }
    $initialPage = 'about:blank'
    if ($LaunchUrlDirectly) { $initialPage = $Page }
    $launchEdge = New-Object Diagnostics.ProcessStartInfo
    $launchEdge.FileName = $edge
    $launchEdge.Arguments = ($edgeArguments + ('"{0}"' -f $initialPage)) -join ' '
    $launchEdge.UseShellExecute = $false
    Write-Host "$Name launching owned Edge"
    $started = [Diagnostics.Process]::Start($launchEdge)
    Write-Host "$Name Edge launcher PID $($started.Id)"
    $browser = $null
    try {
        $waiting = [Diagnostics.Stopwatch]::StartNew()
        while (-not $browser -and $waiting.Elapsed.TotalSeconds -lt 16) {
            $started.Refresh()
            if (-not $started.HasExited -and $started.MainWindowHandle -ne [IntPtr]::Zero) {
                $browser = $started
                break
            }
            $entry = Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -OperationTimeoutSec 3 |
                Where-Object { $_.CommandLine -and $_.CommandLine.Contains($profile) -and $_.CommandLine -notmatch '--type=' } |
                Select-Object -First 1
            if ($entry) {
                $candidate = Get-Process -Id $entry.ProcessId -ErrorAction SilentlyContinue
                if ($candidate -and $candidate.MainWindowHandle -ne [IntPtr]::Zero) { $browser = $candidate }
            }
            if (-not $browser) { Start-Sleep -Milliseconds 300 }
        }
        if (-not $browser) {
            $exit = 'still running'
            if ($started.HasExited) { $exit = "exit $($started.ExitCode)" }
            throw "No window for $Name (launcher $exit)."
        }
        Write-Host "$Name owned Edge window PID $($browser.Id)"
        $keyboard = New-Object -ComObject WScript.Shell
        [void]$keyboard.AppActivate($browser.Id)
        Start-Sleep -Milliseconds 400
        if ([GuestEdgeProof]::ForegroundProcess() -ne $browser.Id) { throw "Owned Edge is not foreground for $Name." }
        $probeArgs = ('--probe-foreground {0} "{1}" "{2}" {3}' -f $browser.Id, $Expected, $case, $Seconds)
        $launchProbe = New-Object Diagnostics.ProcessStartInfo
        $launchProbe.FileName = $program
        $launchProbe.Arguments = $probeArgs
        $launchProbe.UseShellExecute = $false
        $launchProbe.CreateNoWindow = $true
        $launchProbe.RedirectStandardOutput = $true
        $launchProbe.RedirectStandardError = $true
        $probe = [Diagnostics.Process]::Start($launchProbe)
        $probeOutput = $probe.StandardOutput.ReadToEndAsync()
        $probeErrors = $probe.StandardError.ReadToEndAsync()
        Start-Sleep -Milliseconds 700
        if ([GuestEdgeProof]::ForegroundProcess() -ne $browser.Id) { throw "Focus changed before $Name navigation." }
        if (-not $LaunchUrlDirectly) {
            $keyboard.SendKeys('^l')
            $keyboard.SendKeys($Page)
            $keyboard.SendKeys('{ENTER}')
        }
        Write-Host "$Name waiting for observer PID $($probe.Id)"
        if (-not $probe.WaitForExit(($Seconds + 38) * 1000)) { throw "Observer timed out in $Name." }
        [IO.File]::WriteAllText((Join-Path $case 'observer.stdout.txt'), $probeOutput.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $case 'observer.stderr.txt'), $probeErrors.GetAwaiter().GetResult())
        if ($probe.ExitCode -ne 0) { throw "Observer exited $($probe.ExitCode) in $Name." }
        Write-Host "$Name observer exited 0"
        $proof = Get-Content -LiteralPath (Join-Path $case 'foreground-uia.json') -Raw | ConvertFrom-Json
        $snapshot = Save-TestBrowser $browser.Id (Join-Path $case 'edge-window.png')
        if (Test-Path -LiteralPath (Join-Path $profile 'DevToolsActivePort')) { throw 'Unexpected debugging port.' }
        $browser.Refresh()
        return [pscustomobject]@{ observer=$proof; nativeImage=$snapshot; browserTitle=$browser.MainWindowTitle }
    } finally {
        if ($browser -and -not $browser.HasExited) {
            [void]$browser.CloseMainWindow()
            [void]$browser.WaitForExit(4000)
        }
    }
}

try {
    $job = Start-Job -ArgumentList $portFile, $traffic, $testPath -ScriptBlock {
        param($portFile, $traffic, $blocked)
        $listener = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::Loopback), 0
        $listener.Start()
        $crlf = [string][char]13 + [char]10
        try {
            [IO.File]::WriteAllText($portFile, [string]$listener.LocalEndpoint.Port)
            while ($true) {
                if (-not $listener.Pending()) {
                    Start-Sleep -Milliseconds 50
                    continue
                }
                $client = $listener.AcceptTcpClient()
                try {
                    $client.ReceiveTimeout = 2000
                    $stream = $client.GetStream()
                    $reader = New-Object IO.StreamReader $stream
                    $line = $reader.ReadLine()
                    if (-not $line -or $line -notmatch '^GET (/\S*) HTTP/') { continue }
                    $requestPath = $Matches[1]
                    [IO.File]::AppendAllText($traffic, $requestPath + $crlf)
                    $body = '<!doctype html><meta charset="utf-8"><title>Loopback allowed</title><h1>&#24050;&#38459;&#27490;&#27492;&#39029;&#38754;</h1><p>&#20320;&#30340;&#32452;&#32455;&#19981;&#20801;&#35768;&#20320;&#26597;&#30475;&#27492;&#32593;&#31449;</p><p>Loopback allowed fixture</p>'
                    if ($requestPath -eq '/lesson') {
                        $body = @"
<!doctype html><title>Classroom policy test</title><h1>Classroom policy test</h1>
<p>Lesson remains visible.</p><iframe title="allowed control" width="450" height="140" src="/iframe-control"></iframe>
<iframe title="policy target" width="450" height="220" src="$blocked"></iframe>
<script>setTimeout(() => fetch('/lesson-still-visible-' + document.visibilityState), 2000);</script>
"@
                    }
                    $bytes = [Text.Encoding]::UTF8.GetBytes($body)
                    $header = [Text.Encoding]::ASCII.GetBytes((@('HTTP/1.1 200 OK', 'Content-Type: text/html; charset=utf-8',
                        "Content-Length: $($bytes.Length)", 'Cache-Control: no-store', 'Connection: close', '', '') -join $crlf))
                    $stream.Write($header, 0, $header.Length)
                    $stream.Write($bytes, 0, $bytes.Length)
                } catch [IO.IOException] {
                    # Browser probes can close before sending a request.
                } finally { $client.Dispose() }
            }
        } finally { $listener.Stop() }
    }
    for ($i = 0; $i -lt 40 -and -not (Test-Path -LiteralPath $portFile); $i++) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-Path -LiteralPath $portFile)) { throw 'Loopback test server did not start.' }
    $port = [int](Get-Content -LiteralPath $portFile)
    $base = "http://127.0.0.1:$port"
    $localTarget = "$base$testPath"
    $results.control = Start-Case 'allowed-before-policy' $localTarget $localTarget 5
    if (-not (Test-Path -LiteralPath $traffic)) { throw 'Allowed control sent no local HTTP requests.' }
    $before = @(Get-Content -LiteralPath $traffic | Where-Object { $_ -eq $testPath }).Count
    if ($before -lt 1) { throw 'Allowed target did not reach the local server.' }
    $results.loopbackDirectHitsBeforePolicy = $before
    if ($ControlOnly) { return }
    $filters = @("https://.douyin.com$testPath", "http://127.0.0.1:$port$testPath")
    $results.policyPatterns = $filters
    if ($ExternalPolicyHandshake) {
        $results.policyHive = 'HKEY_LOCAL_MACHINE (external test operator)'
        Write-Signal 'policy-request.json' @{ runId=$runId; policyHive='HKEY_LOCAL_MACHINE'; patterns=$filters; loopbackPort=$port }
        $externalRequested = $true
        Write-Host 'Waiting for the guest policy operator.'
        $ready = Wait-Signal 'policy-ready.json' 105
        if ($ready.ready -ne $true) { throw 'The guest policy operator declined or failed to apply the policy.' }
        $externalReady = $true
    } else {
        $existing = $hive.OpenSubKey($policyPath)
        $hasPolicyKey = $null -ne $existing
        if ($existing) { $existing.Dispose() }
        $policy = $hive.CreateSubKey($policyPath)
        if (-not $policy) { throw "Cannot write $($hive.Name) Edge policies under guest permissions." }
        $slot = 1
        foreach ($filter in $filters) {
            while ($policy.GetValueNames() -contains [string]$slot) { $slot++ }
            $name = [string]$slot
            $policy.SetValue($name, $filter, [Microsoft.Win32.RegistryValueKind]::String)
            $policyValues[$name] = $filter
        }
        $results.policyNames = @($policyValues.Keys)
    }
    $results.policyApplied = $true
    $results.classroom = Start-Case 'classroom-iframe' "$base/lesson" $localTarget 12
    $results.localDirect = Start-Case 'blocked-loopback-direct' $localTarget $watchedSite 11
    $results.foreground = Start-Case 'blocked-douyin-direct' $douyin $watchedSite 40
    $seen = @(Get-Content -LiteralPath $traffic)
    $results.loopbackPolicyHits = @($seen | Where-Object { $_ -eq $testPath }).Count - $before
    $results.allowedIframeReached = $seen -contains '/iframe-control'
    $results.classroomStayedVisible = $seen -contains '/lesson-still-visible-visible'
} catch {
    $results.error = $_.Exception.Message
    throw
} finally {
    try {
        if ($policy) {
            $allOwned = $true
            foreach ($name in $policyValues.Keys) {
                if ($policy.GetValue($name) -ceq $policyValues[$name]) { $policy.DeleteValue($name, $false) }
                else { $allOwned = $false }
            }
            $empty = $policy.ValueCount -eq 0 -and $policy.SubKeyCount -eq 0
            $policy.Dispose()
            if (-not $hasPolicyKey -and $empty) { $hive.DeleteSubKey($policyPath, $false) }
            $policyRestored = $allOwned
        }
        if ($externalRequested) {
            Write-Signal 'policy-finished.json' @{ runId=$runId; finished=$true }
            if ($externalReady) {
                $restored = Wait-Signal 'policy-restored.json' 35
                $policyRestored = $restored.restored -eq $true
            }
        }
    } finally {
        if ($job) { Stop-Job $job; Remove-Job $job }
        $results.policyRestored = $policyRestored
        $results | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $out 'result.json') -Encoding UTF8
    }
}
