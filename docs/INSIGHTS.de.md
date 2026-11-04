# Insights, Karten und Benutzer-Verlauf

[English](INSIGHTS.md)

![Reiter Insights](img/insights-light.png)

## Reiter Insights (alle Helpdesk-Stufen)

Der Reiter **Insights** beantwortet „Was ist gerade auffällig?“, ohne eine Sitzung zu öffnen. Er liest den Citrix Monitor und braucht keine zusätzliche Einrichtung. Das Ergebnis wird eine Minute zwischengespeichert.

**Jetzt auffällig** listet, das Schwerste zuerst (jeweils mit Symbol und Text, nicht nur mit Farbe):

| Befund | Wann | Knopf |
|---|---|---|
| Langsame Verbindung | eine Live-Sitzung hat ICA-Laufzeit oder verfügbare Bandbreite jenseits der Grenzen von `Sss:Quality` | Benutzer öffnen |
| Langsame Anmeldung | eine Sitzung der letzten 24 Stunden meldete sich langsamer an als `LogonWarnSeconds` **und** mindestens 1,5-mal langsamer als der Durchschnitt ihrer Bereitstellungsgruppe | Benutzer öffnen |
| Langsame Bereitstellungsgruppe | die durchschnittliche Anmeldezeit der Gruppe der letzten 7 Tage liegt jenseits der Grenze | Sitzungen zeigen |
| Maschine nicht registriert | eine Maschine ist eingeschaltet, aber nicht am Delivery Controller registriert (nicht im Wartungsmodus) | Sitzungen zeigen |
| Maschine ausgelastet | der Lastindex einer registrierten Maschine liegt über `LoadWarn` oder `LoadCrit` | Sitzungen zeigen |
| Lange getrennt | eine Sitzung ist seit 4 Stunden oder länger getrennt (sie belegt weiter Lizenz und Speicher), nach der Leerlaufzeit des Brokers | Benutzer öffnen |
| Läuft sehr lange | eine registrierte Maschine läuft seit 14 Tagen oder länger ohne Neustart | Sitzungen zeigen |
| Wartung | Anzahl der Maschinen im Wartungsmodus (Information) | |

*Benutzer öffnen* zeigt die Sitzungen des Benutzers, dort gibt es Abmelden, Nachricht und die Expertenansicht. *Sitzungen zeigen* öffnet die Helpdesk-Liste, gefiltert auf die Maschine oder Bereitstellungsgruppe.

**Anmeldezeit je Bereitstellungsgruppe** zeigt Anzahl, Durchschnitt, Median und die Zeit, unter der 90 % der Anmeldungen bleiben (Sitzungen der letzten 7 Tage mit Anmeldedauer). Sie wird aus den Sitzungsdatensätzen des Monitors berechnet und funktioniert deshalb auch dort, wo die Zusammenfassungstabellen des Monitors leer sind.

Die Tabelle zeigt außerdem den **Durchschnitt jedes der letzten 7 Tage** als kleine Kurve, damit eine schleichende Verschlechterung auffällt und nicht nur der Durchschnitt. Eine Gruppe mit weniger als 5 Sitzungen erscheint grau und wird nicht bewertet.

**Anwendungsabstürze** und **Verbindungsfehler** der letzten 24 Stunden werden nach Anwendung oder Grund gezählt, mit der Zahl der betroffenen Maschinen und der Maschine mit den meisten. Der Fehlergrund steht so, wie der Monitor ihn meldet (oft ein Code); liefert die Site diese Datensätze nicht, entfällt die Tabelle.

**Maschinen** listet jeden VDA mit Zustand (Problem, ausgelastet, in Ordnung, Wartung, aus), Lastindex, Zahl der Sitzungen und Laufzeit.

## Anmeldezeit gegen die Bereitstellungsgruppe

In der Expertenansicht einer Sitzung sieht der Helpdesk eine Zeile wie „Anmeldung 38 s ▲ Bereitstellungsgruppe DG-Desktops: Ø 31 s, Median 28 s, 90 % unter 49 s“. Sie zeigt, ob diese Sitzung (oder dieser Benutzer) langsam ist oder die ganze Gruppe.

## Verlauf eines Benutzers

Über den Sitzungen des Benutzers sieht der Helpdesk die letzten 30 Tage in einer Zeile: Zahl der Sitzungen, durchschnittliche Anmeldezeit (und die der Gruppe), Verbindungsfehler und automatische Wiederverbindungen. Ein gelber oder roter Rahmen kennzeichnet Benutzer mit vielen Problemen (fünf oder mehr Fehler und Wiederverbindungen, oder eine Anmeldezeit deutlich über der der Gruppe). Die Zeile braucht dieselbe Berechtigung wie das Ansehen der Sitzungen des Benutzers und wird fünf Minuten zwischengespeichert.

## Karten für Benutzer

Ein normaler Benutzer (Expertenansicht aus) sieht die eigenen Sitzungen als **Karten**: zuerst ein Satz, ob alles in Ordnung ist, dann je Sitzung Name, Zustand, die beiden Qualitäts-Ampeln und den Abmelden-Knopf. Die technischen Spalten (Bereitstellungsgruppe, Maschine, Start, Dauer, Leerlauf, Client) stehen hinter **Details**. Der Helpdesk und die Expertenansicht behalten die dichte Tabelle. Zeitraum und CSV-Export stehen jetzt beim Verlauf.

## Datenquellen und Grenzen

`Sessions`, `Users`, `Machines`, `LoadIndexes`, `ConnectionFailureLogs` und `SessionAutoReconnects` des Monitors (siehe [DATA-SOURCES](DATA-SOURCES.de.md)). SSS speichert nichts davon. Bei einer Farm mit sehr vielen Sitzungen werden bis zu `Sss:Citrix:Monitor:MaxPages` Seiten gelesen. Fehlt der Site eine Datensatzart, entfällt nur der betreffende Teil.
