# Security Policy

## Reporting a vulnerability

Please do not open a public issue. Use GitHub's private vulnerability reporting ("Security" tab → "Report a vulnerability") or write to github@itcma.net.

## Security model

SSS lets ordinary users log off sessions on a Citrix site through a service account that holds Citrix rights. The whole design therefore revolves around one rule: **a user may only ever see and affect their own sessions.**

| Control | How |
|---|---|
| **Identity** | Taken only from the server-side authentication context (IIS Windows authentication, anonymous access off, every endpoint requires an authenticated user). No endpoint accepts a user name or SID as input. |
| **Isolation on read** | The Broker is queried by the caller's SID (`-UserSID`); the Monitor is queried by the user id resolved from that SID. Results are never filtered client-side. |
| **Isolation on log-off** | `SessionService` re-reads the caller's own live sessions and intersects them with the request. The PowerShell worker checks `UserSID` of every session a second time before `Stop-BrokerSession`. Foreign or unknown ids are rejected and audited. |
| **Helpdesk** | Separate `/api/helpdesk/*` endpoints behind a group check (the group SID is compared with the group SIDs in the caller's Windows token; no group configured or unresolvable = nobody). Input is validated (SID pattern, plain-text search, session id pattern, scope enum, reason capped at 200 characters without control characters). Every action is audited with actor, affected user and reason; the user is chosen by SID from the helpdesk's own search results. The normal endpoints stay "own sessions only" for helpdesk members too (covered by tests). |
| **Expert view** | `GET api/sessions/{key}/details` looks the session up by the caller's own Monitor user id **and** the session key (a GUID, validated); somebody else's key answers 404. Infrastructure properties of the machine and desktop group are only returned to helpdesk members. Optional record sets are queried only for the verified session key. Support info is assembled in the browser; nothing is sent anywhere. |
| **Messages (helpdesk)** | `POST api/helpdesk/message`: group check, CSRF, rate limit, text max. 300 characters without control characters, fixed title from the configuration, session ids checked against the chosen user's live sessions and again in the worker (`UserSID`); audited with actor, user, session and text. |
| **Status page** | `GET api/helpdesk/status` (helpdesk only) runs read-only checks and returns technical details for the helpdesk's eyes. |
| **StoreFront link** | `Sss:StoreFrontUrl` must be an absolute http(s) address (checked at startup) because it becomes an `href`. |
| **CSRF** | ASP.NET Core anti-forgery token (header `X-CSRF-TOKEN`) on every POST; cookie is `HttpOnly`, `SameSite=Strict`, `Secure` outside development. |
| **Abuse** | Per-user rate limits: log-off 10/min, reading 120/min (configurable) → HTTP 429. |
| **Input validation** | SID, session ids, controller names and OData field names are validated against strict patterns, in C# and again in the PowerShell worker. Only five worker operations exist (`ping`, `list`, `listall`, `logoff`, `message`); user data never becomes part of a command string. |
| **Browser hardening** | Content-Security-Policy `default-src 'self'` (no inline script or style), `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Cross-Origin-Opener-Policy`, `Cross-Origin-Resource-Policy`, HSTS; the UI never uses `innerHTML`; API responses are `no-store`. |
| **Error handling** | Failures return only a status code (503 backend, 500 bug); details go to the Windows event log. |
| **Exports** | CSV cells that start with `= + - @` are neutralised against formula injection. |
| **SSRF** | The Monitor client follows paging links only on the configured host. |
| **Test hooks** | `Auth:Mode=Dev` (fixed demo identity) and `Citrix:SnapinOverride` (runs a script from the configuration) are refused unless `ASPNETCORE_ENVIRONMENT=Development`. |
| **Audit** | Every log-off attempt (success, failure, rejection) is written to `audit-YYYYMMDD.jsonl` with user, SID, session id and client IP. |
| **Dependencies** | The web application has no third-party NuGet packages and no JavaScript libraries. |

## Operator checklist

- Publish SSS **only over HTTPS**. Use an `https://` Monitor URL (`Sss:Citrix:Monitor:BaseUrl`); SSS logs a warning at startup if it is `http://`.
- Run the app pool as a **gMSA** and give it a **dedicated Citrix administrator entry** with the least role that allows listing and logging off sessions and reading Monitor data. *Help Desk Administrator* is a convenient start, but it can do more than SSS needs (for example machine power actions); create a custom role once you know the permissions you need. A compromised SSS process can do whatever that account can do.
- Keep **write access** to the application folder and `sss.config.json` limited to administrators. The installer grants the app pool identity only read access, plus modify on `logs`. Whoever can edit the configuration controls which process the application starts.
- Protect the **audit log**: the app pool identity must be able to append to it, so it could also delete it. Forward `logs\audit-*.jsonl` to a central log collector (SIEM) if you rely on it for accountability.
- Restart the app pool after changing group memberships of the service account; a running process keeps its old token.
- Keep the **helpdesk group small**: members can see every user's sessions and log them off. Helpdesk staff must log on again after being added to the group. Review the audit log for `helpdesk:*` and `helpdesk-logoff:*` entries; they contain actor, affected user and reason. Consider `Sss:Helpdesk:ReasonRequired`.

- Releases come with a SHA-256 checksum file; verify the zip before installing. GitHub Actions are pinned to commit SHAs, CodeQL and Dependabot run on the repository.

## Known limits

- "Log off" starts the log-off; the Broker confirms only that the operation began on the machine.
- The audit log is a plain file. It is not tamper-proof against someone with administrator rights on the server.
- Users can see which of their own sessions run on which machine. No other users' data is exposed.
- Behind a reverse proxy the recorded client IP is the proxy's address.
