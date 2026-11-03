# Configuration reference

[Deutsch](CONFIGURATION.de.md)

SSS reads `appsettings.json` (defaults, shipped with the application) and then `sss.config.json` next to it (your settings, kept by `install.ps1 -Update`). Everything lives in the section `Sss`. Write only what you change:

```json
{
  "Sss": {
    "Provider": "Citrix",
    "Citrix": { "Controllers": [ "ddc01.corp.local", "ddc02.corp.local" ] },
    "Helpdesk": { "Group": "CORP\\SSS-Helpdesk" }
  }
}
```

- Keys are case-insensitive. A key SSS does not know is ignored but reported: the **Status** tab shows it as "Configuration keys", `Test-SssSetup.ps1` warns, and the application log names it (usually a wrong nesting, for example `Sss:UseHttps` instead of `Sss:Citrix:Monitor:UseHttps`).
- Environment variables work as well, with `__` as separator: `Sss__Citrix__Workers=4`. They override the files.
- `sss.local.json` (optional, next to it) is read after `sss.config.json` and overrides it. Keep `sss.config.json` identical on all servers of a [multi-server setup](MULTI-SERVER.md#server-list-and-configuration) and put settings that belong to one server only (paths, a local bootstrap controller) here. `install.ps1 -Update` keeps both files.
- A change takes effect after the app pool is recycled (`Restart-WebAppPool SSS`).
- Values are validated at startup. A wrong value stops the application with a clear message in the Windows event log (source *IIS AspNetCore Module V2*) instead of running in an unsafe state.

## Editing and checking the configuration

SSS has no configuration GUI on purpose: a web page that writes its own configuration would give an attacker who reaches the application a way to change roles and audit settings, and it would cause drift between servers. These tools help instead:

| Tool | What it does |
|---|---|
| **Examples** in `docs\examples` | complete starting points: `sss.config.single-server.json`, `sss.config.enterprise.json` (roles, discovery, balancing, audit destinations, metrics), `sss.config.multi-server.json` (server list, shared key ring) and `sss.local.example.json` (settings of one server only). Copy one, replace the names. |
| **Schema** `docs\sss.config.schema.json` | describes every setting with type, default, allowed values and description. In Visual Studio Code add `"$schema": "<path>/sss.config.schema.json"` as the first key (the examples do) and you get completion, the description of each setting and a warning for an unknown or wrongly typed key. The file is generated from the program and checked by the tests, so it always matches the version. |
| **`Test-SssConfig.ps1`** | checks a configuration without starting SSS: the errors that would stop it, and warnings (for example no auditor group, HTTP-only Monitor, several servers without a shared key ring, unknown keys). `.\Test-SssConfig.ps1` checks what is installed, `.\Test-SssConfig.ps1 -Path .\new.json` checks a file before you copy it to a server. Exit code 0 = no errors. `install.ps1` runs the same check after every install and update. |
| **Status tab, "Effective configuration"** (Admin) | every setting SSS runs with, and where the value comes from: `sss.config.json`, `sss.local.json`, an environment variable or the default. Read only, secrets masked. The quickest way to see why one server behaves differently from another. |

`install.ps1 -Interactive` also asks for the auditor and admin groups, a central audit folder and controller discovery and writes them into the first `sss.config.json` (the same as `-AuditorGroup`, `-AdminGroup`, `-AuditShare`, `-Discovery`).

## General

| Key | Default | Meaning |
|---|---|---|
| `Provider` | `Mock` | `Citrix` for a real site. `Mock` serves demo data and shows a banner. |
| `RefreshSeconds` | `30` | Automatic refresh interval of the web interface. |
| `StoreFrontUrl` | `""` | Optional absolute `http(s)` address; the interface then shows an "Open StoreFront" link (to start or reconnect sessions). |
| `PathBase` | none | Application path for local runs behind a prefix. Behind IIS the ASP.NET Core Module sets it; leave it empty. |

## Authentication (`Sss:Auth`)

| Key | Default | Meaning |
|---|---|---|
| `Mode` | `Iis` | `Iis` = the Windows authentication of IIS identifies the user (production). `Dev` = fixed demo user, only accepted when `ASPNETCORE_ENVIRONMENT=Development`. |
| `DevUserName`, `DevUserSid`, `DevIsHelpdesk`, `DevRoles` | demo values | Development mode only. `DevRoles` is a list such as `"L2,Auditor"` (`L1`, `L2`, `L3`, `Auditor`, `Admin`). |

## Helpdesk (`Sss:Helpdesk`)

| Key | Default | Meaning |
|---|---|---|
| `Group` | `""` | AD group (`DOMAIN\Group`) whose members have all helpdesk rights (level 3: find users, see and log off their sessions, send messages, processes, profile, expert view, status). Kept for existing installations; finer levels are in `Sss:Roles`. Resolved to a SID at startup; if it cannot be resolved the group grants nothing. |
| `ReasonRequired` | `true` | Require a ticket number or reason (any text, not checked) for every helpdesk log-off, message, process list, profile and end-process; it is written to the audit trail. See [Roles](ROLES.md#ticket-number-or-reason-mandatory). |
| `BulkAlertThreshold` | `5` | A helpdesk request that logs off this many sessions or more also writes the event `alert:bulk-logoff`. 0 = off. |
| `MaxSessions` | `1000` | Upper limit of the farm-wide session overview. |
| `MessageTitle` | `IT Helpdesk` | Title of messages sent to a session. |

## Roles (`Sss:Roles`)

AD groups (`DOMAIN\Group`) for `Level1Group`, `Level2Group`, `Level3Group`, `AuditorGroup` and `AdminGroup`; all empty by default. What each role may do: [Roles](ROLES.md).

## Citrix (`Sss:Citrix`)

| Key | Default | Meaning |
|---|---|---|
| `Controllers` | `[]` | Delivery Controller host names, tried in order (failover). Required for `Provider = Citrix` (or set `Discovery:Bootstrap`). Letters, digits, `.`, `_`, `-` only. |
| `Discovery` | off | Finds all controllers of the site from one of them: `Enabled`, `Bootstrap`, `RefreshMinutes` (15), `AllowedSuffixes`, `IncludeInactive`. See [Controllers](CONTROLLERS.md#discovery). |
| `Balancing` | `Failover` | `Strategy` (`Failover`, `RoundRobin`, `LeastLatency`), `Preferred` (controllers or zones), `FailureThreshold` (3), `OpenSeconds` (30). See [Controllers](CONTROLLERS.md#spreading-the-requests). |
| `Workers` | `2` | Warm PowerShell workers (1-16). Each holds the loaded Citrix snap-in (about 60-100 MB). |
| `RequestTimeoutSeconds` | `20` | Time limit of one Broker or Monitor request. |
| `PowerShellPath` | `powershell.exe` | Windows PowerShell 5.1 host with the Citrix snap-ins. |
| `WorkerScript` | next to the app | Path of `sss-broker-worker.ps1`. Only change for tests. |
| `SnapinOverride` | none | Test hook (fake cmdlets). Refused outside the Development environment. |

### Monitor (`Sss:Citrix:Monitor`)

History, quality data and the expert view come from the Monitor OData service (the data source of Director).

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | `false` = live sessions and log-off only, no history, no quality, no expert view. |
| `UseHttps` | `true` | Scheme of the derived addresses. Set `false` when the Monitor service answers on `http://` only (see [Monitor over HTTPS](MULTI-SERVER.md#monitor-over-https)). |
| `BaseUrl` | derived | One explicit address, e.g. `https://ddc01.corp.local/Citrix/Monitor/OData/v4/Data/`. Overrides the derived address. |
| `BaseUrls` | derived | One address per controller, tried in order. Default: `{scheme}://{controller}/Citrix/Monitor/OData/v4/Data/` for every entry of `Controllers`. |
| `MetricsDateField` | `CollectedDate` | `SessionMetrics` property that picks the newest sample. |
| `IcaRttField` | `IcaRttMS` | Property with the ICA round-trip time in ms. |
| `BandwidthField` | `OutputBandwidthAvailable` | Property with the bandwidth towards the client (bit/s on CVAD 2507). |
| `BandwidthDivisor` | `1000` | Converts the bandwidth field to kbit/s. |
| `MaxPages` | `5` | Upper limit of OData pages followed per query. |

The field names are verified on CVAD 2203, 2402, 2507 and 2607 with `Test-SssCitrix.ps1`. Field names must be plain identifiers (they end up in a query).

### Processes (`Sss:Citrix:Processes`)

Listing and ending the applications and processes of a session, and reading the profile type and size. Off by default. Needs the JEA endpoint on the VDAs; see [Ending applications and processes](PROCESSES.md).

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Turns the feature on (the "Processes" button appears in the expert view). |
| `Configuration` | `SSS.Processes` | Name of the JEA session configuration on the VDAs (`Install-SssJea.ps1 -ConfigurationName`). |
| `Audience` | `All` | `All` = users for their own sessions and the helpdesk; `Helpdesk` = helpdesk only. |
| `TimeoutSeconds` | `60` | Time limit of a process request (connecting to the VDA is slower than a Broker call). |
| `ProfileInfo` | `true` | Shows profile type and size in the expert view (button "Check profile type and size"). Uses the same JEA endpoint, so it needs `Enabled = true`. |
| `ProfileScanSeconds` | `20` | Time limit of the profile folder scan on the VDA; `0` = type only, no size. |

## Quality thresholds (`Sss:Quality`)

Traffic lights in the session list. Values at or above *Warn* turn yellow, at or above *Crit* red (bandwidth: at or below).

| Key | Default | Meaning |
|---|---|---|
| `RttWarnMs` / `RttCritMs` | `80` / `150` | ICA round-trip time, "Your device & network". |
| `BandwidthWarnKbps` / `BandwidthCritKbps` | `1500` / `400` | Available bandwidth (lower is worse). |
| `LogonWarnSeconds` / `LogonCritSeconds` | `45` / `90` | Logon duration, "Data center & VDA". |
| `LoadWarn` / `LoadCrit` | `7000` / `9000` | Machine load index (0-10000). |

## Rate limits (`Sss:RateLimit`)

Per user (per IP when no user is known), per server.

| Key | Default | Meaning |
|---|---|---|
| `LogoffRequestsPerMinute` | `10` | Log-off and end-process requests. |
| `ReadRequestsPerMinute` | `120` | Listing sessions, details, process lists, CSV export. |
| `HelpdeskRequestsPerMinute` | `60` | All helpdesk calls per helpdesk user. |

## Audit (`Sss:Audit`)

| Key | Default | Meaning |
|---|---|---|
| `Directory` | `logs` | Folder of the audit trail; relative paths are based on the application folder. The app pool identity needs *modify* on it. |
| `PerServerFiles` | `false` | `true` = `audit-YYYYMMDD-SERVER.jsonl`, so several servers can write into one shared folder. |
| `OpsStream` | `true` | Warnings and errors of SSS itself go into a second stream `ops-YYYYMMDD.jsonl` and through the same destinations. |
| `RetentionDays` | `0` | Delete local files older than this once every destination has passed them. 0 = keep. |
| `Sinks` | none | Central destinations: `Unc`, `Syslog`, `Http`, `Snmp` (each a list). Every destination has `Enabled`, `Name`, `Streams`, `Include`, `OnlyProblems`, `BatchSize` and its own keys. All of it is described in [Audit trail and central logging](AUDIT.md#destinations). |

Format and fields: [Audit trail](AUDIT.md#the-record).

## Data folder (`Sss:DataDirectory`)

Default `data` next to the application. Holds what SSS keeps between restarts: the forwarding positions of the audit destinations, the cached controller list and the drain flag. The app pool identity needs *modify* on it (`install.ps1` sets it). Kept on updates.

## SSS servers (`Sss:Instances`)

| Key | Default | Meaning |
|---|---|---|
| `Directory` | `""` | Shared folder every SSS server can write; each server reports there and reads the others. Empty = single server. |
| `Expected` | `[]` | Computer names of all SSS servers. A listed server that does not report is `missing`, a reporting one that is not listed `unexpected`. |
| `HeartbeatSeconds` | `60` | How often a server reports. |
| `StaleSeconds` | `180` | A server that has not reported for this long counts as not running. |

See [Server list and configuration](MULTI-SERVER.md#server-list-and-configuration).

## Metrics (`Sss:Metrics`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Serves `GET /metrics` (Prometheus text format, no personal data). IIS must allow anonymous access to the path (`install.ps1 -MetricsEndpoint`). |
| `AllowedIps` | `[]` | Addresses or networks (`10.0.5.0/24`) that may read it. The server itself is always allowed. |

## Health probe (`Sss:Health`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Serves `GET /healthz` for load balancers. IIS must allow anonymous access to that one path (`install.ps1 -HealthEndpoint`). Details: [Operations](OPERATIONS.md#health-probe-for-load-balancers). |

## Several servers (`Sss:DataProtection`)

| Key | Default | Meaning |
|---|---|---|
| `KeyDirectory` | `""` | Shared folder for the key ring, so CSRF tokens issued by one server are accepted by the others. Empty = keys stay on the server. |
| `ProtectToSid` | `""` | Optional SID (for example of the gMSA) the keys are encrypted for with DPAPI-NG. Windows only. |

More on multi-server setups: [Several controllers and servers](MULTI-SERVER.md).

## Logging

Standard ASP.NET Core logging, configured in `appsettings.json` under `Logging`. The default level is `Warning`; unknown configuration keys, HTTP-only Monitor access and worker problems are logged there. In IIS the output goes to the Windows event log only for startup failures (the ASP.NET Core Module); enable `stdoutLogEnabled` in `web.config` temporarily if you need the full console output.
