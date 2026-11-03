# Mehrere Delivery Controller und mehrere Webserver

[English](MULTI-SERVER.md)

Ein SSS-Server vor einem Delivery Controller ist die getestete Basis. Diese Seite zeigt, was sich bei größeren Umgebungen ändert.
Das Verhalten mit mehreren Controllern und Servern ist umgesetzt und durch Unit-Tests abgedeckt, wurde aber **noch nicht auf einer echten Umgebung mit mehreren Controllern oder Webservern geprüft**.

## Mehrere Delivery Controller

SSS kann die Controller der Site auch selbst finden und die Anfragen auf sie verteilen: siehe [Delivery Controller: Erkennung und Lastverteilung](CONTROLLERS.de.md). Die einfache Form ist eine Liste, in der gewünschten Reihenfolge:

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
2. HTTP im Verwaltungsnetz belassen: in `sss.config.json` `{ "Sss": { "Citrix": { "Monitor": { "UseHttps": false } } } }` setzen (auf die Verschachtelung achten; der Status-Reiter listet Schlüssel, die SSS nicht kennt) und den App-Pool neu starten. Die Windows-Authentifizierung schützt die Anmeldedaten weiterhin, die Daten laufen aber unverschlüsselt.

`Test-SssSetup.ps1` und der Status-Reiter zeigen das verwendete Protokoll und nennen diese Ursache bei einem 404.

## Mehrere SSS-Webserver

Auf jedem Server dieselbe Installation (gleiche `sss.config.json`, gleiches Dienstkonto) hinter einem Load Balancer oder einem DNS-Alias.

| Thema | Vorgehen |
|---|---|
| Dasselbe Dienstkonto | Eine gMSA für alle Server (`-PrincipalsAllowedToRetrieveManagedPassword` nennt jeden Server). Der Citrix-Administratoreintrag wird nur einmal angelegt. |
| Kerberos hinter einem Alias | SPN für den Alias an der gMSA registrieren (`setspn -S HTTP/sss.corp.local CORP\svc-sss$`), `useAppPoolCredentials` auf true setzen und die Kernelmodus-Authentifizierung der Site ausschalten. Sonst fällt der Browser hinter dem Load Balancer auf NTLM zurück oder scheitert. |
| CSRF-Schlüssel | Jeder Server erzeugt eigene Schlüssel. Bei einem Load Balancer **ohne** feste Zuordnung (Sticky Sessions) lehnt Server B ein Token von Server A ab. Entweder Sticky Sessions einschalten oder den Schlüsselring teilen: `Sss:DataProtection:KeyDirectory` = Ordner auf einer Dateifreigabe, den das Dienstkonto ändern darf. Optional verschlüsselt `ProtectToSid` (SID der gMSA) die Schlüssel mit DPAPI-NG. |
| Health-Probe | `GET /<App>/healthz` antwortet `{"status":"ok"}`, `degraded` (Monitor nicht erreichbar, alles andere läuft) oder HTTP 503 (Broker nicht erreichbar). Es braucht keine Anmeldung: `.\install.ps1 -Update -HealthEndpoint` gibt genau diesen Pfad anonym frei. Ohne das bekommt die Probe `401`, was viele Load Balancer ebenfalls als „lebt“ werten. |
| Audit-Protokoll | Jeder Server schreibt sein eigenes Protokoll und kopiert es an zentrale Ziele (UNC-Ordner, Syslog/CEF, HTTP, SNMP). Der Audit-Reiter zeigt dann die Ereignisse aller Server. Siehe [Audit-Protokoll und zentrales Logging](AUDIT.de.md). |
| Zustand pro Server | Ratenbegrenzung, Namens-Cache und PowerShell-Worker gibt es je Server. Die Limits gelten also pro Server, und der Controller sieht `Workers` × Anzahl Server Verbindungen. Das ist unkritisch, aber bei `Citrix:Workers` zu bedenken. |
| Updates | Server nacheinander aktualisieren (`.\install.ps1 -Update`); die anderen bedienen weiter. Den Server vorher aus dem Load Balancer nehmen, siehe *Ausleiten* unten. |
| Konfiguration | `sss.config.json` auf allen Servern **identisch** halten und Einstellungen, die nur zu einem Server gehören (Pfade, ein lokaler Bootstrap-Controller), in `sss.local.json` daneben legen. `install.ps1 -Update` behält beide. Siehe *Serverliste und Konfiguration* unten. |

## Serverliste und Konfiguration

Server, die einander nicht kennen, laufen unbemerkt auseinander: einer hat eine alte Version, einer einen anderen Schwellenwert, einer ist ausgefallen. SSS kann deshalb eine **Liste aller seiner Server** führen.

```json
"Instances": {
  "Directory": "\\\\fs01\\sss-state",
  "Expected": [ "IIS01", "IIS02", "IIS03" ]
}
```

- Jeder Server schreibt jede Minute eine kleine Statusdatei in den gemeinsamen Ordner (Name, Version, Startzeit, ein Fingerabdruck von `sss.config.json` und jedes ihrer Abschnitte, die Schlüsselnamen aus `sss.local.json`, Gesundheit, Ausleiten) und liest die der anderen. Die App-Pool-Identität braucht auf dem Ordner *Ändern*.
- **Status-Reiter (Admin), „SSS-Server“:** jeder Server mit seinem Zustand. `online`; `draining` (wird ausgeleitet); `stale` (seit `StaleSeconds` keine Meldung, Standard 180, vermutlich ausgefallen); `missing` (in `Expected` genannt, nie gemeldet: ein vergessener oder noch nicht installierter Server); `unexpected` (meldet sich, steht aber nicht in `Expected`: ein Server, den niemand eingeplant hat). Mit `Expected` ist die Liste per Definition vollständig, ohne sind die sich meldenden Server die Liste.
- **Abweichung:** Die Konfiguration, die die meisten Online-Server haben, ist die Referenz; ein Server mit einer anderen wird markiert, samt den **Abschnitten, die sich unterscheiden** (`Citrix`, `Quality` ...). Leerraum und Schlüsselreihenfolge spielen keine Rolle, die Reihenfolge von Listeneinträgen schon (sie ist die Failover-Reihenfolge). Dasselbe gilt für eine andere Version.
- Aus `sss.local.json` werden nur Schlüssel*namen* gezeigt, nie Werte.

`.\Sync-SssConfig.ps1 -Servers iis01,iis02,iis03` vergleicht die Dateien auf den Servern (ändert nichts). Mit `-Apply` (und `-ReferenceFile` für eine Datei aus deinem Konfigurations-Repository) rollt es die Referenz Server für Server aus: Sicherung, Ausleiten, Kopieren, App-Pool recyceln. Es braucht die administrative Freigabe (`\\Server\C$`) und WinRM für das Recyceln. Eine Alternative ist jede Konfigurationsverwaltung, die ihr schon habt (DSC, Ansible, eine Datei-Einstellung per GPO): der Fingerabdruck zeigt, ob es geklappt hat.

### Ausleiten

Solange die Datei `data\drain.flag` existiert, antwortet `GET /healthz` mit `503 {"status":"draining"}`, der Load Balancer nimmt den Server also heraus, bevor du aktualisierst. Datei anlegen, ein paar Probe-Intervalle warten, aktualisieren (`install.ps1 -Update` oder `Sync-SssConfig.ps1`); der Neustart der Anwendung beendet das Ausleiten. `Sync-SssConfig.ps1 -DrainSeconds` macht das selbst.

### Metriken

`Sss:Metrics:Enabled` = `true` stellt `GET /metrics` im Prometheus-Textformat bereit (Controller up/Latenz/Fehler, Zustand der Audit-Ziele, Server online/fehlend/abweichend, Audit-Ereignisse je Art). Es enthält keine personenbezogenen Daten. Der IIS muss für den Pfad anonymen Zugriff erlauben (`install.ps1 -Update -MetricsEndpoint`), und SSS antwortet nur den Adressen in `Sss:Metrics:AllowedIps` (und dem Server selbst).

## Weitere Abhängigkeiten

- Auf jedem SSS-Server: Citrix-PowerShell-Snap-ins (mit Studio oder dem PowerShell SDK), Windows PowerShell 5.1, IIS mit Windows-Authentifizierung und ASP.NET Core Module V2.
- Netzwerk: SSS-Server zu jedem Controller auf Port 80 oder 443 (Broker SDK und Monitor). Zeit und DNS müssen für Kerberos stimmen.
- Die Tiefe des Verlaufs bestimmen die Grooming-Einstellungen des Monitors der Site, nicht SSS.
- Hinter NetScaler/ADC muss die Windows-Anmeldung IIS erreichen (TLS-Passthrough oder Kerberos Constrained Delegation). SSS übernimmt keine Identität aus Headern.
- Das Helpdesk sieht alles, was die Citrix-Rolle des Dienstkontos sieht; einen Scope-Filter in SSS gibt es noch nicht.
