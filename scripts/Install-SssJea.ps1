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
  The accounts that may connect, e.g. CORP\svc-sss$ (the gMSA of the SSS app pool). Several accounts are allowed:
  -Principal 'CORP\svc-sss$','CORP\admin.alice' also lets Alice run Test-SssCitrix.ps1 -Vda for a test. Run the
  script again with only the service account to take such a test account out.

.PARAMETER ConfigurationName
  Name of the endpoint. Must match Sss:Processes:Configuration in sss.config.json (default SSS.Processes).

.PARAMETER AllowedFrom
  Restricts the Windows Firewall rules for WinRM (TCP 5985) to these computers: host names (resolved now) or IP
  addresses/ranges of the SSS server(s). Recommended: without it every computer that can reach the VDA may try to connect
  (the endpoint still only accepts the accounts in -Principal).

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
    [string[]]$Principal = @(),
    [string]$ConfigurationName = 'SSS.Processes',
    [string]$TranscriptDirectory = 'C:\ProgramData\SSS\jea-transcripts',
    [string[]]$AllowedFrom = @(),
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

if ($Principal.Count -eq 0) { throw "Give the SSS service account with -Principal, e.g. -Principal 'CORP\svc-sss`$'." }
foreach ($pr in $Principal) {
    if ($pr -notmatch '^[^\\]+\\[^\\]+$') { throw "Principal must be written as DOMAIN\account, not '$pr'." }
    try { $null = (New-Object System.Security.Principal.NTAccount($pr)).Translate([System.Security.Principal.SecurityIdentifier]) }
    catch { throw "Principal '$pr' cannot be resolved: $($_.Exception.Message)" }
}

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
            # The endpoint runs in session 0, where Windows reports no window titles for other sessions. "IsApp" is therefore
            # a best guess from the program path: everything outside the Windows folder and the Citrix/FSLogix infrastructure,
            # plus a few well-known Windows programs.
            $windows = [string]$env:SystemRoot
            $noise = 'svchost', 'conhost', 'sihost', 'taskhostw', 'ctfmon', 'runtimebroker', 'searchui', 'searchapp', 'shellexperiencehost', 'startmenuexperiencehost', 'smartscreen', 'dllhost',
                'fontdrvhost', 'dwm', 'csrss', 'winlogon', 'wininit', 'services', 'lsass', 'spoolsv', 'sipahost', 'vdaredirector', 'websocketagent', 'wfshell', 'ctxgfx', 'cmstart', 'rdpclip', 'wudfhost', 'backgroundtaskhost', 'applicationframehost', 'textinputhost', 'registry', 'system', 'idle'
            $known = 'explorer', 'notepad', 'mspaint', 'mstsc', 'wordpad', 'snippingtool', 'taskmgr', 'cmd', 'powershell', 'powershell_ise', 'regedit', 'mmc', 'calc', 'calculatorapp'
            # Process.Path is empty for processes of other sessions here; WMI reports the executable path reliably.
            $paths = @{}
            foreach ($w in @(Get-WmiObject -Class Win32_Process -ErrorAction SilentlyContinue)) { if ($w -and $w.ExecutablePath) { $paths[[int]$w.ProcessId] = [string]$w.ExecutablePath } }
            Get-Process -IncludeUserName | Where-Object { $_.UserName -eq $account } | ForEach-Object {
                $path = $paths[[int]$_.Id]
                if (-not $path) { try { $path = [string]$_.Path } catch { } }
                $isApp = ($known -contains $_.ProcessName.ToLowerInvariant())
                if (-not $path -and $paths.Count -eq 0) { $isApp = -not ($noise -contains $_.ProcessName.ToLowerInvariant()) }   # no paths at all: fall back to a list of system process names
                elseif (-not $isApp -and $path -and -not $path.StartsWith($windows, [System.StringComparison]::OrdinalIgnoreCase) -and $path -notmatch '\\(Citrix|FSLogix)\\') { $isApp = $true }
                [pscustomobject]@{
                    Id        = $_.Id
                    Name      = $_.ProcessName
                    SessionId = $_.SessionId
                    Started   = $(try { $_.StartTime.ToUniversalTime().ToString('o') } catch { $null })
                    MemoryMb  = [int]($_.WorkingSet64 / 1MB)
                    Window    = $_.MainWindowTitle
                    Path      = $path
                    IsApp     = [bool]$isApp
                }
            }
        } },
        @{ Name = 'Get-SssProfile'; ScriptBlock = {
            [CmdletBinding()]
            param(
                [Parameter(Mandatory = $true)][ValidatePattern('^S-1-\d+(-\d+)+$')][string]$UserSid,
                [ValidateRange(0, 120)][int]$ScanSeconds = 20)
            # Read only. Restricted session: no Sort-Object / Select-Object parameters, so lists are picked by hand.
            $r = [ordered]@{ Type = 'Local'; Loaded = $false; LocalPath = $null; StorePath = $null; Container = $null; ContainerMb = $null; ContainerMaxMb = $null
                Roaming = $false; Temporary = $false; Mandatory = $false; SizeMb = $null; SizePartial = $false; ScanSeconds = 0; Notes = @()
                Tree = @(); Files = @(); Redirected = @(); Settings = @(); Exclusions = @(); LocalProfiles = $null }
            $p = Get-WmiObject -Class Win32_UserProfile -Filter "SID='$UserSid'" -ErrorAction SilentlyContinue
            $p = @($p)[0]
            if ($p) {
                $r.LocalPath = [string]$p.LocalPath; $r.Loaded = [bool]$p.Loaded
                $r.Roaming = [bool]$p.RoamingConfigured
                if ($p.RoamingPath) { $r.StorePath = [string]$p.RoamingPath }
                $st = [int]$p.Status
                $r.Temporary = [bool]($st -band 1); $r.Mandatory = [bool]($st -band 4)
                if ($st -band 8) { $r.Notes += 'corrupted' }
            } else { $r.Notes += 'no-profile-record' }
            if (-not $r.LocalPath) {
                $r.LocalPath = [string](Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$UserSid" -ErrorAction SilentlyContinue).ProfileImagePath
                if (-not $r.LocalPath) { $r.LocalPath = $null }
            }
            try { $r.LocalProfiles = @(Get-WmiObject -Class Win32_UserProfile -ErrorAction SilentlyContinue | Where-Object { -not $_.Special }).Count } catch { }

            # Technology: FSLogix, Citrix Profile Management, Windows roaming or local
            $fsRoot = 'HKLM:\SOFTWARE\FSLogix\Profiles'
            $fsSession = Join-Path "$fsRoot\Sessions" $UserSid
            $uname = ''
            try { $uname = ((New-Object System.Security.Principal.SecurityIdentifier($UserSid)).Translate([System.Security.Principal.NTAccount]).Value).Split('\')[-1] } catch { }
            # FSLogix mounts the container as a volume labelled "Profile-<user>": its used and total space is the container size
            $vol = $null
            foreach ($v in @(Get-WmiObject -Class Win32_Volume -Filter "Label LIKE 'Profile-%'" -ErrorAction SilentlyContinue)) {
                if ($v -and [string]$v.Label -ieq ('Profile-' + $uname)) { $vol = $v }
            }
            if ($vol -and $vol.Capacity) { $r.ContainerMb = [int](([int64]$vol.Capacity - [int64]$vol.FreeSpace) / 1MB); $r.ContainerMaxMb = [int]([int64]$vol.Capacity / 1MB) }
            $upmKey = 'HKLM:\SOFTWARE\Policies\Citrix\UserProfileManager'
            if (Test-Path $fsSession) {
                $r.Type = 'FSLogix'
                $props = Get-ItemProperty -Path $fsSession -ErrorAction SilentlyContinue
                foreach ($n in ($props.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' })) {
                    $v = [string]$n.Value
                    if ($v -match '\.vhdx?$') { $r.Container = $v }
                    elseif ($n.Name -match '^(Status|Reason|ProfileState|ErrorCode)$') { $r.Notes += ('{0}={1}' -f $n.Name, $v) }
                }
            } elseif ($vol) {
                $r.Type = 'FSLogix'; $r.Notes += 'detected-by-volume'
            } elseif ((Get-Service -Name 'ctxProfile' -ErrorAction SilentlyContinue).Status -eq 'Running') {
                $r.Type = 'Citrix UPM'
                $store = (Get-ItemProperty -Path $upmKey -Name PathToUserStore -ErrorAction SilentlyContinue).PathToUserStore
                if ($store) { $r.StorePath = [string]$store }
            } elseif ($r.Roaming) { $r.Type = 'Roaming' }
            if ($r.Mandatory) { $r.Notes += 'mandatory' }
            if ($r.Temporary) { $r.Notes += 'temporary' }

            # Configuration of the technology: FSLogix (policy wins over local setting) and Citrix Profile Management
            $fsNames = 'Enabled', 'VHDLocations', 'SizeInMBs', 'IsDynamic', 'VolumeType', 'ProfileType', 'ConcurrentUserSessions', 'DeleteLocalProfileWhenVHDShouldApply',
                'FlipFlopProfileDirectoryName', 'RedirXMLSourceFolder', 'CCDLocations', 'PreventLoginWithFailure', 'PreventLoginWithTempProfile'
            $redirDir = $null
            foreach ($base in 'HKLM:\SOFTWARE\FSLogix\Profiles', 'HKLM:\SOFTWARE\Policies\FSLogix\Profiles') {
                $kp = Get-ItemProperty -Path $base -ErrorAction SilentlyContinue
                if (-not $kp) { continue }
                foreach ($n in $fsNames) {
                    $val = $kp.$n
                    if ($null -ne $val) {
                        $text = (@($val) -join '; ')
                        $r.Settings = @($r.Settings | Where-Object { -not ($_.Source -eq 'FSLogix' -and $_.Name -eq $n) }) + [pscustomobject]@{ Source = 'FSLogix'; Name = $n; Value = $text }
                        if ($n -eq 'RedirXMLSourceFolder') { $redirDir = [string]@($val)[0] }
                    }
                }
            }
            foreach ($base in 'HKLM:\SOFTWARE\FSLogix\ODFC', 'HKLM:\SOFTWARE\Policies\FSLogix\ODFC') {
                $kp = Get-ItemProperty -Path $base -ErrorAction SilentlyContinue
                if (-not $kp) { continue }
                foreach ($n in 'Enabled', 'VHDLocations') {
                    if ($null -ne $kp.$n) { $r.Settings += [pscustomobject]@{ Source = 'FSLogix Office'; Name = $n; Value = (@($kp.$n) -join '; ') } }
                }
            }
            if ($redirDir) {
                try {
                    $xml = [xml](Get-Content -LiteralPath (Join-Path $redirDir 'redirections.xml') -Raw -ErrorAction Stop)
                    $count = 0
                    foreach ($e in @($xml.FrxProfileFolderRedirection.Excludes.Exclude)) {
                        if ($e -and $count -lt 40) { $r.Exclusions += [pscustomobject]@{ Source = 'FSLogix redirections.xml'; Item = [string]$e.'#text' }; $count++ }
                    }
                } catch { $r.Notes += 'redirections-xml-not-readable' }
            }
            foreach ($base in $upmKey, 'HKLM:\SOFTWARE\Citrix\UserProfileManager') {
                $kp = Get-ItemProperty -Path $base -ErrorAction SilentlyContinue
                if (-not $kp) { continue }
                $count = 0
                foreach ($n in ($kp.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' })) {
                    if ($count -ge 60) { break }
                    $text = (@($n.Value) -join '; ')
                    if ($text.Length -gt 200) { $text = $text.Substring(0, 200) }
                    if ($n.Name -match 'Exclu') {
                        foreach ($item in @($n.Value)) { if ($item) { $r.Exclusions += [pscustomobject]@{ Source = 'Citrix UPM ' + $n.Name; Item = [string]$item } } }
                    } else { $r.Settings += [pscustomobject]@{ Source = 'Citrix UPM'; Name = $n.Name; Value = $text } }
                    $count++
                }
            }

            # Folder redirection of the logged-on user (policy or user shell folders)
            $names = @{ 'Desktop' = 'Desktop'; 'Personal' = 'Documents'; '{374DE290-123F-4565-9164-39C4925E467B}' = 'Downloads'; 'My Pictures' = 'Pictures'; 'My Music' = 'Music'
                'My Video' = 'Videos'; 'Favorites' = 'Favorites'; 'AppData' = 'AppData (Roaming)'; 'Start Menu' = 'Start Menu' }
            $sf = Get-ItemProperty -Path "Registry::HKEY_USERS\$UserSid\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders" -ErrorAction SilentlyContinue
            if ($sf) {
                foreach ($k in @($names.Keys)) {
                    $val = [string]$sf.$k
                    if ($val.StartsWith('\\')) { $r.Redirected += [pscustomobject]@{ Name = $names[$k]; Target = $val } }
                }
            }

            # Container size from a readable VHD(X) path
            if ($r.Container -and $null -eq $r.ContainerMb) { try { $r.ContainerMb = [int]((Get-Item -LiteralPath $r.Container -ErrorAction Stop).Length / 1MB) } catch { } }

            # Folder tree (four levels) and the largest files, time boxed; reparse points are not followed
            if ($ScanSeconds -gt 0 -and $r.LocalPath -and (Test-Path -LiteralPath $r.LocalPath)) {
                $sw = [System.Diagnostics.Stopwatch]::StartNew()
                $total = [int64]0
                $dirs = @{}
                $files = New-Object System.Collections.ArrayList
                $fileMin = [int64]0
                $stack = New-Object System.Collections.Stack
                $stack.Push(@($r.LocalPath, ''))
                while ($stack.Count -gt 0) {
                    if ($sw.Elapsed.TotalSeconds -gt $ScanSeconds) { $r.SizePartial = $true; break }
                    $item = $stack.Pop(); $dir = $item[0]; $rel = [string]$item[1]
                    $parts = @()
                    if ($rel) { $parts = $rel.Split('\') }
                    try {
                        $di = New-Object System.IO.DirectoryInfo($dir)
                        foreach ($f in $di.EnumerateFiles()) {
                            $len = [int64]$f.Length
                            $total += $len
                            $keys = @()
                            if ($parts.Count -eq 0) { $keys = @('(files)') } else { for ($d = 1; $d -le [math]::Min(4, $parts.Count); $d++) { $keys += ($parts[0..($d - 1)] -join '\') } }
                            foreach ($key in $keys) { $dirs[$key] = [int64]$dirs[$key] + $len }
                            if ($files.Count -lt 10) { [void]$files.Add(@(($(if ($rel) { $rel + '\' } else { '' }) + $f.Name), $len)) }
                            elseif ($len -gt $fileMin) {
                                $mi = 0
                                for ($j = 1; $j -lt $files.Count; $j++) { if ($files[$j][1] -lt $files[$mi][1]) { $mi = $j } }
                                $files[$mi] = @(($(if ($rel) { $rel + '\' } else { '' }) + $f.Name), $len)
                            }
                            if ($files.Count -ge 10) { $fileMin = $files[0][1]; for ($j = 1; $j -lt $files.Count; $j++) { if ($files[$j][1] -lt $fileMin) { $fileMin = $files[$j][1] } } }
                        }
                        foreach ($d in $di.EnumerateDirectories()) {
                            if ($d.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { continue }
                            $stack.Push(@($d.FullName, $(if ($rel) { $rel + '\' + $d.Name } else { $d.Name })))
                        }
                    } catch { }
                }
                $r.ScanSeconds = [int][math]::Ceiling($sw.Elapsed.TotalSeconds)
                $r.SizeMb = [int]($total / 1MB)
                # the 80 biggest folders (parents are at least as big as their children, so they come along) and the 10 biggest files
                $left = New-Object System.Collections.ArrayList
                foreach ($k in @($dirs.Keys)) { if ([int64]$dirs[$k] -ge 1MB) { [void]$left.Add(@($k, [int64]$dirs[$k])) } }
                $tree = @()
                for ($i = 0; $i -lt 80 -and $left.Count -gt 0; $i++) {
                    $bi = 0
                    for ($j = 1; $j -lt $left.Count; $j++) { if ($left[$j][1] -gt $left[$bi][1]) { $bi = $j } }
                    $tree += [pscustomobject]@{ Path = [string]$left[$bi][0]; Mb = [int]($left[$bi][1] / 1MB) }
                    $left.RemoveAt($bi)
                }
                $r.Tree = $tree
                $big = @()
                while ($files.Count -gt 0) {
                    $bi = 0
                    for ($j = 1; $j -lt $files.Count; $j++) { if ($files[$j][1] -gt $files[$bi][1]) { $bi = $j } }
                    $big += [pscustomobject]@{ Path = [string]$files[$bi][0]; Mb = [int]($files[$bi][1] / 1MB) }
                    $files.RemoveAt($bi)
                }
                $r.Files = $big
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
    # No extra modules: importing CimCmdlets (or Management/Utility, which are snap-ins in Windows PowerShell 5.1) into the
    # restricted session breaks the implicit remoting on the SSS side ("alias gcls is read-only"). Get-WmiObject is part of the
    # already loaded Management snap-in, so Get-SssProfile uses it instead of Get-CimInstance.
    $psrc = Join-Path $capDir 'SssProcesses.psrc'
    New-PSRoleCapabilityFile -Path $psrc -VisibleFunctions 'Get-SssProcess', 'Get-SssProfile', 'Stop-SssProcess' -FunctionDefinitions $functions
    # a module manifest next to the role capability folder makes the capability discoverable by name
    New-ModuleManifest -Path (Join-Path $moduleDir 'SssProcesses.psd1') -RootModule '' -ModuleVersion '1.0.0' -Description 'SSS process endpoint role capability'

    $pssc = Join-Path $work "$ConfigurationName.pssc"
    $roles = @{}
    foreach ($pr in $Principal) { $roles[$pr] = @{ RoleCapabilities = 'SssProcesses' } }
    New-PSSessionConfigurationFile -Path $pssc -SessionType RestrictedRemoteServer -RunAsVirtualAccount `
        -TranscriptDirectory $TranscriptDirectory -RoleDefinitions $roles

    # make the role capability findable: JEA looks in <module>\RoleCapabilities of modules on PSModulePath
    $machineModules = Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules'
    $target = Join-Path $machineModules 'SssProcesses'
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item $moduleDir $target -Recurse -Force

    if (Get-PSSessionConfiguration -Name $ConfigurationName -ErrorAction SilentlyContinue) { Unregister-PSSessionConfiguration -Name $ConfigurationName -Force }
    Register-PSSessionConfiguration -Name $ConfigurationName -Path $pssc -Force | Out-Null
    Ok "Endpoint '$ConfigurationName' registered for $($Principal -join ', ')"
    # Transcripts show process names and paths: only administrators and the system may read them
    try {
        & icacls $TranscriptDirectory /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
        if ($LASTEXITCODE -eq 0) { Ok 'Transcript folder readable only by administrators and the system' }
    } catch { Write-Host "    WARNING: could not restrict the transcript folder: $($_.Exception.Message)" -ForegroundColor Yellow }
    foreach ($pr in $Principal) {
        if ($pr -notmatch '\$$') { Write-Host "    NOTE: '$pr' looks like a person's account. Keep it only for testing and run the script again without it afterwards." -ForegroundColor Yellow }
    }
    if ($AllowedFrom.Count -gt 0) {
        $remote = @()
        foreach ($a in $AllowedFrom) {
            if ($a -match '^[0-9a-fA-F:./]+$') { $remote += $a }
            else { try { $remote += ([System.Net.Dns]::GetHostAddresses($a) | ForEach-Object { $_.IPAddressToString }) } catch { throw "Cannot resolve '$a' for -AllowedFrom: $($_.Exception.Message)" } }
        }
        try {
            Get-NetFirewallRule -Name 'WINRM-HTTP-In-TCP*' -ErrorAction Stop | Set-NetFirewallRule -RemoteAddress $remote
            Ok ('WinRM (TCP 5985) accepts connections only from ' + ($remote -join ', '))
        } catch { Write-Host "    WARNING: could not restrict the firewall rule: $($_.Exception.Message)" -ForegroundColor Yellow }
    } else {
        Write-Host '    NOTE: WinRM (TCP 5985) is reachable from every computer that can see this VDA. Use -AllowedFrom <SSS server> to restrict it.' -ForegroundColor Yellow
    }
    Ok "Transcripts: $TranscriptDirectory"
}

Write-Host ''
Write-Host "Done. Next on the SSS server: set Sss:Processes:Enabled = true in sss.config.json, restart the app pool, then run" -ForegroundColor Cyan
Write-Host "  .\Test-SssCitrix.ps1 -Controller <ddc> -UserName <DOMAIN\user> -Vda <this server's DNS name>" -ForegroundColor Cyan
