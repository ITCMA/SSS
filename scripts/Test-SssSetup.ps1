<#
.SYNOPSIS
  Checks an SSS installation on the IIS server and prints the exact command that fixes each finding.

.DESCRIPTION
  Run in an elevated Windows PowerShell on the IIS server. Read-only: it changes nothing.
  Checks IIS (ASP.NET Core Module, Windows authentication), the app pool and its identity, the group-managed
  service account, the Citrix administrator entry of that account, the helpdesk group, the configuration file,
  folder permissions and finally calls the application itself.
  Test-SssCitrix.ps1 goes deeper into the Citrix data; this script answers "is everything wired up?".

.EXAMPLE
  .\Test-SssSetup.ps1 -Controller ddc01.corp.local

.NOTES
  Add -Redact before pasting the output into a public issue: it replaces user, domain, host names, SIDs,
  IP addresses and GUIDs (-Mask adds more words).
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Controller = '',
    [string]$SiteName = 'Default Web Site',
    [string]$AppPath = 'sss',
    [string]$AppPoolName = 'SSS',
    [string]$PhysicalPath = 'C:\inetpub\sss',
    [switch]$Redact,
    [string[]]$Mask = @()
)

$ErrorActionPreference = 'Continue'
$script:problems = 0
$script:warnings = 0

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
    Add-MaskTerm $Controller '<controller>'
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
function Bad($m, $fix) {
    Write-Host "[FAIL] $m" -ForegroundColor Red
    if ($fix) { Write-Host "       Fix: $fix" -ForegroundColor Yellow }
    $script:problems++
}
function Warn($m, $fix) {
    Write-Host "[WARN] $m" -ForegroundColor Yellow
    if ($fix) { Write-Host "       Hint: $fix" -ForegroundColor Yellow }
    $script:warnings++
}
function Info($m) { Write-Host "[info] $m" }

Write-Host '== SSS setup check ==' -ForegroundColor Cyan

# 1. IIS ------------------------------------------------------------------------------------------
try { Import-Module WebAdministration -ErrorAction Stop } catch { Bad 'IIS management module (WebAdministration) not available' 'Install-WindowsFeature Web-Scripting-Tools'; return }

if (Get-WebGlobalModule -Name 'AspNetCoreModuleV2' -ErrorAction SilentlyContinue) { Ok 'ASP.NET Core Module V2 installed' }
else { Bad 'ASP.NET Core Module V2 missing' 'Install the ".NET Hosting Bundle" (current .NET 10 bundle) and run "iisreset"' }

$appIisPath = "IIS:\Sites\$SiteName\$AppPath"
if (Test-Path $appIisPath) { Ok "IIS application $SiteName/$AppPath exists" }
else { Bad "IIS application $SiteName/$AppPath not found" '.\install.ps1 (see README, "Install")'; return }

$loc = "$SiteName/$AppPath"
$auth = '/system.webServer/security/authentication'
$win = (Get-WebConfigurationProperty -Filter "$auth/windowsAuthentication" -Name enabled -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $loc).Value
$anon = (Get-WebConfigurationProperty -Filter "$auth/anonymousAuthentication" -Name enabled -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $loc).Value
if ($win -and -not $anon) { Ok 'Windows authentication on, anonymous off' }
else { Bad "Authentication wrong (Windows: $win, Anonymous: $anon)" "Set-WebConfigurationProperty -Filter '$auth/windowsAuthentication' -Name enabled -Value `$true -PSPath 'MACHINE/WEBROOT/APPHOST' -Location '$loc'; and anonymousAuthentication -Value `$false" }

if (Get-WebBinding -Name $SiteName -Protocol 'https' -ErrorAction SilentlyContinue) { Ok 'Site has an HTTPS binding' }
else { Warn "Site '$SiteName' has no HTTPS binding" 'Publish SSS over HTTPS only: New-WebBinding -Name <site> -Protocol https -Port 443 -SslFlags 0 (and bind a certificate)' }

# 2. App pool and identity ------------------------------------------------------------------------
$poolPath = "IIS:\AppPools\$AppPoolName"
$identity = $null
if (Test-Path $poolPath) {
    $pool = Get-Item $poolPath
    if ($pool.state -eq 'Started') { Ok "App pool '$AppPoolName' is started" } else { Bad "App pool '$AppPoolName' is $($pool.state)" "Start-WebAppPool -Name '$AppPoolName'" }
    if ($pool.startMode -eq 'AlwaysRunning') { Ok 'App pool is AlwaysRunning (no cold start)' } else { Warn "startMode is $($pool.startMode)" "Set-ItemProperty '$poolPath' -Name startMode -Value AlwaysRunning" }
    $identity = if ($pool.processModel.identityType -eq 'SpecificUser') { $pool.processModel.userName } else { $pool.processModel.identityType }
    Info "App pool identity: $identity"
} else {
    Bad "App pool '$AppPoolName' not found" '.\install.ps1'
}

# 3. Service account (gMSA) -----------------------------------------------------------------------
if ($identity -and $identity -match '\$$') {
    $sam = ($identity -split '\\')[-1].TrimEnd('$')
    if (Get-Command Test-ADServiceAccount -ErrorAction SilentlyContinue) {
        if (Test-ADServiceAccount -Identity $sam) { Ok "gMSA '$identity' can be used on this server" }
        else { Bad "gMSA '$identity' is not usable on this server" "Install-ADServiceAccount $sam ; and allow this server in PrincipalsAllowedToRetrieveManagedPassword" }
    } else { Info 'ActiveDirectory module not installed; skipped Test-ADServiceAccount' }
} elseif ($identity) {
    Warn "App pool does not run as a group-managed service account ($identity)" 'Create a gMSA and pass it to install.ps1 with -ServiceAccount (see README, "Prepare the service account")'
}

# 4. Configuration --------------------------------------------------------------------------------
$cfgPath = Join-Path $PhysicalPath 'sss.config.json'
$cfg = $null
if (Test-Path $cfgPath) {
    try { $cfg = (Get-Content $cfgPath -Raw | ConvertFrom-Json).Sss; Ok 'sss.config.json is valid JSON' }
    catch { Bad "sss.config.json cannot be parsed: $($_.Exception.Message)" "Fix the JSON syntax in $cfgPath" }
} else { Warn 'No sss.config.json (defaults apply: demo mode)' "Create $cfgPath with Provider = Citrix and your Controllers" }
if ($cfg) {
    if ($cfg.Provider -eq 'Citrix') { Ok 'Provider = Citrix' } else { Warn "Provider is '$($cfg.Provider)': SSS shows demo data" 'Set Sss:Provider to "Citrix"' }
    if (-not $Controller -and $cfg.Citrix.Controllers) { $Controller = @($cfg.Citrix.Controllers)[0] }
    if ($Redact) { foreach ($c in @($cfg.Citrix.Controllers)) { Add-MaskTerm $c '<controller>'; if ($c -match '^[^.]+\.(.+)$') { Add-MaskTerm $Matches[1] '<domain>.local' } } }
    if ($cfg.Citrix -and $cfg.Citrix.Monitor -and $cfg.Citrix.Monitor.BaseUrl -like 'http://*') { Warn 'Monitor is accessed over HTTP' 'Set Sss:Citrix:Monitor:BaseUrl to https://<controller>/Citrix/Monitor/OData/v4/Data/' }
    elseif ($cfg.Provider -eq 'Citrix' -and -not ($cfg.Citrix.Monitor -and $cfg.Citrix.Monitor.BaseUrl) -and $cfg.Citrix.Monitor -and $cfg.Citrix.Monitor.UseHttps -eq $false) { Warn 'Monitor uses the default http:// address (UseHttps = false)' 'Remove Sss:Citrix:Monitor:UseHttps or set Sss:Citrix:Monitor:BaseUrl to an https:// address' }
}

# 5. Helpdesk group -------------------------------------------------------------------------------
if ($cfg -and $cfg.Helpdesk -and $cfg.Helpdesk.Group) {
    try {
        [void](New-Object System.Security.Principal.NTAccount($cfg.Helpdesk.Group)).Translate([System.Security.Principal.SecurityIdentifier])
        Ok "Helpdesk group '$($cfg.Helpdesk.Group)' resolves"
    } catch { Bad "Helpdesk group '$($cfg.Helpdesk.Group)' does not resolve (helpdesk features stay off)" 'Create the group (New-ADGroup) or correct Sss:Helpdesk:Group (DOMAIN\Group)' }
} else { Info 'No helpdesk group configured (optional)' }

# 6. Folder permissions ---------------------------------------------------------------------------
if ($identity) {
    $logs = Join-Path $PhysicalPath 'logs'
    if (Test-Path $logs) {
        $acl = (Get-Acl $logs).Access | Where-Object { $_.IdentityReference.Value -like "*$(($identity -split '\\')[-1])*" -and $_.FileSystemRights -match 'Modify|FullControl|Write' }
        if ($acl) { Ok 'Service account may write the audit log folder' }
        else { Bad 'Service account has no write access to the audit log folder' "icacls '$logs' /grant '${identity}:(OI)(CI)M'" }
    } else { Warn 'logs folder does not exist yet' "New-Item -ItemType Directory '$logs'" }
}

# 7. Citrix administrator entry of the service account -------------------------------------------
if ($Controller -and $identity -and $identity -match '\\') {
    try {
        Add-PSSnapin Citrix.DelegatedAdmin.Admin.V1 -ErrorAction Stop
        $admin = Get-AdminAdministrator -AdminAddress $Controller -Name $identity -ErrorAction SilentlyContinue
        if (-not $admin) { Bad "'$identity' is not a Citrix administrator" "New-AdminAdministrator -AdminAddress $Controller -Name '$identity' -Enabled `$true; Add-AdminRight -AdminAddress $Controller -Administrator '$identity' -Role 'Help Desk Administrator' -All" }
        elseif (-not $admin.Enabled) { Bad "Citrix administrator '$identity' is disabled" "Set-AdminAdministrator -AdminAddress $Controller -Name '$identity' -Enabled `$true" }
        elseif (@($admin.Rights).Count -eq 0) { Bad "Citrix administrator '$identity' has no role" "Add-AdminRight -AdminAddress $Controller -Administrator '$identity' -Role 'Help Desk Administrator' -All" }
        else { Ok ("Citrix administrator '{0}' exists with {1} right(s)" -f $identity, @($admin.Rights).Count) }
    } catch { Info "Could not read Citrix administrators (Citrix snap-ins missing or no access): $($_.Exception.Message)" }
}

# 8. The application itself -----------------------------------------------------------------------
$hostName = [System.Net.Dns]::GetHostEntry([string]::Empty).HostName
$urls = @()
if (Get-WebBinding -Name $SiteName -Protocol 'https' -ErrorAction SilentlyContinue) { $urls += "https://$hostName/$AppPath/api/me" }
if (Get-WebBinding -Name $SiteName -Protocol 'http' -ErrorAction SilentlyContinue) { $urls += "http://localhost/$AppPath/api/me" }
foreach ($u in $urls) {
    try {
        $r = Invoke-WebRequest -Uri $u -UseDefaultCredentials -UseBasicParsing -TimeoutSec 60
        Ok "GET $u -> $($r.StatusCode)"
        $me = $r.Content | ConvertFrom-Json
        if ($me.demo) { Warn 'The application runs in demo mode' 'Set Sss:Provider to "Citrix" in sss.config.json' }
        if ($me.helpdesk) {
            $base = $u -replace '/api/me$', ''
            try {
                $st = Invoke-RestMethod -Uri "$base/api/helpdesk/status" -UseDefaultCredentials -TimeoutSec 60
                foreach ($c in $st) {
                    if ($c.ok -eq $true) { Ok ("status: {0} ({1})" -f $c.id, $c.detail) }
                    elseif ($c.ok -eq $false -and $c.id -eq 'monitor' -and $c.detail -match '404') {
                        Bad ("status: {0} ({1})" -f $c.id, $c.detail) ('The Monitor service answers on http:// only (it listens on its own address, not in IIS). Either set Sss:Citrix:Monitor:UseHttps to false, or enable HTTPS in Citrix.Monitor.exe.Config (owin:baseAddress) on the controller, see "Monitor over HTTPS" in docs/MULTI-SERVER.md')
                    }
                    elseif ($c.ok -eq $false) { Bad ("status: {0} ({1})" -f $c.id, $c.detail) 'See README, "Troubleshooting"' }
                    else { Warn ("status: {0} ({1})" -f $c.id, $c.detail) }
                }
            } catch { Info "Status endpoint not readable: $($_.Exception.Message)" }
        } else { Info 'You are not a helpdesk member, so the detailed status check was skipped (the "Status" tab shows the same checks).' }
        $probe = $u -replace '/api/me$', '/healthz'
        try {
            $h = Invoke-WebRequest -Uri $probe -UseBasicParsing -TimeoutSec 30
            Ok "GET $probe without credentials -> $($h.StatusCode) (usable as load balancer probe)"
        } catch { Info "GET $probe without credentials is not answered ($($_.Exception.Message)). A load balancer probe needs: install.ps1 -Update -HealthEndpoint" }
        break
    } catch { Bad "GET $u failed: $($_.Exception.Message)" 'Check the Windows event log (source "IIS AspNetCore Module V2") and the app pool identity' }
}

Write-Host ''
if ($script:problems -eq 0) { Write-Host ("No problems found ({0} warning(s))." -f $script:warnings) -ForegroundColor Green }
else { Write-Host ("{0} problem(s), {1} warning(s)." -f $script:problems, $script:warnings) -ForegroundColor Red }
exit $script:problems
