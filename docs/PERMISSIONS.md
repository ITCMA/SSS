# Permissions at a glance

[Deutsche Version](PERMISSIONS.de.md)

SSS needs very few rights, and only one account holds Citrix rights: the **service account** of the app pool. End users and helpdesk staff never get any Citrix rights through SSS.

## Who needs what

| Who | Right | Where | Why | Set by |
|---|---|---|---|---|
| **Service account** (gMSA, e.g. `CORP\svc-sss$`) | is the identity of the app pool | IIS | SSS talks to Citrix as this account | `install.ps1 -ServiceAccount` |
| | allowed to retrieve its password on the IIS server | Active Directory | gMSA technique | `New-ADServiceAccount ... -PrincipalsAllowedToRetrieveManagedPassword` |
| | **Citrix administrator** with role *Help Desk Administrator*, scope *All* | Delivery Controller (Delegated Administration) | read sessions (Broker), log off, send messages, read Monitor data | `New-AdminAdministrator` + `Add-AdminRight` |
| | read on the application folder, **modify** on `logs` | IIS server file system | audit trail | `install.ps1` (icacls) |
| | network access to the controller (Broker SDK, Monitor OData, usually TCP 80/443) | firewall | queries | network team |
| **End users** | may reach the SSS URL (Windows authentication) | IIS | sign-in | nothing to do: all domain users |
| | no Citrix rights | - | users only see and affect their own sessions; SSS enforces that | - |
| **Helpdesk** | member of the AD group in `Sss:Helpdesk:Group` | Active Directory | find users, see and log off their sessions, send messages, expert view | `New-ADGroup` + `Add-ADGroupMember`, then log on again |
| **Installing administrator** | local administrator on the IIS server | IIS server | `install.ps1` | - |
| | Domain Admin (once) | Active Directory | KDS root key and gMSA | - |
| | full Citrix administrator (once) | Delivery Controller | create the administrator entry | - |

Helpdesk members need no extra Citrix role: the helpdesk acts through the service account. Keep the helpdesk group small, because its members see all users' sessions.

## All steps in one block

```powershell
# --- Active Directory (Domain Admin) -------------------------------------------------------
Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))      # only if the domain has no KDS root key yet
New-ADServiceAccount -Name svc-sss -DNSHostName svc-sss.corp.local `
  -PrincipalsAllowedToRetrieveManagedPassword 'IIS-SERVER$'
New-ADGroup -Name SSS-Helpdesk -GroupScope Global -GroupCategory Security   # optional
Add-ADGroupMember SSS-Helpdesk -Members helpdesk.user1, helpdesk.user2

# --- IIS server (elevated PowerShell) --------------------------------------------------------
Install-ADServiceAccount svc-sss
Test-ADServiceAccount svc-sss                                  # must return True

# --- Delivery Controller (elevated PowerShell) -----------------------------------------------
Add-PSSnapin Citrix.DelegatedAdmin.Admin.V1
New-AdminAdministrator -Name 'CORP\svc-sss$' -Enabled $true
Add-AdminRight -Administrator 'CORP\svc-sss$' -Role 'Help Desk Administrator' -All

# --- IIS server: install and verify ----------------------------------------------------------
.\install.ps1 -Interactive
.\Test-SssSetup.ps1                                            # prints the fix for every finding
```

`Test-SssSetup.ps1` and the **Status** tab (helpdesk) check every line of the table above and print the exact command for whatever is missing.

## Why *Help Desk Administrator*?

It is the smallest built-in role that covers what SSS does: list sessions, log them off, send messages, read Monitor data. It can do more than that (for example machine power actions). In production, create a custom role with only the permissions SSS needs and assign it instead. A compromised SSS process can do whatever the service account can do, so the role matters.

## Ending applications and processes (not implemented yet)

Citrix offers no supported Broker or Monitor command to end a single process in a session. Director does it through its own channel to the VDA. The options we see for SSS:

1. **Remote command channel to the VDAs** (WinRM). Needs a right on every VDA.
2. **Just Enough Administration (JEA) endpoint on the VDAs** that exposes exactly two commands (list and end a process of one session id). The service account is then allowed to run only these two, not to administer the machine. This is the recommended design; it needs a rollout of the JEA configuration to the VDAs (GPO or master image).
3. Using Director's internal interface: undocumented, not recommended.

Whichever channel is chosen, SSS would verify that the session belongs to the user, restrict the action to that session id and audit it like a log-off.
