# Changelog

All notable changes to SSS. The project follows [Semantic Versioning](docs/COMPATIBILITY.md); versions below 1.0.0 may still change configuration keys between minor versions, and every such change is listed here.

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
