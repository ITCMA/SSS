# Datenquellen und Qualitätsbewertung

[English version](DATA-SOURCES.md)

SSS hat keine eigene Datenbank. Es fragt zwei Citrix-Quellen auf dem Delivery Controller ab und führt die Antworten zusammen.

```
Browser → IIS (Windows-Authentifizierung) → SSS
                                             ├─ Broker-SDK     → Live-Sitzungen, Abmelden, Nachrichten
                                             └─ Monitor-OData  → Verlauf, Qualitätswerte, Experten-Details
                                                (immer als Dienstkonto, nie als Benutzer)
```

## Live-Sitzungen, Abmelden, Nachrichten: Broker-SDK

- Dieselbe Quelle wie `Get-BrokerSession`: aktuell, direkt vom Broker Service.
- Das Broker-SDK gibt es nur als Windows-PowerShell-5.1-Snap-in. SSS hält **zwei vorgewärmte Worker-Prozesse** (`Citrix:Workers`), die das Snap-in einmal laden und über stdin/stdout ein kleines JSON-Protokoll sprechen. Operationen: `ping`, `list`, `listall`, `logoff`, `message`.
- Der Benutzer wird **in der Broker-Abfrage** gefiltert (`-UserSID`), nie nachträglich. Felder: Status (aktiv/getrennt), App oder Desktop, Delivery Group, Maschine, Startzeit, Leerlaufzeit, Client-Name und -Adresse, geöffnete Anwendungen.
- Abmelden ist `Stop-BrokerSession` (asynchron: „abgemeldet“ heißt „Abmeldung gestartet“). Nachrichten sind `Send-BrokerSessionMessage`. Beide prüfen den Besitzer der Sitzung im Worker erneut.

## Verlauf, Qualität, Experten-Ansicht: Monitor-OData v4

Dieselben Daten wie im Director (`/Citrix/Monitor/OData/v4/Data/`), gelesen mit dem Dienstkonto.

| Bedarf | Abfrage |
|---|---|
| numerische Benutzer-ID | `Users?$filter=Sid eq '<sid>'` (30 Minuten gemerkt) |
| Live-Sitzungen + letzte Messung | `Sessions?$filter=UserId eq <id> and EndDate eq null` mit `Machine`, `DesktopGroup`, `CurrentConnection`, neuestem `SessionMetrics`-Eintrag |
| Verlauf | `Sessions?$filter=UserId eq <id> and EndDate ge <Beginn>` (seitenweise, bis `MaxPages`) |
| Experten-Ansicht | genau die eine Sitzung über `UserId` **und** `SessionKey`, mit Maschine, Verbindung und bis zu 240 `SessionMetrics`-Einträgen; dazu `Connections`, `ConnectionFailureLogs`, `ApplicationInstances` dieser Sitzung |
| Helpdesk-Suche | `Users?$filter=contains(...)` |

Broker- und Monitor-Sitzungen werden über den **SessionKey** verbunden. Fällt Monitor aus, laufen die Live-Sitzungen weiter und die Oberfläche zeigt einen Hinweis; fällt der Broker aus, zeigt die Seite „Dienst vorübergehend nicht verfügbar“.

Die drei optionalen Datensätze der Experten-Ansicht gibt es nicht auf jeder Umgebung. SSS meldet einen fehlenden Satz als „von dieser Umgebung nicht geliefert“ und zeigt den Rest. `Test-SssCitrix.ps1` zeigt, welche deine Umgebung hat.

## Ampeln

| Ampel | Messwert | Quelle | Grün / Gelb / Rot (Standard) |
|---|---|---|---|
| Ihr Gerät & Netzwerk | ICA-Round-Trip-Zeit | `SessionMetrics.IcaRttMS` | unter 80 ms / ab 80 / ab 150 |
| Ihr Gerät & Netzwerk | verfügbare Bandbreite | `SessionMetrics.OutputBandwidthAvailable` (bit/s, als kbit/s angezeigt) | über 1500 / bis 1500 / bis 400 kbit/s |
| Rechenzentrum & VDA | Anmeldedauer | `Sessions.LogOnDuration` (ms) | unter 45 s / ab 45 / ab 90 |
| Rechenzentrum & VDA | Last-Index des Servers | noch nicht angebunden | ab 7000 / ab 9000 |

- Der **schlechteste bekannte** Wert einer Ampel gewinnt. Ein fehlender Wert ist „unbekannt“ (?), nie rot.
- Die Schwellwerte stehen in `Sss:Quality`. Die Ampel hat immer Symbol und Text, nicht nur Farbe.
- Standard-Ansicht: die zwei Ampeln plus ein Hinweis in einer Zeile, wenn eine gelb oder rot ist. Experten-Ansicht: jede Zahl, die Citrix gesammelt hat, und Diagramme im Zeitverlauf aller numerischen `SessionMetrics`-Spalten (Spalten, die immer 0 sind, entfallen): Latenz, Bandbreite und Rechenzentrums-Latenz.

## Grenzen

- Die Einheit der Bandbreite (bit/s) wurde aus Messwerten abgeleitet und ist einstellbar (`Citrix:Monitor:BandwidthDivisor`).
- Eine frische Sitzung hat einige Minuten lang evtl. noch keine Messung.
- Beendete Sitzungen in der Verlaufsliste zeigen nur die Anmeldedauer; die Experten-Ansicht zeigt ihre Messwerte, solange Monitor sie noch nicht entfernt hat. Wie lange Monitor Daten aufbewahrt, bestimmen dessen Grooming-Einstellungen, nicht SSS.
- Die Farm-Übersicht des Helpdesks kommt nur vom Broker (ein Monitor-Aufruf pro Benutzer wäre zu teuer); die Qualität erscheint, sobald ein Benutzer geöffnet wird.
- Citrix-Eigenschaftsnamen werden unverändert angezeigt, damit die Experten-Ansicht zu dem passt, was Administratoren vom Director und von OData kennen.
