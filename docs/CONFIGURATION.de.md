# Konfigurationsreferenz

[English](CONFIGURATION.md)

SSS liest `appsettings.json` (Standardwerte, mit der Anwendung geliefert) und danach `sss.config.json` daneben (deine Einstellungen, bleibt bei `install.ps1 -Update` erhalten). Alles steht im Abschnitt `Sss`. Schreibe nur, was du änderst:

```json
{
  "Sss": {
    "Provider": "Citrix",
    "Citrix": { "Controllers": [ "ddc01.corp.local", "ddc02.corp.local" ] },
    "Helpdesk": { "Group": "CORP\\SSS-Helpdesk" }
  }
}
```

- Schlüssel unterscheiden nicht zwischen Groß- und Kleinschreibung. Ein Schlüssel, den SSS nicht kennt, wird ignoriert, aber gemeldet: Der Reiter **Status** zeigt ihn unter „Konfigurationsschlüssel“, `Test-SssSetup.ps1` warnt, und das Anwendungsprotokoll nennt ihn (meist falsche Verschachtelung, etwa `Sss:UseHttps` statt `Sss:Citrix:Monitor:UseHttps`).
- Umgebungsvariablen funktionieren ebenfalls, mit `__` als Trenner: `Sss__Citrix__Workers=4`. Sie überschreiben die Dateien.
- Eine Änderung wirkt nach dem Recycling des App-Pools (`Restart-WebAppPool SSS`).
- Werte werden beim Start geprüft. Ein falscher Wert stoppt die Anwendung mit einer klaren Meldung im Windows-Ereignisprotokoll (Quelle *IIS AspNetCore Module V2*), statt in einem unsicheren Zustand zu laufen.

## Allgemein

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Provider` | `Mock` | `Citrix` für eine echte Umgebung. `Mock` liefert Demodaten und zeigt einen Hinweis. |
| `RefreshSeconds` | `30` | Intervall der automatischen Aktualisierung der Oberfläche. |
| `StoreFrontUrl` | `""` | Optionale absolute `http(s)`-Adresse; die Oberfläche zeigt dann einen Link „StoreFront öffnen“ (zum Starten oder Wiederverbinden von Sitzungen). |
| `PathBase` | keiner | Anwendungspfad für lokale Läufe hinter einem Präfix. Hinter dem IIS setzt ihn das ASP.NET Core Module; leer lassen. |

## Authentifizierung (`Sss:Auth`)

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Mode` | `Iis` | `Iis` = die Windows-Authentifizierung des IIS erkennt den Benutzer (Produktion). `Dev` = fester Demo-Benutzer, nur erlaubt bei `ASPNETCORE_ENVIRONMENT=Development`. |
| `DevUserName`, `DevUserSid`, `DevIsHelpdesk` | Demowerte | Nur im Entwicklungsmodus. |

## Helpdesk (`Sss:Helpdesk`)

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Group` | `""` | AD-Gruppe (`DOMÄNE\Gruppe`), deren Mitglieder Benutzer suchen, deren Sitzungen sehen und abmelden, Nachrichten senden sowie Expertenansicht und Status sehen dürfen. Leer = Helpdesk aus. Wird beim Start in eine SID aufgelöst; gelingt das nicht, bleibt die Funktion aus und der Status-Reiter sagt es. |
| `ReasonRequired` | `false` | Für jedes Abmelden durch das Helpdesk einen Freitext-Grund verlangen (im Audit-Protokoll). |
| `MaxSessions` | `1000` | Obergrenze der farmweiten Sitzungsübersicht. |
| `MessageTitle` | `IT Helpdesk` | Titel der Nachrichten, die an eine Sitzung gesendet werden. |

## Citrix (`Sss:Citrix`)

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Controllers` | `[]` | Hostnamen der Delivery Controller, der Reihe nach probiert (Failover). Pflicht bei `Provider = Citrix`. Nur Buchstaben, Ziffern, `.`, `_`, `-`. |
| `Workers` | `2` | Vorgewärmte PowerShell-Worker (1-16). Jeder hält das geladene Citrix-Snap-in (etwa 60-100 MB). |
| `RequestTimeoutSeconds` | `20` | Zeitlimit einer Broker- oder Monitor-Anfrage. |
| `PowerShellPath` | `powershell.exe` | Windows-PowerShell-5.1-Host mit den Citrix-Snap-ins. |
| `WorkerScript` | neben der App | Pfad zu `sss-broker-worker.ps1`. Nur für Tests ändern. |
| `SnapinOverride` | keiner | Testhaken (Attrappen-Cmdlets). Außerhalb der Entwicklungsumgebung abgelehnt. |

### Monitor (`Sss:Citrix:Monitor`)

Verlauf, Qualitätsdaten und Expertenansicht kommen vom Monitor-OData-Dienst (der Datenquelle von Director).

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Enabled` | `true` | `false` = nur Live-Sitzungen und Abmelden, kein Verlauf, keine Qualität, keine Expertenansicht. |
| `UseHttps` | `true` | Schema der abgeleiteten Adressen. `false`, wenn der Monitor-Dienst nur über `http://` antwortet (siehe [Monitor über HTTPS](MULTI-SERVER.de.md#monitor-über-https)). |
| `BaseUrl` | abgeleitet | Eine feste Adresse, z. B. `https://ddc01.corp.local/Citrix/Monitor/OData/v4/Data/`. Hat Vorrang vor der abgeleiteten. |
| `BaseUrls` | abgeleitet | Eine Adresse pro Controller, der Reihe nach probiert. Standard: `{Schema}://{Controller}/Citrix/Monitor/OData/v4/Data/` für jeden Eintrag von `Controllers`. |
| `MetricsDateField` | `CollectedDate` | Eigenschaft von `SessionMetrics`, die die neueste Messung bestimmt. |
| `IcaRttField` | `IcaRttMS` | Eigenschaft mit der ICA-Laufzeit in ms. |
| `BandwidthField` | `OutputBandwidthAvailable` | Eigenschaft mit der Bandbreite zum Client (bit/s bei CVAD 2507). |
| `BandwidthDivisor` | `1000` | Rechnet das Bandbreitenfeld in kbit/s um. |
| `MaxPages` | `5` | Obergrenze der OData-Seiten, denen pro Abfrage gefolgt wird. |

Die Feldnamen sind auf CVAD 2203, 2402, 2507 und 2607 mit `Test-SssCitrix.ps1` geprüft. Feldnamen müssen einfache Bezeichner sein (sie landen in einer Abfrage).

### Prozesse (`Sss:Citrix:Processes`)

Anwendungen und Prozesse einer Sitzung anzeigen und beenden. Standardmäßig aus. Braucht den JEA-Endpunkt auf den VDAs; siehe [Anwendungen und Prozesse beenden](PROCESSES.de.md).

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Enabled` | `false` | Schaltet die Funktion ein (der Knopf „Prozesse“ erscheint in der Expertenansicht). |
| `Configuration` | `SSS.Processes` | Name der JEA-Sitzungskonfiguration auf den VDAs (`Install-SssJea.ps1 -ConfigurationName`). |
| `Audience` | `All` | `All` = Benutzer für die eigenen Sitzungen und das Helpdesk; `Helpdesk` = nur Helpdesk. |
| `TimeoutSeconds` | `60` | Zeitlimit einer Prozessanfrage (die Verbindung zum VDA ist langsamer als ein Broker-Aufruf). |

## Qualitätsschwellen (`Sss:Quality`)

Ampeln in der Sitzungsliste. Werte ab *Warn* werden gelb, ab *Crit* rot (Bandbreite: ab und darunter).

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `RttWarnMs` / `RttCritMs` | `80` / `150` | ICA-Laufzeit, „Ihr Gerät & Netzwerk“. |
| `BandwidthWarnKbps` / `BandwidthCritKbps` | `1500` / `400` | Verfügbare Bandbreite (niedriger ist schlechter). |
| `LogonWarnSeconds` / `LogonCritSeconds` | `45` / `90` | Anmeldedauer, „Rechenzentrum & VDA“. |
| `LoadWarn` / `LoadCrit` | `7000` / `9000` | Lastindex der Maschine (0-10000). |

## Ratenbegrenzung (`Sss:RateLimit`)

Pro Benutzer (pro IP, wenn kein Benutzer bekannt ist), pro Server.

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `LogoffRequestsPerMinute` | `10` | Abmelden und Prozess-beenden-Anfragen. |
| `ReadRequestsPerMinute` | `120` | Sitzungen auflisten, Details, Prozesslisten, CSV-Export. |
| `HelpdeskRequestsPerMinute` | `60` | Alle Helpdesk-Aufrufe pro Helpdesk-Benutzer. |

## Audit (`Sss:Audit`)

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Directory` | `logs` | Ordner des Audit-Protokolls; relative Pfade beziehen sich auf den Anwendungsordner. Die App-Pool-Identität braucht dort *Ändern*. |
| `PerServerFiles` | `false` | `true` = `audit-YYYYMMDD-SERVER.jsonl`, damit mehrere Server in einen gemeinsamen Ordner schreiben können. |

Format und Felder: [Betrieb](OPERATIONS.de.md#audit-protokoll).

## Health-Probe (`Sss:Health`)

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Enabled` | `true` | Stellt `GET /healthz` für Load Balancer bereit. Der IIS muss für genau diesen Pfad anonymen Zugriff erlauben (`install.ps1 -HealthEndpoint`). Details: [Betrieb](OPERATIONS.de.md#health-probe-für-load-balancer). |

## Mehrere Server (`Sss:DataProtection`)

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `KeyDirectory` | `""` | Gemeinsamer Ordner für den Schlüsselring, damit CSRF-Tokens eines Servers von den anderen akzeptiert werden. Leer = Schlüssel bleiben auf dem Server. |
| `ProtectToSid` | `""` | Optionale SID (z. B. der gMSA), für die die Schlüssel mit DPAPI-NG verschlüsselt werden. Nur Windows. |

Mehr zu Setups mit mehreren Servern: [Mehrere Controller und Server](MULTI-SERVER.de.md).

## Protokollierung

Standard-Protokollierung von ASP.NET Core, konfiguriert in `appsettings.json` unter `Logging`. Die Standardstufe ist `Warning`; unbekannte Konfigurationsschlüssel, HTTP-Zugriff auf den Monitor und Worker-Probleme werden dort protokolliert. Im IIS landet die Ausgabe nur bei Startfehlern im Windows-Ereignisprotokoll (ASP.NET Core Module); für die volle Konsolenausgabe `stdoutLogEnabled` in `web.config` vorübergehend aktivieren.
