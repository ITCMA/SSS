# Einrichtungsanleitung

[English](SETUP.md)

Diese Anleitung führt dich von null zu einem laufenden SSS in deiner Citrix-Umgebung. Die Kurzfassung:

1. **Demo ausprobieren** auf irgendeinem Rechner (ohne Citrix): [Lokal ausprobieren](#lokal-ausprobieren-macos-linux-windows).
2. **Dienstkonto vorbereiten** (eine gMSA) mit einer Citrix-Rolle: [Dienstkonto vorbereiten](#dienstkonto-vorbereiten-gmsa).
3. **Auf dem IIS installieren** mit `install.ps1` und mit `Test-SssSetup.ps1` prüfen: [Installation auf dem IIS](#installation-auf-dem-iis-windows-server).
4. **Mit der Citrix-Umgebung verbinden** und `Test-SssCitrix.ps1` ausführen: [Anbindung an eine echte Citrix-Farm](#anbindung-an-eine-echte-citrix-farm).
5. **Helpdesk einschalten**: [Helpdesk](#helpdesk).
6. Optional: [Prozesse beenden und Profilanalyse auf den VDAs](PROCESSES.de.md), [mehrere Controller und Server](MULTI-SERVER.de.md).

Was jede Option bewirkt, steht in der [Konfigurationsreferenz](CONFIGURATION.de.md), was der Server braucht in den [Voraussetzungen](REQUIREMENTS.de.md), der Alltag im [Betrieb](OPERATIONS.de.md).

## Lokal ausprobieren (macOS, Linux, Windows)

Voraussetzung: [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/ITCMA/SSS.git && cd SSS
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5080 dotnet run --project src/Sss.Web
```

<http://localhost:5080> öffnen. In der Entwicklung werden ein fester Demo-Benutzer und Beispieldaten verwendet.

```bash
dotnet test
```

## Dienstkonto vorbereiten (gMSA)

SSS sollte unter einem **Group Managed Service Account (gMSA)** laufen: einem Active-Directory-Konto für Dienste, dessen Passwort Windows selbst erzeugt und regelmäßig wechselt. Niemand kennt oder tippt es, man kann sich damit nicht interaktiv anmelden, und es läuft nie ab. Erkennbar ist es am `$` am Ende, zum Beispiel `CORP\svc-sss$`. Mit diesem Konto spricht SSS mit dem Delivery Controller, es braucht also genau die Citrix-Rechte, die SSS benötigt, und nicht mehr.

**1. gMSA anlegen** (Domain-Admin; AD-PowerShell-Modul):

```powershell
# einmal pro Domäne, nur falls noch kein KDS-Root-Key existiert (im Lab sofort wirksam):
Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))

# 'IIS-SERVER$' = Computerkonto des Servers, auf dem SSS läuft (oder eine Sicherheitsgruppe solcher Server)
New-ADServiceAccount -Name svc-sss -DNSHostName svc-sss.corp.local `
  -PrincipalsAllowedToRetrieveManagedPassword 'IIS-SERVER$'
```

**2. Auf dem IIS-Server installieren und testen** (PowerShell als Administrator; braucht das AD-Modul / RSAT):

```powershell
Install-ADServiceAccount svc-sss
Test-ADServiceAccount svc-sss      # muss True liefern
```

**3. Citrix-Rolle zuweisen.** Der Kontenauswahldialog von Citrix Studio listet gMSAs meist nicht auf. Lege den Administrator deshalb per PowerShell auf einem Delivery Controller an (PowerShell als Administrator). Das ist der empfohlene Weg:

```powershell
Add-PSSnapin Citrix.DelegatedAdmin.Admin.V1
New-AdminAdministrator -Name 'CORP\svc-sss$' -Enabled $true
Add-AdminRight -Administrator 'CORP\svc-sss$' -Role 'Help Desk Administrator' -All
Get-AdminAdministrator | Select-Object Name, Enabled, @{n='Rights';e={ @($_.Rights).Count }}   # Rights muss 1 oder mehr sein
```

`New-AdminAdministrator` allein legt einen Administrator **ohne Rechte** an (`Rights : {}`) – `Add-AdminRight` ist nötig. Die eingebaute Rolle **Help Desk Administrator** darf Sitzungen lesen, abmelden und Monitor-Daten lesen; lege eine engere eigene Rolle an, sobald du weißt, was du brauchst (siehe [SECURITY.md](../SECURITY.md)). Danach den App-Pool von SSS neu starten.

*Alternative über eine AD-Gruppe:* `svc-sss$` in eine Sicherheitsgruppe aufnehmen und diese Gruppe in Studio hinzufügen. Das funktioniert, aber ein laufender App-Pool behält das Token, mit dem er gestartet wurde. Eine Änderung der Gruppenmitgliedschaft wirkt daher eventuell erst nach einem vollständigen Neustart des App-Pools (manchmal erst nach einem Neustart des Servers). Der direkte Eintrag oben vermeidet das Problem.

**4. Dem Installer übergeben** mit `-ServiceAccount 'CORP\svc-sss$'` (einfache Anführungszeichen, sonst expandiert PowerShell das `$`). Der Installer setzt es als Identität des App-Pools und gibt ihm Leserechte auf die Anwendung und Schreibrechte auf den Audit-Log-Ordner.

Ohne `-ServiceAccount` läuft der App-Pool als `ApplicationPoolIdentity`. Das meldet sich an anderen Servern als Computerkonto an und hat keine Citrix-Rechte – genug, um die Oberfläche anzusehen, aber nicht für den echten Betrieb. Der Installer unterstützt nur gMSAs (er setzt ein leeres App-Pool-Passwort).

## Installation auf dem IIS (Windows Server)

Voraussetzungen auf dem Server: IIS mit dem [.NET Hosting Bundle](https://dotnet.microsoft.com/download/dotnet) (für das ASP.NET Core Module; SSS selbst ist self-contained), die Citrix-PowerShell-Snap-ins (oder das Remote PowerShell SDK) und ein [Group Managed Service Account](#dienstkonto-vorbereiten-gmsa) mit einer Citrix-Rolle.

1. `sss-<version>-win-x64.zip` und die `.sha256`-Datei von den [Releases](https://github.com/ITCMA/SSS/releases) laden, die Prüfsumme kontrollieren (`Get-FileHash`) und das Zip auf dem Server entpacken.
2. In einer Windows PowerShell mit Administratorrechten:

```powershell
.\install.ps1 -Interactive            # fragt Controller, Dienstkonto und Helpdesk-Gruppe ab
.\install.ps1 -Controllers ddc01.corp.local,ddc02.corp.local -ServiceAccount 'CORP\svc-sss$'
```

3. Verdrahtung prüfen: `.\Test-SssSetup.ps1`. Das Skript ist schreibgeschützt und nennt zu jedem Befund den genauen Befehl zur Behebung (IIS, App-Pool, gMSA, Citrix-Administratoreintrag, Helpdesk-Gruppe, Berechtigungen, die Anwendung selbst). Welches Konto welches Recht braucht, steht in [Berechtigungen auf einen Blick](PERMISSIONS.de.md).


Standardmäßig wird SSS zur Anwendung `Default Web Site/sss` und liegt auf einem Web-Studio-Server damit neben Studio. Mit `-SiteName`, `-AppPath` und `-PhysicalPath` lässt sich das ändern, mit `-WhatIf` eine Vorschau anzeigen. Das Skript legt einen eigenen App-Pool an (kein Managed Code, *AlwaysRunning*, kein Idle-Timeout, kein periodisches Recycling), aktiviert die Windows-Authentifizierung, deaktiviert den anonymen Zugriff, setzt Berechtigungen, schreibt eine Start-`sss.config.json` (wird bei Updates behalten) und führt einen Smoke-Test aus. Ein Update geht mit `.\install.ps1 -Update`: Es werden nur die Dateien ersetzt (Konfiguration, App-Pool und Berechtigungen bleiben), die vorige Version bleibt in `<Installationsordner>.previous` erhalten und wird bei einem fehlgeschlagenen Smoke-Test automatisch wiederhergestellt. Die Seite ist kurz über `app_offline.htm` offline. `-Uninstall` entfernt Anwendung und App-Pool, behält aber Dateien, Konfiguration und Audit-Logs.

## Konfiguration

Die Standardwerte stehen in `src/Sss.Web/appsettings.json`. Überschreiben lassen sie sich in einer Datei `sss.config.json` neben der Anwendung (Abschnitt `Sss`), zum Beispiel:

```json
{
  "Sss": {
    "RefreshSeconds": 30,
    "RateLimit": { "LogoffRequestsPerMinute": 10 },
    "Quality": { "RttWarnMs": 80, "RttCritMs": 150 }
  }
}
```

Nach Änderungen ist ein Neustart (Recycling des App-Pools) nötig.

## Anbindung an eine echte Citrix-Farm

`Sss:Provider` in der `sss.config.json` auf `Citrix` setzen:

```json
{
  "Sss": {
    "Provider": "Citrix",
    "Citrix": {
      "Controllers": [ "ddc01.corp.local", "ddc02.corp.local" ],
      "Workers": 2,
      "Monitor": { "BaseUrl": "https://ddc01.corp.local/Citrix/Monitor/OData/v4/Data/" }
    }
  }
}
```

- **Live-Sitzungen und Abmelden** laufen über das Broker-SDK in vorgewärmten Windows-PowerShell-5.1-Workern. Der Server braucht die Citrix-PowerShell-Snap-ins (kommen mit Studio/Web Studio) oder das *Remote PowerShell SDK*, wenn SSS auf einem separaten Server läuft.
- **Verlauf und Qualität** kommen aus der Monitor-OData-API. Ist sie nicht erreichbar, zeigt die Oberfläche einen Hinweis und alles andere funktioniert weiter.
- Die Identität des App-Pools (am besten ein gMSA) braucht eine delegierte Citrix-Administratorrolle, die Sitzungen lesen, abmelden und Monitor-Daten lesen darf (z. B. eine eigene Rolle auf Basis von *Help Desk Administrator*).
- Vor dem ersten Einsatz auf dem Server `scripts/Test-SssCitrix.ps1` ausführen. Es liest nur und prüft Erreichbarkeit und die genauen OData-Feldnamen. Aufruf: `.\Test-SssCitrix.ps1 -Controller ddc01 -UserName CORP\alice` (der Benutzername muss `DOMAIN\username` lauten), während der Benutzer eine Sitzung hat. Auf CVAD 2507 CU1 geprüft: Alle von SSS verwendeten Monitor-Felder existieren; die Bandbreite wird aus `SessionMetrics.OutputBandwidthAvailable` (bit/s) bewertet. Der Monitor wird standardmäßig über `https://` angesprochen; für einen Controller ohne HTTPS-Bindung `Citrix:Monitor:UseHttps` auf `false` setzen oder die volle Adresse in `Citrix:Monitor:BaseUrl` angeben. Vor dem Posten der Ausgabe von `Test-SssSetup.ps1` oder `Test-SssCitrix.ps1` in einem öffentlichen Issue `-Redact` anhängen: Es ersetzt Benutzer, Domäne, Hostnamen, SIDs, IP-Adressen und GUIDs (`-Mask wort1,wort2` ergänzt weitere).
- „Abmelden“ startet den Vorgang (`Stop-BrokerSession` arbeitet asynchron); die Sitzung verschwindet nach wenigen Sekunden aus der Liste.

## Helpdesk

Mitglieder einer AD-Gruppe können Benutzer finden und **deren** Sitzungen sehen und abmelden. Alle anderen bekommen bei allen Helpdesk-Endpunkten HTTP 403 und sehen den Reiter nie.

1. Eine Sicherheitsgruppe anlegen, z. B. `SSS-Helpdesk`, und die Helpdesk-Mitarbeiter aufnehmen: `New-ADGroup -Name SSS-Helpdesk -GroupScope Global -GroupCategory Security`.
2. Die Gruppe in die `sss.config.json` eintragen (oder bei einer Neuinstallation `-HelpdeskGroup 'CORP\SSS-Helpdesk'` an `install.ps1` übergeben) und den App-Pool recyceln:

```json
{ "Sss": { "Helpdesk": { "Group": "CORP\\SSS-Helpdesk", "ReasonRequired": false, "MaxSessions": 1000 } } }
```

3. Helpdesk-Mitarbeiter müssen sich nach der Aufnahme in die Gruppe neu anmelden, weil Gruppenmitgliedschaften im Windows-Anmeldetoken stehen.

Im Reiter **Helpdesk** suchst du nach einem Namensteil (Benutzer mit aktiver Sitzung aus dem Broker, Benutzer nur mit Verlauf aus Monitor) oder wählst einen Benutzer aus der Liste **aller aktiven Sitzungen** der Farm (filterbar, begrenzt auf `MaxSessions`). Beim Öffnen eines Benutzers siehst du dieselben Tabellen wie er selbst, mit denselben Abmelden-Knöpfen und einem optionalen **Grund**. Mit `ReasonRequired` wird der Grund zur Pflicht.

Alles, was der Helpdesk tut, steht im Audit-Log mit handelndem Benutzer, betroffenem Benutzer und Grund: Suchen, das Öffnen eines Benutzers (höchstens einmal pro 5 Minuten und Benutzer), die Farm-Übersicht und jedes Abmelden. Helpdesk-Anfragen sind ratenbegrenzt (60/min). Der Helpdesk nutzt dasselbe Dienstkonto und dieselbe Citrix-Rolle wie das Self-Service, es sind keine zusätzlichen Citrix-Rechte nötig. Wer Zugriff auf den Helpdesk hat, sieht die Sitzungen aller Benutzer: Die Gruppe klein halten.

## Fehlersuche

Als erstes: den Reiter **Status** öffnen (Helpdesk) oder `Test-SssSetup.ps1` ausführen. Beide prüfen dasselbe und nennen die fehlschlagende Stelle.

| Symptom | Ursache und Lösung |
|---|---|
| Seite lädt, zeigt aber „Der Dienst ist vorübergehend nicht verfügbar“ (HTTP 503) | In die Windows-Ereignisanzeige schauen (*Anwendung*, Quelle `.NET Runtime`, Text `An unhandled exception has occurred`). `Insufficient administrative privilege` bedeutet: Die Identität des App-Pools ist kein Citrix-Administrator, oder Rolle/Bereich erlauben das Lesen von Sitzungen nicht. Das gMSA in Studio unter *Administratoren* mit z. B. der Rolle *Help Desk Administrator* und dem Bereich *Alle* anlegen. |
| `Insufficient administrative privilege`, obwohl das Recht existiert (läuft als geplanter Task unter dem gMSA, scheitert im IIS) | Der laufende App-Pool hat noch ein altes Token, typischerweise nach einer Änderung der Gruppenmitgliedschaft. Das gMSA direkt als Citrix-Administrator mit Rolle eintragen (siehe Schritt 3) und den App-Pool vollständig neu starten. |
| „Something went wrong“ direkt nach dem Laden | Der Browser erreicht die API nicht. Die Seite über die exakte URL mit `/sss/` am Ende öffnen und im Netzwerk-Tab nachsehen. |
| Verlauf oder Qualität fehlen, Hinweis wird angezeigt | Der Monitor-OData-Dienst ist nicht erreichbar oder das Konto darf ihn nicht lesen; `Test-SssCitrix.ps1` ausführen. |
| `Test-SssCitrix.ps1` meldet „UserName must be in the format DOMAIN\username“ | Den Benutzer als `CORP\alice` übergeben. |
