# Delivery Controllers: discovery and load balancing

[Deutsch](CONTROLLERS.de.md)

SSS can work with a fixed list of Delivery Controllers, find them itself, or talk to a virtual address in front of them. All three are supported, and they can be combined. A *site* has several controllers that share one database, so every controller answers with the same data; spreading the requests is therefore safe.

| Option | When it fits | Configuration |
|---|---|---|
| **A. Fixed list** | small site, controllers rarely change | `Citrix:Controllers` = all names |
| **B. Discovery** (recommended for larger sites) | controllers are added or replaced now and then | `Citrix:Discovery:Enabled` = `true`, `Bootstrap` = one controller |
| **C. Virtual address** | a load balancer (NetScaler/ADC, DNS alias) already fronts the controllers | `Citrix:Controllers` = `["ddc.corp.local"]` and `Strategy` = `Failover` |

## Discovery

```json
"Citrix": {
  "Controllers": [ "ddc01.corp.local" ],
  "Discovery": { "Enabled": true, "RefreshMinutes": 15 }
}
```

- SSS asks one controller (`Discovery:Bootstrap`, by default the first entry of `Controllers`) for **all controllers of its site** (`Get-BrokerController`) and keeps the list: in memory, and in `data\controllers.json` so a restart works even when the bootstrap controller is down.
- The list is refreshed every `RefreshMinutes` (default 15). If the asked controller is down, the others known so far are asked. If nobody answers, the last known list stays in use; an **empty** answer never replaces it.
- Entries of `Controllers` always stay in the list (and come first for failover), discovered ones are added. A controller that leaves the site is dropped at the next refresh.
- **Safety:** only names inside the allowed domain are accepted (`Discovery:AllowedSuffixes`, by default the domain of the bootstrap controller, for example `.corp.local`), and only plain host names. A manipulated answer cannot make SSS send requests elsewhere. Controllers the Broker reports as not active (failed, in maintenance) are ignored unless `IncludeInactive` is `true`.
- The Status tab (Admin) lists every controller with its source (`config` or `discovered`), state, version, zone, response time and the number of answers and failures, warns about different versions (during an upgrade) and shows when discovery fails.
- Several sites of one farm are separate sites with their own controllers and database. Discovery works per site; see the note on several sites below.

## Spreading the requests

```json
"Balancing": { "Strategy": "LeastLatency", "Preferred": [ "DC1" ] }
```

| Strategy | Behaviour |
|---|---|
| `Failover` (default) | The controller that answered last is asked first, the others follow in the configured order when it fails. Same behaviour as before. |
| `RoundRobin` | Each request starts with the next controller. |
| `LeastLatency` | Two healthy controllers are drawn at random and the one with the lower average response time is asked first. Load spreads out, slow controllers get less, and a new controller is tried at once. |

- `Preferred`: controller names **or zone names** that are used first while they are healthy, for example the controllers in the same data center as the web server. The others stay as fallback.
- **Health:** a controller that fails `FailureThreshold` times in a row (default 3) is skipped for `OpenSeconds` (default 30) and then tried again. If all are marked bad, they are tried anyway, so a request is never refused for that reason alone.
- The same order is used for the Broker (PowerShell workers) and the Monitor (OData). The status check and `/healthz` ask a controller for real (`Get-BrokerSite`), so a dead Broker is seen even when the worker is alive.
- Each request carries the order to the worker; which controller answered and which ones failed are reported back and feed the statistics.

## Option C: a virtual address

If your site already has a load balancer or DNS alias in front of the controllers, list only that name and leave `Strategy` at `Failover`. SSS then has a single address, and the balancer does the rest. Two points to watch: the Broker SDK and the Monitor must be reachable on the alias (ports 80/443 and the certificate must match the alias), and the Status tab then shows one controller only, so you do not see the individual ones. Combine it with discovery if you want both: the alias as the first entry, discovery for the real names.

## Several sites in one farm

A farm with several **sites** has separate Delivery Controllers and databases per site. SSS currently talks to **one site** (the one the listed controllers belong to). Showing the sessions of a user across several sites in one list is planned, not yet implemented.
