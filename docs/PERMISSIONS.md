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

## Narrowing the role (open item before 1.0)

*Help Desk Administrator* is broader than SSS needs. A custom role with only the permissions SSS uses is the goal; it has **not been derived and tested on a real site yet**, so no list is given here. To derive it on a test site:

1. Create a copy of the role: in Studio (Delegated Administration, Roles) or with `New-AdminRole` and `Add-AdminPermission` from the `Citrix.DelegatedAdmin.Admin.V1` snap-in. `Get-AdminRole -Name 'Help Desk Administrator'` shows its permissions.
2. Assign the copy to the SSS service account instead of *Help Desk Administrator*.
3. Remove permissions one at a time, restart the app pool and run the checks below after each step. Put a permission back when one check fails.
4. Checks: `Test-SssCitrix.ps1` (all green); in the web interface: list own sessions, history, log off a test session, helpdesk search and farm overview, send a message, expert view of a session.
5. Send the resulting permission list (without site specific names) in an issue, so it can be documented and tested for the next release.

## Ending applications and processes (optional)

Off by default. It needs one more right, on the **VDAs** instead of the controller: the service account may connect to a restricted PowerShell endpoint (JEA, `SSS.Processes`) that offers only "list the processes of this user" and "end one process of this user". `scripts/Install-SssJea.ps1 -Principal 'CORP\svc-sss$'` sets it up on a VDA, no other right is granted and the account gets no general access to the machine. Details: [Ending applications and processes](PROCESSES.md).

| Who | Right | Where | Why | Set by |
|---|---|---|---|---|
| Service account | may use the JEA endpoint `SSS.Processes` | every VDA | list and end the user's own processes | `Install-SssJea.ps1` |
| | network access to WinRM (TCP 5985) | firewall, VDAs | the connection | network team |
