# Fake Citrix Broker cmdlets for testing sss-broker-worker.ps1 without a Delivery Controller.
$script:FakeSessions = @(
    [pscustomobject]@{ Uid = 101; SessionKey = [guid]'11111111-1111-1111-1111-111111111111'; UserSID = 'S-1-5-21-1-1-1-1001'; UserName = 'DEMO\alice'
        SessionState = 'Active'; SessionType = 'Desktop'; DesktopGroupName = 'DG-Desktops'; DNSName = 'vdi-01.demo.local'; MachineName = 'DEMO\VDI-01'
        StartTime = [datetime]'2026-10-29T08:00:00Z'; IdleDuration = [timespan]::FromSeconds(90); ClientName = 'LAPTOP-A'; ClientAddress = '10.0.0.5'; ApplicationsInUse = @() },
    [pscustomobject]@{ Uid = 102; SessionKey = $null; UserSID = 'S-1-5-21-1-1-1-1001'; UserName = 'DEMO\alice'
        SessionState = 'Disconnected'; SessionType = 'Application'; DesktopGroupName = 'DG-Apps'; DNSName = 'rds-01.demo.local'; MachineName = 'DEMO\RDS-01'
        StartTime = [datetime]'2026-10-28T08:00:00Z'; IdleDuration = $null; ClientName = 'Zürich-PC'; ClientAddress = '10.0.0.5'; ApplicationsInUse = @('Excel', 'Word') },
    [pscustomobject]@{ Uid = 201; SessionKey = $null; UserSID = 'S-1-5-21-1-1-1-2002'; UserName = 'DEMO\bob'
        SessionState = 'Active'; SessionType = 'Desktop'; DesktopGroupName = 'DG-Desktops'; DNSName = 'vdi-02.demo.local'; MachineName = 'DEMO\VDI-02'
        StartTime = [datetime]'2026-10-29T09:00:00Z'; IdleDuration = $null; ClientName = 'BOB-PC'; ClientAddress = '10.0.0.6'; ApplicationsInUse = @() }
)
$script:Stopped = New-Object System.Collections.ArrayList

function Get-BrokerSession {
    [CmdletBinding()]
    param([string]$AdminAddress, [string]$UserSID, [int64]$Uid, [int]$MaxRecordCount)
    $r = $script:FakeSessions
    if ($UserSID) { $r = @($r | Where-Object { $_.UserSID -eq $UserSID }) }
    if ($Uid) { $r = @($r | Where-Object { $_.Uid -eq $Uid }) }
    $r
}

function Stop-BrokerSession {
    [CmdletBinding()]
    param([string]$AdminAddress, [Parameter(Mandatory)]$InputObject)
    [void]$script:Stopped.Add([int64]$InputObject.Uid)
}

function Send-BrokerSessionMessage {
    [CmdletBinding()]
    param([string]$AdminAddress, [Parameter(Mandatory)]$InputObject, [string]$MessageStyle, [string]$Title, [string]$Text)
    [void]$script:Messaged.Add(("{0}|{1}|{2}" -f [int64]$InputObject.Uid, $Title, $Text))
}
$script:Messaged = New-Object System.Collections.ArrayList

# JEA endpoint on a VDA: the fakes remember which machine was contacted.
$script:JeaMachines = New-Object System.Collections.ArrayList
function New-PSSession {
    [CmdletBinding()]
    param([string]$ComputerName, [string]$ConfigurationName)
    [void]$script:JeaMachines.Add("$ComputerName|$ConfigurationName")
    [pscustomobject]@{ Machine = $ComputerName }
}
function Import-PSSession { [CmdletBinding()] param($Session, [string[]]$CommandName, [switch]$AllowClobber, [switch]$DisableNameChecking) }
function Remove-PSSession { [CmdletBinding()] param($Session) }
function Get-SssProcess {
    [CmdletBinding()]
    param([string]$UserSid)
    if ($UserSid -eq 'S-1-5-21-1-1-1-1001') {
        [pscustomobject]@{ Id = 4120; Name = 'explorer'; SessionId = 2; Started = '2026-10-29T08:00:00.0000000Z'; MemoryMb = 150; Window = ''; IsApp = $true }
        [pscustomobject]@{ Id = 5304; Name = 'chrome'; SessionId = 2; Started = '2026-10-29T08:30:00.0000000Z'; MemoryMb = 600; Window = 'Wiki - Chrome'; IsApp = $true }
    }
}
function Stop-SssProcess {
    [CmdletBinding()]
    param([string]$UserSid, [int]$ProcessId, [switch]$Force)
    if ($ProcessId -eq 4120) { return 'not-owned' }
    if ($Force) { 'killed' } else { 'closed' }
}
function Get-SssProfile {
    [CmdletBinding()]
    param([string]$UserSid, [int]$ScanSeconds)
    [pscustomobject]@{ Type = 'FSLogix'; Loaded = $true; LocalPath = 'C:\Users\alice'; StorePath = $null; Container = '\\files\profiles\alice\Profile.vhdx'; ContainerMb = 9120; ContainerMaxMb = 30720
        Roaming = $false; Temporary = $false; Mandatory = $false; SizeMb = 6480; SizePartial = ($ScanSeconds -lt 10); ScanSeconds = 3; Notes = @('Status=0')
        Top = @([pscustomobject]@{ Name = 'AppData'; Mb = 4210 }, [pscustomobject]@{ Name = 'Documents'; Mb = 1360 }) }
}
