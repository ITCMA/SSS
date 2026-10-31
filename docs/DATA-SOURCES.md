# Data sources and quality rating

[Deutsche Version](DATA-SOURCES.de.md)

SSS has no database of its own. It asks two Citrix sources on the Delivery Controller and merges the answers.

```
Browser → IIS (Windows authentication) → SSS
                                          ├─ Broker SDK     → live sessions, log-off, messages
                                          └─ Monitor OData  → history, quality metrics, expert details
                                             (always as the service account, never as the user)
```

## Live sessions, log-off, messages: Broker SDK

- Same source as `Get-BrokerSession`: current, straight from the Broker Service.
- The Broker SDK exists only as Windows PowerShell 5.1 snap-in. SSS keeps **two warm worker processes** (`Citrix:Workers`) that load the snap-in once and talk a small JSON protocol over stdin/stdout. Operations: `ping`, `list`, `listall`, `logoff`, `message`.
- The user is filtered **inside the Broker query** (`-UserSID`), never afterwards. Fields: state (active/disconnected), app or desktop, delivery group, machine, start time, idle time, client name and address, applications in use.
- Log-off is `Stop-BrokerSession` (asynchronous: "logged off" means "log-off started"). Messages are `Send-BrokerSessionMessage`. Both re-check the session owner inside the worker.

## History, quality, expert view: Monitor OData v4

The same data Director shows (`/Citrix/Monitor/OData/v4/Data/`), read with the service account.

| Need | Query |
|---|---|
| numeric user id | `Users?$filter=Sid eq '<sid>'` (cached 30 min) |
| live sessions + latest measurement | `Sessions?$filter=UserId eq <id> and EndDate eq null` with `Machine`, `DesktopGroup`, `CurrentConnection`, newest `SessionMetrics` sample |
| history | `Sessions?$filter=UserId eq <id> and EndDate ge <start>` (paged, up to `MaxPages`) |
| expert view | the one session by `UserId` **and** `SessionKey`, with machine, connection and up to 240 `SessionMetrics` samples; plus `Connections`, `ConnectionFailureLogs`, `ApplicationInstances` of that session |
| helpdesk search | `Users?$filter=contains(...)` |

Broker and Monitor sessions are joined by the **SessionKey**. If Monitor fails, live sessions keep working and the UI shows a notice; if the Broker fails, the page shows "service temporarily unavailable".

The three optional record sets of the expert view may not exist on every site. SSS reports a missing set as "not delivered by this site" and shows the rest. `Test-SssCitrix.ps1` tells you which ones your site has.

### Logon waterfall

The expert view draws the logon as bars on one time axis, so the step that eats the time stands out. The steps come from the properties of the `Connection` records Monitor keeps for the session (the original logon is preferred over a later reconnect):

- every `<Step>StartDate` / `<Step>EndDate` pair becomes a bar with a measured position, for example `Hdx`, `Gpo` (group policies), `LogOnScripts`, `ProfileLoad`, `Interactive` (shell and desktop);
- every `<Step>Duration` without such a pair (for example `BrokeringDuration`, `AuthenticationDuration`) becomes a bar with a known length but an **estimated position** (dashed): brokering ends where the logon starts, other steps follow the HDX connection;
- the overall window `LogOnStartDate`/`LogOnEndDate` and the session's `LogOnDuration` are the frame, not steps.

SSS does not hard-code the field names beyond this pattern, so new Citrix versions appear automatically. A site that delivers no step data shows no waterfall; `Test-SssCitrix.ps1` lists the `Connection` properties and tells you which steps your site provides. The longest measured step is highlighted with its share of the logon.

### Profile type and size

Monitor does not know what kind of profile a user has or how large it is. SSS therefore reads it **on the VDA**, on demand, through the read-only command `Get-SssProfile` of the JEA endpoint (the same endpoint as for [processes](PROCESSES.md); re-run `Install-SssJea.ps1` on the VDAs once to add it). The VDA reports:

| Detected | How |
|---|---|
| Microsoft FSLogix | a session entry under `HKLM\SOFTWARE\FSLogix\Profiles\Sessions\<SID>` or the mounted container volume labelled `Profile-<user>`, whose used and total space is shown as the container size |
| Citrix Profile Management (UPM) | the service `ctxProfile` is running; the user store from the `PathToUserStore` policy |
| Roaming profile (Windows) | `Win32_UserProfile` says roaming is configured; the roaming path |
| Local profile | none of the above; temporary and mandatory profiles are flagged |

The size of the profile folder is counted with a time limit (`Processes:ProfileScanSeconds`, default 20 s; the result then says "partial"), reparse points are not followed, and the largest folders are listed. All of this is read only. Which technology is detected is a best effort based on the usual markers; a site with an unusual setup may show "Local profile" for a container profile.

## Traffic lights

| Light | Metric | Source | Green / yellow / red (defaults) |
|---|---|---|---|
| Your device & network | ICA round-trip time | `SessionMetrics.IcaRttMS` | below 80 ms / from 80 / from 150 |
| Your device & network | available bandwidth | `SessionMetrics.OutputBandwidthAvailable` (bit/s, shown as kbit/s) | above 1500 / up to 1500 / up to 400 kbit/s |
| Datacenter & VDA | logon duration | `Sessions.LogOnDuration` (ms) | below 45 s / from 45 / from 90 |
| Datacenter & VDA | server load index | not connected yet | from 7000 / from 9000 |

- The **worst known** value of a light wins. A missing value is "unknown" (?), never red.
- Thresholds are in `Sss:Quality`. The traffic light always has a symbol and text, not only a colour.
- Standard view: the two lights plus a one-line hint when one is yellow or red. Expert view: every number Citrix collected, and charts over time of all numeric `SessionMetrics` columns (columns that are always 0 are left out): latency, bandwidth and datacenter latency.

## Limits

- The bandwidth unit (bit/s) was derived from measurements and is configurable (`Citrix:Monitor:BandwidthDivisor`).
- A fresh session may have no measurement for a few minutes.
- Ended sessions in the history list show only the logon duration; the expert view still shows their measurements if Monitor has not removed them yet. How long Monitor keeps data is set by Monitor's grooming settings, not by SSS.
- The helpdesk farm overview comes from the Broker only (one Monitor call per user would be too expensive); quality appears when a user is opened.
- Citrix property names are shown as they are, so the expert view matches what an administrator knows from Director and OData.
