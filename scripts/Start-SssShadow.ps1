<#
.SYNOPSIS
  Starter for the "Remote support" button of SSS. Runs on the helpdesk PC, started by the browser through the sss-shadow:// link.

.DESCRIPTION
  The link has the form  sss-shadow://<vda host name>/<windows session id>?control=0|1  and is checked strictly: host name and numbers only,
  nothing else is passed on. It then starts only  mstsc.exe /v:<host> /shadow:<id> [/control]  . The consent prompt for the user always stays on
  (/noConsentPrompt is never used). If Register-SssShadow.ps1 stored an allowed host suffix, the host must belong to it.
  Whether the helpdesk person may shadow at all, and the consent of the user, are decided by Windows on the VDA.

  -DryRun shows the command line instead of starting it.
#>
[CmdletBinding()]
param([Parameter(Position = 0)][string]$Url, [switch]$DryRun)

$ErrorActionPreference = 'Stop'

function Show-Problem([string]$Text) {
    if ($DryRun) { Write-Error $Text; return }
    try { Add-Type -AssemblyName System.Windows.Forms; [void][System.Windows.Forms.MessageBox]::Show($Text, 'SSS remote support') } catch { }
}

try {
    $m = [regex]::Match([string]$Url, '^sss-shadow://([A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?)/(\d{1,5})/?\?control=([01])/?$')
    if (-not $m.Success) { Show-Problem 'The link is not a valid SSS remote support link.'; return }
    $hostName = $m.Groups[1].Value; $sessionId = $m.Groups[2].Value; $control = $m.Groups[3].Value -eq '1'

    $suffix = $null
    try { $suffix = (Get-ItemProperty -Path 'HKCU:\Software\SSS\Shadow' -Name AllowedHostSuffix -ErrorAction Stop).AllowedHostSuffix } catch { }
    if ($suffix) {
        $ok = $false
        foreach ($s in ($suffix -split '[,; ]+' | Where-Object { $_ })) {
            $s = $s.TrimStart('.')
            if ($hostName -ieq $s -or $hostName.EndsWith('.' + $s, [System.StringComparison]::OrdinalIgnoreCase)) { $ok = $true }
        }
        if (-not $ok) { Show-Problem "The host $hostName is not in the allowed domain list of this PC."; return }
    }

    $arguments = @("/v:$hostName", "/shadow:$sessionId")
    if ($control) { $arguments += '/control' }
    $mstsc = Join-Path $env:windir 'System32\mstsc.exe'
    if ($DryRun) { Write-Output ("$mstsc " + ($arguments -join ' ')); return }
    Start-Process -FilePath $mstsc -ArgumentList $arguments
} catch {
    Show-Problem ('Remote support could not be started: ' + $_.Exception.Message)
}
