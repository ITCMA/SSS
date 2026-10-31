<#
  SSS broker worker: a long-running Windows PowerShell 5.1 process that talks to the Citrix Broker SDK.
  Protocol: one JSON request per line on stdin, one JSON response per line on stdout.
  Only a few operations exist (ping, list, listall, logoff, message, processes, endprocess, profile) and every value is validated before it reaches a cmdlet.
  Keep this file compatible with Windows PowerShell 5.1 (no ternary, no ?? operators).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Controllers,
    [string]$SnapinOverride = '',
    [string]$JeaConfiguration = ''
)

$ErrorActionPreference = 'Stop'
try {
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [Console]::InputEncoding = $utf8
    [Console]::OutputEncoding = $utf8
} catch { }

$ControllerList = @($Controllers -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^[A-Za-z0-9._-]{1,253}$' })
if ($ControllerList.Count -eq 0) { throw 'No valid controller names given.' }

if ($SnapinOverride) { . $SnapinOverride } else { Add-PSSnapin Citrix.Broker.Admin.V2 }

$SidPattern = '^S-1-\d+(-\d+)+$'
$UidPattern = '^\d{1,18}$'
$QueryPattern = '^[\p{L}\p{N} ._@\\-]{0,64}$'
$script:Active = $ControllerList[0]

function Send($obj) {
    $json = ConvertTo-Json -InputObject $obj -Compress -Depth 6
    # ASCII-only output keeps the stream independent of console code pages
    $json = [regex]::Replace($json, '[^\x00-\x7F]', { param($m) '\u{0:x4}' -f [int][char]$m.Value })
    [Console]::Out.WriteLine($json)
    [Console]::Out.Flush()
}

function Invoke-Broker([scriptblock]$Action) {
    $order = @($script:Active) + @($ControllerList | Where-Object { $_ -ne $script:Active })
    $last = $null
    foreach ($c in $order) {
        try {
            $r = & $Action $c
            $script:Active = $c
            return ,$r
        } catch { $last = $_ }
    }
    throw $last
}

function Get-Utc($d) {
    if ($d) { return ([datetime]$d).ToUniversalTime().ToString('o') }
    return $null
}

function Convert-Session($s) {
    $apps = @()
    if ($s.ApplicationsInUse) { $apps = @($s.ApplicationsInUse | ForEach-Object { [string]$_ }) }
    $idle = $null
    if ($s.IdleDuration) { $idle = [int][math]::Floor(([timespan]$s.IdleDuration).TotalSeconds) }
    $machine = [string]$s.DNSName
    if (-not $machine) { $machine = [string]$s.MachineName }
    $key = $null
    if ($s.SessionKey) { $key = [string]$s.SessionKey }
    return @{
        uid           = [string]$s.Uid
        sessionKey    = $key
        state         = [string]$s.SessionState
        type          = [string]$s.SessionType
        desktopGroup  = [string]$s.DesktopGroupName
        machine       = $machine
        startTime     = Get-Utc $s.StartTime
        idleSeconds   = $idle
        clientName    = [string]$s.ClientName
        clientAddress = [string]$s.ClientAddress
        apps          = $apps
        userName      = [string]$s.UserName
        userSid       = [string]$s.UserSID
    }
}

function Get-UserSessions([string]$sid) {
    $raw = Invoke-Broker { param($c) @(Get-BrokerSession -AdminAddress $c -UserSID $sid -MaxRecordCount 500) }
    return @($raw | Where-Object { $_ -and $_.UserSID -eq $sid } | ForEach-Object { Convert-Session $_ })
}

function Stop-UserSessions([string]$sid, $uids) {
    $accepted = @()
    $rejected = @()
    foreach ($uid in $uids) {
        $u = [string]$uid
        if ($u -notmatch $UidPattern) { $rejected += $u; continue }
        $session = Invoke-Broker { param($c) Get-BrokerSession -AdminAddress $c -Uid ([int64]$u) -ErrorAction SilentlyContinue }
        $session = @($session) | Select-Object -First 1
        # Second ownership check, independent of the web application
        if ($session -and $session.UserSID -eq $sid) {
            Invoke-Broker { param($c) Stop-BrokerSession -AdminAddress $c -InputObject $session } | Out-Null
            $accepted += $u
        } else {
            $rejected += $u
        }
    }
    return @{ accepted = @($accepted); rejected = @($rejected) }
}

function Send-UserMessage([string]$sid, $uids, [string]$title, [string]$text) {
    $accepted = @()
    foreach ($uid in $uids) {
        $u = [string]$uid
        if ($u -notmatch $UidPattern) { continue }
        $session = Invoke-Broker { param($c) Get-BrokerSession -AdminAddress $c -Uid ([int64]$u) -ErrorAction SilentlyContinue }
        $session = @($session) | Select-Object -First 1
        # same independent ownership check as for log-off
        if ($session -and $session.UserSID -eq $sid) {
            Invoke-Broker { param($c) Send-BrokerSessionMessage -AdminAddress $c -InputObject $session -MessageStyle Information -Title $title -Text $text } | Out-Null
            $accepted += $u
        }
    }
    return @{ accepted = @($accepted) }
}

# Processes of a session: the VDA offers a JEA endpoint with two commands (scripts/Install-SssJea.ps1). The session is looked up
# again here and must belong to the SID (same independent ownership check as for log-off); only the machine of that session is contacted.
function Get-OwnedSession([string]$sid, [string]$uid) {
    if ($uid -notmatch $UidPattern) { throw 'Invalid session id.' }
    if (-not $JeaConfiguration) { throw 'Process control is not enabled (Sss:Processes:Enabled).' }
    $session = Invoke-Broker { param($c) Get-BrokerSession -AdminAddress $c -Uid ([int64]$uid) -ErrorAction SilentlyContinue }
    $session = @($session) | Select-Object -First 1
    if (-not $session -or $session.UserSID -ne $sid) { throw 'Session not found.' }
    $machine = [string]$session.DNSName
    if ($machine -notmatch '^[A-Za-z0-9._-]{1,253}$') { throw 'The session has no usable machine name.' }
    return $machine
}

function Use-Jea([string]$machine, [string[]]$Commands, [scriptblock]$Action) {
    $s = New-PSSession -ComputerName $machine -ConfigurationName $JeaConfiguration -ErrorAction Stop
    try {
        Import-PSSession -Session $s -CommandName $Commands -AllowClobber -DisableNameChecking -ErrorAction Stop | Out-Null
        return (& $Action)
    } finally { Remove-PSSession $s -ErrorAction SilentlyContinue }
}

function Get-SessionProcesses([string]$sid, [string]$uid) {
    $machine = Get-OwnedSession $sid $uid
    $list = Use-Jea $machine @('Get-SssProcess') { @(Get-SssProcess -UserSid $sid) }
    $rows = @($list | Where-Object { $_ } | ForEach-Object {
        @{ id = [int]$_.Id; name = [string]$_.Name; sessionId = [int]$_.SessionId; started = [string]$_.Started; memoryMb = [int]$_.MemoryMb; window = [string]$_.Window }
    })
    return @{ machine = $machine; processes = $rows }
}

function Get-SessionProfile([string]$sid, [string]$uid, [int]$scanSeconds) {
    $machine = Get-OwnedSession $sid $uid
    $p = Use-Jea $machine @('Get-SssProfile') { Get-SssProfile -UserSid $sid -ScanSeconds $scanSeconds }
    $top = @(@($p.Top) | Where-Object { $_ } | ForEach-Object { @{ name = [string]$_.Name; mb = [int]$_.Mb } })
    return @{ machine = $machine; type = [string]$p.Type; loaded = [bool]$p.Loaded; localPath = [string]$p.LocalPath; storePath = [string]$p.StorePath
        container = [string]$p.Container; containerMb = $p.ContainerMb; roaming = [bool]$p.Roaming; temporary = [bool]$p.Temporary; mandatory = [bool]$p.Mandatory
        sizeMb = $p.SizeMb; sizePartial = [bool]$p.SizePartial; scanSeconds = [int]$p.ScanSeconds; notes = @(@($p.Notes) | Where-Object { $_ } | ForEach-Object { [string]$_ }); top = $top }
}

function Stop-SessionProcess([string]$sid, [string]$uid, [int]$procId, [bool]$force) {
    $machine = Get-OwnedSession $sid $uid
    $result = Use-Jea $machine @('Stop-SssProcess') {
        if ($force) { Stop-SssProcess -UserSid $sid -ProcessId $procId -Force } else { Stop-SssProcess -UserSid $sid -ProcessId $procId }
    }
    return [string]@($result)[0]
}

while ($true) {
    $line = [Console]::In.ReadLine()
    if ($null -eq $line) { break }
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $id = $null
    try {
        $req = $line | ConvertFrom-Json
        $id = $req.id
        switch ([string]$req.op) {
            'ping' {
                Send @{ id = $id; ok = $true }
            }
            'list' {
                $sid = [string]$req.sid
                if ($sid -notmatch $SidPattern) { throw 'Invalid SID.' }
                Send @{ id = $id; ok = $true; sessions = @(Get-UserSessions $sid) }
            }
            'listall' {
                # helpdesk overview: every session of the farm, optionally filtered by a part of the user name
                $q = [string]$req.q
                if ($q -notmatch $QueryPattern) { throw 'Invalid filter.' }
                $max = [int]$req.max
                if ($max -lt 1 -or $max -gt 2000) { throw 'Invalid maximum.' }
                $raw = Invoke-Broker { param($c) @(Get-BrokerSession -AdminAddress $c -MaxRecordCount ($max + 1)) }
                $truncated = ($raw.Count -gt $max)
                $rows = @($raw | Where-Object { $_ })
                if ($q) { $rows = @($rows | Where-Object { ([string]$_.UserName).IndexOf($q, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 }) }
                Send @{ id = $id; ok = $true; truncated = $truncated; sessions = @($rows | Select-Object -First $max | ForEach-Object { Convert-Session $_ }) }
            }
            'logoff' {
                $sid = [string]$req.sid
                if ($sid -notmatch $SidPattern) { throw 'Invalid SID.' }
                $res = Stop-UserSessions $sid @($req.uids)
                Send @{ id = $id; ok = $true; accepted = $res.accepted; rejected = $res.rejected }
            }
            'message' {
                $sid = [string]$req.sid
                if ($sid -notmatch $SidPattern) { throw 'Invalid SID.' }
                $title = [string]$req.title
                $text = [string]$req.text
                if ($title -notmatch '^[^\p{Cc}]{1,300}$' -or $text -notmatch '^[^\p{Cc}]{1,300}$') { throw 'Invalid message.' }
                $res = Send-UserMessage $sid @($req.uids) $title $text
                Send @{ id = $id; ok = $true; accepted = $res.accepted }
            }
            'processes' {
                $sid = [string]$req.sid
                if ($sid -notmatch $SidPattern) { throw 'Invalid SID.' }
                $res = Get-SessionProcesses $sid ([string]$req.uid)
                Send @{ id = $id; ok = $true; machine = $res.machine; processes = @($res.processes) }
            }
            'profile' {
                $sid = [string]$req.sid
                if ($sid -notmatch $SidPattern) { throw 'Invalid SID.' }
                $scan = [int]$req.scanSeconds
                if ($scan -lt 0 -or $scan -gt 120) { throw 'Invalid scan time.' }
                $res = Get-SessionProfile $sid ([string]$req.uid) $scan
                $res.id = $id; $res.ok = $true
                Send $res
            }
            'endprocess' {
                $sid = [string]$req.sid
                if ($sid -notmatch $SidPattern) { throw 'Invalid SID.' }
                $procId = [int]$req.pid
                if ($procId -lt 1) { throw 'Invalid process id.' }
                Send @{ id = $id; ok = $true; result = (Stop-SessionProcess $sid ([string]$req.uid) $procId ([bool]$req.force)) }
            }
            default { throw 'Unknown operation.' }
        }
    } catch {
        # the identity helps to diagnose missing Citrix rights; the message only goes to the server log
        $who = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        Send @{ id = $id; ok = $false; error = ('[running as {0}] {1}' -f $who, $_.Exception.Message) }
    }
}
