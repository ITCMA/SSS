# Several Delivery Controllers and several web servers

[Deutsch](MULTI-SERVER.de.md)

One SSS server in front of one Delivery Controller is the tested baseline. This page lists what changes when your site is bigger.
The multi-controller and multi-server behaviour below is implemented and covered by unit tests, but has **not yet been verified on a real site with several controllers or web servers**.

## Several Delivery Controllers

List all controllers, in the order you prefer:

```json
"Citrix": { "Controllers": [ "ddc01.corp.local", "ddc02.corp.local" ] }
```

| Part | Behaviour |
|---|---|
| Broker (sessions, log-off, messages) | The PowerShell workers try the controllers in order. The controller that answered last is tried first, the next one takes over when it fails. |
| Monitor (history, quality, expert view) | One address per controller is derived (`https://<controller>/Citrix/Monitor/OData/v4/Data/`) and tried the same way. To control the list yourself set `Citrix:Monitor:BaseUrls` (one full address per controller). `Citrix:Monitor:BaseUrl` stays as a single explicit address. |

The Monitor database is shared by all controllers of a site, so any controller returns the same data.

### Monitor over HTTPS

SSS uses `https://` for the Monitor by default (`Citrix:Monitor:UseHttps`). The Monitor OData service is **not** an IIS application. It is hosted by the Citrix Monitor service itself (`Citrix.Monitor.exe`) on its own address, and by default that address is `http://`. If `https://<controller>/Citrix/Monitor/...` answers `404` while `http://` answers `200`, the service has not been switched to HTTPS yet. You have two options:

1. Enable HTTPS on the controller: install a certificate and bind it as described in Citrix's [TLS on Delivery Controllers](https://docs.citrix.com/en-us/citrix-virtual-apps-desktops/2402-ltsr/secure/tls) documentation, and change `owin:baseAddress` in `Citrix.Monitor.exe.Config` to `https://`, then restart the Citrix Monitor Service. Repeat on every controller.
2. Keep HTTP inside the management network: set `"Monitor": { "UseHttps": false }` in `sss.config.json`. Windows authentication still protects the credentials, but the data is not encrypted on the wire.

`Test-SssSetup.ps1` and the Status tab show which transport is in use and name this cause when it sees the 404.

## Several SSS web servers

Run the same installation on each server (same `sss.config.json`, same service account) behind a load balancer or a DNS alias.

| Topic | What to do |
|---|---|
| Same service account | Use one gMSA for all servers (`-PrincipalsAllowedToRetrieveManagedPassword` lists every server). It needs the Citrix administrator entry once. |
| Kerberos behind an alias | Register the SPN for the alias on the gMSA (`setspn -S HTTP/sss.corp.local CORP\svc-sss$`), set `useAppPoolCredentials` to true and turn kernel-mode authentication off for the site. Without it the browser falls back to NTLM or fails behind the load balancer. |
| CSRF keys | Each server creates its own keys. With a load balancer **without** sticky sessions a token issued by server A is rejected by server B. Either enable sticky sessions, or share the key ring: `Sss:DataProtection:KeyDirectory` = a folder on a file share the service account may modify. Optionally `ProtectToSid` = the SID of the gMSA encrypts the keys with DPAPI-NG. |
| Health probe | `GET /<app>/healthz` answers `{"status":"ok"}`, `degraded` (Monitor down, everything else works) or HTTP 503 (Broker unreachable). It needs no credentials: run `.\install.ps1 -Update -HealthEndpoint` to open exactly that path for anonymous access. Without it the probe gets `401`, which many load balancers also accept as "alive". |
| Audit trail | Every server writes its own trail. Set `Sss:Audit:PerServerFiles` to `true` and `Sss:Audit:Directory` to a shared folder to collect everything in one place (`audit-YYYYMMDD-SERVER.jsonl`, each line also carries a `server` field). Forward the files with your usual log shipper or Windows Event Forwarding tooling. |
| Per-server state | Rate limits, the user-name cache and the PowerShell workers exist once per server. The limits therefore apply per server and the controller sees `Workers` x servers connections. This is harmless, keep it in mind when you size `Citrix:Workers`. |
| Updates | Update one server at a time (`.\install.ps1 -Update`); the others keep serving. |

## Other dependencies

- On every SSS server: the Citrix PowerShell snap-ins (installed with Studio or the PowerShell SDK), Windows PowerShell 5.1, IIS with Windows authentication and the ASP.NET Core Module V2.
- Network: SSS server to each controller on port 80 or 443 (Broker SDK and Monitor). Time and DNS must be correct for Kerberos.
- History depth is decided by the Monitor grooming settings of the site, not by SSS.
- Behind NetScaler/ADC the Windows sign-in has to reach IIS (TLS pass-through, or Kerberos constrained delegation). SSS does not accept identity from headers.
- The helpdesk sees everything the Citrix role of the service account sees; there is no scope filter inside SSS yet.
