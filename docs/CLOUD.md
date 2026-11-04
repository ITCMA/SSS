# Citrix Cloud (DaaS)

[Deutsch](CLOUD.de.md)

SSS can read a **Citrix Cloud** site instead of an on-premises Delivery Controller. This first step is **read-only** and **not yet tested against a real tenant** (it follows the documented Citrix Cloud API; please report what you see).

| Works | Does not work (yet) |
|---|---|
| Own sessions and history, quality lights, expert view | Log off, messages to a session |
| Helpdesk list, Insights, Map, Environment (all read from the cloud Monitor service) | Processes, profile analysis, policies (they need the on-premises Broker and the JEA endpoint on the VDAs) |
| Status page (Monitor check) | Controller discovery (not needed in the cloud) |

The log-off and message buttons are hidden and a banner says "read-only". The server answers such requests with HTTP 501.

## Setup

1. In Citrix Cloud open **Identity and Access Management > API Access** and create a **secure client**. Note the **customer id** (shown above the list), the **client id** and the **secret** (shown once). Give the client only the rights needed to read the Monitor data; no administrator rights are needed for the read-only mode.
2. Put the settings into `sss.config.json`, **without the secret**:

```json
{ "Sss": { "Provider": "Citrix", "Citrix": { "Cloud": { "Enabled": true, "Region": "eu", "CustomerId": "<customer id>", "ClientId": "<client id>" } } } }
```

3. Give the secret through the environment of the application pool, not through a file in the repository: `Sss__Citrix__Cloud__ClientSecret`. The Status tab and the effective configuration mask it.
4. `Region` is `eu` (api-eu.cloud.com), `us` (api.cloud.com), `ap-s` (api-ap-s.cloud.com) or `jp` (api.citrixcloud.jp). Without Windows authentication toward Citrix, the IIS server needs only outbound HTTPS to that host.

With `Enabled` the keys `Controllers`, `Discovery` and `Monitor:BaseUrl` are ignored and the process features are switched off. No PowerShell worker is started.

## How it works and what it protects

SSS signs in to `cctrustoauth2/<customer id>/tokens/clients` with the client credentials, keeps the bearer token in memory until shortly before it expires and sends it, with the `Citrix-CustomerId` header, to `https://<api host>/monitorodata/` only. A request to any other host is refused before the token is added. A refused sign-in shows "service unavailable" in the browser; the reason (without the secret) is in the log.

## Known limits

- `scripts/Test-SssCitrix.ps1` checks an on-premises site. For the cloud use `scripts/Test-SssCloud.ps1 -CustomerId ... -ClientId ... -Region eu` (asks for the secret without showing it; reads and prints counts only), then the Status tab.
- The live list reads the Monitor service, which can lag the Broker by a short time; the states are Active and Disconnected.
- Actions through the DaaS REST API (log off, message) are planned as a second step.
