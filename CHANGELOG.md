# Changelog

All notable changes to SSS. The project follows [Semantic Versioning](docs/COMPATIBILITY.md); versions below 1.0.0 may still change configuration keys between minor versions, and every such change is listed here.

## Unreleased

### Added
- **Insights, more**: machine load of the last 7 days (CPU and memory, average and peak, from `ResourceUtilizationSummary`), start times of applications (average, 90th percentile and the machine where an application starts slowest, from `HistoricalApplicationLaunches`; user names are not read), costs and savings of the last 30 days per delivery group (`MachineCostAllocationSummaries`) and a timeline "What changed" (machine registered, lost its registration, switched on; for the auditor also log-offs, messages and ended processes from the audit trail). Each part says so when the site delivers no data.
- **Health score** from 0 to 100 per machine (Insights, machines table) and per user (user history card), as a number with symbol and word.
- **Director-like look**: the period of crashes and failures is a row of buttons (like Director's 24 hours / 7 days switch), bars have a light track and fade by rank, the Environment tab has a ring for the Workspace app versions and the applications by peak of concurrent instances, Insights says "as of" when the data was read. The "Over time" graphs are in the gallery.
- Insights: costs, load, start times and the timeline explain themselves (help text under each heading and tooltips on the columns); crashes and failures say "n of m machines"; the Status tab warns when processes are on but `Sss:Roles:Level3Group` is empty (nobody can then read processes).
- **Insights starts with the day**: five key figures (logons, average logon time, crashes, connection failures, conspicuous machines; a click jumps to the part) and one chart of the last 24 hours: bars for the logons per hour, a line for the average logon time with its own scale and the warning limit, and crashes (triangles) and connection failures (dots) on the same time axis, with machine and user in the tooltip. It replaces the separate bar strips under the tables.
- **Machine page**: a click on a machine (tile, table or load strip) opens everything on one page: state and health score, load and utilisation, the last 24 hours, crashes and connection failures there, what changed, and the sessions running on it with an Open user button. For this, `GET api/helpdesk/insights/machine?name=` (helpdesk).
- **Load of the machines over the day**: one row per machine, one cell per hour of the last 24 hours (darker = more CPU, a red cross where the registration was lost), from the hourly utilisation of the Monitor.
- **Delivery groups at a glance**: one card per group; the worst of logon time, machines, crashes and failures sets the light and each line names its reason; a click filters the machines to that group.
- **Connection path** on the Environment tab: how many sessions run through which gateway, directly, or unknown (from the "connected via" host the Monitor records).
- **Day chart with hover**: pointing at (or tapping) an hour shows a tip with its logons, average logon time and every crash and failure of that hour with machine and user.
- **Machines, for big sites**: a search field filters tiles and table while you type (each word must be part of the name, group or state), "only conspicuous", tiles grouped per delivery group and collapsible (hidden by default above 40 machines, the choice is remembered), the count "n of m machines", and a **Show sessions** button per table row.
- **Insights, more pictures**: the logon time of every delivery group as lines over 7 days with the warning limit.
- **Machine tiles** in Insights: one tile per VDA with a colour and symbol by health, load bar and sessions; a click lists the machine's sessions. The charts of the Environment tab are arranged in a calm grid (four cards in a row, two bar charts side by side).
- Helpdesk list: a **Processes** button per session for level 3 (opens the user's view and the process list); in the user's view the button no longer needs the expert view.

### Fixed
- Connection path and diagnosis: a "connected via" host of the machine itself (`127.0.0.1`, `::1`, `localhost`) counts as direct, not as a gateway.
- The day chart is more compact (smaller type, lower), the logon time per group uses the full width, and the Environment charts are in the grid of four cards, the connection path on its own row, the two bar charts side by side.
- Crashes without a session of the Monitor now name the user from the launch record of the program (same program, same machine, running at the time of the crash).
- Crash details without a session key (or with a session the Monitor no longer lists) take the user from the one session that ran on that machine at that time; on a machine with several sessions at once the user stays empty.
- Machine load listed only a few machines (one page of minute values): the daily values are asked for first. Crash and failure details find the user from the sessions of the last 7 days and the live sessions. Application start times also read the running launches and name the number of starts read when none is usable.

## 0.4.3 - 2026-11-04 (beta)

### Added
- **Citrix Cloud (DaaS), read-only** ([docs/CLOUD.md](docs/CLOUD.md)): `Sss:Citrix:Cloud` signs in with an API client and reads the cloud Monitor service instead of the Delivery Controller and the PowerShell Broker. Sessions, history, expert view, Insights, Map and Environment work; log-off, messages and processes are hidden and answered with 501. Not yet tested against a real tenant.
- **Map tab**: all live sessions on a world map (zoom with the wheel, buttons or keys, move by dragging). Each place shows its number of sessions; far zoomed in, every session is its own dot with the user name. Search by user, device, machine, delivery group or city highlights and zooms to the matches; a click on a place lists its sessions with an Open user button. The outlines and a list of about 5,700 places are bundled (Natural Earth, public domain) and the position is found in the browser, so no address or name goes to a map service. Cities missing from the list are shown in the middle of the country (marked as approximate); sessions without a country are listed below the map. For every helpdesk level.
- Insights: the Details of application crashes name the user in more cases (the session lookup covers up to 600 sessions instead of 200).
- **Insights: crashes and failures in depth**: period switch (1, 3, 6, 12, 24 hours), users, machines, first and last time per application or reason, and a Details dialog with every single record (when, user, machine, delivery group, client, version, path, description) and a hint on the pattern; a table of all disconnected sessions with the time since the disconnect (new `stateChange` from the Broker worker).
- **Insights, more depth**: application crashes and connection failures of the last 24 hours (by application or reason, machines affected, machine with most), sessions disconnected for hours, machines running 14 days or more, a 7-day curve of the logon time per delivery group (groups with fewer than 5 sessions are not rated), the machine's running time.
- **Insights tab** ([docs/INSIGHTS.md](docs/INSIGHTS.md)) for every helpdesk level: conspicuous slow connections and logons, machines not registered or busy, each with a button to open the user or the filtered session list; logon time per delivery group (average, median, 90 %); machine overview with load index.
- **Delivery group comparison**: the expert view shows the logon time of a session against its group's last 7 days.
- **History of a user** (a card with coloured tiles and a bar comparison with the group): sessions, logon time against the group, connection failures and automatic reconnects of the last 30 days above the user's sessions.
- **Cards for users**: the own sessions as cards (one sentence on whether all is fine, quality lights, log off, technical data under "Details"); the helpdesk and the expert view keep the table. History period and export moved to the history section.

### Changed
- **Event log**: on a single server the data protection keys (CSRF) now live in `data\keys` and are encrypted with DPAPI (machine scope). Before, ASP.NET Core wrote them in clear text and logged the warning "No XML encryptor configured" (event 1000, `XmlKeyManager`) at every start of the app pool. The first start after the update creates new keys, so an open page needs one reload. With a shared `KeyDirectory`, set `ProtectToSid` to avoid the warning.
- Citrix Cloud: one polite retry after HTTP 429; `scripts/Test-SssCloud.ps1` checks the sign-in and the reading of the Monitor service.
- Screenshots: light mode only, all recreated by `scripts/Make-Screenshots.sh`; new gallery ([docs/GALLERY.md](docs/GALLERY.md)).
- Expert view, Diagnosis: a connection counts as direct only when the "connected via" host is the client or the VDA itself (without a host name the address decides). Anything else, for example a NetScaler whose address Citrix also records as the client address, is shown as gateway. A direct connection shows only the arrow with "Direct", without a gateway box.
- `web.config` sends `X-Frame-Options: DENY` (the CSP `frame-ancestors 'none'` was already there); closes the code-scanning alert about missing clickjacking protection.
- Accessibility and feedback: the message field has a visible label, the area names "Views" and "Controls" are translated, a log-off says "Logging off the session …" and locks the row actions until it is done, an empty filter says "No sessions match the filter" with a button to reset it (instead of "no sessions"), and the keyboard focus stays on the same button when the tables refresh.

## 0.4.2 - 2026-11-04 (beta)

### Added
- **Environment tab charts**: sessions by platform and by location (donuts), VDA machines by state, most started applications; not registered machines are listed last.
- **Expert view**: a connection without a NetScaler Gateway says "No gateway in use: the connection goes directly to the VDA" instead of showing the client as gateway.
- **Environment tab**: sessions without a client version are listed as "unknown"; VDA machines without an agent version are summed per delivery group (with the number switched off) instead of one "?" row; the helpdesk session list keeps user names and the Open button on one line.
- **Expert view**: a "Stability of the session" card (connections, failures, automatic reconnects from `SessionAutoReconnects`); application records show the application name; sizes in GB, AppX durations from start and end, no negative Wi-Fi values.
- **Environment tab**: list of the VDA machines (delivery group, agent, registration, power, maintenance, sessions, last change) and the Local Host Cache report; sessions without a Citrix location form an "unknown" row, so the rows add up to all sessions; the own-networks table is explained as optional.
- **JEA endpoint: `Get-SssPolicy`** (read only) shows the Citrix policy values in force in a session (helpdesk level 3, new "Policies" tab in the expert view); `Get-SssProcess` also returns the product name of each program. Re-run `Install-SssJea.ps1` on the VDAs (or the master image) to get both; without it SSS keeps working and shows the short names.
- **Load index of the VDA** now feeds the "Datacenter & VDA" traffic light (it was "not connected yet").
- **Logon details**: user initialisation and AppX steps, profile type and size, number of large files, slowest group policy extensions, HDX protocol with MTU, gateway location (from the Monitor's `LogOnMetrics`, `SessionLogonMetrics`, `SessionMetricsLatest`, `GatewayPopInfo`).
- **Environment tab**: VDA machines by agent version and operating system; the program file behind each short application name.
- **Environment tab** (role Admin, [docs/ENVIRONMENT.md](docs/ENVIRONMENT.md)): Workspace app versions and platforms of all live sessions (devices, sessions, share), sessions per location, with locations defined by your own networks (`Sss:Environment:Locations`). Counts only, cached for a minute.
- **Outdated client hint**: with a minimum version per platform (`Sss:Environment:MinClientVersion`) outdated clients are marked, and the user sees a hint with the version in the own session list (switch off with `HintOutdatedClient`).
- **Environment tab**: the Citrix-reported country and city of the connections and the most used applications inside the sessions (names, launches, peak, users, machines), when the Monitor delivers them.
- **Expert view**: a "Connection" card (endpoint, gateway, VDA with WAN, datacenter and network latency, protocol, encryption), charts for the client device and the VDA machine (CPU, memory, sessions), the applications started in the session with their start duration, application crashes, sub-steps in the logon (group policies and scripts inside the interactive session, power-on and boot below the machine start), precise start and end times, and the user's seven-day average next to each logon step.
- `Test-SssCitrix.ps1` also prints the properties of record types that may carry logon details, scores or load data.
- `Test-SssCitrix.ps1` lists the entity sets and properties of the Monitor (client, platform, location, policy and score names) and whether candidate sets contain data, to show what else the site delivers.

### Changed
- The logon waterfall is drawn smaller and leaves room for the comparison with the user's average.
- **The ticket number is optional again** (`Sss:Helpdesk:ReasonRequired` defaults to `false`; the field stays available and is logged when filled in). Set it to `true` to require it.
- The expert view of a helpdesk member below level 3 no longer offers the Profile tab (it answered "something went wrong"); a missing right is now named.
- Layout: connection card next to the quality explanation, the waterfall below it at full width; property groups pack into columns; the Environment tables have even columns and spacing; table text wraps instead of pushing the action column out of view.
- The effective-configuration view masks the values of HTTP destination headers (they may carry keys); other key/value lists are shown in clear.

## 0.4.1 - 2026-11-03 (beta)

### Added
- **Configuration tooling** ([docs/CONFIGURATION.md](docs/CONFIGURATION.md#editing-and-checking-the-configuration)): a JSON schema `docs/sss.config.schema.json` generated from the option classes (completion, descriptions and warnings in editors), example configurations in `docs/examples`, `Test-SssConfig.ps1` / `Sss.Web.exe --check-config` to validate a configuration without starting SSS (errors and warnings, also run by `install.ps1` after install and update), a read-only "Effective configuration" view with the source of every value in the Status tab, and more questions in `install.ps1 -Interactive` (auditor and admin group, central audit folder, controller discovery).
- **Roles** ([docs/ROLES.md](docs/ROLES.md)): helpdesk levels 1 (look), 2 (log off, message) and 3 (processes, profiles), plus separate **Auditor** and **Admin** roles, each tied to an AD group (`Sss:Roles`). A helpdesk member no longer sees the audit trail or the system pages unless granted. `Sss:Helpdesk:Group` keeps meaning "everything of level 3".
- **Mandatory ticket number or reason** for helpdesk actions that change something or read processes and profiles (content not checked, written to the audit trail).
- **Central audit trail** ([docs/AUDIT.md](docs/AUDIT.md)): every line names who acted (also for own actions), the role, the affected user, time to the millisecond, address, server and request id, and is chained to the previous line by a hash. A forwarder with store-and-forward copies it to a UNC folder, Syslog (RFC 5424, JSON or CEF, UDP/TCP/TLS), HTTP (Ndjson, JSON array, Splunk HEC, Elastic bulk, chat webhook text) and SNMP v2c traps, with filters per destination. Operational warnings and errors travel in a second stream.
- **Audit tab** for the Auditor role: search and filter, evaluation (per day, most active people, actions, results, servers), CSV export, state of the destinations and an integrity check.
- **Bulk alert**: a helpdesk request that logs off five sessions or more writes `alert:bulk-logoff` (threshold configurable), which a chat or SNMP destination can pick up.
- **Delivery Controller discovery** and **load balancing** ([docs/CONTROLLERS.md](docs/CONTROLLERS.md)): the controllers of the site are read from one bootstrap controller, cached and refreshed; requests are spread by failover, round robin or the faster of two random healthy controllers, with preferred controllers or zones and a circuit breaker for failing ones. The status check and `/healthz` now really ask a controller.
- **Server list** for several SSS servers ([docs/MULTI-SERVER.md](docs/MULTI-SERVER.md#server-list-and-configuration)): every server reports to a shared folder; the Status tab shows missing, stale and unexpected servers, other versions and configuration drift with the sections that differ. `sss.local.json` holds settings of one server only.
- `Sync-SssConfig.ps1` compares and rolls out `sss.config.json` one server at a time (backup, drain, copy, recycle); `/healthz` answers 503 while `data\drain.flag` exists.
- `GET /metrics` in the Prometheus text format (off by default, restricted to listed addresses; `install.ps1 -MetricsEndpoint`).
- Release provenance attestation for the zip (GitHub Actions; a failing attestation does not block the release).
- **Architecture documentation** with diagrams of all components and communication paths, and a description of the optional component on the VDAs: what it is for and what works with and without it ([docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)).
- **Expert view in tabs** (Diagnosis, Over time, Properties, Profile). The dialog follows the window width (up to 1700 px) and keeps its header in place.
- **Diagnosis tab**: explains both traffic lights metric by metric (value, rating, rule), names the cause of a yellow or red light and says why a light is unknown. A live session without a network value shows "not measured yet" in the list.
- **Times to the second** in all tables, **timestamps to the millisecond** and durations in a sensible precision in the expert view. Start/end pairs show the difference; step durations and waterfall bars are rated good / normal / slow by color, symbol and text.
- Charts show the warning and critical limit of the latency, minimum/average/maximum, and gaps where nothing was measured.

### Changed
- All configuration checks moved into one validator that SSS runs at startup (messages unchanged) and `--check-config` runs with additional warnings. New checks: quality thresholds in the right order, rate limits and instance timings, audit destinations, metrics addresses, role group names without a domain.
- `Sss:Helpdesk:ReasonRequired` now defaults to `true`; set it to `false` to keep the ticket number optional.
- The audit field `reason` holds the ticket number or reason only; the search text, message text and process id moved to the new field `detail`. `actor` is now always filled (the person who clicked, also for own actions).
- New folder `data` (`Sss:DataDirectory`) for the controller cache, the forwarding positions of the audit destinations and the drain flag; `install.ps1` creates it, grants the app pool identity *modify* and keeps it on updates, as it does for `logs` and now also for `sss.local.json`.
- A value of 0 for ICA round-trip time is "not measured" (like bandwidth) and no longer rated as perfect. The newest usable value of the latest 10 metric samples is used per metric, so a newest sample without ICA values no longer turns a light to "unknown".
- Rarely useful metric charts moved under "More metrics".

## 0.4.0 - 2026-10-31 (beta)

### Added
- **Expert view**: everything Citrix Monitor collected about a session, grouped like Director's detail pages, with charts over time (hover values, time grid) and a "Copy support info" button.
- **Logon waterfall**: logon time by step (brokering, HDX, authentication, group policies, logon scripts, profile load, shell), built from the Monitor connection data; time that Monitor does not itemize is shown as a grey bar.
- **Profile analysis** read on the VDA: type (local, roaming, Citrix Profile Management, FSLogix), container and folder size, folder tree and largest files, folder redirection, FSLogix and UPM settings and exclusions, and findings with advice.
- **Applications and processes** of a session: list and end processes, for users (own sessions) and the helpdesk, through a restricted JEA endpoint on the VDAs (`scripts/Install-SssJea.ps1`). Off by default.
- **Helpdesk**: message to a session, status page, expert view of any session.
- **Several Delivery Controllers and web servers**: Monitor failover across controllers, shared key ring for CSRF tokens, per-server audit files, anonymous `GET /healthz` probe for load balancers (`install.ps1 -HealthEndpoint`).
- `Test-SssSetup.ps1`, `Test-SssCitrix.ps1` with `-Redact` and `-Vda`, `install.ps1 -Interactive/-Update` with rollback.
- Documentation: configuration reference, requirements, operations, processes, multi-server, data sources, permissions (English and German).

### Changed
- The Monitor is addressed over `https://` by default. Set `Sss:Citrix:Monitor:UseHttps` to `false` for a controller whose Monitor service answers on `http://` only (its OData service is not an IIS application).
- `web.config` no longer wraps the handler in `<location path=".">`, so sub paths such as `/healthz` inherit it.
- Unknown keys in `sss.config.json` are reported (status tab, setup check, log).

### Security
- The JEA endpoint offers three commands, accepts only the accounts given with `-Principal`, can be limited to the SSS servers with `-AllowedFrom`, and writes transcripts readable by administrators only. See [docs/PROCESSES.md](docs/PROCESSES.md#what-this-exposes-and-what-it-does-not).

## 0.3.x

First releases with the Citrix provider (Broker SDK workers, Monitor OData), helpdesk role, IIS installer and release builds.
