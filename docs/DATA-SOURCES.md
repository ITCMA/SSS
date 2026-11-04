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
| live sessions + latest measurement | `Sessions?$filter=UserId eq <id> and EndDate eq null` with `Machine`, `DesktopGroup`, `CurrentConnection`, latest 10 `SessionMetrics` samples |
| history | `Sessions?$filter=UserId eq <id> and EndDate ge <start>` (paged, up to `MaxPages`) |
| expert view | the one session by `UserId` **and** `SessionKey`, with machine, connection and up to 240 `SessionMetrics` samples; plus `Connections`, `ConnectionFailureLogs`, `ApplicationInstances` of that session |
| helpdesk search | `Users?$filter=contains(...)` |

Broker and Monitor sessions are joined by the **SessionKey**. If Monitor fails, live sessions keep working and the UI shows a notice; if the Broker fails, the page shows "service temporarily unavailable".

The three optional record sets of the expert view may not exist on every site. SSS reports a missing set as "not delivered by this site" and shows the rest. `Test-SssCitrix.ps1` tells you which ones your site has.

### Connection, endpoint and VDA data in the expert view

Besides the session and its metrics the expert view reads, where the site delivers them (checked in `$metadata`, otherwise the part is simply missing):

- the **connection** (Summary tab, "Connection"): endpoint name, address, platform and Workspace app version, location, network type, Gateway (`ConnectedViaHostName`), protocol and encryption, with the latencies between the parts (WAN, datacenter, network, ICARTT) like Director's session topology;
- **EndpointMetrics** (CPU, memory, GPU, Wi-Fi signal and throughput of the client device) and **ResourceUtilization** of the VDA machine (CPU, memory in percent, session count) as charts in "Over time";
- the **Insights** ([INSIGHTS.md](INSIGHTS.md)): all live sessions with their latest metrics, the sessions of the last 7 days with logon duration and delivery group, the machines with their newest load index, and the counts of `ConnectionFailureLogs` and `SessionAutoReconnects` per session;
- the **logon details** the Monitor keeps apart: user initialisation and AppX steps as additional steps of the logon, profile type and size, the number of large files, the slowest group policy extensions, and the HDX protocol with MTU and the gateway location;
- the **load index** of the VDA machine (0-10000), which feeds the second traffic light ("Datacenter & VDA") like the logon duration;
- the **applications started in the session** (`ActiveApplicationLaunches`, `HistoricalApplicationLaunches`) with start duration, and **application crashes** (`ApplicationFaults`).

### Logon waterfall

The expert view draws the logon as bars on one time axis, so the step that eats the time stands out. The steps come from the properties of the `Connection` records Monitor keeps for the session (the original logon is preferred over a later reconnect):

- every `<Step>StartDate` / `<Step>EndDate` pair becomes a bar with a measured position, for example `Hdx`, `Gpo` (group policies), `LogOnScripts`, `ProfileLoad`, `Interactive` (shell and desktop);
- every `<Step>Duration` without such a pair (for example `BrokeringDuration`, `AuthenticationDuration`) becomes a bar with a known length but an **estimated position** (dashed): brokering ends where the logon starts, other steps follow the HDX connection;
- time inside the logon window that no step explains (connection set-up, authentication, session start ...) is drawn as a grey "not itemized" bar, so a pause in the chart is visible as missing data and not mistaken for idle time. All positions are the timestamps Monitor delivers, relative to `LogOnStartDate`; steps can overlap, for example group policies and the shell start;
- the overall window `LogOnStartDate`/`LogOnEndDate` and the session's `LogOnDuration` are the frame, not steps.

SSS does not hard-code the field names beyond this pattern, so new Citrix versions appear automatically. A site that delivers no step data shows no waterfall; `Test-SssCitrix.ps1` lists the `Connection` properties and tells you which steps your site provides. The longest measured step is highlighted with its share of the logon. Steps that lie completely inside a longer step are shown below it (group policies and logon scripts inside the interactive session; power-on and boot below the machine start), the tooltip names start and end to the millisecond, and for each step the **average of the same user's logons of the last seven days** is shown next to it (▲ clearly slower, ▼ faster, ≈ similar) once at least two earlier logons exist.

### Profile type and size

Monitor does not know what kind of profile a user has or how large it is. SSS therefore reads it **on the VDA**, on demand, through the read-only command `Get-SssProfile` of the JEA endpoint (the same endpoint as for [processes](PROCESSES.md); re-run `Install-SssJea.ps1` on the VDAs once to add it). The VDA reports:

| Detected | How |
|---|---|
| Microsoft FSLogix | a session entry under `HKLM\SOFTWARE\FSLogix\Profiles\Sessions\<SID>` or the mounted container volume labelled `Profile-<user>`, whose used and total space is shown as the container size |
| Citrix Profile Management (UPM) | the service `ctxProfile` is running; the user store from the `PathToUserStore` policy |
| Roaming profile (Windows) | `Win32_UserProfile` says roaming is configured; the roaming path |
| Local profile | none of the above; temporary and mandatory profiles are flagged |

Besides type and size SSS reads, read only: the folder tree (four levels, the 80 largest folders), the 10 largest files, the folders that are **redirected** (User Shell Folders of the logged-on user), the **FSLogix** settings (`Profiles` and `ODFC`, local and policy), the exclusions from `redirections.xml` if the share is readable, the **Citrix Profile Management** policy values including exclusion lists, and the number of local profiles on the machine. A rule set turns this into findings (full or bloated container, temporary profile, roaming profile that is large or not redirected, Outlook/Teams/browser/OneDrive/Temp caches, unredirected personal data, missing exclusions, profile streaming off, many leftover local profiles). Findings are advice, not proof.

The size of the profile folder is counted with a time limit (`Processes:ProfileScanSeconds`, default 20 s; the result then says "partial"), reparse points are not followed, and the largest folders are listed. All of this is read only. Which technology is detected is a best effort based on the usual markers; a site with an unusual setup may show "Local profile" for a container profile.

## Traffic lights

| Light | Metric | Source | Green / yellow / red (defaults) |
|---|---|---|---|
| Your device & network | ICA round-trip time | `SessionMetrics.IcaRttMS` | below 80 ms / from 80 / from 150 |
| Your device & network | available bandwidth | `SessionMetrics.OutputBandwidthAvailable` (bit/s, shown as kbit/s) | above 1500 / up to 1500 / up to 400 kbit/s |
| Datacenter & VDA | logon duration | `Sessions.LogOnDuration` (ms) | below 45 s / from 45 / from 90 |
| Datacenter & VDA | server load index | `LoadIndexes.EffectiveLoadIndex` of the machine (when the site delivers it) | from 7000 / from 9000 |

- The **worst known** value of a light wins. A missing value is "unknown" (?), never red.
- A value of **0** for ICA round-trip time or bandwidth means "not measured" in Monitor and counts as missing. Monitor writes several kinds of sample rows and the newest one does not always carry every metric, so SSS reads the latest 10 samples and takes the **newest usable value per metric**; the expert view shows when that sample was taken.
- Thresholds are in `Sss:Quality`. The traffic light always has a symbol and text, not only a colour.
- Standard view: the two lights plus a one-line hint when one is yellow or red, or "not measured yet" while a live session has no network value.
- Expert view, tab **Diagnosis**: for each light the metrics with value, rating and rule, the cause in plain words ("ICA round-trip time 210 ms (poor)"), the reason when a light is unknown, and the age of the newest network sample. Tab **Over time**: charts of the numeric `SessionMetrics` columns (latency with the warning and critical limit as reference lines, gaps where nothing was measured, minimum/average/maximum; rarely useful columns sit under "More metrics"). Tab **Properties**: every value Citrix collected.
- SSS rates only what Monitor reports per session. Director may show additional or differently aggregated values (for example for the session topology), so numbers can differ.

### Times and ratings in the expert view

Times are shown to the second, Citrix timestamps to the millisecond. Durations use a sensible precision (46 ms, 2.08 s, 10.6 s, 1 min 12 s). Where two values belong together (`<Step>StartDate` and `<Step>EndDate`), the difference follows the end time (`Δ 2.1 s`), and step durations in the *Properties* tab and the bars of the waterfall are colored: green = good, blue = normal, red = slow (also marked with a symbol and a text for screen readers). The limits are rules of thumb for a typical site; the whole logon uses the limits of the traffic light.

| Step | good below | slow from |
|---|---|---|
| Brokering | 1 s | 3 s |
| HDX connection | 1.5 s | 4 s |
| Authentication | 2 s | 6 s |
| Group policies | 5 s | 15 s |
| Logon scripts | 5 s | 15 s |
| Profile load | 5 s | 15 s |
| Shell / desktop (interactive) | 8 s | 20 s |
| Machine start | 30 s | 90 s |
| Whole logon | `Sss:Quality:LogonWarnSeconds` | `Sss:Quality:LogonCritSeconds` |

Other durations are shown without a rating.

## Limits

- The bandwidth unit (bit/s) was derived from measurements and is configurable (`Citrix:Monitor:BandwidthDivisor`).
- A fresh session may have no measurement for a few minutes.
- Ended sessions in the history list show only the logon duration; the expert view still shows their measurements if Monitor has not removed them yet. How long Monitor keeps data is set by Monitor's grooming settings, not by SSS.
- The helpdesk farm overview comes from the Broker only (one Monitor call per user would be too expensive); quality appears when a user is opened.
- Citrix property names are shown as they are, so the expert view matches what an administrator knows from Director and OData.
