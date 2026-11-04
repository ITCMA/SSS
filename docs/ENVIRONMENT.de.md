# Umgebung: Client-Versionen und Standorte

[English](ENVIRONMENT.md)

Der Reiter **Umgebung** (Rolle *Admin*) zeigt für alle Live-Sitzungen der Site, welche Workspace-App-Versionen und Plattformen sich verbinden und von welchen Ihrer Standorte. Er beantwortet Fragen wie „wie viele Geräte haben noch eine alte Workspace-App?“ oder „von wo verbinden sich unsere Benutzer?“, ohne den Director zu öffnen. Es werden **nur Zahlen** gezeigt, nichts pro Person.

Oben im Reiter stehen **Diagramme** (Sitzungen nach Plattform und Standort, VDA-Maschinen nach Zustand, die am häufigsten gestarteten Anwendungen); die Tabellen darunter enthalten die Einzelheiten. Sitzungen ohne Client-Version oder Standort erscheinen als *unbekannt*, damit die Zahlen immer aufgehen.

SSS liest Version und Plattform jeder Live-Verbindung aus dem Citrix Monitor (`Connection.ClientVersion`, `Connection.ClientPlatform`) und zählt Geräte und Sitzungen je Version und Plattform, mit einem Balken für den Anteil. Die Felder werden nur angefragt, wenn das `$metadata` der Site sie auflistet. Liefert Ihre Site sie nicht, sagt der Reiter das; `Test-SssCitrix.ps1` listet auf, was der Monitor anbietet (Datentabellen und Eigenschaften zu Client, Plattform, Standort, Richtlinien oder Score), damit Sie sehen, was verfügbar ist. Die Namen lassen sich in `Sss:Citrix:Monitor:ClientVersionField` und `ClientPlatformField` ändern.

Die Zahlen stammen aus den **Live-Sitzungen** und werden eine Minute zwischengespeichert. Geräte werden nach Clientname gezählt, Benutzer nach der Benutzer-ID des Monitors.

### Hinweis bei veralteten Clients

Legen Sie je Plattform die älteste akzeptierte Version fest:

```json
"Environment": { "MinClientVersion": { "Windows": "26.3.0", "*": "26.3.0" } }
```

Versionen werden nach ihren Zahlen verglichen (`26.10.0` ist neuer als `26.9.0`, `26.09.0.17` entspricht `26.9.0.17`). `*` gilt für jede Plattform ohne eigenen Eintrag. Clients unterhalb des Minimums sind im Reiter als **veraltet** markiert, und der **Benutzer** sieht in der eigenen Sitzungsliste einen kurzen Hinweis („Ihre Workspace-App ist veraltet. Bitte aktualisieren Sie sie.“) samt Version. Abschalten mit `"HintOutdatedClient": false`; ohne Minimum wird nichts markiert.

## Standorte

Eine lokale Site liefert keinen Standort: Die Citrix-Cloud leitet ihn aus der Client-Adresse mit einem Geolokalisierungsdienst ab, und interne Adressen lassen sich gar nicht verorten. SSS lässt Sie deshalb **Ihre** Netze Namen zuordnen:

```json
"Environment": {
  "Locations": [
    { "Name": "Zentrale",          "Networks": [ "10.20.0.0/16" ] },
    { "Name": "Niederlassung",     "Networks": [ "10.30.0.0/16", "192.0.2.0/24" ] },
    { "Name": "Homeoffice (VPN)",  "Networks": [ "10.99.0.0/16" ] }
  ]
}
```

Eine Sitzung gehört zum Standort mit dem **genauesten** passenden Netz (ein `/24` schlägt ein `/16`); alles andere steht als „nicht zugeordnet“. Die Tabelle zeigt Sitzungen, Geräte und Benutzer je Standort. Eine Weltkarte gibt es bewusst nicht: Für interne Netze würde sie nichts Wahres zeigen, und eine Tabelle beantwortet die Frage schneller.

### Standorte laut Citrix

Liefert die Site `Connection.ClientLocationCountry` und `ClientLocationCity` (der Monitor einer Site mit Gateway oder Cloud-Telemetrie kann das), listet der Reiter zusätzlich **Land und Stadt, wie Citrix sie meldet**, mit Sitzungen, Geräten und Benutzern. Verbindungen ohne Standort erscheinen als „unbekannt“. Diese Liste braucht keine Konfiguration; die Netze oben bleiben der Weg, eigene Standorte zu benennen, auch für interne Adressen.

## Anwendungen innerhalb der Sitzungen

Der Monitor aktueller Sites hält auch die **innerhalb der Desktops** gestarteten Anwendungen (die Daten hinter Directors Ansicht „lokale Apps“, erhoben vom uberAgent-Teil des VDA-Agenten). Der Reiter listet die **meistgenutzten Anwendungen der letzten 7 Tage** mit Starts, der höchsten gleichzeitigen Zahl, Benutzern pro Tag (höchster Tag) und Maschinen, mit den echten Anwendungsnamen. Liefert die Site solche Daten nicht, sagt der Reiter es. In der Experten-Ansicht einer Sitzung erscheinen dieselben Daten als Tabelle **In der Sitzung gestartete Anwendungen** (Name, Version, Programmpfad, Startzeit, Startdauer), dazu die **Anwendungsabstürze** dieser Sitzung.

### VDA-Maschinen nach Agent-Version

Unter den Zahlen listet eine Tabelle jede Maschine (Bereitstellungsgruppe, Registrierung, Betriebszustand, Wartungsmodus, Sitzungen, letzte Registrierungsänderung), danach den Local-Host-Cache-Bericht, wenn die Site ihn liefert. Sitzungen ohne von Citrix gemeldeten Standort bilden eine Zeile **unbekannt** in der Citrix-Standorttabelle; die Tabelle der *eigenen Standorte* ist optional und hilft nur dafür.

Der Reiter zählt außerdem die **VDA-Maschinen nach Agent-Version und Betriebssystem** (aus dem Monitor), mit den Maschinen in Wartung und den nicht registrierten, damit alte Agenten so auffallen wie alte Workspace-Apps.

### Kurze Anwendungsnamen

Der Monitor speichert die Namen lokal gestarteter Anwendungen verkürzt (zum Beispiel „GglChrm“ für Google Chrome). SSS zeigt den Namen, wie Citrix ihn liefert, und darunter die **Programmdatei** eines Starts dieser Anwendung (`chrome`), was die Kurznamen lesbar macht. Ein lesbarer Produktname kommt vom VDA-Endpunkt, wenn Sie die Prozesse einer Sitzung öffnen ([Prozesse](PROCESSES.de.md)).

## Datenschutz

Standorte und Gerätelisten sind personenbezogene Daten im Sinne des Datenschutzrechts, auch wenn sie als Zahlen erscheinen. Der Reiter zeigt nur Summen, verlangt die Rolle Admin und speichert nichts. Stimmen Sie die Nutzung mit dem Datenschutzbeauftragten und, wo vorhanden, dem Betriebsrat ab, bevor Sie ihn einschalten, vor allem den Hinweis bei veralteten Clients, der einen Benutzer über sein eigenes Gerät informiert.

## Was das nicht tut

- Citrix-Cloud-Funktionen aus der Cloud-Telemetrie (Device-Trust- und Posture-Richtlinien, Geolokalisierungskarten) gibt es für eine lokale Site nicht, und sie werden nicht nachgebaut.
- SSS sammelt keinen Verlauf: Es zeigt die Lage der Live-Sitzungen zum Zeitpunkt der Anfrage. Eine Statistik über Tage braucht eine Datenbank und ist ein anderes Produkt (dafür Director oder uberAgent nutzen).
