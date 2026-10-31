<#
.SYNOPSIS
  Sets up the restricted PowerShell endpoint (JEA) on a VDA that lets SSS list and end the processes of a user's session.

.DESCRIPTION
  Run in an elevated Windows PowerShell on every VDA (or in the master image / through a startup script).
  It registers the session configuration "SSS.Processes". The endpoint
    - can be used only by the SSS service account (-Principal),
    - runs as a temporary local administrator (virtual account) that exists only for the duration of one call,
    - offers exactly three commands, Get-SssProcess, Stop-SssProcess and Get-SssProfile (read only), and nothing else,
    - touches only processes whose owner is the user SID that SSS passes in, never system or other users' processes,
    - never ends the Citrix session processes wfshell.exe, ctxgfx.exe and cmstart.exe,
    - writes a transcript of every call to the folder given by -TranscriptDirectory.
  The script changes only the WinRM session configuration (it enables WinRM remoting if needed).
  Remove it again with -Remove.

.PARAMETER Principal
  The SSS service account that may connect, e.g. CORP\svc-sss$ (the gMSA of the SSS app pool).

.PARAMETER ConfigurationName
  Name of the endpoint. Must match Sss:Processes:Configuration in sss.config.json (default SSS.Processes).

.PARAMETER TranscriptDirectory
  Where JEA writes the transcripts (default C:\ProgramData\SSS\jea-transcripts).

.EXAMPLE
  .\Install-SssJea.ps1 -Principal 'CORP\svc-sss$'

.EXAMPLE
  .\Install-SssJea.ps1 -Remove
#>
#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Principal = '',
    [string]$ConfigurationName = 'SSS.Processes',
    [string]$TranscriptDirectory = 'C:\ProgramData\SSS\jea-transcripts',
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "    $m" -ForegroundColor Green }

if ($ConfigurationName -notmatch '^[A-Za-z0-9._-]{1,64}$') { throw "ConfigurationName '$ConfigurationName' contains invalid characters." }

if ($Remove) {
    Step "Removing endpoint $ConfigurationName"
    if (Get-PSSessionConfiguration -Name $ConfigurationName -ErrorAction SilentlyContinue) {
        if ($PSCmdlet.ShouldProcess($ConfigurationName, 'Unregister-PSSessionConfiguration')) {
            Unregister-PSSessionConfiguration -Name $ConfigurationName -Force
            Ok 'Removed'
        }
    } else { Ok 'Not registered, nothing to do' }
    return
}

if (-not $Principal) { throw "Give the SSS service account with -Principal, e.g. -Principal 'CORP\svc-sss`$'." }
if ($Principal -notmatch '^[^\\]+\\[^\\]+$') { throw "Principal must be written as DOMAIN\account, not '$Principal'." }
try { $null = (New-Object System.Security.Principal.NTAccount($Principal)).Translate([System.Security.Principal.SecurityIdentifier]) }
catch { throw "Principal '$Principal' cannot be resolved: $($_.Exception.Message)" }

Step 'Preparing WinRM'
if ($PSCmdlet.ShouldProcess('WinRM', 'Enable-PSRemoting')) { Enable-PSRemoting -SkipNetworkProfileCheck -Force | Out-Null; Ok 'WinRM enabled (TCP 5985)' }

$work = Join-Path $env:ProgramData "SSS\jea-$ConfigurationName"
$moduleDir = Join-Path $work "Modules\SssProcesses"
$capDir = Join-Path $moduleDir 'RoleCapabilities'
if ($PSCmdlet.ShouldProcess($work, 'Write role capability and session configuration')) {
    New-Item -ItemType Directory -Force -Path $capDir, $TranscriptDirectory | Out-Null

    # --- the two commands --------------------------------------------------------------------
    $functions = @(
        @{ Name = 'Get-SssProcess'; ScriptBlock = {
            [CmdletBinding()]
            param([Parameter(Mandatory = $true)][ValidatePattern('^S-1-\d+(-\d+)+$')][string]$UserSid)
            $account = (New-Object System.Security.Principal.SecurityIdentifier($UserSid)).Translate([System.Security.Principal.NTAccount]).Value
            Get-Process -IncludeUserName | Where-Object { $_.UserName -eq $account } | ForEach-Object {
                [pscustomobject]@{
                    Id        = $_.Id
                    Name      = $_.ProcessName
                    SessionId = $_.SessionId
                    Started   = $(try { $_.StartTime.ToUniversalTime().ToString('o') } catch { $null })
                    MemoryMb  = [int]($_.WorkingSet64 / 1MB)
                    Window    = $_.MainWindowTitle
                }
            }
        } },
        @{ Name = 'Get-SssProfile'; ScriptBlock = {
            [CmdletBinding()]
            param(
                [Parameter(Mandatory = $true)][ValidatePattern('^S-1-\d+(-\d+)+$')][string]$UserSid,
                [ValidateRange(0, 120)][int]$ScanSeconds = 20)
            $r = [ordered]@{ Type = 'Local'; Loaded = $false; LocalPath = $null; StorePath = $null; Container = $null; ContainerMb = $null
                Roaming = $false; Temporary = $false; Mandatory = $false; SizeMb = $null; SizePartial = $false; ScanSeconds = 0; Notes = @(); Top = @() }
            $p = Get-CimInstance -ClassName Win32_UserProfile -Filter "SID='$UserSid'" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($p) {
                $r.LocalPath = [string]$p.LocalPath; $r.Loaded = [bool]$p.Loaded
                $r.Roaming = [bool]$p.RoamingConfigured
                if ($p.RoamingPath) { $r.StorePath = [string]$p.RoamingPath }
                $st = [int]$p.Status
                $r.Temporary = [bool]($st -band 1); $r.Mandatory = [bool]($st -band 4)
                if ($st -band 8) { $r.Notes += 'corrupted' }
            } else { $r.Notes += 'no-profile-record' }

            # Which technology holds the profile?
            $fsRoot = 'HKLM:\SOFTWARE\FSLogix\Profiles'
            $fsSession = Join-Path "$fsRoot\Sessions" $UserSid
            $vol = Get-CimInstance -ClassName Win32_Volume -Filter "Label LIKE 'Profile-%'" -ErrorAction SilentlyContinue | Select-Object -First 1
            if (Test-Path $fsSession) {
                $r.Type = 'FSLogix'
                $props = Get-ItemProperty -Path $fsSession -ErrorAction SilentlyContinue
                foreach ($n in ($props.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' })) {
                    $v = [string]$n.Value
                    if ($v -match '\.vhdx?$') { $r.Container = $v }
                    elseif ($n.Name -match '^(Status|Reason|ProfileState|ErrorCode)$') { $r.Notes += ('{0}={1}' -f $n.Name, $v) }
                }
            } elseif ($vol -and (Get-ItemProperty -Path $fsRoot -Name Enabled -ErrorAction SilentlyContinue).Enabled -eq 1) {
                $r.Type = 'FSLogix'; $r.Notes += 'detected-by-volume'
            } elseif ((Get-Service -Name 'ctxProfile' -ErrorAction SilentlyContinue).Status -eq 'Running') {
                $r.Type = 'Citrix UPM'
                $store = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Citrix\UserProfileManager' -Name PathToUserStore -ErrorAction SilentlyContinue).PathToUserStore
                if ($store) { $r.StorePath = [string]$store }
            } elseif ($r.Roaming) { $r.Type = 'Roaming' }
            if ($r.Mandatory) { $r.Notes += 'mandatory' }
            if ($r.Temporary) { $r.Notes += 'temporary' }

            # Size of the VHD(X) container, when it can be read
            if ($r.Container) { try { $r.ContainerMb = [int]((Get-Item -LiteralPath $r.Container -ErrorAction Stop).Length / 1MB) } catch { } }

            # Size of the profile folder (time boxed, reparse points are not followed)
            if ($ScanSeconds -gt 0 -and $r.LocalPath -and (Test-Path -LiteralPath $r.LocalPath)) {
                $sw = [System.Diagnostics.Stopwatch]::StartNew()
                $total = [int64]0
                $tops = @{}
                $stack = New-Object System.Collections.Stack
                $stack.Push(@($r.LocalPath, ''))
                while ($stack.Count -gt 0) {
                    if ($sw.Elapsed.TotalSeconds -gt $ScanSeconds) { $r.SizePartial = $true; break }
                    $item = $stack.Pop(); $dir = $item[0]; $top = $item[1]
                    try {
                        $di = New-Object System.IO.DirectoryInfo($dir)
                        foreach ($f in $di.EnumerateFiles()) { $total += $f.Length; $k = if ($top) { $top } else { '(files)' }; $tops[$k] = [int64]$tops[$k] + $f.Length }
                        foreach ($d in $di.EnumerateDirectories()) {
                            if ($d.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { continue }
                            $stack.Push(@($d.FullName, $(if ($top) { $top } else { $d.Name })))
                        }
                    } catch { }
                }
                $r.ScanSeconds = [int][math]::Ceiling($sw.Elapsed.TotalSeconds)
                $r.SizeMb = [int]($total / 1MB)
                $r.Top = @($tops.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 6 | ForEach-Object { [pscustomobject]@{ Name = [string]$_.Key; Mb = [int]($_.Value / 1MB) } })
            }
            [pscustomobject]$r
        } },
        @{ Name = 'Stop-SssProcess'; ScriptBlock = {
            [CmdletBinding()]
            param(
                [Parameter(Mandatory = $true)][ValidatePattern('^S-1-\d+(-\d+)+$')][string]$UserSid,
                [Parameter(Mandatory = $true)][ValidateRange(1, 2147483647)][int]$ProcessId,
                [switch]$Force)
            $protected = 'wfshell', 'ctxgfx', 'cmstart'
            $account = (New-Object System.Security.Principal.SecurityIdentifier($UserSid)).Translate([System.Security.Principal.NTAccount]).Value
            $p = Get-Process -Id $ProcessId -IncludeUserName -ErrorAction SilentlyContinue
            if (-not $p) { return 'not-found' }
            if ($p.UserName -ne $account) { return 'not-owned' }
            if ($protected -contains $p.ProcessName.ToLowerInvariant()) { return 'protected' }
            if ($Force) { Stop-Process -Id $ProcessId -Force; return 'killed' }
            if ($p.MainWindowHandle -eq 0) { return 'no-window' }
            if ($p.CloseMainWindow()) { return 'closed' }
            return 'not-closed'
        } }
    )
    $psrc = Join-Path $capDir 'SssProcesses.psrc'
    New-PSRoleCapabilityFile -Path $psrc -VisibleFunctions 'Get-SssProcess', 'Get-SssProfile', 'Stop-SssProcess' -FunctionDefinitions $functions
    # a module manifest next to the role capability folder makes the capability discoverable by name
    New-ModuleManifest -Path (Join-Path $moduleDir 'SssProcesses.psd1') -RootModule '' -ModuleVersion '1.0.0' -Description 'SSS process endpoint role capability'

    $pssc = Join-Path $work "$ConfigurationName.pssc"
    New-PSSessionConfigurationFile -Path $pssc -SessionType RestrictedRemoteServer -RunAsVirtualAccount `
        -TranscriptDirectory $TranscriptDirectory `
        -RoleDefinitions @{ $Principal = @{ RoleCapabilities = 'SssProcesses' } }

    # make the role capability findable: JEA looks in <module>\RoleCapabilities of modules on PSModulePath
    $machineModules = Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules'
    $target = Join-Path $machineModules 'SssProcesses'
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item $moduleDir $target -Recurse -Force

    if (Get-PSSessionConfiguration -Name $ConfigurationName -ErrorAction SilentlyContinue) { Unregister-PSSessionConfiguration -Name $ConfigurationName -Force }
    Register-PSSessionConfiguration -Name $ConfigurationName -Path $pssc -Force | Out-Null
    Ok "Endpoint '$ConfigurationName' registered for $Principal"
    Ok "Transcripts: $TranscriptDirectory"
}

Write-Host ''
Write-Host "Done. Next on the SSS server: set Sss:Processes:Enabled = true in sss.config.json, restart the app pool, then run" -ForegroundColor Cyan
Write-Host "  .\Test-SssCitrix.ps1 -Controller <ddc> -UserName <DOMAIN\user> -Vda <this server's DNS name>" -ForegroundColor Cyan
