<#
.SYNOPSIS
  Compares sss.config.json on several SSS servers and, on request, rolls a reference configuration out to them one server at a time.

.DESCRIPTION
  Without -Apply nothing is changed: the script reads the configuration of every server (through the administrative share), compares it
  with the reference and names the sections that differ. Whitespace and the order of keys do not matter, the order of list entries does.

  With -Apply each server that differs is handled in turn, so the others keep serving:
    1. the old file is saved next to the new one (sss.config.json.bak-<time>),
    2. the server is taken out of the load balancer: SSS answers 503 on /healthz while data\drain.flag exists,
    3. after -DrainSeconds the file is replaced and the application pool is recycled (SSS reads its configuration at startup),
    4. the recycle ends the drain, the server answers /healthz again.
  Use -NoRestart to copy the file only and recycle yourself later.

  Settings that belong to one server only (paths, a local bootstrap controller) go into sss.local.json, which this script never touches.

  Needs: read/write access to the administrative share of the servers (\\server\C$) and, for the recycle, PowerShell remoting (WinRM) to them.
  Compatible with Windows PowerShell 5.1.

.PARAMETER Servers
  Computer names of all SSS servers (the same list as Sss:Instances:Expected).

.PARAMETER ReferenceFile
  A local sss.config.json that is the reference (for example from your configuration repository). Without it the first server is the reference.

.PARAMETER RemoteFolder
  Installation folder on the servers (the -PhysicalPath of install.ps1).

.PARAMETER AppPoolName
  Application pool to recycle.

.PARAMETER Apply
  Roll the reference out. Without it the script only compares.

.PARAMETER DrainSeconds
  How long to wait after the drain flag is set before the file is replaced. Make it longer than the probe interval of the load balancer times its failure count.

.EXAMPLE
  .\Sync-SssConfig.ps1 -Servers iis01,iis02,iis03

.EXAMPLE
  .\Sync-SssConfig.ps1 -Servers iis01,iis02,iis03 -ReferenceFile .\sss.config.json -Apply -DrainSeconds 60
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)][string[]]$Servers,
    [string]$ReferenceFile = '',
    [string]$RemoteFolder = 'C:\inetpub\sss',
    [string]$AppPoolName = 'SSS',
    [switch]$Apply,
    [int]$DrainSeconds = 45,
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'
function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "    $m" -ForegroundColor Green }
function Warn($m) { Write-Host "    WARNING: $m" -ForegroundColor Yellow }

if ($Servers | Where-Object { $_ -notmatch '^[A-Za-z0-9._-]{1,253}$' }) { throw 'A server name contains invalid characters.' }
if ($RemoteFolder -notmatch '^[A-Za-z]:\\') { throw "RemoteFolder must start with a drive letter, for example C:\inetpub\sss." }
if ($AppPoolName -notmatch '^[A-Za-z0-9_ -]+$') { throw 'AppPoolName contains invalid characters.' }
if ($DrainSeconds -lt 0 -or $DrainSeconds -gt 600) { throw 'DrainSeconds must be between 0 and 600.' }

function Get-RemotePath($server, $relative) {
    '\\{0}\{1}$\{2}' -f $server, $RemoteFolder.Substring(0, 1), (Join-Path $RemoteFolder.Substring(3) $relative)
}

# Canonical text: keys sorted and lower case, list order kept. Windows PowerShell 5.1 cannot read comments in JSON; then the raw text without white space is used.
function ConvertTo-Canonical($value) {
    if ($null -eq $value) { return 'null' }
    if ($value -is [string]) { return '"' + $value + '"' }
    if ($value -is [bool]) { if ($value) { return 'true' } else { return 'false' } }
    if ($value -is [System.Management.Automation.PSCustomObject]) {
        $parts = @($value.PSObject.Properties | Sort-Object { $_.Name.ToLowerInvariant() } | ForEach-Object { '"' + $_.Name.ToLowerInvariant() + '":' + (ConvertTo-Canonical $_.Value) })
        return '{' + ($parts -join ',') + '}'
    }
    if ($value -is [System.Collections.IEnumerable]) { return '[' + ((@($value) | ForEach-Object { ConvertTo-Canonical $_ }) -join ',') + ']' }
    return ([string]$value)
}

function Read-Config($path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $raw = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    $result = @{ Raw = $raw; Sections = @{}; Whole = ''; Parsed = $true }
    try {
        $json = $raw | ConvertFrom-Json
        $result.Whole = ConvertTo-Canonical $json
        $root = $json
        if ($json.PSObject.Properties['Sss']) { $root = $json.Sss }
        foreach ($p in $root.PSObject.Properties) { $result.Sections[$p.Name] = ConvertTo-Canonical $p.Value }
    } catch {
        $result.Parsed = $false
        $result.Whole = ($raw -replace '\s', '')
    }
    return $result
}

function Get-Differences($reference, $other) {
    if ($null -eq $other) { return @('(file missing)') }
    if ($reference.Whole -eq $other.Whole) { return @() }
    if (-not $reference.Parsed -or -not $other.Parsed) { return @('(file content; contains comments, no section detail)') }
    $names = @($reference.Sections.Keys) + @($other.Sections.Keys) | Sort-Object -Unique
    return @($names | Where-Object { $reference.Sections[$_] -ne $other.Sections[$_] })
}

Step 'Reading the configuration of every server'
$configs = @{}
foreach ($s in $Servers) {
    $path = Get-RemotePath $s 'sss.config.json'
    try { $configs[$s] = Read-Config $path; Ok "$s : $path" }
    catch { $configs[$s] = $null; Warn "$s : not readable ($($_.Exception.Message))" }
}

if ($ReferenceFile) {
    $reference = Read-Config $ReferenceFile
    if ($null -eq $reference) { throw "ReferenceFile '$ReferenceFile' not found." }
    $referenceName = $ReferenceFile
} else {
    $referenceName = $Servers[0]
    $reference = $configs[$referenceName]
    if ($null -eq $reference) { throw "The reference server $referenceName has no readable sss.config.json." }
}

Step "Comparison with the reference ($referenceName)"
$drift = @()
foreach ($s in $Servers) {
    $diff = @(Get-Differences $reference $configs[$s])
    if ($diff.Count -eq 0) { Ok "$s : identical" }
    else { Warn "$s : differs in $($diff -join ', ')"; $drift += $s }
}
if ($drift.Count -eq 0) { Write-Host 'All servers have the same configuration.' -ForegroundColor Green; return }
if (-not $Apply) {
    Write-Host ''
    Write-Host "$($drift.Count) server(s) differ. Run again with -Apply to roll the reference out (one server at a time)." -ForegroundColor Yellow
    exit 2
}

foreach ($s in $drift) {
    if ($ReferenceFile -eq '' -and $s -eq $referenceName) { continue }
    if (-not $PSCmdlet.ShouldProcess($s, 'Replace sss.config.json and recycle the application pool')) { continue }
    Step "Updating $s"
    $target = Get-RemotePath $s 'sss.config.json'
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    if (Test-Path -LiteralPath $target) { Copy-Item -LiteralPath $target -Destination "$target.bak-$stamp"; Ok "Old file saved as sss.config.json.bak-$stamp" }

    $dataFolder = Get-RemotePath $s 'data'
    New-Item -ItemType Directory -Force -Path $dataFolder | Out-Null
    $flag = Join-Path $dataFolder 'drain.flag'
    if ($DrainSeconds -gt 0) {
        Set-Content -LiteralPath $flag -Value 'drain' -Encoding ASCII
        Ok "Drain flag set, waiting $DrainSeconds s for the load balancer"
        Start-Sleep -Seconds $DrainSeconds
    }

    if ($ReferenceFile) { Copy-Item -LiteralPath $ReferenceFile -Destination $target -Force }
    else { Copy-Item -LiteralPath (Get-RemotePath $referenceName 'sss.config.json') -Destination $target -Force }
    Ok 'New configuration copied'

    if ($NoRestart) { Warn 'Application pool not recycled (-NoRestart): the new file is read at the next start; the drain flag stays until then.'; continue }
    try {
        Invoke-Command -ComputerName $s -ScriptBlock { param($pool) Import-Module WebAdministration; Restart-WebAppPool -Name $pool } -ArgumentList $AppPoolName
        Ok "Application pool $AppPoolName recycled; the start of SSS ends the drain"
    } catch {
        Warn "Could not recycle the pool on $s ($($_.Exception.Message)). Recycle it yourself; the drain flag ends with the restart."
    }
}

Write-Host ''
Write-Host 'Done. Check the Status tab of SSS (servers): every server should show "configuration in sync".' -ForegroundColor Green
