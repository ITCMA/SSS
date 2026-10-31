<#
.SYNOPSIS
  Sets up the restricted PowerShell endpoint (JEA) on a VDA that lets SSS list and end the processes of a user's session.

.DESCRIPTION
  Run in an elevated Windows PowerShell on every VDA (or in the master image / through a startup script).
  It registers the session configuration "SSS.Processes". The endpoint
    - can be used only by the SSS service account (-Principal),
    - runs as a temporary local administrator (virtual account) that exists only for the duration of one call,
    - offers exactly two commands, Get-SssProcess and Stop-SssProcess, and nothing else,
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
    New-PSRoleCapabilityFile -Path $psrc -VisibleFunctions 'Get-SssProcess', 'Stop-SssProcess' -FunctionDefinitions $functions
    # a module manifest next to the role capability folder makes the capability discoverable by name
    New-ModuleManifest -Path (Join-Path $moduleDir 'SssProcesses.psd1') -RootModule '' -ModuleVersion '1.0.0' -Description 'SSS process endpoint role capability'

    $pssc = Join-Path $work "$ConfigurationName.pssc"
    New-PSSessionConfigurationFile -Path $pssc -SessionType RestrictedRemoteServer -RunAsVirtualAccount `
        -TranscriptDirectory $TranscriptDirectory -ModulesToImport @() `
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
