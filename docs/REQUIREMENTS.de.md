# Voraussetzungen und Abhängigkeiten

[English](REQUIREMENTS.md)

## SSS-Server (IIS)

| Was | Detail |
|---|---|
| Betriebssystem | Windows Server 2016 oder neuer (getestet auf 2019) mit IIS |
| IIS-Funktionen | *Windows-Authentifizierung* und *Anwendungsinitialisierung* (`install.ps1` installiert sie auf einem Server-Betriebssystem), eine Site mit HTTPS-Bindung |
| ASP.NET Core Module V2 | aus dem „.NET Hosting Bundle“ eines aktuellen .NET-10-Releases. Die Anwendung selbst ist **eigenständig** (self-contained), eine .NET-Runtime wird nicht gebraucht |
| Windows PowerShell 5.1 | in Windows enthalten. Die Citrix-Snap-ins basieren auf dem .NET Framework, daher wird kein PowerShell 7 verwendet |
| Citrix-PowerShell-Snap-ins | `Citrix.Broker.Admin.V2` auf dem SSS-Server: mit Studio installiert, oder das *Remote PowerShell SDK* von der Citrix-Downloadseite. Nötig für Sitzungen, Abmelden, Nachrichten |
| Dienstkonto | ein gruppenverwaltetes Dienstkonto (gMSA) als Identität des App-Pools, siehe [Berechtigungen](PERMISSIONS.de.md). Ein normales Domänenkonto geht auch; die App-Pool-Identität (`IIS AppPool\SSS`) erreicht den Controller nicht |
| Festplatte | etwa 150 MB für die Anwendung, dazu kleine, wachsende Audit-Dateien in `logs` |
| Arbeitsspeicher | etwa 100 MB für den Webprozess plus 60-100 MB je PowerShell-Worker (`Workers`, Standard 2) |

## Citrix-Umgebung

| Was | Detail |
|---|---|
| Versionen | Citrix Virtual Apps and Desktops 2203, 2402, 2507 und 2607 (LTSR) getestet. Andere aktuelle Releases sollten funktionieren; mit `Test-SssCitrix.ps1` die Monitor-Feldnamen prüfen |
| Editionen | beliebig; die Tiefe der Monitor-Daten hängt von Edition und den Grooming-Einstellungen der Site ab |
| Delivery Controller | vom SSS-Server auf TCP 80 oder 443 erreichbar (Broker SDK, Monitor OData) |
| Delegierte Administration | das Dienstkonto ist Citrix-Administrator mit der Rolle *Help Desk Administrator* (oder einer eigenen Rolle, die Sitzungen lesen, abmelden, Nachrichten senden und Monitor-Daten lesen darf), Bereich *Alle* |
| Monitor-Dienst | erreichbar über `http://` oder `https://`; siehe [Monitor über HTTPS](MULTI-SERVER.de.md#monitor-über-https) |
| Prozesssteuerung (optional) | WinRM (TCP 5985) vom SSS-Server zu den VDAs und der JEA-Endpunkt aus `Install-SssJea.ps1` auf jedem VDA, siehe [Anwendungen und Prozesse](PROCESSES.de.md) |

## Active Directory

| Was | Detail |
|---|---|
| KDS-Stammschlüssel | einmalig für gMSAs nötig (`Add-KdsRootKey`) |
| Helpdesk-Gruppe | optional, eine Sicherheitsgruppe, deren Mitglieder die Helpdesk-Funktionen erhalten |
| Kerberos | korrekte Zeit und DNS. Hinter einem Load-Balancer-Alias ein SPN `HTTP/<Alias>` an der gMSA ([Mehrere Controller und Server](MULTI-SERVER.de.md)) |
| Vertrauensstellungen | Benutzer aus vertrauten Domänen funktionieren, solange ihre SID aufgelöst wird; Namen erscheinen als `DOMÄNE\Benutzer` |

## Client (Browser)

Jeder aktuelle Browser (Chrome, Edge, Firefox, Safari) mit JavaScript. Die Windows-Authentifizierung braucht einen Browser, der sich an Intranetseiten automatisch anmeldet (Edge und Chrome tun das für Seiten in der Zone *Lokales Intranet*; die URL bei Bedarf per Gruppenrichtlinie eintragen). Die Oberfläche hat keine externen Abhängigkeiten: kein CDN, keine Schriften, kein Tracking.

## Netzwerk und Ports

| Von | Nach | Port | Zweck |
|---|---|---|---|
| Browser | SSS-Server | 443 | die Weboberfläche |
| SSS-Server | Delivery Controller | 80 oder 443 | Broker SDK, Monitor OData |
| SSS-Server | VDA | 5985 (WinRM) | nur mit Prozesssteuerung |
| SSS-Server | Domänencontroller | übliche AD-Ports | gMSA, Kerberos, Gruppenabfrage |
| SSS-Server | Dateifreigabe | 445 | nur mit `DataProtection:KeyDirectory` oder gemeinsamem Audit-Ordner |

Eine eingehende Verbindung von den Controllern oder VDAs zu SSS ist nicht nötig, und SSS braucht keinen Internetzugang.

## Komponenten von Drittanbietern

Die Webanwendung hat **keine NuGet-Pakete von Drittanbietern**; sie nutzt nur, was mit .NET geliefert wird. Die Weboberfläche ist reines JavaScript ohne Bibliotheken. Das Testprojekt nutzt xUnit und Microsoft.AspNetCore.Mvc.Testing. Die Build- und CI-Aktionen sind auf Commit-Hashes festgelegt und werden von Dependabot aktualisiert.

## Marken

Citrix, Citrix Virtual Apps and Desktops und Director sind Marken der Cloud Software Group, Inc. SSS ist ein unabhängiges Projekt und weder mit Citrix verbunden noch von Citrix empfohlen.
