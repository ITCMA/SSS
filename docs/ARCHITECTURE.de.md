# Architektur

[English version](ARCHITECTURE.md)

SSS ist eine einzige ASP.NET-Core-Anwendung (.NET 10), die In-Process im IIS läuft. Sie liefert eine statische Single-Page-Oberfläche und eine kleine JSON-API.

```
Browser ──HTTPS──> IIS (Windows-Authentifizierung, anonym aus)
                     └─ ASP.NET Core Module (In-Process)
                          └─ Sss.Web
                               ├─ wwwroot/            statische Oberfläche (HTML, CSS, JS, 5 Sprachen)
                               ├─ Program.cs          Sicherheits-Pipeline + API-Endpunkte
                               ├─ Services/SessionService   Geschäftsregeln, Audit
                               └─ ISessionProvider
                                    ├─ MockSessionProvider      Demo-Daten (Entwicklung, Tests)
                                    └─ CitrixSessionProvider
                                         ├─ BrokerWorkerPool ── vorgewärmte Windows-PowerShell-5.1-Worker ──> Broker-SDK ──> Delivery Controller
                                         └─ ODataMonitorClient ───────────────────────────────────────────> Monitor-OData ──> Delivery Controller
```

## Ablauf: Sitzungen anzeigen

1. Der IIS authentifiziert den Benutzer (Kerberos/NTLM). ASP.NET Core stellt die `WindowsIdentity` bereit, `ResolveUser` entnimmt ihr die **SID**. Nichts anderes identifiziert den Aufrufer.
2. `GET api/sessions?range=24h` → Rate-Limit (pro Benutzer) → `SessionService.GetAsync`.
3. `CitrixSessionProvider` fragt den Broker (Live-Sitzungen, **im** Broker nach SID gefiltert) und parallel Monitor (beendete Sitzungen und letzte Messwerte). Fehler bei Monitor werden zu Warnungen, ein Fehler beim Broker zu HTTP 503.
4. Die Qualität bewertet `QualityEvaluator` mit konfigurierbaren Schwellwerten: *Ihr Gerät & Netzwerk* aus ICA-Round-Trip-Zeit und verfügbarer Bandbreite, *Rechenzentrum & VDA* aus der Anmeldedauer.
5. Die Oberfläche zeigt das JSON ausschließlich über `textContent` an.

## Ablauf: Abmelden

1. `POST api/sessions/{uid}/logoff` (oder `logoff-disconnected` / `logoff-all`) → CSRF-Token-Prüfung → Rate-Limit.
2. `SessionService` lädt die **eigenen aktiven Sitzungen des Aufrufers** erneut und bildet die Schnittmenge mit der Anfrage; alles andere wird abgelehnt und protokolliert.
3. `BrokerWorkerPool` schickt nur die geprüften IDs an einen Worker. Der Worker prüft die `UserSID` jeder Sitzung **ein zweites Mal**, bevor er `Stop-BrokerSession` aufruft. Das Cmdlet arbeitet asynchron, „abgemeldet“ heißt also „Abmeldung gestartet“.
4. Jedes Ziel und jede Ablehnung landet in `logs/audit-YYYYMMDD.jsonl`.

## Bausteine

| Teil | Datei | Zweck |
|---|---|---|
| Pipeline & API | `Program.cs` | Konfigurationsprüfung, Header, Auth, CSRF, Rate-Limits, Endpunkte |
| Regeln | `Services/SessionService.cs` | Besitzprüfung, Sammelaktionen, Verlaufszeiträume |
| Qualität | `Services/QualityEvaluator.cs` | Ampeln aus Messwerten und Schwellwerten |
| Audit | `Services/AuditLog.cs` | Append-only JSON-Lines, eine Datei pro Tag |
| CSV | `Services/CsvWriter.cs` | Export mit Schutz vor Tabellenformeln |
| Provider-Schnittstelle | `Providers/ISessionProvider.cs` | was die App von einem Backend braucht |
| Demo-Backend | `Providers/MockSessionProvider.cs` | Beispieldaten zweier Benutzer |
| Broker | `Citrix/BrokerWorkerPool.cs`, `PowerShellWorker.cs`, `sss-broker-worker.ps1` | vorgewärmte Worker, JSON-Lines-Protokoll, nur `ping`/`list`/`logoff` |
| Monitor | `Citrix/ODataMonitorClient.cs` | Benutzersuche per SID, aktive und beendete Sitzungen, letzte Messwerte |
| Zusammenführung | `Citrix/CitrixSessionProvider.cs` | kombiniert Broker- und Monitor-Daten |
| Oberfläche | `wwwroot/` | abhängigkeitsfreie SPA, relative URLs, `i18n/*.json` |

## Warum PowerShell-Worker?

Das Broker-SDK gibt es nur als Windows-PowerShell-5.1-Snap-ins. Pro Anfrage PowerShell zu starten würde Sekunden dauern, deshalb hält SSS einen kleinen Pool (Standard 2) dauerhaft laufender Worker, die das Snap-in einmal laden. Nach Timeout oder Absturz wird ein Worker automatisch neu gestartet. Die Worker sprechen ein kleines zeilenweises JSON-Protokoll über stdin/stdout, und das Worker-Skript akzeptiert nur drei Operationen mit geprüften Argumenten.

## Konfigurationsreferenz (`sss.config.json`, Abschnitt `Sss`)

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Provider` | `Mock` | `Citrix` für eine echte Farm |
| `RefreshSeconds` | `30` | automatische Aktualisierung der Oberfläche |
| `Auth:Mode` | `Iis` | `Dev` (fester Demo-Benutzer) wird nur in der Development-Umgebung akzeptiert |
| `Audit:Directory` | `logs` | Ort des Audit-Logs (relativ zur App) |
| `RateLimit:LogoffRequestsPerMinute` | `10` | pro Benutzer |
| `RateLimit:ReadRequestsPerMinute` | `120` | pro Benutzer |
| `RateLimit:HelpdeskRequestsPerMinute` | `60` | pro Helpdesk-Benutzer |
| `Helpdesk:Group` | `""` | AD-Gruppe der Helpdesk-Mitarbeiter; leer = Funktion aus |
| `Helpdesk:ReasonRequired` | `false` | Grund beim Helpdesk-Abmelden verpflichtend |
| `Helpdesk:MaxSessions` | `1000` | Obergrenze der Farm-Übersicht |
| `Quality:*` | siehe `appsettings.json` | Schwellwerte für die Ampeln |
| `Citrix:Controllers` | `[]` | Delivery Controller, der Reihe nach probiert |
| `Citrix:Workers` | `2` | vorgewärmte PowerShell-Worker |
| `Citrix:RequestTimeoutSeconds` | `20` | pro Worker- oder Monitor-Anfrage |
| `Citrix:Monitor:Enabled` | `true` | `false` = nur Live-Sitzungen und Abmelden |
| `Citrix:Monitor:BaseUrl` | `http://{Controller}/Citrix/Monitor/OData/v4/Data/` | besser `https://` |
| `Citrix:Monitor:MetricsDateField` / `IcaRttField` / `BandwidthField` | `CollectedDate` / `IcaRttMS` / `OutputBandwidthAvailable` | Namen der Messwert-Felder (auf CVAD 2507 CU1 geprüft) |
| `Citrix:Monitor:BandwidthDivisor` | `1000` | rechnet das Bandbreitenfeld (bit/s) in kbit/s um |
| `StoreFrontUrl` | `""` | optionaler Link „StoreFront öffnen“ (absolute http(s)-Adresse) |
| `Helpdesk:MessageTitle` | `IT Helpdesk` | Titel der Nachrichten an eine Sitzung |

## Experten-Ansicht, Nachrichten, Status

- `GET api/sessions/{key}/details` (eigene) und `GET api/helpdesk/sessions/{key}/details?sid=` (Helpdesk): `ODataMonitorClient.GetDetailsAsync` liest eine Sitzung samt der letzten 240 Messpunkte und optionalen Datensätzen (`Connections`, `ConnectionFailureLogs`, `ApplicationInstances`) und liefert allgemeine *Gruppen* (Eigenschaft/Wert), *Zeitreihen* (numerische Messwerte) und *Tabellen*. Eigenschaftsnamen sind nicht fest verdrahtet, neue Citrix-Felder erscheinen daher automatisch. Siehe [DATA-SOURCES.de.md](DATA-SOURCES.de.md).
- `POST api/helpdesk/message`: Worker-Operation `message` (`Send-BrokerSessionMessage`), doppelte Besitzprüfung, protokolliert.
- `GET api/helpdesk/status`: `StatusService` führt die Prüfungen des Reiters Status aus (Version, Dienstkonto, Audit-Ordner, Broker- und Monitor-Roundtrip, HTTPS, Helpdesk-Gruppe).

## Prozesse beenden (nicht umgesetzt)

Einen unterstützten Broker-/Monitor-Befehl, der einen Prozess in einer Sitzung beendet, gibt es nicht; der Director nutzt einen eigenen Kanal zum VDA. Bevorzugter Entwurf: ein JEA-Endpunkt auf den VDAs mit zwei Befehlen, gebunden an eine Sitzungs-ID, aufgerufen vom Dienstkonto. Siehe [PERMISSIONS.de.md](PERMISSIONS.de.md).

Weitere Details: [SECURITY.md](../SECURITY.md).
