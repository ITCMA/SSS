# Set-up guide

[Deutsch](SETUP.de.md)

This guide takes you from nothing to a working SSS on your Citrix site. The short version:

1. **Try the demo** on any computer (no Citrix needed): [Try it locally](#try-it-locally-macos-linux-windows).
2. **Prepare a service account** (a gMSA) with a Citrix role: [Prepare the service account](#prepare-the-service-account-gmsa).
3. **Install on IIS** with `install.ps1` and check it with `Test-SssSetup.ps1`: [Install on IIS](#install-on-iis-windows-server).
4. **Connect to your Citrix site** and run `Test-SssCitrix.ps1`: [Connecting to a real Citrix site](#connecting-to-a-real-citrix-site).
5. **Switch on the helpdesk**: [Helpdesk](#helpdesk).
6. Optional: [ending processes and profile analysis on the VDAs](PROCESSES.md), [several controllers and servers](MULTI-SERVER.md).

What each option does is in the [configuration reference](CONFIGURATION.md); what the server needs in the [requirements](REQUIREMENTS.md); day-to-day work in [operations](OPERATIONS.md).

## Try it locally (macOS, Linux, Windows)

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/ITCMA/SSS.git && cd SSS
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5080 dotnet run --project src/Sss.Web
```

Open <http://localhost:5080>. In development a fixed demo user and sample data are used.

```bash
dotnet test
```

## Prepare the service account (gMSA)

SSS should run under a **group managed service account (gMSA)**: an Active Directory account for services whose password is generated and rotated by Windows itself. Nobody knows or types it, it cannot be used for interactive logons and it never expires. It is recognisable by the trailing `$`, for example `CORP\svc-sss$`. The account is what SSS uses to talk to the Delivery Controller, so it needs exactly the Citrix rights SSS requires and nothing else.

**1. Create the gMSA** (Domain Admin; AD PowerShell module):

```powershell
# once per domain, only if no KDS root key exists yet (effective immediately in a lab):
Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))

# 'IIS-SERVER$' = computer account of the server that runs SSS (or a security group of such servers)
New-ADServiceAccount -Name svc-sss -DNSHostName svc-sss.corp.local `
  -PrincipalsAllowedToRetrieveManagedPassword 'IIS-SERVER$'
```

**2. Install and test it on the IIS server** (elevated PowerShell; needs the AD module / RSAT):

```powershell
Install-ADServiceAccount svc-sss
Test-ADServiceAccount svc-sss      # must return True
```

**3. Give it a Citrix role.** Citrix Studio's account picker usually does not list gMSAs, so create the administrator with PowerShell on a Delivery Controller (elevated PowerShell). This is the recommended way:

```powershell
Add-PSSnapin Citrix.DelegatedAdmin.Admin.V1
New-AdminAdministrator -Name 'CORP\svc-sss$' -Enabled $true
Add-AdminRight -Administrator 'CORP\svc-sss$' -Role 'Help Desk Administrator' -All
Get-AdminAdministrator | Select-Object Name, Enabled, @{n='Rights';e={ @($_.Rights).Count }}   # Rights must be 1 or more
```

`New-AdminAdministrator` alone creates an administrator **without any rights** (`Rights : {}`) – `Add-AdminRight` is required. The built-in **Help Desk Administrator** role may read sessions, log them off and read Monitor data; create a stricter custom role once you know what you need (see [SECURITY.md](../SECURITY.md)). Afterwards restart the SSS app pool.

*Alternative via an AD group:* add `svc-sss$` to a security group and add that group in Studio. This works, but a running app pool keeps the token it was started with, so a membership change may only take effect after a full restart of the app pool (sometimes after a reboot). The direct entry above avoids that problem.

**4. Pass it to the installer** with `-ServiceAccount 'CORP\svc-sss$'` (single quotes, otherwise PowerShell expands the `$`). The installer sets it as the app pool identity and grants it read access to the application and write access to the audit log folder.

Without `-ServiceAccount` the app pool runs as `ApplicationPoolIdentity`, which authenticates to other servers as the computer account and has no Citrix rights – fine for a first look at the UI, not for real use. Only gMSAs are supported by the installer (it sets an empty app pool password).

## Install on IIS (Windows Server)

Prerequisites on the server: IIS with the [.NET Hosting Bundle](https://dotnet.microsoft.com/download/dotnet) (for the ASP.NET Core Module; SSS itself is self-contained), the Citrix PowerShell snap-ins (or the Remote PowerShell SDK), and a [group-managed service account](#prepare-the-service-account-gmsa) with a Citrix role.

1. Download `sss-<version>-win-x64.zip` and its `.sha256` file from the [releases](https://github.com/ITCMA/SSS/releases), check the checksum (`Get-FileHash`) and extract the zip on the server.
2. In an elevated Windows PowerShell:

```powershell
.\install.ps1 -Interactive            # asks for controllers, service account, helpdesk group
.\install.ps1 -Controllers ddc01.corp.local,ddc02.corp.local -ServiceAccount 'CORP\svc-sss$'   # or non-interactive
```

3. Check the wiring: `.\Test-SssSetup.ps1`. It is read-only and prints the exact command that fixes each finding (IIS, app pool, gMSA, Citrix administrator entry, helpdesk group, permissions, the application itself). Which account needs which permission is summarised in [Permissions at a glance](PERMISSIONS.md).

By default SSS becomes the application `Default Web Site/sss`, so on a Web Studio server it lives next to Studio. Use `-SiteName`, `-AppPath`, `-PhysicalPath` to change that and `-WhatIf` to preview. The script creates a dedicated app pool (no managed code, *AlwaysRunning*, no idle timeout, no periodic recycle), enables Windows authentication, disables anonymous access, sets permissions, writes a starter `sss.config.json` (kept on updates) and runs a smoke test. Update with `.\install.ps1 -Update`: only the files are replaced (configuration, app pool and permissions stay as they are), the previous version is kept in `<install folder>.previous`, and it is restored automatically if the smoke test fails. The site goes briefly offline via `app_offline.htm`. `-Uninstall` removes the application and app pool but keeps files, configuration and audit logs.

## Configuration

Defaults live in `src/Sss.Web/appsettings.json`. Override them in a file `sss.config.json` next to the application (section `Sss`), for example:

```json
{
  "Sss": {
    "RefreshSeconds": 30,
    "RateLimit": { "LogoffRequestsPerMinute": 10 },
    "Quality": { "RttWarnMs": 80, "RttCritMs": 150 }
  }
}
```

A restart (app pool recycle) is required after changes.

## Connecting to a real Citrix site

Set `Sss:Provider` to `Citrix` in `sss.config.json`:

```json
{
  "Sss": {
    "Provider": "Citrix",
    "Citrix": {
      "Controllers": [ "ddc01.corp.local", "ddc02.corp.local" ],
      "Workers": 2,
      "Monitor": { "BaseUrl": "https://ddc01.corp.local/Citrix/Monitor/OData/v4/Data/" }
    }
  }
}
```

- **Live sessions and log-off** use the Broker SDK through warm Windows PowerShell 5.1 workers. The server needs the Citrix PowerShell snap-ins (installed with Studio/Web Studio) or the *Remote PowerShell SDK* when SSS runs on a separate server.
- **History and quality** come from the Monitor OData API. If it is unavailable the UI shows a notice and everything else keeps working.
- The app pool identity (use a gMSA) needs a Citrix delegated-administration role that can read sessions, log them off, and read Monitor data (e.g. a custom role based on *Help Desk Administrator*).
- Before the first use run `scripts/Test-SssCitrix.ps1` on the server. It is read-only and verifies connectivity and the exact OData property names. Run it as `.\Test-SssCitrix.ps1 -Controller ddc01 -UserName CORP\alice` (the user name must be `DOMAIN\username`) while that user has a session. Verified on CVAD 2507 CU1: all Monitor fields SSS uses exist; bandwidth is rated from `SessionMetrics.OutputBandwidthAvailable` (bit/s). The Monitor is addressed over `https://` by default; set `Citrix:Monitor:UseHttps` to `false` for a controller without an HTTPS binding, or give the full address in `Citrix:Monitor:BaseUrl`. Before posting the output of `Test-SssSetup.ps1` or `Test-SssCitrix.ps1` in a public issue add `-Redact`: it replaces user, domain, host names, SIDs, IP addresses and GUIDs (`-Mask word1,word2` adds more).
- "Log off" starts the log-off (`Stop-BrokerSession` is asynchronous); the session disappears from the list within a few seconds.

## Helpdesk

Members of an AD group can find users and see and log off **their** sessions. Everyone else gets HTTP 403 on all helpdesk endpoints and never sees the tab.

1. Create a security group, e.g. `SSS-Helpdesk`, and add the helpdesk staff: `New-ADGroup -Name SSS-Helpdesk -GroupScope Global -GroupCategory Security`.
2. Put the group into `sss.config.json` (or pass `-HelpdeskGroup 'CORP\SSS-Helpdesk'` to `install.ps1` on a fresh install) and recycle the app pool:

```json
{ "Sss": { "Helpdesk": { "Group": "CORP\\SSS-Helpdesk", "ReasonRequired": false, "MaxSessions": 1000 } } }
```

3. Helpdesk staff must log on again after being added to the group, because group memberships live in the Windows logon token.

In the **Helpdesk** tab you can search by part of a name (users with a live session from the Broker, users with history only from Monitor) or pick a user from the list of **all live sessions** of the farm (filterable, capped at `MaxSessions`). Opening a user shows the same tables as the user sees, with the same log-off buttons and an optional **reason**. Set `ReasonRequired` to make the reason mandatory.

In a user's live-session table the **Message** button shows a short text (max. 300 characters, title `Sss:Helpdesk:MessageTitle`) in the session, for example "please save your work, the server is restarting". The **Details** buttons of the expert view work for every session of the chosen user.

Everything a helpdesk user does is written to the audit log with the acting user, the affected user and the reason: searches, opening a user (at most once per 5 minutes per user), the farm overview, and every log-off. Helpdesk requests are rate limited (60/min). The helpdesk uses the same service account and Citrix role as the self service, so no additional Citrix rights are needed. Whoever has access to the helpdesk can see all users' sessions: keep the group small.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Page loads but shows "The service is temporarily unavailable" (HTTP 503) | Look in the Windows event log (*Application*, source `.NET Runtime`, text `An unhandled exception has occurred`). `Insufficient administrative privilege` means the app pool identity is not a Citrix administrator or its role/scope does not allow reading sessions: add the gMSA in Studio under *Administrators* with e.g. the *Help Desk Administrator* role and scope *All*. |
| `Insufficient administrative privilege` although the right exists (works as a scheduled task under the gMSA, fails in IIS) | The running app pool still has an old token, typically after a group membership change. Add the gMSA directly as a Citrix administrator with a role (see step 3 above) and restart the app pool completely. |
| Anything unclear | Open the **Status** tab (helpdesk) or run `Test-SssSetup.ps1`: both check the same things and name the failing one. |
| "Something went wrong" right after loading | The browser could not reach the API. Open the page via the exact URL ending in `/sss/` and check the network tab. |
| History or quality missing, notice shown | The Monitor OData service is not reachable or the account may not read it; run `Test-SssCitrix.ps1`. |
| `Test-SssCitrix.ps1` says "UserName must be in the format DOMAIN\username" | Pass the user as `CORP\alice`. |
