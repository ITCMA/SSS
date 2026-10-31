<#
.SYNOPSIS
  Installs or updates SSS (Session Self Service) as an IIS application.

.DESCRIPTION
  Run in an elevated Windows PowerShell on the IIS server (for example the Web Studio server).
  The script
    - checks IIS, the ASP.NET Core Module and the required IIS features,
    - copies the published application (keeps sss.config.json and logs on updates, uses app_offline.htm),
    - creates a dedicated app pool: no managed code, AlwaysRunning, no idle timeout, no periodic recycle,
    - creates the IIS application below an existing site (default: Default Web Site/sss) with preload enabled,
    - enables Windows authentication and disables anonymous access for that application,
    - grants the app pool identity read access to the app and write access to the log folder,
    - writes a starter sss.config.json if none exists and runs a smoke test.
  Use -WhatIf to see what would change.
  -Interactive asks for the few required values. -Update replaces only the files (configuration, app pool and
  permissions stay as they are), keeps the previous version and restores it if the smoke test fails.

.PARAMETER SourcePath
  Folder with the published application (the "app" folder of the release zip).

.PARAMETER ServiceAccount
  Recommended: a group-managed service account, e.g. CORP\svc-sss$ . It must be installed on this server
  (Install-ADServiceAccount) and hold a Citrix delegated-administration role (read sessions, log off, read Monitor data).
  Without it the app pool runs as ApplicationPoolIdentity, which reaches other servers as the machine account.

.PARAMETER HelpdeskGroup
  Optional AD group (e.g. CORP\SSS-Helpdesk) whose members may find users and log off their sessions.
  Written to a new sss.config.json only; for an existing configuration edit Sss:Helpdesk:Group yourself.

.PARAMETER HealthEndpoint
  Opens GET <app>/healthz for anonymous access so a load balancer can probe it (answers only {"status":"ok"} or 503).
  Works together with -Update.

.EXAMPLE
  .\install.ps1 -Controllers ddc01.corp.local,ddc02.corp.local -ServiceAccount 'CORP\svc-sss$' -HelpdeskGroup 'CORP\SSS-Helpdesk'

.EXAMPLE
  .\install.ps1 -Interactive

.EXAMPLE
  .\install.ps1 -Update

.EXAMPLE
  .\install.ps1 -Uninstall
#>
#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot 'app'),
    [string]$SiteName = 'Default Web Site',
    [string]$AppPath = 'sss',
    [string]$AppPoolName = 'SSS',
    [string]$PhysicalPath = 'C:\inetpub\sss',
    [string]$ServiceAccount = '',
    [string[]]$Controllers = @(),
    [string]$HelpdeskGroup = '',
    [switch]$Interactive,
    [switch]$Update,
    [switch]$Uninstall,
    [switch]$SkipSmokeTest,
    [switch]$HealthEndpoint
)

$ErrorActionPreference = 'Stop'
function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "    $m" -ForegroundColor Green }
function Warn($m) { Write-Host "    WARNING: $m" -ForegroundColor Yellow }

if ($Interactive -and -not $Update -and -not $Uninstall) {
    function Ask($prompt, $default) {
        $suffix = if ($default) { " [$default]" } else { '' }
        $v = Read-Host "$prompt$suffix"
        if ([string]::IsNullOrWhiteSpace($v)) { return $default } else { return $v.Trim() }
    }
    Write-Host 'SSS installation: press Enter to accept the value in brackets.' -ForegroundColor Cyan
    if ($Controllers.Count -eq 0) {
        $c = Ask 'Delivery Controller(s), comma separated (empty = demo mode)' ''
        if ($c) { $Controllers = @($c -split '\s*,\s*' | Where-Object { $_ }) }
    }
    if (-not $ServiceAccount) { $ServiceAccount = Ask 'Service account (gMSA, e.g. CORP\svc-sss$; empty = app pool identity)' '' }
    if (-not $HelpdeskGroup) { $HelpdeskGroup = Ask 'Helpdesk AD group (e.g. CORP\SSS-Helpdesk; empty = no helpdesk)' '' }
    if (-not $HealthEndpoint) { $HealthEndpoint = (Ask 'Open /healthz for a load balancer probe without credentials? (y/N)' 'N') -match '^(y|j)' }
    $SiteName = Ask 'IIS site' $SiteName
    $AppPath = Ask 'Application path' $AppPath
    $PhysicalPath = Ask 'Install folder' $PhysicalPath
}

if ($AppPath -notmatch '^[A-Za-z0-9_-]+$') { throw "AppPath '$AppPath' may only contain letters, digits, '_' and '-'." }
if ($AppPoolName -notmatch '^[A-Za-z0-9_ -]+$') { throw "AppPoolName '$AppPoolName' contains invalid characters." }

Step 'Checking IIS'
Import-Module WebAdministration -ErrorAction Stop
if (-not (Test-Path "IIS:\Sites\$SiteName")) { throw "IIS site '$SiteName' not found. Use -SiteName to pick an existing site." }

$appIisPath = "IIS:\Sites\$SiteName\$AppPath"
$poolIisPath = "IIS:\AppPools\$AppPoolName"

# ---------------------------------------------------------------------------------------------
if ($Uninstall) {
    Step 'Uninstalling'
    if ((Test-Path $appIisPath) -and $PSCmdlet.ShouldProcess($appIisPath, 'Remove IIS application')) {
        Remove-WebApplication -Name $AppPath -Site $SiteName
        Ok "Removed application $SiteName/$AppPath"
    }
    if ((Test-Path $poolIisPath) -and $PSCmdlet.ShouldProcess($poolIisPath, 'Remove app pool')) {
        Remove-WebAppPool -Name $AppPoolName
        Ok "Removed app pool $AppPoolName"
    }
    try {
        Clear-WebConfiguration -Filter '/system.webServer/security/authentication' -PSPath 'MACHINE/WEBROOT/APPHOST' -Location "$SiteName/$AppPath/healthz" -ErrorAction Stop
        Ok 'Removed the anonymous /healthz rule'
    } catch { }
    Write-Host "Files, sss.config.json and audit logs in $PhysicalPath were kept. Delete the folder manually if you no longer need them."
    return
}

if (-not (Get-WebGlobalModule -Name 'AspNetCoreModuleV2' -ErrorAction SilentlyContinue)) {
    throw 'The ASP.NET Core Module V2 is not installed. Install the ".NET Hosting Bundle" (any current .NET 10 bundle) and run this script again. The application itself is self-contained and needs no runtime.'
}
Ok 'ASP.NET Core Module V2 found'

if (Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue) {
    foreach ($feature in 'Web-Windows-Auth', 'Web-AppInit') {
        $f = Get-WindowsFeature -Name $feature
        if ($f -and -not $f.Installed) {
            if ($PSCmdlet.ShouldProcess($feature, 'Install Windows feature')) {
                Install-WindowsFeature -Name $feature | Out-Null
                Ok "Installed Windows feature $feature"
            }
        } else { Ok "Windows feature $feature present" }
    }
} else {
    Warn 'Get-WindowsFeature is not available (client Windows?). Make sure "Windows Authentication" and "Application Initialization" are enabled.'
}

if ($ServiceAccount -and $ServiceAccount -notmatch '\$$') {
    Warn "ServiceAccount '$ServiceAccount' does not end with '$'. A group-managed service account (gMSA) is strongly recommended."
}

# ---------------------------------------------------------------------------------------------
Step 'Deploying files'
if (-not (Test-Path (Join-Path $SourcePath 'Sss.Web.dll')) -or -not (Test-Path (Join-Path $SourcePath 'web.config'))) {
    throw "SourcePath '$SourcePath' does not contain a published SSS application (Sss.Web.dll / web.config missing). Use the 'app' folder of the release zip."
}
if (-not (Test-Path (Join-Path $SourcePath 'Citrix\sss-broker-worker.ps1'))) { throw "Citrix\sss-broker-worker.ps1 is missing in '$SourcePath'." }

$offline = Join-Path $PhysicalPath 'app_offline.htm'
$exists = Test-Path $PhysicalPath
if ($Update -and -not $exists) { throw "-Update needs an existing installation in $PhysicalPath. Run without -Update for the first installation." }
$previous = "$PhysicalPath.previous"
if ($exists -and -not $WhatIfPreference) {
    # keep the running version so a failed update can be undone
    & robocopy $PhysicalPath $previous /MIR /XD logs /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { Warn "Could not back up the current version to $previous (robocopy exit code $LASTEXITCODE)." }
}
if ($PSCmdlet.ShouldProcess($PhysicalPath, 'Copy application files')) {
    New-Item -ItemType Directory -Force -Path $PhysicalPath | Out-Null
    if ($exists) {
        Set-Content -Path $offline -Value '<html><body>SSS is being updated. Please try again in a moment.</body></html>' -Encoding UTF8
        Start-Sleep -Seconds 3
    }
    try {
        & robocopy $SourcePath $PhysicalPath /MIR /XF sss.config.json app_offline.htm /XD logs /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
    } finally {
        Remove-Item $offline -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $PhysicalPath 'logs') | Out-Null
    Ok "Files copied to $PhysicalPath"
}

$configPath = Join-Path $PhysicalPath 'sss.config.json'
if ($Update) {
    Ok 'Update mode: configuration, app pool and permissions stay unchanged'
    if ($PSCmdlet.ShouldProcess($AppPoolName, 'Restart app pool')) { Restart-WebAppPool -Name $AppPoolName }
}
elseif (-not (Test-Path $configPath)) {
    if ($PSCmdlet.ShouldProcess($configPath, 'Write starter configuration')) {
        if ($Controllers.Count -gt 0) {
            $cfg = @{ Sss = @{ Provider = 'Citrix'; Citrix = @{ Controllers = @($Controllers); Workers = 2 } } }
        } else {
            $cfg = @{ Sss = @{ Provider = 'Mock' } }
            Warn 'No -Controllers given: SSS starts in demo mode (sample data). Edit sss.config.json and set Provider to "Citrix".'
        }
        if ($HelpdeskGroup) { $cfg.Sss.Helpdesk = @{ Group = $HelpdeskGroup } }
        $json = ConvertTo-Json -InputObject $cfg -Depth 6
        [System.IO.File]::WriteAllText($configPath, $json, (New-Object System.Text.UTF8Encoding($false)))
        Ok "Wrote $configPath"
    }
} else {
    Ok 'Existing sss.config.json kept'
}

# ---------------------------------------------------------------------------------------------
if (-not $Update) {
Step "Configuring app pool '$AppPoolName'"
if ($PSCmdlet.ShouldProcess($poolIisPath, 'Create/configure app pool')) {
    if (-not (Test-Path $poolIisPath)) { New-WebAppPool -Name $AppPoolName | Out-Null }
    Set-ItemProperty $poolIisPath -Name managedRuntimeVersion -Value ''
    Set-ItemProperty $poolIisPath -Name startMode -Value 'AlwaysRunning'
    Set-ItemProperty $poolIisPath -Name autoStart -Value $true
    Set-ItemProperty $poolIisPath -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
    Set-ItemProperty $poolIisPath -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
    if ($ServiceAccount) {
        Set-ItemProperty $poolIisPath -Name processModel -Value @{ identityType = 'SpecificUser'; userName = $ServiceAccount; password = '' }
        Ok "Identity: $ServiceAccount"
    } else {
        Warn 'Running as ApplicationPoolIdentity. Reaching the Delivery Controller needs a service account with a Citrix role (use -ServiceAccount).'
    }
    Ok 'No managed code, AlwaysRunning, idle timeout 0, periodic recycle off'
}

Step "Configuring application $SiteName/$AppPath"
if ($PSCmdlet.ShouldProcess($appIisPath, 'Create/configure application')) {
    if (-not (Test-Path $appIisPath)) {
        New-WebApplication -Name $AppPath -Site $SiteName -PhysicalPath $PhysicalPath -ApplicationPool $AppPoolName | Out-Null
    } else {
        Set-ItemProperty $appIisPath -Name applicationPool -Value $AppPoolName
        Set-ItemProperty $appIisPath -Name physicalPath -Value $PhysicalPath
    }
    Set-ItemProperty $appIisPath -Name preloadEnabled -Value $true

    $location = "$SiteName/$AppPath"
    $auth = '/system.webServer/security/authentication'
    Set-WebConfigurationProperty -Filter "$auth/windowsAuthentication" -Name enabled -Value $true -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $location
    Set-WebConfigurationProperty -Filter "$auth/anonymousAuthentication" -Name enabled -Value $false -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $location
    Ok 'Windows authentication on, anonymous off'
}

Step 'Setting permissions'
$identity = if ($ServiceAccount) { $ServiceAccount } else { "IIS AppPool\$AppPoolName" }
if ($PSCmdlet.ShouldProcess($PhysicalPath, "Grant $identity access")) {
    & icacls $PhysicalPath /grant "${identity}:(OI)(CI)RX" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed for $identity on $PhysicalPath" }
    & icacls (Join-Path $PhysicalPath 'logs') /grant "${identity}:(OI)(CI)M" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed for $identity on logs" }
    Ok "${identity}: read on the application, modify on logs (audit trail)"
}

}   # end of "if (-not $Update)"

if ($HealthEndpoint -and -not $Uninstall) {
    Step 'Load balancer probe'
    # Only /healthz is opened for anonymous access; it answers {"status":"ok"} or 503 and reveals nothing else.
    $healthLocation = "$SiteName/$AppPath/healthz"
    $auth = '/system.webServer/security/authentication'
    if ($PSCmdlet.ShouldProcess($healthLocation, 'Allow anonymous access to /healthz')) {
        Set-WebConfigurationProperty -Filter "$auth/anonymousAuthentication" -Name enabled -Value $true -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $healthLocation
        Set-WebConfigurationProperty -Filter "$auth/windowsAuthentication" -Name enabled -Value $false -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $healthLocation
        # anonymous requests run as the app pool identity (empty user name) instead of IUSR, which has no access to the application folder
        Set-WebConfigurationProperty -Filter "$auth/anonymousAuthentication" -Name userName -Value '' -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $healthLocation
        Ok "GET /$AppPath/healthz is open without credentials"
    }
    if (-not $SkipSmokeTest -and -not $WhatIfPreference) {
        $probeHost = [System.Net.Dns]::GetHostEntry([string]::Empty).HostName
        $probeUrl = if (Get-WebBinding -Name $SiteName -Protocol 'https' -ErrorAction SilentlyContinue) { "https://$probeHost/$AppPath/healthz" } else { "http://localhost/$AppPath/healthz" }
        try {
            $r = Invoke-WebRequest -Uri $probeUrl -UseBasicParsing -TimeoutSec 60
            Ok "GET $probeUrl without credentials -> $($r.StatusCode) $($r.Content)"
        } catch {
            Warn "GET $probeUrl without credentials failed: $($_.Exception.Message) (503 means the Broker is not reachable; 401 means the rule above did not apply, check the site's authentication settings)"
        }
    }
}

$httpsBinding = Get-WebBinding -Name $SiteName -Protocol 'https' -ErrorAction SilentlyContinue
if (-not $Update -and -not $httpsBinding) { Warn "Site '$SiteName' has no HTTPS binding. Publish SSS over HTTPS only (the CSRF cookie is Secure)." }

if ($PSCmdlet.ShouldProcess($AppPoolName, 'Start app pool')) {
    if ((Get-WebAppPoolState -Name $AppPoolName).Value -ne 'Started') { Start-WebAppPool -Name $AppPoolName }
}

# ---------------------------------------------------------------------------------------------
if (-not $SkipSmokeTest -and -not $WhatIfPreference) {
    Step 'Smoke test'
    # The certificate matches the server's host name, not "localhost", so test with the host name.
    $hostName = [System.Net.Dns]::GetHostEntry([string]::Empty).HostName
    $targets = @()
    if ($httpsBinding) { $targets += "https://$hostName/$AppPath/api/me" }
    if (Get-WebBinding -Name $SiteName -Protocol 'http' -ErrorAction SilentlyContinue) { $targets += "http://localhost/$AppPath/api/me" }
    $passed = $false
    foreach ($url in $targets) {
        try {
            $r = Invoke-WebRequest -Uri $url -UseDefaultCredentials -UseBasicParsing -TimeoutSec 60
            Ok "GET $url -> $($r.StatusCode) $($r.Content)"
            $passed = $true
            break
        } catch {
            Warn "GET $url failed: $($_.Exception.Message)"
        }
    }
    if (-not $passed -and $Update -and (Test-Path $previous)) {
        Warn 'The updated version does not answer. Restoring the previous version.'
        Set-Content -Path $offline -Value '<html><body>SSS is being restored.</body></html>' -Encoding UTF8
        try { & robocopy $previous $PhysicalPath /MIR /XF sss.config.json app_offline.htm /XD logs /NFL /NDL /NJH /NJS /NP | Out-Null }
        finally { Remove-Item $offline -Force -ErrorAction SilentlyContinue }
        Restart-WebAppPool -Name $AppPoolName
        throw 'Update failed and was rolled back. The previous version is running again.'
    }
    if (-not $passed) {
        Write-Host '    Open the page in a browser using the server host name, and check the Windows event log (source "IIS AspNetCore Module V2").'
    }
}

Write-Host ''
Write-Host "Done. SSS: $SiteName/$AppPath  |  files: $PhysicalPath  |  config: $configPath" -ForegroundColor Green
Write-Host 'Next: run .\Test-SssSetup.ps1 (checks the wiring and prints a fix for each finding), then Test-SssCitrix.ps1 as the service account.'
