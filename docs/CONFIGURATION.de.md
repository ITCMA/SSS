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
- `sss.local.json` (optional, daneben) wird nach `sss.config.json` gelesen und überschreibt sie. Halte `sss.config.json` auf allen Servern eines [Mehrserver-Setups](MULTI-SERVER.de.md#serverliste-und-konfiguration) identisch und lege Einstellungen, die nur zu einem Server gehören (Pfade, ein lokaler Bootstrap-Controller), hier ab. `install.ps1 -Update` behält beide Dateien.
- Eine Änderung wirkt nach dem Recycling des App-Pools (`Restart-WebAppPool SSS`).
- Werte werden beim Start geprüft. Ein falscher Wert stoppt die Anwendung mit einer klaren Meldung im Windows-Ereignisprotokoll (Quelle *IIS AspNetCore Module V2*), statt in einem unsicheren Zustand zu laufen.

## Konfiguration bearbeiten und prüfen

SSS hat absichtlich keine Konfigurations-GUI: Eine Webseite, die ihre eigene Konfiguration schreibt, gäbe jedem, der die Anwendung erreicht, die Möglichkeit, Rollen und Audit-Einstellungen zu ändern, und sie würde Abweichungen zwischen Servern erzeugen. Stattdessen helfen diese Werkzeuge:

| Werkzeug | Was es tut |
|---|---|
| **Beispiele** in `docs\examples` | vollständige Ausgangspunkte: `sss.config.single-server.json`, `sss.config.enterprise.json` (Rollen, Erkennung, Lastverteilung, Audit-Ziele, Metriken), `sss.config.multi-server.json` (Serverliste, gemeinsamer Schlüsselring) und `sss.local.example.json` (Einstellungen nur eines Servers). Eine kopieren, die Namen ersetzen. |
| **Schema** `docs\sss.config.schema.json` | beschreibt jede Einstellung mit Typ, Standardwert, erlaubten Werten und Beschreibung. In Visual Studio Code als ersten Schlüssel `"$schema": "<Pfad>/sss.config.schema.json"` eintragen (die Beispiele tun es), dann gibt es Vervollständigung, die Beschreibung jeder Einstellung und eine Warnung bei unbekanntem oder falsch getyptem Schlüssel. Die Datei wird aus dem Programm erzeugt und von den Tests geprüft, passt also immer zur Version. |
| **`Test-SssConfig.ps1`** | prüft eine Konfiguration, ohne SSS zu starten: die Fehler, die es stoppen würden, und Warnungen (zum Beispiel keine Auditor-Gruppe, Monitor nur per HTTP, mehrere Server ohne gemeinsamen Schlüsselring, unbekannte Schlüssel). `.\Test-SssConfig.ps1` prüft das Installierte, `.\Test-SssConfig.ps1 -Path .\neu.json` eine Datei, bevor du sie auf einen Server kopierst. Exitcode 0 = keine Fehler. `install.ps1` führt dieselbe Prüfung nach jeder Installation und jedem Update aus. |
| **Status-Reiter, „Wirksame Konfiguration“** (Admin) | jede Einstellung, mit der SSS läuft, und woher der Wert kommt: `sss.config.json`, `sss.local.json`, eine Umgebungsvariable oder der Standard. Nur lesend, Geheimnisse maskiert. Der schnellste Weg zu sehen, warum sich ein Server anders verhält als ein anderer. |

`install.ps1 -Interactive` fragt außerdem nach Auditor- und Admin-Gruppe, einem zentralen Audit-Ordner und der Controller-Erkennung und schreibt sie in die erste `sss.config.json` (dasselbe wie `-AuditorGroup`, `-AdminGroup`, `-AuditShare`, `-Discovery`).

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
| `DevUserName`, `DevUserSid`, `DevIsHelpdesk`, `DevRoles` | Demowerte | Nur im Entwicklungsmodus. `DevRoles` ist eine Liste wie `"L2,Auditor"` (`L1`, `L2`, `L3`, `Auditor`, `Admin`). |

## Helpdesk (`Sss:Helpdesk`)

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Group` | `""` | AD-Gruppe (`DOMÄNE\Gruppe`), deren Mitglieder alle Helpdesk-Rechte haben (Stufe 3: Benutzer suchen, Sitzungen sehen und abmelden, Nachrichten senden, Prozesse, Profil, Expertenansicht, Status). Bleibt für bestehende Installationen; feinere Stufen stehen in `Sss:Roles`. Wird beim Start in eine SID aufgelöst; gelingt das nicht, gibt die Gruppe nichts frei. |
| `ReasonRequired` | `false` | Mit `true` für jedes Abmelden, jede Nachricht, Prozessliste, jedes Profil und Prozess-Beenden durch das Helpdesk eine Ticketnummer oder einen Grund verlangen (beliebiger Text, wird nicht geprüft); er steht im Audit-Protokoll. Siehe [Rollen](ROLES.de.md#ticketnummer-oder-grund-pflicht). |
| `BulkAlertThreshold` | `5` | Eine Helpdesk-Anfrage, die so viele Sitzungen oder mehr abmeldet, schreibt zusätzlich das Ereignis `alert:bulk-logoff`. 0 = aus. |
| `MaxSessions` | `1000` | Obergrenze der farmweiten Sitzungsübersicht. |
| `MessageTitle` | `IT Helpdesk` | Titel der Nachrichten, die an eine Sitzung gesendet werden. |

## Rollen (`Sss:Roles`)

AD-Gruppen (`DOMÄNE\Gruppe`) für `Level1Group`, `Level2Group`, `Level3Group`, `AuditorGroup` und `AdminGroup`; standardmäßig alle leer. Was jede Rolle darf: [Rollen](ROLES.de.md).

## Citrix (`Sss:Citrix`)

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Controllers` | `[]` | Hostnamen der Delivery Controller, der Reihe nach probiert (Failover). Pflicht bei `Provider = Citrix`. Nur Buchstaben, Ziffern, `.`, `_`, `-`. |
| `Discovery` | aus | Findet alle Controller der Site von einem aus: `Enabled`, `Bootstrap`, `RefreshMinutes` (15), `AllowedSuffixes`, `IncludeInactive`. Siehe [Controller](CONTROLLERS.de.md#erkennung). |
| `Balancing` | `Failover` | `Strategy` (`Failover`, `RoundRobin`, `LeastLatency`), `Preferred` (Controller oder Zonen), `FailureThreshold` (3), `OpenSeconds` (30). Siehe [Controller](CONTROLLERS.de.md#anfragen-verteilen). |
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

Anwendungen und Prozesse einer Sitzung anzeigen und beenden sowie Profiltyp und Größe lesen. Standardmäßig aus. Braucht den JEA-Endpunkt auf den VDAs; siehe [Anwendungen und Prozesse beenden](PROCESSES.de.md).

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Enabled` | `false` | Schaltet die Funktion ein (der Knopf „Prozesse“ erscheint in der Expertenansicht). |
| `Configuration` | `SSS.Processes` | Name der JEA-Sitzungskonfiguration auf den VDAs (`Install-SssJea.ps1 -ConfigurationName`). |
| `Audience` | `All` | `All` = Benutzer für die eigenen Sitzungen und das Helpdesk; `Helpdesk` = nur Helpdesk. |
| `TimeoutSeconds` | `60` | Zeitlimit einer Prozessanfrage (die Verbindung zum VDA ist langsamer als ein Broker-Aufruf). |
| `ProfileInfo` | `true` | Zeigt Profiltyp und Größe in der Expertenansicht (Knopf „Profiltyp und Größe ermitteln“). Nutzt denselben JEA-Endpunkt, braucht also `Enabled = true`. |
| `ProfileScanSeconds` | `20` | Zeitlimit des Profilordner-Scans auf dem VDA; `0` = nur Typ, keine Größe. |

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
| `OpsStream` | `true` | Warnungen und Fehler von SSS selbst kommen in einen zweiten Strom `ops-YYYYMMDD.jsonl` und über dieselben Ziele. |
| `RetentionDays` | `0` | Lokale Dateien löschen, die älter sind, sobald jedes Ziel sie passiert hat. 0 = behalten. |
| `Sinks` | keine | Zentrale Ziele: `Unc`, `Syslog`, `Http`, `Snmp` (jeweils eine Liste). Jedes Ziel hat `Enabled`, `Name`, `Streams`, `Include`, `OnlyProblems`, `BatchSize` und eigene Schlüssel. Alles steht in [Audit-Protokoll und zentrales Logging](AUDIT.de.md#ziele). |

Format und Felder: [Audit-Protokoll](AUDIT.de.md#der-eintrag).

## Datenordner (`Sss:DataDirectory`)

Standard `data` neben der Anwendung. Enthält, was SSS zwischen Neustarts behält: die Weiterleitungs-Positionen der Audit-Ziele, die zwischengespeicherte Controller-Liste und die Ausleit-Markierung. Die App-Pool-Identität braucht *Ändern* (`install.ps1` setzt es). Bleibt bei Updates erhalten.

## SSS-Server (`Sss:Instances`)

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Directory` | `""` | Gemeinsamer Ordner, den jeder SSS-Server beschreiben kann; jeder Server meldet sich dort und liest die anderen. Leer = einzelner Server. |
| `Expected` | `[]` | Computernamen aller SSS-Server. Ein genannter Server, der sich nicht meldet, ist `missing`, ein meldender, der nicht genannt ist, `unexpected`. |
| `HeartbeatSeconds` | `60` | Wie oft sich ein Server meldet. |
| `StaleSeconds` | `180` | Ein Server, der so lange nicht gemeldet hat, gilt als nicht laufend. |

Siehe [Serverliste und Konfiguration](MULTI-SERVER.de.md#serverliste-und-konfiguration).

## Umgebung (`Sss:Environment`)

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Locations` | `[]` | Ihre Standorte: eine Liste aus `{ "Name": "...", "Networks": [ "10.20.0.0/16", ... ] }`. Der Reiter Umgebung zählt Sitzungen je Standort; das genaueste Netz gewinnt. |
| `MinClientVersion` | `{}` | Älteste akzeptierte Workspace-App-Version je Plattform (`"*"` = jede andere Plattform). Ältere Clients werden im Reiter Umgebung markiert, ihre Benutzer bekommen einen Hinweis. Leer = keine Prüfung. |
| `HintOutdatedClient` | `true` | Dem Benutzer den Hinweis zeigen, wenn die eigene Workspace-App unter dem Minimum liegt. |

Details und Datenschutzhinweise: [Umgebung](ENVIRONMENT.de.md). Die Feldnamen von Client-Version und Plattform im Monitor sind `Sss:Citrix:Monitor:ClientVersionField` und `ClientPlatformField`.

## Metriken (`Sss:Metrics`)

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Enabled` | `false` | Stellt `GET /metrics` bereit (Prometheus-Textformat, keine personenbezogenen Daten). Der IIS muss für den Pfad anonymen Zugriff erlauben (`install.ps1 -MetricsEndpoint`). |
| `AllowedIps` | `[]` | Adressen oder Netze (`10.0.5.0/24`), die es lesen dürfen. Der Server selbst darf immer. |

## Health-Probe (`Sss:Health`)

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Enabled` | `true` | Stellt `GET /healthz` für Load Balancer bereit. Der IIS muss für genau diesen Pfad anonymen Zugriff erlauben (`install.ps1 -HealthEndpoint`). Details: [Betrieb](OPERATIONS.de.md#health-probe-für-load-balancer). |

## Mehrere Server (`Sss:DataProtection`)

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `KeyDirectory` | `""` | Gemeinsamer Ordner für den Schlüsselring, damit CSRF-Tokens eines Servers von den anderen akzeptiert werden. Leer = Schlüssel bleiben auf dem Server. |
| `ProtectToSid` | `""` | Optionale SID (z. B. der gMSA), für die die Schlüssel mit DPAPI-NG verschlüsselt werden. Nur Windows. Ohne `KeyDirectory` liegen die Schlüssel in `data\keys` und werden automatisch mit DPAPI (Computerbereich) verschlüsselt. |

Mehr zu Setups mit mehreren Servern: [Mehrere Controller und Server](MULTI-SERVER.de.md).

## Protokollierung

Standard-Protokollierung von ASP.NET Core, konfiguriert in `appsettings.json` unter `Logging`. Die Standardstufe ist `Warning`; unbekannte Konfigurationsschlüssel, HTTP-Zugriff auf den Monitor und Worker-Probleme werden dort protokolliert. Im IIS landet die Ausgabe nur bei Startfehlern im Windows-Ereignisprotokoll (ASP.NET Core Module); für die volle Konsolenausgabe `stdoutLogEnabled` in `web.config` vorübergehend aktivieren.
