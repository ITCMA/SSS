<#
.SYNOPSIS
  Checks an SSS configuration without starting the web application: errors that would stop SSS, and warnings about risky or incomplete settings.

.DESCRIPTION
  Runs the same checks SSS runs at startup (Sss.Web.exe --check-config) and lists the warnings too.
  Without -Path it checks what is installed (sss.config.json and sss.local.json in the install folder, plus environment variables).
  With -Path it checks that one file (and a sss.local.json next to it) on top of the defaults, so a new configuration can be validated
  before it is copied to a server. Exit code: 0 = no errors, 1 = errors, 2 = file not found or program missing.

  For editing, point your editor at docs\sss.config.schema.json: Visual Studio Code then offers completion, the description of every setting and
  warnings for unknown keys. Start from one of the files in docs\examples.

.PARAMETER Path
  A configuration file to check instead of the installed one.

.PARAMETER InstallFolder
  Where SSS is installed (the -PhysicalPath of install.ps1). Sss.Web.exe is expected in it.

.EXAMPLE
  .\Test-SssConfig.ps1

.EXAMPLE
  .\Test-SssConfig.ps1 -Path .\docs\examples\sss.config.enterprise.json
#>
[CmdletBinding()]
param(
    [string]$Path = '',
    [string]$InstallFolder = 'C:\inetpub\sss'
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $InstallFolder 'Sss.Web.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    # next to the unpacked release: the application is in the "app" folder
    $alt = Join-Path $PSScriptRoot 'app\Sss.Web.exe'
    if (Test-Path -LiteralPath $alt) { $exe = $alt }
    else { Write-Host "Sss.Web.exe not found in $InstallFolder (use -InstallFolder)." -ForegroundColor Red; exit 2 }
}

if ($Path) {
    if (-not (Test-Path -LiteralPath $Path)) { Write-Host "File not found: $Path" -ForegroundColor Red; exit 2 }
    $output = & $exe --check-config (Resolve-Path -LiteralPath $Path).Path 2>&1
} else {
    $output = & $exe --check-config 2>&1
}
$code = $LASTEXITCODE

foreach ($line in $output) {
    $text = [string]$line
    if ($text.StartsWith('[ERROR]')) { Write-Host $text -ForegroundColor Red }
    elseif ($text.StartsWith('[WARN')) { Write-Host $text -ForegroundColor Yellow }
    elseif ($text.StartsWith('OK')) { Write-Host $text -ForegroundColor Green }
    else { Write-Host $text }
}
exit $code
