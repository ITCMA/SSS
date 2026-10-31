<#
  SSS broker worker: a long-running Windows PowerShell 5.1 process that talks to the Citrix Broker SDK.
  Protocol: one JSON request per line on stdin, one JSON response per line on stdout.
  Only a few operations exist (ping, list, listall, logoff, message) and every value is validated before it reaches a cmdlet.
  Keep this file compatible with Windows PowerShell 5.1 (no ternary, no ?? operators).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Controllers,
    [string]$SnapinOverride = ''
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
            default { throw 'Unknown operation.' }
        }
    } catch {
        # the identity helps to diagnose missing Citrix rights; the message only goes to the server log
        $who = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        Send @{ id = $id; ok = $false; error = ('[running as {0}] {1}' -f $who, $_.Exception.Message) }
    }
}
