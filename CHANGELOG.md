# Changelog

All notable changes to SSS. The project follows [Semantic Versioning](docs/COMPATIBILITY.md); versions below 1.0.0 may still change configuration keys between minor versions, and every such change is listed here.

## Unreleased

### Added
- **Architecture documentation** with diagrams of all components and communication paths, and a description of the optional component on the VDAs: what it is for and what works with and without it ([docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)).
- **Expert view in tabs** (Diagnosis, Over time, Properties, Profile). The dialog follows the window width (up to 1700 px) and keeps its header in place.
- **Diagnosis tab**: explains both traffic lights metric by metric (value, rating, rule), names the cause of a yellow or red light and says why a light is unknown. A live session without a network value shows "not measured yet" in the list.
- **Times to the second** in all tables, **timestamps to the millisecond** and durations in a sensible precision in the expert view. Start/end pairs show the difference; step durations and waterfall bars are rated good / normal / slow by color, symbol and text.
- Charts show the warning and critical limit of the latency, minimum/average/maximum, and gaps where nothing was measured.

### Changed
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
