# SSS – Session Self Service

[English version](README.md)

**Deine Benutzer lösen ihre Citrix-Probleme selbst, in Sekunden. Dein Helpdesk sieht mit einem Klick, *warum* eine Sitzung langsam ist.**

SSS ist ein kleines, schnelles Web-Portal für **Citrix Virtual Apps and Desktops (CVAD)**. Benutzer melden sich mit ihrem Windows-Konto an und sehen **nur ihre eigenen Sitzungen**: wie gut jede ist (Ampeln), was vorher lief (Verlauf) und einen **Abmelden**-Knopf, der funktioniert. Der Helpdesk bekommt obendrauf die Tiefenansicht: Anmelde-Wasserfall, Leistungsdiagramme, Profilanalyse mit Empfehlungen und Zugriff auf Prozesse. Kein Agent, keine Datenbank, keine Software auf den Clients, eine einzige IIS-Anwendung.

<p align="center">
  <img src="docs/img/overview-light.png" alt="Selbstbedienung: Übersicht mit Qualitäts-Ampeln" width="49%">
  <img src="docs/img/overview-dark.png" alt="Dunkelmodus" width="49%">
</p>
<p align="center"><em>Demodaten, heller und dunkler Modus</em></p>

> **Status: Beta.** Durchgängig getestet auf Testfarmen mit Citrix Virtual Apps and Desktops **2203, 2402, 2507 und 2607 (LTSR)** (jeweils ein Delivery Controller). Mehrere Delivery Controller und mehrere Webserver sind umgesetzt ([Doku](docs/MULTI-SERVER.de.md)), aber noch nicht in einer echten Umgebung geprüft. Ausprobieren geht ohne Citrix: Der eingebaute Demo-Modus braucht nur das .NET SDK.

## Für deine Benutzer: sich selbst helfen, mit einem Klick

Eine hängende Anwendung, ein zäher Desktop, eine Sitzung, die auf einem anderen Rechner offen blieb: Heute ist das ein Ticket. Mit SSS öffnet der Benutzer eine Seite und sieht, was los ist.

- **Alle eigenen Sitzungen auf einen Blick**: Desktops und Apps, aktiv oder getrennt, wo sie laufen, seit wann, von welchem Gerät.
- **Sitzungsqualität mit Ampeln**, getrennt nach *Ihr Gerät & Netzwerk* (Latenz, Bandbreite) und *Rechenzentrum & VDA* (Anmeldezeit, Serverlast), mit einem Tipp in einfacher Sprache („Hohe Latenz: WLAN, VPN oder Entfernung zum Rechenzentrum prüfen“). Farben haben immer ein Symbol und ein Wort dabei.
- **Verlauf** der letzten Stunde, des Tages, der Woche oder des Monats: was lief, wie lange, wie gut.
- **Abmelden** einer hängenden Sitzung, aller getrennten oder aller, jeweils mit Bestätigung. Einen „Trennen“-Knopf gibt es bewusst nicht.
- **Nur eigene Sitzungen**, auf dem Server abgesichert (siehe Sicherheit). Filter, Sortierung, CSV-Export, heller und dunkler Modus, handytauglich, per Tastatur und Screenreader bedienbar, auf Deutsch, English, Français, Español und 中文.
- Optionaler Link zu StoreFront, um den Desktop neu zu starten.

## Für deinen Helpdesk: die Diagnose in einem Dialog

Mitglieder einer AD-Gruppe bekommen einen **Helpdesk**-Reiter und eine **Experten-Ansicht** mit allem, was Citrix weiß, ohne Director und ohne Fernzugriff auf die Sitzung.

<p align="center">
  <img src="docs/img/expert-light.png" alt="Experten-Ansicht mit Anmelde-Wasserfall und Leistungsdiagrammen" width="49%">
  <img src="docs/img/profile-light.png" alt="Profilanalyse mit Befunden und Ordnerbaum" width="49%">
</p>
<p align="center"><em>Anmelde-Wasserfall und Leistungsdiagramme; Profiltyp, Größe, Ordnerbaum und Befunde</em></p>

- **Benutzer finden** oder **alle Live-Sitzungen der Farm** auflisten; Sitzungen und Verlauf jedes Benutzers öffnen.
- Sitzungen **abmelden** (mit Pflicht- oder optionalem Grund) und eine **Nachricht an eine Sitzung** senden („bitte Arbeit speichern, der Server startet neu“).
- **Leistungsdaten und Diagramme** im Zeitverlauf: ICA-Laufzeit, verfügbare Bandbreite, Rechenzentrums-Latenz, mit Werten beim Überfahren, dazu Verbindungsverlauf sowie Fehler- und Trennungsgründe.
- **Anmelde-Wasserfall**: wohin die Anmeldezeit geht (Vermittlung, HDX-Verbindung, Authentifizierung, Gruppenrichtlinien, Anmeldeskripte, Profil laden, Shell). Der langsamste Schritt ist hervorgehoben, Zeit, die Citrix nicht aufschlüsselt, wird als solche gezeigt.
- **Profilanalyse, auf dem VDA gelesen**: lokal, servergespeichert, Citrix Profile Management oder **FSLogix**; Container- und Ordnergröße; ein **Ordnerbaum** wie bei TreeSize und die **größten Dateien**; Ordnerumleitung; FSLogix- und UPM-Einstellungen samt Ausschlüssen; dazu **Befunde mit Empfehlung** (Container fast voll, Outlook- oder Teams-Caches, persönliche Daten im Profil, fehlende Ausschlüsse, alte lokale Profile, ...).
- **Anwendungen und Prozesse beenden** einer Sitzung über einen eingeschränkten Endpunkt auf den VDAs (standardmäßig aus).
- **„Support-Info kopieren“** legt die wichtigsten Fakten einer Sitzung für ein Ticket in die Zwischenablage.
- **Statusseite** mit Live-Prüfungen statt Suche in der Ereignisanzeige; **Audit-Protokoll** für jede Suche, Ansicht, Abmeldung, Nachricht und Prozessaktion.

<p align="center"><img src="docs/img/processes-light.png" alt="Anwendungen und Prozesse einer Sitzung" width="60%"></p>

## Warum SSS

| | |
|---|---|
| **Leicht und schnell** | ASP.NET Core direkt im IIS. Keine Datenbank, kein Agent, nichts auf den Clients zu installieren, keine Cloud. Vorgewärmte PowerShell-Worker halten das Citrix-SDK geladen. Läuft neben Web Studio auf demselben Server. |
| **Sicher gebaut** | Die Identität kommt nur aus der Windows-Authentifizierung des IIS. Jede Aktion wird auf dem Server und noch einmal im Worker geprüft, ein Benutzer kann also keine fremde Sitzung anfassen. CSRF-Schutz, strenge Content-Security-Policy, Ratenbegrenzung, **keine Pakete von Drittanbietern**. [Bedrohungsanalyse](docs/THREAT-MODEL.de.md). |
| **Einfach zu betreiben** | Ein Dienstkonto (gMSA), ein Skript: `install.ps1`. `Test-SssSetup.ps1` prüft die Verdrahtung und nennt zu jedem Befund die Abhilfe. Updates rollen von selbst zurück, wenn die neue Version nicht antwortet. |
| **Bereit für größere Umgebungen** | Mehrere Delivery Controller mit Failover, mehrere Webserver hinter einem Load Balancer (Health-Probe, gemeinsamer Schlüsselring), Audit-Protokoll, Versionsregel, Dokumentation auf Deutsch und Englisch. |

## Loslegen

**Demo ausprobieren (etwa zwei Minuten, ohne Citrix):**

```bash
git clone https://github.com/ITCMA/SSS.git && cd SSS
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5080 dotnet run --project src/Sss.Web
```

<http://localhost:5080> öffnen und an die Adresse `?shot=profile` oder `?shot=processes` hängen, um die Experten-Dialoge zu sehen. Braucht das [.NET 10 SDK](https://dotnet.microsoft.com/download).

**In deiner Citrix-Umgebung installieren:** das Zip von den [Releases](https://github.com/ITCMA/SSS/releases) laden und der **[Einrichtungsanleitung](docs/SETUP.de.md)** folgen: Dienstkonto vorbereiten, `install.ps1` ausführen, mit `Test-SssSetup.ps1` und `Test-SssCitrix.ps1` prüfen. Was der Server braucht, steht in den [Voraussetzungen](docs/REQUIREMENTS.de.md).

## Sicherheit in Kürze

- Die Benutzeridentität kommt **ausschließlich** aus der Windows-Authentifizierung des IIS, nie aus Anfrageparametern.
- Jede Abmeldung, Nachricht und Prozessaktion wird serverseitig noch einmal gegen die eigenen Live-Sitzungen des Aufrufers geprüft, bevor etwas an Citrix geht; fremde oder unbekannte IDs werden abgelehnt und protokolliert.
- Anti-Forgery-Token, Ratenbegrenzung pro Benutzer, strenge CSP und Sicherheits-Header, keine Inline-Skripte, kein `innerHTML`.
- Der Helpdesk sind getrennte Endpunkte hinter einer AD-Gruppe; alles, was er tut, wird protokolliert.
- Die Prozesssteuerung nutzt einen eingeschränkten JEA-Endpunkt mit drei Befehlen und zeigt genau, was er kann und was nicht ([Doku](docs/PROCESSES.de.md#was-das-freigibt-und-was-nicht)).
- Details und Meldeweg: [SECURITY.md](SECURITY.md), [Bedrohungsanalyse](docs/THREAT-MODEL.de.md).

## Dokumentation

| | |
|---|---|
| [Einrichtungsanleitung](docs/SETUP.de.md) | von null zu einem laufenden SSS: Dienstkonto, Installation, Anbindung, Helpdesk |
| [Voraussetzungen](docs/REQUIREMENTS.de.md) | Software, Konten, Citrix, Ports |
| [Konfigurationsreferenz](docs/CONFIGURATION.de.md) | jede Option mit Standardwert und Bedeutung |
| [Berechtigungen auf einen Blick](docs/PERMISSIONS.de.md) | wer braucht welches Recht, wo und warum |
| [Betrieb](docs/OPERATIONS.de.md) | Update, Health-Probe, Statusseite, Audit-Protokoll, Fehlersuche |
| [Anwendungen, Prozesse und Profile](docs/PROCESSES.de.md) | der JEA-Endpunkt auf den VDAs, Einrichtung, Sicherheit |
| [Mehrere Controller und Server](docs/MULTI-SERVER.de.md) | Failover, Load Balancer, Kerberos-Alias, Monitor über HTTPS |
| [Datenquellen](docs/DATA-SOURCES.de.md) | woher die Daten kommen und wie die Qualität bewertet wird |
| [Architektur](docs/ARCHITECTURE.de.md) | Bausteine und Abläufe |
| [Versionierung und Kompatibilität](docs/COMPATIBILITY.de.md), [Changelog](CHANGELOG.md) | was stabil ist, wie du aktualisierst |
| [Bedrohungsanalyse](docs/THREAT-MODEL.de.md), [Sicherheitsrichtlinie](SECURITY.md) | was SSS schützt und wie du Probleme meldest |
| [Mitwirken](CONTRIBUTING.md) | Entwicklung, Tests, Bildschirmfotos |

## Roadmap

- [x] Citrix-Provider: Live-Sitzungen und Abmelden über das Broker-SDK (vorgewärmte Windows-PowerShell-5.1-Worker), Verlauf über Monitor-OData
- [x] Getestet auf CVAD 2203, 2402, 2507 und 2607 (ein Delivery Controller)
- [x] Helpdesk-Rolle (AD-Gruppe): Suche, Farm-Übersicht, Abmelden mit Grund, Nachricht an eine Sitzung, Audit
- [x] Experten-Ansicht mit Diagrammen, Anmelde-Wasserfall, Profilanalyse, Support-Info, StoreFront-Link, Statusseite, Setup-Prüfung
- [x] Einzelne Prozesse einer Sitzung beenden (JEA-Endpunkt auf den VDAs)
- [x] IIS-Installer mit automatischem Rückfall, Release-Builds über GitHub Actions
- [ ] Mehrere Controller und mehrere Webserver in einer echten Umgebung prüfen
- [ ] Freundliches „Schließen“ einer Anwendung (braucht einen Helfer in der Sitzung des Benutzers)
- [ ] Engere eigene Citrix-Rolle für das Dienstkonto ([so leitest du sie her](docs/PERMISSIONS.de.md#die-rolle-enger-fassen-offen-vor-10))
- [ ] Citrix-Cloud-Backend (DaaS)
- [ ] 1.0 nach einer öffentlichen Beta-Phase

## Lizenz

[Apache License 2.0](LICENSE)

*Citrix ist eine Marke der Cloud Software Group, Inc. Dieses Projekt ist unabhängig und weder mit ihr verbunden noch von ihr unterstützt.*
