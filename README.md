# SSS – Session Self Service

[Deutsche Version](README.de.md)

**Your users fix their own Citrix problems in seconds. Your helpdesk sees *why* a session is slow, in one click.**

SSS is a small, fast web portal for **Citrix Virtual Apps and Desktops (CVAD)**. Users sign in with their Windows account and see **only their own sessions**: how good each one is (traffic lights), what ran before (history), and a **Log off** button that works. The helpdesk gets the deep view on top: logon waterfall, performance graphs, profile analysis with advice, and control over processes. No agent, no database, no client software, one IIS application.

<p align="center">
  <img src="docs/img/overview-light.png" alt="Self-service overview with session quality traffic lights" width="49%">
  <img src="docs/img/overview-dark.png" alt="Dark mode" width="49%">
</p>
<p align="center"><em>Demo data, light and dark mode</em></p>

> **Status: beta.** Tested end to end on Citrix Virtual Apps and Desktops **2203, 2402, 2507 and 2607 (LTSR)** test sites (single Delivery Controller). Several Delivery Controllers and several web servers are implemented ([docs](docs/MULTI-SERVER.md)) but not yet verified on a real site. Try it without Citrix: the built-in demo mode needs only the .NET SDK.

## For your users: help themselves in one click

A frozen application, a slow desktop, a session left open on another machine: today that is a ticket. With SSS the user opens one page and sees what is going on.

- **All own sessions at a glance**: desktops and apps, active or disconnected, where they run, since when, from which device.
- **Session quality with traffic lights**, split into *Your device & network* (latency, bandwidth) and *Datacenter & VDA* (logon time, server load), with a plain-language tip ("High latency: check Wi-Fi, VPN or the distance to the datacenter"). Colours always come with a symbol and a word.
- **History** of the last hour, day, week or month: what was running, how long, how good it was.
- **Log off** a frozen session, all disconnected ones, or everything, with a confirmation. There is deliberately no "disconnect" button.
- **Own sessions only**, guaranteed on the server (see Security). Filter, sort, CSV export, light and dark mode, phone friendly, keyboard and screen-reader friendly, in English, Deutsch, Français, Español and 中文.
- Optional link to StoreFront to start the desktop again.

## For your helpdesk: the diagnosis in one dialog

Members of an AD group get a **Helpdesk** tab and an **Expert view** with everything Citrix knows, without opening Director and without a remote session.

<p align="center">
  <img src="docs/img/expert-light.png" alt="Expert view with logon waterfall and performance graphs" width="49%">
  <img src="docs/img/profile-light.png" alt="Profile analysis with findings and folder tree" width="49%">
</p>
<p align="center"><em>Logon waterfall and performance graphs; profile type, size, folder tree and findings</em></p>

- **Find a user** or list **all live sessions of the farm**; open any user's sessions and history.
- **Log off** sessions (with a mandatory or optional reason) and **send a message** to a session ("please save your work, the server restarts").
- **Performance data and graphs** over time: ICA round-trip time, available bandwidth, datacenter latency, with values on hover, plus connection history and failure or disconnect reasons.
- **Logon waterfall**: where the logon time goes (brokering, HDX connection, authentication, group policies, logon scripts, profile load, shell). The slowest step is highlighted, time Citrix does not itemize is shown as such.
- **Profile analysis, read on the VDA**: local, roaming, Citrix Profile Management or **FSLogix**; container and folder size; a TreeSize-like **folder tree** and the **largest files**; folder redirection; FSLogix and UPM settings and exclusions; and **findings with advice** (container nearly full, Outlook or Teams caches, personal data in the profile, missing exclusions, leftover local profiles, ...).
- **End applications and processes** of a session through a restricted endpoint on the VDAs (off by default).
- **"Copy support info"** puts the key facts of a session into the clipboard for a ticket.
- **Status page** with live health checks instead of digging through event logs; **audit trail** of every search, view, log-off, message and process action.

<p align="center"><img src="docs/img/processes-light.png" alt="Applications and processes of a session" width="60%"></p>

## Why SSS

| | |
|---|---|
| **Light and fast** | ASP.NET Core running inside IIS. No database, no agent, nothing to install on clients, no cloud. Warm PowerShell workers keep the Citrix SDK loaded. Runs next to Web Studio on the same server. |
| **Secure by design** | The identity comes only from IIS Windows authentication. Every action is checked again on the server and again in the worker, so a user cannot touch a session that is not theirs. CSRF protection, strict content security policy, rate limits, **no third-party packages**. [Threat model](docs/THREAT-MODEL.md). |
| **Simple to run** | One service account (gMSA), one script: `install.ps1`. `Test-SssSetup.ps1` checks the wiring and prints the fix for every finding. Updates roll back by themselves if the new version does not answer. |
| **Ready for bigger sites** | Several Delivery Controllers with failover, several web servers behind a load balancer (health probe, shared key ring), audit trail, versioning policy, English and German documentation. |

## Get started

**Try the demo (about two minutes, no Citrix needed):**

```bash
git clone https://github.com/ITCMA/SSS.git && cd SSS
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5080 dotnet run --project src/Sss.Web
```

Open <http://localhost:5080> and add `?shot=profile` or `?shot=processes` to the address to see the expert dialogs. Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

**Install on your Citrix site:** download the zip from the [releases](https://github.com/ITCMA/SSS/releases), then follow the **[set-up guide](docs/SETUP.md)**: prepare a service account, run `install.ps1`, check with `Test-SssSetup.ps1` and `Test-SssCitrix.ps1`. What the server needs is listed in the [requirements](docs/REQUIREMENTS.md).

## Security in short

- The user identity comes **only** from IIS Windows authentication, never from request parameters.
- Every log-off, message and process action is re-checked server-side against the caller's own live sessions before anything is sent to Citrix; foreign or unknown ids are rejected and audited.
- Anti-forgery token, per-user rate limits, strict CSP and security headers, no inline scripts, no `innerHTML`.
- The helpdesk is a separate set of endpoints behind an AD group; everything it does is audited.
- Process control uses a restricted JEA endpoint with three commands and shows exactly what it can and cannot do ([docs](docs/PROCESSES.md#what-this-exposes-and-what-it-does-not)).
- Details and how to report a problem: [SECURITY.md](SECURITY.md), [threat model](docs/THREAT-MODEL.md).

## Documentation

| | |
|---|---|
| [Set-up guide](docs/SETUP.md) | from nothing to a working SSS: service account, installation, connection, helpdesk |
| [Requirements](docs/REQUIREMENTS.md) | software, accounts, Citrix, ports |
| [Configuration reference](docs/CONFIGURATION.md) | every option with default and meaning |
| [Permissions at a glance](docs/PERMISSIONS.md) | who needs which right, where and why |
| [Operations](docs/OPERATIONS.md) | update, health probe, status page, audit trail, troubleshooting |
| [Applications, processes and profiles](docs/PROCESSES.md) | the JEA endpoint on the VDAs, setup, security |
| [Several controllers and servers](docs/MULTI-SERVER.md) | failover, load balancer, Kerberos alias, Monitor over HTTPS |
| [Data sources](docs/DATA-SOURCES.md) | where the data comes from and how quality is rated |
| [Architecture](docs/ARCHITECTURE.md) | components and request flows |
| [Versioning and compatibility](docs/COMPATIBILITY.md), [Changelog](CHANGELOG.md) | what is stable, how to update |
| [Threat model](docs/THREAT-MODEL.md), [Security policy](SECURITY.md) | what SSS protects and how to report problems |
| [Contributing](CONTRIBUTING.md) | development, tests, screenshots |

## Roadmap

- [x] Citrix provider: live sessions and log-off via Broker SDK (warm Windows PowerShell 5.1 workers), history via Monitor OData
- [x] Tested on CVAD 2203, 2402, 2507 and 2607 (single Delivery Controller)
- [x] Helpdesk role (AD group): search, farm overview, log off with reason, message to a session, audit
- [x] Expert view with charts, logon waterfall, profile analysis, support info, StoreFront link, status page, setup check
- [x] End single processes of a session (JEA endpoint on the VDAs)
- [x] IIS installer with automatic rollback, release builds via GitHub Actions
- [ ] Verify several controllers and several web servers on a real site
- [ ] Polite "close" of an application (needs a helper inside the user's session)
- [ ] Stricter custom Citrix role for the service account ([how to derive it](docs/PERMISSIONS.md#narrowing-the-role-open-item-before-10))
- [ ] Citrix Cloud (DaaS) backend
- [ ] 1.0 after a public beta period

## License

[Apache License 2.0](LICENSE)

*Citrix is a trademark of Cloud Software Group, Inc. This project is independent and not affiliated with or endorsed by it.*
