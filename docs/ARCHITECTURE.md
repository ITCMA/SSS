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

## Configuration reference (`sss.config.json`, section `Sss`)

| Key | Default | Meaning |
|---|---|---|
| `Provider` | `Mock` | `Citrix` for a real site |
| `RefreshSeconds` | `30` | automatic refresh of the UI |
| `Auth:Mode` | `Iis` | `Dev` (fixed demo user) is only accepted in the Development environment |
| `Audit:Directory` | `logs` | audit trail location (relative to the app) |
| `RateLimit:LogoffRequestsPerMinute` | `10` | per user |
| `RateLimit:ReadRequestsPerMinute` | `120` | per user |
| `RateLimit:HelpdeskRequestsPerMinute` | `60` | per helpdesk user |
| `Helpdesk:Group` | `""` | AD group of helpdesk staff; empty = feature off |
| `Helpdesk:ReasonRequired` | `false` | reason mandatory for helpdesk log-offs |
| `Helpdesk:MaxSessions` | `1000` | cap of the farm-wide overview |
| `Quality:*` | see `appsettings.json` | thresholds for the traffic lights |
| `Citrix:Controllers` | `[]` | Delivery Controllers, tried in order |
| `Citrix:Workers` | `2` | warm PowerShell workers |
| `Citrix:RequestTimeoutSeconds` | `20` | per worker or Monitor request |
| `Citrix:Monitor:Enabled` | `true` | `false` = live sessions and log-off only |
| `Citrix:Monitor:BaseUrl` | `http://{controller}/Citrix/Monitor/OData/v4/Data/` | use `https://` |
| `Citrix:Monitor:MetricsDateField` / `IcaRttField` / `BandwidthField` | `CollectedDate` / `IcaRttMS` / `OutputBandwidthAvailable` | metric property names (verified on CVAD 2507 CU1) |
| `Citrix:Monitor:BandwidthDivisor` | `1000` | converts the bandwidth field (bit/s) to kbit/s |
| `StoreFrontUrl` | `""` | optional "Open StoreFront" link (absolute http(s) address) |
| `Helpdesk:MessageTitle` | `IT Helpdesk` | title of messages sent to a session |

## Expert view, messages, status

- `GET api/sessions/{key}/details` (own) and `GET api/helpdesk/sessions/{key}/details?sid=` (helpdesk): `ODataMonitorClient.GetDetailsAsync` reads one session plus its latest 240 metric samples, optional record sets (`Connections`, `ConnectionFailureLogs`, `ApplicationInstances`) and returns generic *groups* (property name/value pairs), *series* (numeric metrics over time) and *tables*. Property names are not hard-coded, so new Citrix fields appear automatically. See [DATA-SOURCES.md](DATA-SOURCES.md).
- `POST api/helpdesk/message`: worker operation `message` (`Send-BrokerSessionMessage`), double ownership check, audited.
- `GET api/helpdesk/status`: `StatusService` runs the health checks shown in the Status tab (version, service account, audit folder, Broker and Monitor round trips, HTTPS, helpdesk group).

## Ending processes (not implemented)

A supported Broker/Monitor command to end a process inside a session does not exist; Director uses its own channel to the VDA. The preferred design is a JEA endpoint on the VDAs with two commands bound to a session id, called by the service account. See [PERMISSIONS.md](PERMISSIONS.md).

Further details: [SECURITY.md](../SECURITY.md).
