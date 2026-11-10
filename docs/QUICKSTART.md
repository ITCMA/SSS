# Quick start

[Deutsche Version](QUICKSTART.de.md)

What to install, where, with which parameters, and a solid basic configuration. All names below (`corp.local`, `CORP\...`, `ddc01`) are placeholders: replace them with your own. Details for each step are in the [set-up guide](SETUP.md).

## What goes where

| # | Where | What | Command | Needed for |
|---|-------|------|---------|-----------|
| 1 | Active Directory | gMSA and security groups | see [set-up guide](SETUP.md#prepare-the-service-account-gmsa) | everything |
| 2 | **IIS server** (e.g. the Web Studio server) | SSS itself | `.\install.ps1 -Interactive` | everything |
| 3 | **IIS server** | edit `sss.config.json`, recycle the app pool | see [basic configuration](#basic-configuration) | everything |
| 4 | **Every VDA** (or the master image) | restricted PowerShell endpoint (JEA) | `.\Install-SssJea.ps1 -Principal 'CORP\svc-sss$' -AllowedFrom sss01.corp.local` | processes, profile, policies, **HDX tab**, **remote support** |
| 5 | **Every helpdesk PC**, once per Windows user | starter for remote support | `.\Register-SssShadow.ps1 -AllowedHostSuffix 'corp.local'` | remote support button |
| 6 | VDAs, by group policy | allow shadowing with the user's permission | see [Remote support](SHADOW.md#set-up) | remote support |

> ### Do not forget step 5
> **`Register-SssShadow.ps1 -AllowedHostSuffix '<your domain>'` must be run on every helpdesk PC, by every Windows user who uses remote support.** Without it the button is visible but a click does nothing. The suffix limits the starter to VDAs of your own domain: always set it. Check it without SSS: press Win+R and enter `sss-shadow://<vda>.<your domain>/<session id>?control=1`; `mstsc` must open.

## Updating

| Where | Command |
|-------|---------|
| IIS server | `.\install.ps1 -Update`, then recycle the app pool |
| VDAs (after a release that changes the endpoint, see the [changelog](../CHANGELOG.md)) | `.\Install-SssJea.ps1 -Principal 'CORP\svc-sss$' -AllowedFrom sss01.corp.local` again |
| Helpdesk PCs | copy the new `Start-SssShadow.ps1`, run `Register-SssShadow.ps1 -AllowedHostSuffix '<your domain>'` again |

## Basic configuration

A solid start for `sss.config.json` (next to the application; recycle the app pool after every change, then check with `.\Test-SssConfig.ps1`):

```json
{
  "$schema": "docs/sss.config.schema.json",
  "Sss": {
    "Provider": "Citrix",
    "Citrix": {
      "Controllers": [ "ddc01.corp.local", "ddc02.corp.local" ],
      "Processes": { "Enabled": true }
    },
    "Roles": {
      "Level1Group": "CORP\\SSS-Helpdesk-L1",
      "Level2Group": "CORP\\SSS-Helpdesk-L2",
      "Level3Group": "CORP\\SSS-Helpdesk-L3",
      "AuditorGroup": "CORP\\SSS-Auditors",
      "AdminGroup": "CORP\\SSS-Admins"
    }
  }
}
```

The example shows only what you must set. Everything else has a default. Remote support (`Sss:Shadow`) is **on by default for all three helpdesk levels** and needs no entry; add it only to switch something off, e.g. `"Shadow": { "AllowLevel1": false }` or `"Control": false` for view only. `StoreFrontUrl` is optional (it only adds an "Open StoreFront" link in the interface) and is left out.

Recommendations:

- Run SSS under a **gMSA** with a read-only Citrix role plus the rights to log off sessions; never under a personal account.
- Keep the **helpdesk groups small** and separate the levels (L1 view, L2 act, L3 deep). Add an auditor group and an admin group.
- Leave **remote support on** (it is on by default) and switch levels off individually with `AllowLevel1/2/3` if needed. The user is always asked for consent.
- Set `ReasonRequired` so that every helpdesk action carries a reason in the audit trail.
- Use `-AllowedFrom` on the VDAs so that only the SSS server can reach the endpoint.
- Send the audit trail to a share (`-AuditShare` or `Sss:Audit:Sinks`) so that it survives a server loss.
- After the install run `Test-SssConfig.ps1`, `Test-SssSetup.ps1` and `Test-SssCitrix.ps1`; they print the exact fix for each finding.

## Check it works

`https://<sss server>/sss/api/me` (open it in a browser where you are signed in) shows your role and, for remote support, `"shadow": true` and `"shadowState": "ok"`. Any other `shadowState` tells why the button is missing (see [Remote support](SHADOW.md)).

## Keep your own data out of the repository

Real domain names, server names, user names, sessions and configuration belong only on your servers, never into issues, pull requests, documentation, screenshots or the repository. The screenshots of this project come from the demo mode with invented data.
