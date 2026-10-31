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
- A change takes effect after the app pool is recycled (`Restart-WebAppPool SSS`).
- Values are validated at startup. A wrong value stops the application with a clear message in the Windows event log (source *IIS AspNetCore Module V2*) instead of running in an unsafe state.

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
| `DevUserName`, `DevUserSid`, `DevIsHelpdesk` | demo values | Development mode only. |

## Helpdesk (`Sss:Helpdesk`)

| Key | Default | Meaning |
|---|---|---|
| `Group` | `""` | AD group (`DOMAIN\Group`) whose members may find users, see and log off their sessions, send messages and see the expert view and status. Empty = helpdesk off. Resolved to a SID at startup; if it cannot be resolved the feature stays off and the Status tab says so. |
| `ReasonRequired` | `false` | Require a free-text reason for every helpdesk log-off (written to the audit log). |
| `MaxSessions` | `1000` | Upper limit of the farm-wide session overview. |
| `MessageTitle` | `IT Helpdesk` | Title of messages sent to a session. |

## Citrix (`Sss:Citrix`)

| Key | Default | Meaning |
|---|---|---|
| `Controllers` | `[]` | Delivery Controller host names, tried in order (failover). Required for `Provider = Citrix`. Letters, digits, `.`, `_`, `-` only. |
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

Listing and ending the applications and processes of a session. Off by default. Needs the JEA endpoint on the VDAs; see [Ending applications and processes](PROCESSES.md).

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Turns the feature on (the "Processes" button appears in the expert view). |
| `Configuration` | `SSS.Processes` | Name of the JEA session configuration on the VDAs (`Install-SssJea.ps1 -ConfigurationName`). |
| `Audience` | `All` | `All` = users for their own sessions and the helpdesk; `Helpdesk` = helpdesk only. |
| `TimeoutSeconds` | `60` | Time limit of a process request (connecting to the VDA is slower than a Broker call). |

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

Format and fields: [Operations](OPERATIONS.md#audit-trail).

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
