<#
.SYNOPSIS
  Sets up the "Remote support" button of SSS on a helpdesk PC (once per PC and Windows user, no administrator rights needed).

.DESCRIPTION
  Copies Start-SssShadow.ps1 to %LOCALAPPDATA%\SSS and registers the link type sss-shadow:// for the current Windows user (HKCU). A click on
  "Remote support" in SSS then starts  mstsc /shadow  for the session. The starter only accepts a host name and numbers (see Start-SssShadow.ps1).

  -AllowedHostSuffix 'corp.local' limits it to VDAs of that domain (recommended). Several: 'corp.local,lab.corp.local'.
  -Remove undoes everything.

.EXAMPLE
  .\Register-SssShadow.ps1 -AllowedHostSuffix 'corp.local'
#>
[CmdletBinding()]
param([string]$AllowedHostSuffix, [switch]$Remove)

$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'SSS'
$script = Join-Path $target 'Start-SssShadow.ps1'
$classes = 'HKCU:\Software\Classes\sss-shadow'
$settings = 'HKCU:\Software\SSS\Shadow'

if ($Remove) {
    Remove-Item -Path $classes -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $settings -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $script -Force -ErrorAction SilentlyContinue
    Write-Host 'Removed the sss-shadow:// link type and the starter.' -ForegroundColor Green
    return
}

$source = Join-Path $PSScriptRoot 'Start-SssShadow.ps1'
if (-not (Test-Path $source)) { throw "Start-SssShadow.ps1 not found next to this script ($PSScriptRoot)." }
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -Path $source -Destination $script -Force

New-Item -Path "$classes\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path $classes -Name '(default)' -Value 'URL:SSS remote support'
Set-ItemProperty -Path $classes -Name 'URL Protocol' -Value ''
$command = 'powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $script + '" "%1"'
Set-ItemProperty -Path "$classes\shell\open\command" -Name '(default)' -Value $command

New-Item -Path $settings -Force | Out-Null
if ($AllowedHostSuffix) { Set-ItemProperty -Path $settings -Name AllowedHostSuffix -Value $AllowedHostSuffix }
else { Remove-ItemProperty -Path $settings -Name AllowedHostSuffix -ErrorAction SilentlyContinue }

Write-Host "Registered sss-shadow:// for $env:USERNAME. Starter: $script" -ForegroundColor Green
if ($AllowedHostSuffix) { Write-Host "Only hosts in: $AllowedHostSuffix" } else { Write-Host 'No host restriction set; -AllowedHostSuffix <domain> is recommended.' -ForegroundColor Yellow }
Write-Host 'The first click asks the browser for permission to open the link; tick "always allow" if offered.'
Write-Host 'Test without SSS:  .\Start-SssShadow.ps1 -DryRun "sss-shadow://vda01.corp.local/3?control=1"'
