# Operations

[Deutsch](OPERATIONS.de.md)

Everything an administrator does after the first installation: update, check, monitor, collect the audit trail, find problems.

## Install, update, remove

| Task | Command (elevated PowerShell on the IIS server) |
|---|---|
| First installation, guided | `.\install.ps1 -Interactive` |
| First installation, unattended | `.\install.ps1 -Controllers ddc01.corp.local -ServiceAccount 'CORP\svc-sss$' -HelpdeskGroup 'CORP\SSS-Helpdesk'` |
| Update to a new release | `.\install.ps1 -Update` |
| Update and open the health probe | `.\install.ps1 -Update -HealthEndpoint` |
| Remove the IIS application and app pool | `.\install.ps1 -Uninstall` |
| Preview any of the above | add `-WhatIf` |

`-Update` copies the new files and keeps `sss.config.json`, the app pool, the permissions and the audit trail. The previous version is kept in `<install folder>.previous`. If the new version does not answer the smoke test, the installer restores the previous version by itself. `-Uninstall` keeps files, configuration and audit logs; delete the folder yourself if you no longer need them.

Verify a release before installing: every release has a `.sha256` file next to the zip (`Get-FileHash .\sss-vX.Y.Z-win-x64.zip` must show the same value).

After every install or update run both checks (they read only, change nothing):

```powershell
.\Test-SssSetup.ps1                                       # IIS, app pool, accounts, configuration, the application itself
.\Test-SssCitrix.ps1 -Controller ddc01 -UserName CORP\alice   # Broker, Monitor, field names (alice needs a session)
```

Add `-Redact` to either script before pasting the output into a public issue: it replaces user, domain, host names, SIDs, IP addresses and GUIDs.

## Status page

The **Status** tab (helpdesk members only) runs the same checks as `Test-SssSetup.ps1` from inside the application:

| Check | Meaning |
|---|---|
| version, provider, identity, uptime | which build runs, which data source, as which account, since when |
| audit | the audit folder is writable by the app pool identity |
| config | no unknown keys in `sss.config.json` (otherwise they are listed) |
| helpdesk | the helpdesk group resolves |
| broker | a PowerShell worker answers (time in ms) |
| monitor | the Monitor OData service answers (time in ms) |
| monitor-https | the Monitor is addressed over HTTPS (a warning when it is not) |

The API behind it is `GET /api/helpdesk/status` (helpdesk only).

## Health probe for load balancers

`GET /<application>/healthz` is a small endpoint for load balancers, NetScaler/ADC monitors and uptime checks.

| Answer | Meaning |
|---|---|
| `200 {"status":"ok"}` | the PowerShell worker reaches a controller and the Monitor answers |
| `200 {"status":"degraded"}` | sessions and log-off work, but the Monitor does not answer (history and quality are missing). Keep the node in service |
| `503 {"status":"unavailable"}` | no Broker connection: the node cannot list or log off sessions. Take it out of rotation |

- It reveals nothing else: no version, names or details. For details use the Status tab.
- The result is cached for 10 seconds, so frequent polling does not load the controller. The endpoint is rate limited (120 requests per minute).
- IIS settings of the application live in the shipped `web.config` (handler outside a `location path="."` block), so the extra rule for `/healthz` inherits them.
- It needs **no sign-in**, but IIS requires one for the whole application. `install.ps1 -HealthEndpoint` (also together with `-Update`) opens exactly the path `<application>/healthz` for anonymous access, nothing else, and tests it. To undo it, `install.ps1 -Uninstall` removes the rule; by hand: `Clear-WebConfiguration -Filter /system.webServer/security/authentication -PSPath MACHINE/WEBROOT/APPHOST -Location "Default Web Site/sss/healthz"`.
- Without that rule a probe gets `401`. Many load balancers accept `401` as "alive" (it proves IIS and the application pool respond), but then they do not notice a lost Broker connection. Use the rule if you want real health checks.
- While the file `data\drain.flag` exists the probe answers `503 {"status":"draining"}`, so a load balancer takes the server out before an update ([Draining](MULTI-SERVER.md#draining)).
- Turn the endpoint off in the application with `Sss:Health:Enabled = false`.
- `Test-SssSetup.ps1` calls the path without credentials and tells you whether a probe would succeed.

Example for a monitor that expects `200` and the text `ok`: probe `https://sss.corp.local/sss/healthz`, expected response `"status":"ok"`.

## Audit trail

Every action that changes something or looks at somebody else's data is written to `logs\audit-YYYYMMDD.jsonl` (with hash chain, who, whom, when, from where, which server, ticket or reason), copied to central destinations and evaluated in the **Audit** tab (role *Auditor*). Fields, actions, destinations (UNC, Syslog/CEF, HTTP, SNMP), integrity check, retention and privacy: [Audit trail and central logging](AUDIT.md). Roles: [Roles](ROLES.md).

## Backup and restore

The state is `sss.config.json`, `sss.local.json` (if you use it) and the `logs` folder; the `data` folder only holds a cache and forwarding positions (losing it is harmless: a destination then starts with new events). Back up these (and keep the release zip you installed). Restoring is installing the same release again and copying them back. `install.ps1 -Update` leaves a rollback copy in `<install folder>.previous`.

## Logs and troubleshooting

| Symptom | Where to look |
|---|---|
| 500.19 / 502.5 right after install | Windows event log, source *IIS AspNetCore Module V2*; usually the Hosting Bundle is missing, or `sss.config.json` is invalid (a validation message is logged) |
| Sign-in prompt keeps coming back | Windows authentication on, anonymous off for the application; the site is in the browser's *Local intranet* zone; for an alias: SPN and `useAppPoolCredentials` ([Several controllers and servers](MULTI-SERVER.md)) |
| Page loads, no sessions, banner "unavailable" | the Status tab: `broker` failed. The message names the account the worker runs as; that account needs the Citrix administrator entry |
| History or quality missing | Status: `monitor` failed. A `404` over HTTPS means the Monitor service answers on `http://` only ([Monitor over HTTPS](MULTI-SERVER.md#monitor-over-https)) |
| Status: `config` warns | a key in `sss.config.json` is at the wrong level or misspelled; the warning names it and suggests the right place |
| Helpdesk tab missing | the group in `Sss:Helpdesk:Group` is empty, does not resolve (Status: `helpdesk`), or the user must log on again after being added to the group |
| "Processes" button missing | `Sss:Citrix:Processes:Enabled` is false, the audience is `Helpdesk`, or the expert view is off |
| Browser console shows CSP violations | a reverse proxy rewrites headers; SSS sends its own `Content-Security-Policy` and expects it to stay |

If you need the application's console output, set `stdoutLogEnabled="true"` in `web.config` temporarily (folder `logs\stdout`), recycle the app pool, reproduce, then switch it off again.

## Hardening checklist

- Publish SSS over HTTPS only. The CSRF cookie is `Secure`, `HttpOnly` and `SameSite=Strict`; HSTS is sent outside development.
- Keep the helpdesk group small: its members see every user's sessions.
- Use a gMSA, not a password-based account, and a custom Citrix role as soon as you know what you need.
- Give the app pool identity read access on the application folder and modify access only on `logs` (the installer does this).
- Keep `Auth:Mode` at `Iis` (the Dev mode is refused outside the Development environment anyway).
- Forward the audit trail and watch for `rejected-...` results and unusual `end-process` activity.
- Update regularly; watch the repository's security advisories (see [SECURITY.md](../SECURITY.md)).
