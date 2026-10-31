# Changelog

All notable changes to SSS. The project follows [Semantic Versioning](docs/COMPATIBILITY.md); versions below 1.0.0 may still change configuration keys between minor versions, and every such change is listed here.

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
