# Architecture

[Deutsche Version](ARCHITECTURE.de.md)

SSS is one ASP.NET Core application (.NET 10) hosted in-process in IIS. It serves a static single-page UI and a small JSON API.

```
Browser ──HTTPS──> IIS (Windows authentication, anonymous off)
                     └─ ASP.NET Core Module (in-process)
                          └─ Sss.Web
                               ├─ wwwroot/            static UI (HTML, CSS, JS, 5 languages)
                               ├─ Program.cs          security pipeline + API endpoints
                               ├─ Services/SessionService   business rules, audit
                               └─ ISessionProvider
                                    ├─ MockSessionProvider      demo data (development, tests)
                                    └─ CitrixSessionProvider
                                         ├─ BrokerWorkerPool ── warm Windows PowerShell 5.1 workers ──> Broker SDK ──> Delivery Controller
                                         └─ ODataMonitorClient ───────────────────────────────────────> Monitor OData ──> Delivery Controller
```

## Request flow (list sessions)

1. IIS authenticates the user (Kerberos/NTLM). ASP.NET Core exposes the `WindowsIdentity`; `ResolveUser` takes the **SID** from it. Nothing else identifies the caller.
2. `GET api/sessions?range=24h` → rate limit (per user) → `SessionService.GetAsync`.
3. `CitrixSessionProvider` asks the Broker (live sessions, filtered by SID **inside** the Broker query) and, in parallel, Monitor (ended sessions plus latest metrics). Monitor failures become warnings, a Broker failure becomes HTTP 503.
4. Quality is rated by `QualityEvaluator` from configurable thresholds: *your device & network* from ICA round-trip time and available bandwidth, *datacenter & VDA* from the logon duration.
5. The UI renders the JSON with `textContent` only.

## Request flow (log off)

1. `POST api/sessions/{uid}/logoff` (or `logoff-disconnected` / `logoff-all`) → CSRF token check → rate limit.
2. `SessionService` loads the **caller's own live sessions** again and intersects them with the request; everything else is rejected and audited.
3. `BrokerWorkerPool` sends only the verified ids to a worker. The worker checks `UserSID` of every session **a second time** before `Stop-BrokerSession`. The cmdlet is asynchronous, so "logged off" means "log-off started".
4. Every target and every rejection is written to `logs/audit-YYYYMMDD.jsonl`.

## Components

| Part | File | Purpose |
|---|---|---|
| Pipeline & API | `Program.cs` | configuration checks, headers, auth, CSRF, rate limits, endpoints |
| Rules | `Services/SessionService.cs` | ownership checks, bulk scopes, history ranges |
| Quality | `Services/QualityEvaluator.cs` | traffic lights from metrics and thresholds |
| Audit | `Services/AuditLog.cs` | append-only JSON lines, one file per day |
| CSV | `Services/CsvWriter.cs` | export with spreadsheet-formula neutralisation |
| Provider interface | `Providers/ISessionProvider.cs` | what the app needs from a backend |
| Demo backend | `Providers/MockSessionProvider.cs` | sample data of two users |
| Broker | `Citrix/BrokerWorkerPool.cs`, `PowerShellWorker.cs`, `sss-broker-worker.ps1` | warm workers, JSON-lines protocol, only `ping`/`list`/`logoff` |
| Monitor | `Citrix/ODataMonitorClient.cs` | user lookup by SID, live and ended sessions, latest metrics |
| Merge | `Citrix/CitrixSessionProvider.cs` | combines Broker and Monitor data |
| UI | `wwwroot/` | dependency-free SPA, relative URLs, `i18n/*.json` |

## Why PowerShell workers?

The Broker SDK exists only as Windows PowerShell 5.1 snap-ins. Starting PowerShell per request would take seconds, so SSS keeps a small pool (default 2) of long-running workers that load the snap-in once. A worker is restarted automatically after a timeout or crash. Workers speak a tiny line-delimited JSON protocol over stdin/stdout, and the worker script accepts nothing but three operations with validated arguments.

## Configuration reference

Every option with default and meaning: [CONFIGURATION.md](CONFIGURATION.md). Requirements and ports: [REQUIREMENTS.md](REQUIREMENTS.md).

## Expert view, messages, status

- `GET api/sessions/{key}/details` (own) and `GET api/helpdesk/sessions/{key}/details?sid=` (helpdesk): `ODataMonitorClient.GetDetailsAsync` reads one session plus its latest 240 metric samples, optional record sets (`Connections`, `ConnectionFailureLogs`, `ApplicationInstances`) and returns generic *groups* (property name/value pairs), *series* (numeric metrics over time) and *tables*. Property names are not hard-coded, so new Citrix fields appear automatically. See [DATA-SOURCES.md](DATA-SOURCES.md).
- `POST api/helpdesk/message`: worker operation `message` (`Send-BrokerSessionMessage`), double ownership check, audited.
- `GET api/helpdesk/status`: `StatusService` runs the health checks shown in the Status tab (version, service account, audit folder, Broker and Monitor round trips, HTTPS, helpdesk group).

## Applications and processes

`GET api/sessions/{uid}/processes`, `POST api/sessions/{uid}/processes/{pid}/end` (own sessions) and the matching `api/helpdesk/sessions/{uid}/processes` endpoints (helpdesk, with `sid`) go through `ProcessService`, which accepts only live sessions of the target user and audits every attempt. The worker operations `processes` and `endprocess` look the session up again in the Broker, connect to the machine of that session with `New-PSSession -ConfigurationName` and use implicit remoting (`Import-PSSession`) to call the three commands of the JEA endpoint (the third, `Get-SssProfile`, reads the profile type and size for `GET api/sessions/{uid}/profile`) that `scripts/Install-SssJea.ps1` registers on the VDAs. Details, setup and security notes: [PROCESSES.md](PROCESSES.md).

Operations (health probe, status page, audit format, troubleshooting): [OPERATIONS.md](OPERATIONS.md).

Further details: [SECURITY.md](../SECURITY.md).
