# Delivery Controller: Erkennung und Lastverteilung

[English](CONTROLLERS.md)

SSS kann mit einer festen Liste von Delivery Controllern arbeiten, sie selbst finden oder mit einer virtuellen Adresse davor sprechen. Alle drei Wege sind möglich und lassen sich kombinieren. Eine *Site* hat mehrere Controller mit einer gemeinsamen Datenbank, jeder Controller antwortet also mit denselben Daten; die Anfragen zu verteilen ist deshalb unkritisch.

| Möglichkeit | Passt, wenn | Konfiguration |
|---|---|---|
| **A. Feste Liste** | kleine Site, Controller ändern sich selten | `Citrix:Controllers` = alle Namen |
| **B. Erkennung** (empfohlen für größere Sites) | Controller kommen hinzu oder werden ersetzt | `Citrix:Discovery:Enabled` = `true`, `Bootstrap` = ein Controller |
| **C. Virtuelle Adresse** | ein Load Balancer (NetScaler/ADC, DNS-Alias) steht schon vor den Controllern | `Citrix:Controllers` = `["ddc.corp.local"]` und `Strategy` = `Failover` |

## Erkennung

```json
"Citrix": {
  "Controllers": [ "ddc01.corp.local" ],
  "Discovery": { "Enabled": true, "RefreshMinutes": 15 }
}
```

- SSS fragt einen Controller (`Discovery:Bootstrap`, standardmäßig der erste Eintrag von `Controllers`) nach **allen Controllern seiner Site** (`Get-BrokerController`) und hält die Liste: im Speicher und in `data\controllers.json`, damit ein Neustart auch funktioniert, wenn der Bootstrap-Controller gerade ausgefallen ist.
- Die Liste wird alle `RefreshMinutes` aktualisiert (Standard 15). Ist der gefragte Controller nicht erreichbar, werden die bisher bekannten anderen gefragt. Antwortet niemand, bleibt die letzte bekannte Liste in Gebrauch; eine **leere** Antwort ersetzt sie nie.
- Einträge aus `Controllers` bleiben immer in der Liste (und kommen beim Failover zuerst), erkannte kommen dazu. Ein Controller, der die Site verlässt, fällt bei der nächsten Aktualisierung heraus.
- **Sicherheit:** Es werden nur Namen innerhalb der erlaubten Domäne akzeptiert (`Discovery:AllowedSuffixes`, standardmäßig die Domäne des Bootstrap-Controllers, zum Beispiel `.corp.local`) und nur einfache Hostnamen. Eine manipulierte Antwort kann SSS nicht dazu bringen, Anfragen woandershin zu schicken. Controller, die der Broker als nicht aktiv meldet (ausgefallen, in Wartung), werden ignoriert, außer `IncludeInactive` ist `true`.
- Der Status-Reiter (Admin) listet jeden Controller mit Herkunft (`config` oder `discovered`), Zustand, Version, Zone, Antwortzeit und Anzahl der Antworten und Fehler, warnt vor unterschiedlichen Versionen (während eines Upgrades) und zeigt, wenn die Erkennung fehlschlägt.
- Mehrere Sites einer Farm sind getrennte Sites mit eigenen Controllern und eigener Datenbank. Die Erkennung arbeitet je Site; siehe den Hinweis zu mehreren Sites unten.

## Anfragen verteilen

```json
"Balancing": { "Strategy": "LeastLatency", "Preferred": [ "DC1" ] }
```

| Strategie | Verhalten |
|---|---|
| `Failover` (Standard) | Der Controller, der zuletzt geantwortet hat, wird zuerst gefragt, bei einem Ausfall folgen die anderen in der konfigurierten Reihenfolge. Wie bisher. |
| `RoundRobin` | Jede Anfrage beginnt beim nächsten Controller. |
| `LeastLatency` | Zwei gesunde Controller werden zufällig gezogen, der mit der niedrigeren mittleren Antwortzeit wird zuerst gefragt. Die Last verteilt sich, langsame Controller bekommen weniger, ein neuer Controller wird sofort ausprobiert. |

- `Preferred`: Controller-Namen **oder Zonen-Namen**, die zuerst genutzt werden, solange sie gesund sind, zum Beispiel die Controller im selben Rechenzentrum wie der Webserver. Die anderen bleiben als Rückfall.
- **Gesundheit:** Ein Controller, der `FailureThreshold`-mal hintereinander ausfällt (Standard 3), wird `OpenSeconds` lang übersprungen (Standard 30) und dann wieder versucht. Sind alle als schlecht markiert, werden sie trotzdem versucht, eine Anfrage wird also nie allein deswegen abgelehnt.
- Dieselbe Reihenfolge gilt für den Broker (PowerShell-Worker) und den Monitor (OData). Die Statusprüfung und `/healthz` fragen einen Controller wirklich (`Get-BrokerSite`), ein toter Broker fällt also auch auf, wenn der Worker lebt.
- Jede Anfrage trägt die Reihenfolge zum Worker; welcher Controller geantwortet hat und welche ausgefallen sind, wird zurückgemeldet und speist die Statistik.

## Möglichkeit C: eine virtuelle Adresse

Hat deine Site schon einen Load Balancer oder DNS-Alias vor den Controllern, trage nur diesen Namen ein und lass `Strategy` auf `Failover`. SSS hat dann eine einzige Adresse, den Rest übernimmt der Balancer. Zwei Punkte: Broker SDK und Monitor müssen über den Alias erreichbar sein (Ports 80/443, das Zertifikat muss zum Alias passen), und der Status-Reiter zeigt dann nur einen Controller, du siehst die einzelnen also nicht. Kombinierbar mit der Erkennung: der Alias als erster Eintrag, die Erkennung für die echten Namen.

## Mehrere Sites in einer Farm

Eine Farm mit mehreren **Sites** hat getrennte Delivery Controller und Datenbanken je Site. SSS spricht derzeit mit **einer Site** (der, zu der die eingetragenen Controller gehören). Die Sitzungen eines Benutzers über mehrere Sites in einer Liste zu zeigen ist geplant, aber noch nicht umgesetzt.
