# Mehrere Delivery Controller und mehrere Webserver

[English](MULTI-SERVER.md)

Ein SSS-Server vor einem Delivery Controller ist die getestete Basis. Diese Seite zeigt, was sich bei größeren Umgebungen ändert.
Das Verhalten mit mehreren Controllern und Servern ist umgesetzt und durch Unit-Tests abgedeckt, wurde aber **noch nicht auf einer echten Umgebung mit mehreren Controllern oder Webservern geprüft**.

## Mehrere Delivery Controller

Alle Controller in der gewünschten Reihenfolge eintragen:

```json
"Citrix": { "Controllers": [ "ddc01.corp.local", "ddc02.corp.local" ] }
```

| Teil | Verhalten |
|---|---|
| Broker (Sitzungen, Abmelden, Nachrichten) | Die PowerShell-Worker probieren die Controller der Reihe nach. Der zuletzt erfolgreiche kommt zuerst, bei einem Ausfall übernimmt der nächste. |
| Monitor (Verlauf, Qualität, Expertenansicht) | Pro Controller wird eine Adresse abgeleitet (`https://<Controller>/Citrix/Monitor/OData/v4/Data/`) und genauso der Reihe nach probiert. Die Liste steuerst du mit `Citrix:Monitor:BaseUrls` (je Controller eine volle Adresse). `Citrix:Monitor:BaseUrl` bleibt als einzelne feste Adresse erhalten. |

Die Monitor-Datenbank gehört zur ganzen Site, jeder Controller liefert dieselben Daten.

### Monitor über HTTPS

SSS nutzt für den Monitor standardmäßig `https://` (`Citrix:Monitor:UseHttps`). Der Monitor-OData-Dienst ist **keine** IIS-Anwendung. Ihn stellt der Citrix-Monitor-Dienst selbst (`Citrix.Monitor.exe`) unter einer eigenen Adresse bereit, und diese ist standardmäßig `http://`. Antwortet `https://<Controller>/Citrix/Monitor/...` mit `404`, während `http://` `200` liefert, wurde der Dienst noch nicht auf HTTPS umgestellt. Zwei Wege:

1. HTTPS am Controller aktivieren: Zertifikat installieren und binden wie in der Citrix-Dokumentation [TLS on Delivery Controllers](https://docs.citrix.com/en-us/citrix-virtual-apps-desktops/2402-ltsr/secure/tls) beschrieben, in `Citrix.Monitor.exe.Config` den Wert `owin:baseAddress` auf `https://` ändern und den Citrix Monitor Service neu starten. Auf jedem Controller wiederholen.
2. HTTP im Verwaltungsnetz belassen: in `sss.config.json` `"Monitor": { "UseHttps": false }` setzen. Die Windows-Authentifizierung schützt die Anmeldedaten weiterhin, die Daten laufen aber unverschlüsselt.

`Test-SssSetup.ps1` und der Status-Reiter zeigen das verwendete Protokoll und nennen diese Ursache bei einem 404.

## Mehrere SSS-Webserver

Auf jedem Server dieselbe Installation (gleiche `sss.config.json`, gleiches Dienstkonto) hinter einem Load Balancer oder einem DNS-Alias.

| Thema | Vorgehen |
|---|---|
| Dasselbe Dienstkonto | Eine gMSA für alle Server (`-PrincipalsAllowedToRetrieveManagedPassword` nennt jeden Server). Der Citrix-Administratoreintrag wird nur einmal angelegt. |
| Kerberos hinter einem Alias | SPN für den Alias an der gMSA registrieren (`setspn -S HTTP/sss.corp.local CORP\svc-sss$`), `useAppPoolCredentials` auf true setzen und die Kernelmodus-Authentifizierung der Site ausschalten. Sonst fällt der Browser hinter dem Load Balancer auf NTLM zurück oder scheitert. |
| CSRF-Schlüssel | Jeder Server erzeugt eigene Schlüssel. Bei einem Load Balancer **ohne** feste Zuordnung (Sticky Sessions) lehnt Server B ein Token von Server A ab. Entweder Sticky Sessions einschalten oder den Schlüsselring teilen: `Sss:DataProtection:KeyDirectory` = Ordner auf einer Dateifreigabe, den das Dienstkonto ändern darf. Optional verschlüsselt `ProtectToSid` (SID der gMSA) die Schlüssel mit DPAPI-NG. |
| Health-Probe | `GET /<App>/healthz` antwortet `{"status":"ok"}`, `degraded` (Monitor nicht erreichbar, alles andere läuft) oder HTTP 503 (Broker nicht erreichbar). Es braucht keine Anmeldung: `.\install.ps1 -Update -HealthEndpoint` gibt genau diesen Pfad anonym frei. Ohne das bekommt die Probe `401`, was viele Load Balancer ebenfalls als „lebt“ werten. |
| Audit-Protokoll | Jeder Server schreibt sein eigenes Protokoll. Mit `Sss:Audit:PerServerFiles` = `true` und `Sss:Audit:Directory` auf einen gemeinsamen Ordner landet alles an einer Stelle (`audit-YYYYMMDD-SERVER.jsonl`, jede Zeile enthält zusätzlich `server`). Weiterleiten mit eurem üblichen Log-Shipper oder der Windows-Ereignisweiterleitung. |
| Zustand pro Server | Ratenbegrenzung, Namens-Cache und PowerShell-Worker gibt es je Server. Die Limits gelten also pro Server, und der Controller sieht `Workers` × Anzahl Server Verbindungen. Das ist unkritisch, aber bei `Citrix:Workers` zu bedenken. |
| Updates | Server nacheinander aktualisieren (`.\install.ps1 -Update`); die anderen bedienen weiter. |

## Weitere Abhängigkeiten

- Auf jedem SSS-Server: Citrix-PowerShell-Snap-ins (mit Studio oder dem PowerShell SDK), Windows PowerShell 5.1, IIS mit Windows-Authentifizierung und ASP.NET Core Module V2.
- Netzwerk: SSS-Server zu jedem Controller auf Port 80 oder 443 (Broker SDK und Monitor). Zeit und DNS müssen für Kerberos stimmen.
- Die Tiefe des Verlaufs bestimmen die Grooming-Einstellungen des Monitors der Site, nicht SSS.
- Hinter NetScaler/ADC muss die Windows-Anmeldung IIS erreichen (TLS-Passthrough oder Kerberos Constrained Delegation). SSS übernimmt keine Identität aus Headern.
- Das Helpdesk sieht alles, was die Citrix-Rolle des Dienstkontos sieht; einen Scope-Filter in SSS gibt es noch nicht.
