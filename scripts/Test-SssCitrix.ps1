<#
.SYNOPSIS
  Read-only connectivity and schema check for SSS against a real Citrix Virtual Apps and Desktops site.

.DESCRIPTION
  Run this on the server that will host SSS (Windows PowerShell 5.1), ideally as the account of the SSS app pool
  (for example with: runas /user:DOMAIN\svc-sss "powershell -File Test-SssCitrix.ps1 -Controller ddc01 -UserName DOMAIN\alice").
  It changes nothing. It checks:
    1. Broker SDK is available and the controller answers (Get-BrokerSession for the given user)
    2. Monitor OData is reachable with the current Windows identity
    3. The properties SSS relies on exist in the OData $metadata (User.Sid, Session.*, SessionMetrics date/RTT field)
  Copy the output into a GitHub issue if something does not match. Add -Redact first: it replaces user, domain,
  host names, SIDs, IP addresses and GUIDs in the output (-Mask adds more words, -UseHttp selects http:// for the Monitor).

.EXAMPLE
  .\Test-SssCitrix.ps1 -Controller ddc01.corp.local -UserName CORP\alice

.NOTES
  -UserName must be written as DOMAIN\username. The user must have a session while the script runs
  to check the session properties and the live metrics.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Controller,
    [Parameter(Mandatory = $true)][string]$UserName,
    [switch]$UseHttp,
    [string]$Vda = '',
    [string]$JeaConfiguration = 'SSS.Processes',
    [switch]$Redact,
    [string[]]$Mask = @(),
    [string]$MonitorBaseUrl = $(if ($UseHttp) { "http://$Controller/Citrix/Monitor/OData/v4/Data/" } else { "https://$Controller/Citrix/Monitor/OData/v4/Data/" }),
    [string]$MetricsDateField = 'CollectedDate',
    [string]$IcaRttField = 'IcaRttMS'
)

if ($UserName -notmatch '^[^\\]+\\[^\\]+$') {
    throw "UserName must be in the format DOMAIN\username (for example CORP\alice), not '$UserName'."
}

$ErrorActionPreference = 'Continue'
$failed = 0

# -Redact: replaces user, domain, host names, SIDs and IP addresses in everything the script prints,
# so the output can be pasted into a public issue. Add further words with -Mask.
$script:maskTerms = @()
function Add-MaskTerm([string]$term, [string]$label) {
    if ($term -and $term.Length -ge 2) { $script:maskTerms += [pscustomobject]@{ Term = $term; Label = $label } }
}
function Hide-Sensitive([string]$text) {
    foreach ($m in ($script:maskTerms | Where-Object { $_.Label -eq '<domain>.local' })) {
        $text = [regex]::Replace($text, '(?<![\w.-])([\w-]+\.)*' + [regex]::Escape($m.Term) + '(?![\w])', '<host>.<domain>.local', 'IgnoreCase')
    }
    foreach ($m in ($script:maskTerms | Where-Object { $_.Label -ne '<domain>.local' } | Sort-Object { $_.Term.Length } -Descending)) {
        $text = [regex]::Replace($text, '(?<![\w])' + [regex]::Escape($m.Term) + '(?![\w])', $m.Label, 'IgnoreCase')
    }
    $text = [regex]::Replace($text, 'S-1-\d+(-\d+)+', '<sid>')
    $text = [regex]::Replace($text, '\b\d{1,3}(\.\d{1,3}){3}\b', '<ip>')
    $text = [regex]::Replace($text, '\b[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\b', '<guid>')
    $text
}
if ($Redact) {
    Add-MaskTerm $UserName '<domain>\<user>'
    Add-MaskTerm $Controller '<controller>'
    Add-MaskTerm ($UserName -split '\\')[0] '<domain>'
    Add-MaskTerm ($UserName -split '\\')[1] '<user>'
    if ($Controller -match '^[^.]+\.(.+)$') { Add-MaskTerm $Matches[1] '<domain>.local' }
    Add-MaskTerm $env:USERNAME '<user>'
    Add-MaskTerm $env:USERDOMAIN '<domain>'
    Add-MaskTerm $env:COMPUTERNAME '<server>'
    foreach ($d in @($env:USERDNSDOMAIN)) { if ($d) { Add-MaskTerm $d '<domain>.local' } }
    foreach ($t in $Mask) { Add-MaskTerm $t '<masked>' }
    function Write-Host {
        param([Parameter(Position = 0, ValueFromRemainingArguments = $true)]$Object, $ForegroundColor, $BackgroundColor, [switch]$NoNewline)
        $line = Hide-Sensitive ((@($Object) | ForEach-Object { "$_" }) -join ' ')
        $p = @{ Object = $line }
        if ($ForegroundColor) { $p.ForegroundColor = $ForegroundColor }
        if ($NoNewline) { $p.NoNewline = $true }
        Microsoft.PowerShell.Utility\Write-Host @p
    }
}
function Ok($m)   { Write-Host "[ OK ] $m" -ForegroundColor Green }
function Bad($m)  { Write-Host "[FAIL] $m" -ForegroundColor Red; $script:failed++ }
function Info($m) { Write-Host "[info] $m" }

Write-Host "== SSS Citrix check ==" -ForegroundColor Cyan
Info ("Running as {0}" -f [Security.Principal.WindowsIdentity]::GetCurrent().Name)
Info ("PowerShell {0}" -f $PSVersionTable.PSVersion)

# Monitor transport: https:// unless told otherwise; falls back to http:// when the controller serves the Monitor over http only.
if (-not $PSBoundParameters.ContainsKey('MonitorBaseUrl') -and -not $UseHttp) {
    try { $null = Invoke-WebRequest -Uri ($MonitorBaseUrl + '$metadata') -UseDefaultCredentials -UseBasicParsing -TimeoutSec 20 -ErrorAction Stop }
    catch {
        $httpUrl = $MonitorBaseUrl -replace '^https://', 'http://'
        try {
            $null = Invoke-WebRequest -Uri ($httpUrl + '$metadata') -UseDefaultCredentials -UseBasicParsing -TimeoutSec 20 -ErrorAction Stop
            Info 'Monitor does not answer over https:// but does over http://; checking over http:// (set Sss:Citrix:Monitor:UseHttps to false in sss.config.json, or enable HTTPS on the controller: docs/MULTI-SERVER.md)'
            $MonitorBaseUrl = $httpUrl
        } catch { }
    }
}

# 0. Resolve the user to a SID (exactly what SSS uses; accepts "alice" or "DOMAIN\alice") ---------
$sid = $null
try {
    $sid = (New-Object System.Security.Principal.NTAccount($UserName)).Translate([System.Security.Principal.SecurityIdentifier]).Value
    Ok ("Resolved '{0}' to a SID" -f $UserName)
} catch { Bad ("Could not resolve '{0}' to a SID: {1}" -f $UserName, $_.Exception.Message) }

# 1. Broker SDK -----------------------------------------------------------------------------------
try {
    Add-PSSnapin Citrix.Broker.Admin.V2 -ErrorAction Stop
    Ok 'Citrix.Broker.Admin.V2 snap-in loaded'
    if ($sid) {
        $sessions = @(Get-BrokerSession -AdminAddress $Controller -UserSID $sid -MaxRecordCount 50 -ErrorAction Stop)
        Ok ("Get-BrokerSession -UserSID returned {0} session(s)" -f $sessions.Count)
        if ($sessions.Count -gt 0) {
            $names = ($sessions[0] | Get-Member -MemberType Property | Select-Object -ExpandProperty Name)
            foreach ($p in 'Uid','UserSID','SessionState','SessionType','DesktopGroupName','DNSName','StartTime','IdleDuration','ClientName','ClientAddress','ApplicationsInUse','SessionKey') {
                if ($names -contains $p) { Ok "Session property $p" } else { Bad "Session property $p is missing (SSS will show it empty)" }
            }
            foreach ($s in $sessions) { Info ("Session: state={0} type={1} group={2} machine={3} apps={4}" -f $s.SessionState, $s.SessionType, $s.DesktopGroupName, $s.DNSName, (@($s.ApplicationsInUse) -join ',')) }
        } else {
            Info 'No session returned for this SID. If the user is logged on, check that -Controller is the right Delivery Controller / site.'
        }
    }
} catch { Bad ("Broker SDK: {0}" -f $_.Exception.Message) }

# 2./3. Monitor OData ----------------------------------------------------------------------------
function Get-Json($url) {
    $r = Invoke-WebRequest -Uri $url -UseDefaultCredentials -UseBasicParsing -Headers @{ Accept = 'application/json' } -ErrorAction Stop
    return $r.Content
}

try {
    $meta = Invoke-WebRequest -Uri ($MonitorBaseUrl + '$metadata') -UseDefaultCredentials -UseBasicParsing -ErrorAction Stop
    Ok ("Monitor OData reachable ({0} bytes of `$metadata)" -f $meta.Content.Length)
    $xml = [xml]$meta.Content
    $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace('edm', 'http://docs.oasis-open.org/odata/ns/edm')
    function Test-Prop($entity, $prop) {
        $node = $xml.SelectSingleNode("//edm:EntityType[@Name='$entity']/edm:Property[@Name='$prop']", $ns)
        if ($node) { Ok "$entity.$prop" } else { Bad "$entity.$prop not found in `$metadata" }
    }
    Test-Prop 'User' 'Sid'
    Test-Prop 'User' 'Id'
    foreach ($p in 'UserName','Upn','Domain') { Test-Prop 'User' $p }   # used by the helpdesk user search
    foreach ($p in 'SessionKey','UserId','StartDate','EndDate','LogOnDuration','SessionType') { Test-Prop 'Session' $p }
    Test-Prop 'Machine' 'DnsName'
    Test-Prop 'DesktopGroup' 'Name'
    Test-Prop 'Connection' 'ClientName'
    Test-Prop 'Connection' 'ClientAddress'
    Test-Prop 'SessionMetrics' $MetricsDateField
    Test-Prop 'SessionMetrics' $IcaRttField

    $metrics = $xml.SelectSingleNode("//edm:EntityType[@Name='SessionMetrics']", $ns)
    if ($metrics) {
        Info ('SessionMetrics properties: ' + (($metrics.Property | ForEach-Object { $_.Name }) -join ', '))
        Info 'If bandwidth fields are listed above, tell us their names and units so SSS can rate the connection bandwidth.'
    }

    # Optional parts of the expert view: they only need to exist, SSS reports missing ones as "not delivered"
    foreach ($set in 'Connections', 'ConnectionFailureLogs', 'ApplicationInstances') {
        $et = $xml.SelectSingleNode("//edm:EntityType[@Name='" + $set.TrimEnd('s') + "']", $ns)
        if (-not $et) { $et = $xml.SelectSingleNode("//edm:EntityType[@Name='" + $set + "']", $ns) }
        if ($et -and ($et.Property | Where-Object { $_.Name -eq 'SessionKey' })) {
            Ok ("Expert view: '{0}' available (has SessionKey)" -f $set)
        } else {
            Info ("Expert view: '{0}' not available or without SessionKey; the expert view omits this part." -f $set)
        }
    }
    $session = $xml.SelectSingleNode("//edm:EntityType[@Name='Session']", $ns)
    if ($session) { Info ('Session properties: ' + (($session.Property | ForEach-Object { $_.Name }) -join ', ')) }
} catch { Bad ("Monitor OData `$metadata: {0}" -f $_.Exception.Message) }

if ($sid) {
    try {
        $u = Get-Json ($MonitorBaseUrl + "Users?`$filter=Sid eq '$sid'&`$select=Id&`$top=1") | ConvertFrom-Json
        if ($u.value.Count -gt 0) {
            Ok 'User resolved by SID in Monitor'
            $id = $u.value[0].Id
            $q = "Sessions?`$filter=UserId eq $id and EndDate eq null" +
                 "&`$expand=Machine(`$select=DnsName;`$expand=DesktopGroup(`$select=Name)),CurrentConnection(`$select=ClientName,ClientAddress),SessionMetrics(`$orderby=$MetricsDateField desc;`$top=1)"
            $s = Get-Json ($MonitorBaseUrl + $q) | ConvertFrom-Json
            Ok ("Live-session query works, {0} row(s)" -f $s.value.Count)
            foreach ($row in $s.value) {
                Info ("Live session in Monitor: connection state {0}, logon duration {1} ms, {2} metric sample(s) returned" -f $row.ConnectionState, $row.LogOnDuration, @($row.SessionMetrics).Count)
                $sample = @($row.SessionMetrics) | Select-Object -First 1
                if ($sample) {
                    Info 'Latest SessionMetrics sample (numbers only, please paste this into the issue; units are what we need to learn):'
                    foreach ($p in $sample.PSObject.Properties) {
                        if ($p.Name -notmatch '(^Id$|Id$|Date$)') { Info ("    {0} = {1}" -f $p.Name, $p.Value) }
                    }
                } else {
                    Info '    No SessionMetrics sample yet (detail data can lag a few minutes behind a fresh logon).'
                }
            }
            $since = (Get-Date).ToUniversalTime().AddDays(-7).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
            $h = Get-Json ($MonitorBaseUrl + "Sessions?`$filter=UserId eq $id and EndDate ge $since&`$top=5&`$orderby=EndDate desc") | ConvertFrom-Json
            Ok ("History query works, {0} row(s) in the last 7 days" -f $h.value.Count)
        } else {
            Bad 'User not found by SID in Monitor (never logged on, or User.Sid has another name/format)'
        }
    } catch { Bad ("Monitor queries: {0}" -f $_.Exception.Message) }
} else {
    Info 'Skipping Monitor user queries because the user could not be resolved to a SID.'
}

# 4. Process control on a VDA (optional): the JEA endpoint from Install-SssJea.ps1 ---------------------------
if ($Vda) {
    Write-Host ''
    Write-Host "== Process control on $Vda ==" -ForegroundColor Cyan
    if ($Vda -notmatch '^[A-Za-z0-9._-]{1,253}$') { Bad "-Vda must be a host name, not '$Vda'" }
    elseif (-not $sid) { Bad 'Process control needs the user SID (see the check above)' }
    else {
        $session = $null
        try {
            $session = New-PSSession -ComputerName $Vda -ConfigurationName $JeaConfiguration -ErrorAction Stop
            Ok "Connected to the endpoint '$JeaConfiguration' on $Vda as $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"
            Import-PSSession -Session $session -CommandName 'Get-SssProcess', 'Stop-SssProcess' -AllowClobber -DisableNameChecking -ErrorAction Stop | Out-Null
            Ok 'Get-SssProcess and Stop-SssProcess are offered'
            $procs = @(Get-SssProcess -UserSid $sid)
            Ok ("Get-SssProcess returned {0} process(es) of the user" -f $procs.Count)
            foreach ($p in ($procs | Where-Object { $_.Window } | Select-Object -First 5)) { Info ("Application: {0} (PID {1}) '{2}'" -f $p.Name, $p.Id, $p.Window) }
            $other = Stop-SssProcess -UserSid $sid -ProcessId 2147483647
            if ($other -eq 'not-found' -or $other -eq 'not-owned') { Ok "Safety check: a process id that does not exist is refused ($other)" }
            else { Bad "Safety check failed: Stop-SssProcess answered '$other' for a process id that does not exist" }
        } catch { Bad ("JEA endpoint: {0}" -f $_.Exception.Message) }
        finally { if ($session) { Remove-PSSession $session -ErrorAction SilentlyContinue } }
        Info 'Nothing was ended. Test ending with a harmless process (for example notepad) in the web interface.'
    }
}

Write-Host ''
if ($failed -eq 0) { Write-Host 'All checks passed.' -ForegroundColor Green } else { Write-Host ("{0} check(s) failed." -f $failed) -ForegroundColor Yellow }
exit $failed
