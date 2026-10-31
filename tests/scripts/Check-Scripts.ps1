<#
  Checks the PowerShell scripts of SSS without a Citrix site. Runs in Windows PowerShell 5.1 and in PowerShell 7.
  On Windows it also runs the code of the JEA commands (Get-SssProcess, Get-SssProfile, Stop-SssProcess) for the current user.
  Exit code 0 = all checks passed.
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$script:failed = 0
function Check($name, [scriptblock]$test) {
    try { & $test; Write-Host "[ OK ] $name" -ForegroundColor Green }
    catch { Write-Host "[FAIL] $name : $($_.Exception.Message)" -ForegroundColor Red; $script:failed++ }
}
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Parse($path) {
    $errors = $null; $tokens = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    if ($errors) { throw ($errors | ForEach-Object { $_.Message }) -join '; ' }
    $ast
}

Write-Host "PowerShell $($PSVersionTable.PSVersion) on $([System.Environment]::OSVersion.Platform)"

Check 'all scripts parse' {
    foreach ($f in Get-ChildItem (Join-Path $root 'scripts'), (Join-Path $root 'src\Sss.Web\Citrix') -Filter *.ps1) { [void](Parse $f.FullName) }
}

Check '-Redact masks user, host, SID, IP and GUID' {
    $ast = Parse (Join-Path $root 'scripts\Test-SssCitrix.ps1')
    $defs = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in 'Add-MaskTerm', 'Hide-Sensitive' }, $true)
    Assert ($defs.Count -eq 2) 'mask functions not found'
    $script:maskTerms = @()
    foreach ($d in $defs) { Invoke-Expression $d.Extent.Text }
    Add-MaskTerm 'alice' '<user>'
    Add-MaskTerm 'CORP' '<domain>'
    Add-MaskTerm 'corp.local' '<domain>.local'
    $in = 'CORP\alice on ddc01.corp.local 10.1.2.3 S-1-5-21-111-222-333-1001 0f8fad5b-d9cb-469f-a165-70867728950e PowerShell 5.1.17763.9376 Session.SessionKey'
    $out = Hide-Sensitive $in
    foreach ($leak in 'alice', 'ddc01', '10.1.2.3', '111-222', '0f8fad5b') { Assert (-not $out.Contains($leak)) "'$leak' still visible: $out" }
    Assert ($out.Contains('5.1.17763.9376') -and $out.Contains('Session.SessionKey')) "over-masked: $out"
}

Check 'unknown-key hint and install parameters exist' {
    $ast = Parse (Join-Path $root 'scripts\install.ps1')
    $names = $ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath }
    foreach ($p in 'Update', 'Interactive', 'Uninstall', 'HealthEndpoint', 'ServiceAccount', 'Controllers', 'HelpdeskGroup') { Assert ($names -contains $p) "install.ps1 lacks -$p" }
    $jea = (Parse (Join-Path $root 'scripts\Install-SssJea.ps1')).ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath }
    foreach ($p in 'Principal', 'AllowedFrom', 'TranscriptDirectory', 'ConfigurationName', 'Remove') { Assert ($jea -contains $p) "Install-SssJea.ps1 lacks -$p" }
}

if ($env:OS -ne 'Windows_NT') { Write-Host 'Not Windows: JEA command checks skipped.'; if ($script:failed) { exit 1 } else { exit 0 } }

# --- the JEA commands, run as ordinary functions for the current user -------------------------------------------------
$jeaText = Get-Content (Join-Path $root 'scripts\Install-SssJea.ps1') -Raw
$start = $jeaText.IndexOf('    $functions = @(')
$end = $jeaText.IndexOf('    $psrc = Join-Path')
Assert ($start -ge 0 -and $end -gt $start) 'cannot find the function definitions in Install-SssJea.ps1'
Invoke-Expression ($jeaText.Substring($start, $end - $start))
foreach ($f in $functions) { Set-Item -Path "function:global:$($f.Name)" -Value $f.ScriptBlock }
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value

Check 'role capability and session configuration files are valid' {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('sss-jea-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        New-PSRoleCapabilityFile -Path (Join-Path $tmp 'SssProcesses.psrc') -VisibleFunctions 'Get-SssProcess', 'Get-SssProfile', 'Stop-SssProcess' -FunctionDefinitions $functions
        New-PSSessionConfigurationFile -Path (Join-Path $tmp 'SSS.pssc') -SessionType RestrictedRemoteServer -RunAsVirtualAccount -TranscriptDirectory $tmp `
            -RoleDefinitions @{ 'CORP\svc-sss$' = @{ RoleCapabilities = 'SssProcesses' } }
        [void](Parse (Join-Path $tmp 'SssProcesses.psrc')); [void](Parse (Join-Path $tmp 'SSS.pssc'))
    } finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

Check 'Get-SssProcess lists the own processes with program paths' {
    $list = @(Get-SssProcess -UserSid $sid)
    Assert ($list.Count -gt 0) 'no processes returned'
    $me = $list | Where-Object { $_.Id -eq $PID }
    Assert ($me) 'own process missing'
    Assert ($me.Path) 'program path missing'
}

Check 'Get-SssProcess refuses an invalid SID' {
    $threw = $false
    try { Get-SssProcess -UserSid "S-1-5-21-1' or '1'='1" | Out-Null } catch { $threw = $true }
    Assert $threw 'invalid SID was accepted'
}

Check 'Get-SssProfile reads type and size of the current profile' {
    $p = Get-SssProfile -UserSid $sid -ScanSeconds 10
    Assert ($p.Type) 'no type'
    Assert ($p.LocalPath) 'no local path'
    Assert ($null -ne $p.SizeMb) 'no size'
}

Check 'Stop-SssProcess ends an own process, refuses unknown and foreign ones' {
    $child = Start-Process -FilePath "$env:SystemRoot\System32\ping.exe" -ArgumentList '-n', '60', '127.0.0.1' -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 500
    Assert ((Stop-SssProcess -UserSid $sid -ProcessId $child.Id -Force) -eq 'killed') 'own process not ended'
    Assert ((Stop-SssProcess -UserSid $sid -ProcessId 2147483647) -eq 'not-found') 'unknown id not refused'
    $other = 'S-1-5-18'   # SYSTEM: processes of the current user do not belong to it
    Assert ((Stop-SssProcess -UserSid $other -ProcessId $PID -Force) -ne 'killed') 'foreign process was ended'
}

if ($script:failed) { Write-Host "$script:failed check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All script checks passed.' -ForegroundColor Green
