# Betrieb

[English](OPERATIONS.md)

Alles, was ein Administrator nach der ersten Installation tut: aktualisieren, prüfen, überwachen, das Audit-Protokoll einsammeln, Probleme finden.

## Installieren, aktualisieren, entfernen

| Aufgabe | Befehl (PowerShell als Administrator auf dem IIS-Server) |
|---|---|
| Erstinstallation, geführt | `.\install.ps1 -Interactive` |
| Erstinstallation, unbeaufsichtigt | `.\install.ps1 -Controllers ddc01.corp.local -ServiceAccount 'CORP\svc-sss$' -HelpdeskGroup 'CORP\SSS-Helpdesk'` |
| Auf ein neues Release aktualisieren | `.\install.ps1 -Update` |
| Aktualisieren und die Health-Probe öffnen | `.\install.ps1 -Update -HealthEndpoint` |
| IIS-Anwendung und App-Pool entfernen | `.\install.ps1 -Uninstall` |
| Vorschau auf eines davon | `-WhatIf` anhängen |

`-Update` kopiert die neuen Dateien und behält `sss.config.json`, den App-Pool, die Berechtigungen und das Audit-Protokoll. Die vorherige Version bleibt in `<Installationsordner>.previous`. Antwortet die neue Version dem Smoke-Test nicht, stellt der Installer die vorherige selbst wieder her. `-Uninstall` behält Dateien, Konfiguration und Audit-Protokolle; lösche den Ordner selbst, wenn du sie nicht mehr brauchst.

Ein Release vor der Installation prüfen: Jedes Release hat neben dem Zip eine `.sha256`-Datei (`Get-FileHash .\sss-vX.Y.Z-win-x64.zip` muss denselben Wert zeigen).

Nach jeder Installation und jedem Update beide Prüfungen ausführen (sie lesen nur und ändern nichts):

```powershell
.\Test-SssSetup.ps1                                       # IIS, App-Pool, Konten, Konfiguration, die Anwendung selbst
.\Test-SssCitrix.ps1 -Controller ddc01 -UserName CORP\alice   # Broker, Monitor, Feldnamen (alice braucht eine Sitzung)
```

Vor dem Einfügen der Ausgabe in ein öffentliches Issue bei beiden Skripten `-Redact` anhängen: Es ersetzt Benutzer, Domäne, Hostnamen, SIDs, IP-Adressen und GUIDs.

## Status-Seite

Der Reiter **Status** (nur Helpdesk-Mitglieder) führt dieselben Prüfungen wie `Test-SssSetup.ps1` innerhalb der Anwendung aus:

| Prüfung | Bedeutung |
|---|---|
| version, provider, identity, uptime | welcher Build läuft, welche Datenquelle, mit welchem Konto, seit wann |
| audit | der Audit-Ordner ist für die App-Pool-Identität beschreibbar |
| config | keine unbekannten Schlüssel in `sss.config.json` (sonst werden sie aufgelistet) |
| helpdesk | die Helpdesk-Gruppe wird aufgelöst |
| broker | ein PowerShell-Worker antwortet (Zeit in ms) |
| monitor | der Monitor-OData-Dienst antwortet (Zeit in ms) |
| monitor-https | der Monitor wird über HTTPS angesprochen (sonst eine Warnung) |

Die API dahinter ist `GET /api/helpdesk/status` (nur Helpdesk).

## Health-Probe für Load Balancer

`GET /<Anwendung>/healthz` ist ein kleiner Endpunkt für Load Balancer, NetScaler/ADC-Monitore und Verfügbarkeitsprüfungen.

| Antwort | Bedeutung |
|---|---|
| `200 {"status":"ok"}` | der PowerShell-Worker erreicht einen Controller und der Monitor antwortet |
| `200 {"status":"degraded"}` | Sitzungen und Abmelden funktionieren, der Monitor antwortet aber nicht (Verlauf und Qualität fehlen). Knoten im Betrieb lassen |
| `503 {"status":"unavailable"}` | keine Broker-Verbindung: der Knoten kann Sitzungen weder auflisten noch abmelden. Aus der Rotation nehmen |

- Er verrät nichts weiter: keine Version, keine Namen, keine Details. Für Details dient der Status-Reiter.
- Das Ergebnis wird 10 Sekunden zwischengespeichert, häufiges Abfragen belastet den Controller also nicht. Der Endpunkt ist ratenbegrenzt (120 Anfragen pro Minute).
- Die IIS-Einstellungen der Anwendung stehen in der gelieferten `web.config` (Handler außerhalb eines `location path="."`-Blocks), die Zusatzregel für `/healthz` erbt sie also.
- Er braucht **keine Anmeldung**, der IIS verlangt aber eine für die ganze Anwendung. `install.ps1 -HealthEndpoint` (auch zusammen mit `-Update`) öffnet genau den Pfad `<Anwendung>/healthz` für anonymen Zugriff, sonst nichts, und testet ihn. Zum Rückgängigmachen entfernt `install.ps1 -Uninstall` die Regel; von Hand: `Clear-WebConfiguration -Filter /system.webServer/security/authentication -PSPath MACHINE/WEBROOT/APPHOST -Location "Default Web Site/sss/healthz"`.
- Ohne diese Regel bekommt eine Probe `401`. Viele Load Balancer werten `401` als „lebt“ (es beweist, dass IIS und App-Pool antworten), bemerken dann aber keine verlorene Broker-Verbindung. Nimm die Regel, wenn du echte Gesundheitsprüfungen willst.
- Solange die Datei `data\drain.flag` existiert, antwortet die Probe `503 {"status":"draining"}`, ein Load Balancer nimmt den Server also vor einem Update heraus ([Ausleiten](MULTI-SERVER.de.md#ausleiten)).
- Den Endpunkt in der Anwendung abschalten: `Sss:Health:Enabled = false`.
- `Test-SssSetup.ps1` ruft den Pfad ohne Anmeldedaten auf und sagt dir, ob eine Probe Erfolg hätte.

Beispiel für einen Monitor, der `200` und den Text `ok` erwartet: Probe `https://sss.corp.local/sss/healthz`, erwartete Antwort `"status":"ok"`.

## Audit-Protokoll

Jede Aktion, die etwas ändert oder fremde Daten ansieht, wird nach `logs\audit-YYYYMMDD.jsonl` geschrieben (mit Hash-Kette, wer, an wem, wann, von wo, welcher Server, Ticket oder Grund), an zentrale Ziele kopiert und im Reiter **Audit** (Rolle *Auditor*) ausgewertet. Felder, Aktionen, Ziele (UNC, Syslog/CEF, HTTP, SNMP), Integritätsprüfung, Aufbewahrung und Datenschutz: [Audit-Protokoll und zentrales Logging](AUDIT.de.md). Rollen: [Rollen](ROLES.de.md).

## Sichern und Wiederherstellen

Der Zustand sind `sss.config.json`, `sss.local.json` (falls genutzt) und der Ordner `logs`; der Ordner `data` enthält nur einen Zwischenspeicher und Weiterleitungs-Positionen (ihn zu verlieren ist harmlos: ein Ziel beginnt dann mit neuen Ereignissen). Sichere diese (und das installierte Release-Zip). Wiederherstellen heißt dasselbe Release erneut installieren und sie zurückkopieren. `install.ps1 -Update` hinterlässt eine Rückfallkopie in `<Installationsordner>.previous`.

## Protokolle und Fehlersuche

| Symptom | Wo nachsehen |
|---|---|
| 500.19 / 502.5 direkt nach der Installation | Windows-Ereignisprotokoll, Quelle *IIS AspNetCore Module V2*; meist fehlt das Hosting Bundle oder `sss.config.json` ist ungültig (eine Prüfmeldung wird protokolliert) |
| Anmeldedialog kommt immer wieder | Windows-Authentifizierung an, anonym aus für die Anwendung; die Site liegt in der Browserzone *Lokales Intranet*; bei einem Alias: SPN und `useAppPoolCredentials` ([Mehrere Controller und Server](MULTI-SERVER.de.md)) |
| Seite lädt, keine Sitzungen, Hinweis „nicht verfügbar“ | Status-Reiter: `broker` schlägt fehl. Die Meldung nennt das Konto, unter dem der Worker läuft; dieses Konto braucht den Citrix-Administratoreintrag |
| Verlauf oder Qualität fehlen | Status: `monitor` schlägt fehl. Ein `404` über HTTPS heißt, dass der Monitor-Dienst nur über `http://` antwortet ([Monitor über HTTPS](MULTI-SERVER.de.md#monitor-über-https)) |
| Status: `config` warnt | ein Schlüssel in `sss.config.json` steht auf der falschen Ebene oder ist falsch geschrieben; die Warnung nennt ihn und schlägt die richtige Stelle vor |
| Helpdesk-Reiter fehlt | die Gruppen in `Sss:Roles` (oder die ältere `Sss:Helpdesk:Group`) sind leer, wird nicht aufgelöst (Status: `helpdesk`), oder der Benutzer muss sich nach der Aufnahme in die Gruppe neu anmelden |
| Knopf „Prozesse“ fehlt | `Sss:Citrix:Processes:Enabled` ist false, die Zielgruppe ist `Helpdesk`, oder die Expertenansicht ist aus |
| Browserkonsole meldet CSP-Verstöße | ein Reverse Proxy ändert Header; SSS sendet eine eigene `Content-Security-Policy` und erwartet, dass sie unverändert bleibt |

Brauchst du die Konsolenausgabe der Anwendung, setze `stdoutLogEnabled="true"` in `web.config` vorübergehend (Ordner `logs\stdout`), recycle den App-Pool, stelle das Problem nach und schalte es wieder aus.

## Härtungs-Checkliste

- SSS ausschließlich über HTTPS veröffentlichen. Das CSRF-Cookie ist `Secure`, `HttpOnly` und `SameSite=Strict`; HSTS wird außerhalb der Entwicklung gesendet.
- Die Helpdesk-Gruppe klein halten: Ihre Mitglieder sehen die Sitzungen aller Benutzer.
- Eine gMSA statt eines Kontos mit Kennwort verwenden und eine eigene Citrix-Rolle, sobald klar ist, was gebraucht wird.
- Der App-Pool-Identität Lesezugriff auf den Anwendungsordner und Ändern nur auf `logs` geben (das erledigt der Installer).
- `Auth:Mode` auf `Iis` lassen (der Dev-Modus wird außerhalb der Entwicklungsumgebung ohnehin abgelehnt).
- Das Audit-Protokoll weiterleiten und auf `rejected-...`-Ergebnisse und ungewöhnliche `end-process`-Aktivität achten.
- Regelmäßig aktualisieren; die Sicherheitshinweise des Repositorys beobachten (siehe [SECURITY.md](../SECURITY.md)).
