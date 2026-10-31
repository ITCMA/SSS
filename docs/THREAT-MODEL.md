# Threat model

[Deutsch](THREAT-MODEL.de.md)

What SSS protects, who could attack it, how, and what stops them. It is written from the code and the documented setup; it is not an external audit. Report problems privately, see [SECURITY.md](../SECURITY.md).

## What is worth protecting

| Asset | Why |
|---|---|
| Other users' sessions | a user must never see, log off, message or end processes in a session that is not theirs |
| The service account of the app pool | it holds Citrix rights (read sessions, log off, send messages, read Monitor data) and, with process control, a path to the VDAs |
| Session and profile data | names, client addresses, machine names, process names and paths, folder names and sizes: personal data in the sense of data protection law |
| Audit trail | evidence of who did what |
| Availability | helpdesk and users rely on it during incidents |

## Who could attack

1. **A signed-in user** who wants other users' data or actions.
2. **A malicious or curious helpdesk member** who abuses helpdesk rights.
3. **A remote attacker** without an account, over the network.
4. **A web page the user visits** (cross-site attacks against the signed-in browser).
5. **An attacker who controls a VDA or a server** in the environment and tries to reach SSS or the service account.
6. **Someone who steals the service account** (password or ticket).

## Trust boundaries

```
Browser ──HTTPS+Windows auth──> IIS ──in-process──> SSS ──PowerShell worker──> Delivery Controller (Broker SDK)
                                                        ├──HTTP(S)+Windows auth──> Monitor OData
                                                        └──WinRM (JEA, optional)──> VDA
```

Identity always comes from IIS Windows authentication. SSS never takes a user or SID from a request for the user's own views. The helpdesk passes a target SID, which is accepted only for members of the helpdesk group and only if the session really belongs to that SID.

## Threats and mitigations

| Threat | Mitigation | Left over |
|---|---|---|
| A user reads or changes someone else's sessions by guessing ids | identity from IIS; every action re-checks that the session belongs to the caller (web layer **and** PowerShell worker, independently); foreign ids answer "not found" or are rejected and audited | none known |
| Helpdesk abuse | separate endpoints behind a group check; a reason can be required; every search, view, message, log-off and process action is audited with actor, target and time | a helpdesk member legitimately sees all sessions: keep the group small, forward the audit trail |
| Injection into Citrix queries or PowerShell | every value that reaches a cmdlet or an OData query is validated against a strict pattern (SIDs, numeric ids, GUIDs, host names); field names from configuration must be plain identifiers; no free text reaches a command line | none known |
| Cross-site request forgery | anti-forgery token in a header plus `SameSite=Strict`, `HttpOnly`, `Secure` cookie on every state-changing request | none known |
| Cross-site scripting | the interface writes text only with `textContent`; strict Content-Security-Policy (no inline scripts or styles, no external sources); no third-party libraries | none known |
| Clickjacking, sniffing, referrer leaks | `frame-ancestors 'none'`, `X-Content-Type-Options`, no referrer, same-origin policies, HSTS | none |
| Denial of service by a user | rate limits per user (log-off, read, helpdesk), bounded worker pool, bounded response sizes | a determined attacker with many accounts can still load the controller |
| Leaking details in errors | the browser gets only status codes (503 for Citrix side, 500 for bugs); details go to the server log | log files contain details: protect them |
| Anonymous access | Windows authentication on, anonymous off for the whole application. The only exception is `GET /healthz`, opened on request, which returns `ok`, `degraded` or `unavailable` and nothing else | none |
| Monitor traffic on the wire | Windows authentication (signed, not plaintext credentials); HTTPS where the controller supports it | with `UseHttps = false` the data itself is not encrypted: keep it inside the management network |
| Stolen service account | gMSA recommended (long random password, rotated by AD); account holds only the Citrix role you give it and the JEA rights | with *Help Desk Administrator* the role is broader than SSS needs ([custom role](PERMISSIONS.md)); with process control an attacker can end any user's processes on VDAs that run the endpoint, and read process and folder names (not file contents, no code execution) |
| A VDA attacks SSS | SSS only calls out to the VDA and never accepts data from it as instructions; replies are parsed as data and size-limited | a compromised VDA can feed false process and profile data to the interface |
| Tampering with the audit trail | append-only from the application; one file per day and server | an administrator on the server can edit files: forward the trail to a system they cannot change |
| Vulnerable dependencies | no third-party packages in the web application; build actions pinned to commit hashes; Dependabot; CodeQL | .NET itself and Windows: patch them |
| Supply chain of releases | releases are built by a pinned GitHub Actions workflow from a tag; each zip has a SHA-256 file | the checksum comes from the same place as the zip: compare it over a second path if the stakes are high |

## Privacy

SSS shows users only their own data and helpdesk members the data of the users they look at. It stores nothing except the audit trail and the configuration; there is no database and no telemetry. The audit trail contains user names, SIDs, client addresses, search text, helpdesk reasons and message texts. Define a retention period and who may read it. The expert view and the profile analysis show machine names, process names and paths and folder names, which can reveal what a person works on: decide whether the helpdesk group may see that.

## Out of scope

Protecting the Citrix site itself, the domain, Windows on the SSS server and VDAs, and the browser of the user. SSS relies on them.
