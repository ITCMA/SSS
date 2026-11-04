<#
.SYNOPSIS
  Checks the Citrix Cloud (DaaS) access of SSS: sign-in with the API client and reading of the Monitor service. Read only.
.DESCRIPTION
  Asks for the secret without showing it (or reads it from the environment variable Sss__Citrix__Cloud__ClientSecret).
  Prints counts and property names only: no names, no addresses.
.EXAMPLE
  .\Test-SssCloud.ps1 -CustomerId mycustomer -ClientId 1234-abcd -Region eu
#>
param(
    [Parameter(Mandatory)][string]$CustomerId,
    [Parameter(Mandatory)][string]$ClientId,
    [ValidateSet('eu', 'us', 'ap-s', 'jp')][string]$Region = 'eu'
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$hostName = @{ eu = 'api-eu.cloud.com'; us = 'api.cloud.com'; 'ap-s' = 'api-ap-s.cloud.com'; jp = 'api.citrixcloud.jp' }[$Region]
function Ok($m) { Write-Host "[ OK ] $m" -ForegroundColor Green }
function Bad($m) { Write-Host "[FAIL] $m" -ForegroundColor Red; $script:failed = $true }
function Info($m) { Write-Host "[info] $m" }

$secret = $env:Sss__Citrix__Cloud__ClientSecret
if (-not $secret) {
    $sec = Read-Host -AsSecureString 'Client secret (not shown)'
    $secret = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
}
try {
    $tok = Invoke-RestMethod -Method Post -Uri "https://$hostName/cctrustoauth2/$CustomerId/tokens/clients" -Body @{ grant_type = 'client_credentials'; client_id = $ClientId; client_secret = $secret }
    Ok "Signed in to https://$hostName (token valid for $($tok.expires_in) s)"
} catch {
    Bad "Sign-in failed: $($_.Exception.Message). Check CustomerId, ClientId, secret and Region."
    exit 1
}
$h = @{ Authorization = "CWSAuth bearer=$($tok.access_token)"; 'Citrix-CustomerId' = $CustomerId }
$base = "https://$hostName/monitorodata/"
try {
    $xml = Invoke-WebRequest -Uri ($base + '$metadata') -Headers $h -UseBasicParsing
    Ok "Monitor OData reachable ($($xml.Content.Length) bytes of `$metadata)"
} catch { Bad "Monitor `$metadata not readable: $($_.Exception.Message)"; exit 1 }
foreach ($q in @('Sessions?$filter=EndDate eq null&$top=5&$select=SessionKey,ConnectionState,StartDate', 'Machines?$top=5&$select=Id,CurrentRegistrationState', 'Users?$top=1&$select=Id', 'Connections?$top=1&$select=Id,ClientLocationCountry', 'ApplicationFaults?$top=1&$select=Id', 'ConnectionFailureLogs?$top=1&$select=Id')) {
    $name = $q.Split('?')[0]
    try {
        $r = Invoke-RestMethod -Uri ($base + $q) -Headers $h
        Ok "$name readable ($(@($r.value).Count) row(s) in the sample)"
    } catch { Info "$name could not be read: $($_.Exception.Message)" }
}
if ($failed) { exit 1 }
Write-Host "`nAll checks passed." -ForegroundColor Green
