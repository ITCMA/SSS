# Anwendungen und Prozesse beenden

[English](PROCESSES.md)

Benutzer sehen die Anwendungen und Prozesse ihrer eigenen Sitzungen und können sie schließen (zum Beispiel ein hängendes Excel), ohne sich abzumelden. Das Helpdesk kann dasselbe für die Sitzung jedes Benutzers. Die Funktion ist **standardmäßig aus** und braucht auf jedem VDA einen kleinen Endpunkt.

## So funktioniert es

Citrix bietet keinen unterstützten Broker- oder Monitor-Befehl, um einen einzelnen Prozess zu beenden (Director nutzt einen eigenen Kanal zum VDA). SSS verwendet deshalb **Just Enough Administration (JEA)**, einen eingeschränkten PowerShell-Endpunkt von Windows:

```
Browser --> SSS (IIS) --> PowerShell-Worker --WinRM--> JEA-Endpunkt "SSS.Processes" auf dem VDA
                          (Dienstkonto)                 Get-SssProcess / Stop-SssProcess
```

- Der JEA-Endpunkt bietet genau **drei Befehle**: `Get-SssProcess` und `Stop-SssProcess` für diese Funktion sowie den nur lesenden `Get-SssProfile` für [Profiltyp und Größe](DATA-SOURCES.de.md#profiltyp-und-größe) in der Expertenansicht. Das Dienstkonto kann darüber nichts anderes auf dem VDA ausführen.
- Er läuft als vorübergehender lokaler Administrator (ein *virtuelles Konto*, das nur für diesen Aufruf existiert); das Dienstkonto selbst braucht auf den VDAs keine Administratorrechte.
- **Nur die eigenen Prozesse des Benutzers werden angefasst.** Beide Befehle erhalten die SID des Benutzers und vergleichen sie mit dem Besitzer jedes Prozesses; Systemprozesse, Prozesse anderer Benutzer und unbekannte IDs werden abgelehnt (`not-owned`, `not-found`). Die Citrix-Sitzungsprozesse `wfshell.exe`, `ctxgfx.exe` und `cmstart.exe` sind geschützt (`protected`).
- **SSS prüft die Sitzung doppelt.** Die Webanwendung akzeptiert eine Sitzung nur, wenn sie eine Live-Sitzung des Ziels ist; der PowerShell-Worker sucht sie noch einmal im Broker und lehnt sie ab, wenn sie nicht diesem Benutzer gehört. Nur der Rechner dieser Sitzung wird kontaktiert.
- Jeder Aufruf wird **protokolliert** (`list-processes`, `end-process`, mit dem Helpdesk-Handelnden, der Prozess-ID und ob erzwungen), und JEA schreibt auf dem VDA ein eigenes **Transkript**.

## Einrichten

1. **Auf jedem VDA** (oder im Master-Image oder per Startskript): `Install-SssJea.ps1` aus dem Release als Administrator ausführen.

   ```powershell
   .\Install-SssJea.ps1 -Principal 'CORP\svc-sss$'
   ```

   (Mehrere Konten sind möglich: `-Principal 'a','b'`.) Mit `-AllowedFrom sss01.corp.local` (Hostnamen oder IP-Bereiche deiner SSS-Server) beschränkst du die Firewallregel für WinRM auf diese; ohne den Schalter darf jeder Rechner, der den VDA erreicht, es versuchen. Das Skript aktiviert WinRM (TCP 5985), registriert den Endpunkt `SSS.Processes` für genau die genannten Konten und schreibt die Transkripte nach `C:\ProgramData\SSS\jea-transcripts` (nur für Administratoren und das System lesbar). Sonst ändert es nichts. `-ConfigurationName` und `-TranscriptDirectory` ändern die Standardwerte. `.\Install-SssJea.ps1 -Remove` hebt die Registrierung des Endpunkts wieder auf.

2. **Netzwerkweg öffnen:** TCP 5985 (WinRM über Kerberos, auf Nachrichtenebene verschlüsselt) vom SSS-Server zu den VDAs.

3. **Auf dem SSS-Server** in `sss.config.json`:

   ```json
   { "Sss": { "Citrix": { "Processes": { "Enabled": true } } } }
   ```

   Weitere Schlüssel: `Configuration` (Endpunktname, Standard `SSS.Processes`), `Audience` (`All` oder `Helpdesk`), `TimeoutSeconds` (Standard 60). Siehe die [Konfigurationsreferenz](CONFIGURATION.de.md#prozesse-ssscitrixprocesses). App-Pool neu starten.

4. **Testen** (es wird nichts beendet):

   ```powershell
   .\Test-SssCitrix.ps1 -Controller ddc01 -UserName CORP\alice -Vda vda01.corp.local
   ```

   Das Skript verbindet sich mit dem Endpunkt, listet die Prozesse des Benutzers auf und prüft, dass eine nicht vorhandene Prozess-ID abgelehnt wird. Nur die mit `-Principal` genannten Konten dürfen sich verbinden. Führe es deshalb als Dienstkonto aus (`runas`), oder nimm für den Test zusätzlich dein eigenes Administratorkonto auf und entferne es danach, indem du das Skript nur mit dem Dienstkonto erneut ausführst:

   ```powershell
   .\Install-SssJea.ps1 -Principal 'CORP\svc-sss$','CORP\admin.alice'   # für den Test
   .\Install-SssJea.ps1 -Principal 'CORP\svc-sss$'                      # danach
   ```

   „Access is denied“ im Test heißt, dass das ausführende Konto nicht in dieser Liste steht.

## Verwenden

Die **Expertenansicht** einschalten (Knopf im Kopf). Jede Live-Sitzung hat nun neben *Details* einen Knopf **Prozesse**. Er öffnet eine Liste der Prozesse des Benutzers auf dem Rechner dieser Sitzung:

- Standardmäßig werden nur **Anwendungen** gezeigt (Filter im Dialog); ohne Haken erscheinen auch System- und Hintergrundprozesse. Der Endpunkt läuft in Sitzung 0, wo Windows keine Fenstertitel anderer Sitzungen meldet; „Anwendung“ ist deshalb eine Näherung anhand des Programmpfads: alles außerhalb des Windows-Ordners und der Citrix-/FSLogix-Infrastruktur sowie bekannte Windows-Programme wie Explorer oder Notepad.
- **Schließen** fordert die Anwendung auf, ihr Hauptfenster zu schließen, wie ein Klick auf das X, damit der Benutzer noch speichern kann. Das geht nur, wo Windows dem Endpunkt das Fenster zeigt; bei einer anderen Sitzung kommt meist „kein Fenster“, und **Sofort beenden** ist der Weg.
- **Sofort beenden** beendet den Prozess hart. Nicht gespeicherte Arbeit geht verloren. Beides fragt vorher nach.
- Das Helpdesk findet denselben Knopf, wenn es einen Benutzer geöffnet hat (Reiter Helpdesk).

Auf einem Multi-Session-Host (RDS-Server) zeigt die Liste die Prozesse des Benutzers dieses Rechners; hat er dort mehrere Sitzungen, erscheinen alle.

## Endpunkt aktualisieren

Neue SSS-Versionen können dem Endpunkt Befehle hinzufügen (Version 0.4 brachte `Get-SssProfile`). Nach einem solchen Update `Install-SssJea.ps1 -Principal ...` auf den VDAs erneut ausführen; es ersetzt die Registrierung. Bis dahin funktionieren die übrigen Funktionen weiter, und der Profil-Knopf weist darauf hin, dass der Endpunkt veraltet ist.

## Was schiefgehen kann

| Meldung oder Ergebnis | Ursache |
|---|---|
| „Der Rechner der Sitzung ist für die Prozesssteuerung nicht erreichbar“ | WinRM blockiert, der Endpunkt ist auf diesem VDA nicht registriert, oder der VDA ist ein anderer Rechner als erwartet. Mit `Test-SssCitrix.ps1 -Vda` und `Get-PSSessionConfiguration SSS.Processes` auf dem VDA prüfen |
| `Access is denied` im Test | das Konto ist nicht der `-Principal` von `Install-SssJea.ps1` |
| `no-window` | der Prozess hat kein Fenster zum Schließen; mit *Sofort beenden*, wenn er enden muss |
| `not-closed` | die Anwendung hat die Schließen-Aufforderung ignoriert (zum Beispiel ist ein Dialog offen) |
| `protected` | ein Citrix-Sitzungsprozess; ihn zu beenden würde die Sitzung zerstören |
| `not-owned` / `not-found` | der Prozess ist schon beendet oder gehört nicht dem Benutzer |
| Der Knopf fehlt | Funktion aus, Zielgruppe `Helpdesk`, Expertenansicht aus, oder die Sitzung ist nicht live |

## Was das freigibt und was nicht

Der Endpunkt ist die einzige neue Tür auf dem VDA. Was jemand, der das SSS-Dienstkonto kontrolliert, darüber tun könnte, und was ihn bremst:

| Fähigkeit | Grenze |
|---|---|
| Prozesse, Fenstertitel und Programmpfade der Sitzung eines Benutzers auflisten | nur lesend; eine Benutzer-SID muss übergeben werden; nur dessen Prozesse werden geliefert |
| einen Prozess beenden | nur Prozesse, die der übergebenen SID gehören; nie Systemprozesse; nie `wfshell`, `ctxgfx`, `cmstart`; es lassen sich keine Befehle oder Skripte senden |
| Profiltyp, Größen, Ordnernamen, FSLogix- und UPM-Einstellungen lesen | nur lesend; der **Inhalt** von Dateien wird nie gelesen, nur Namen und Größen |
| alles andere auf dem VDA | nicht möglich: Die Sitzung ist auf diese drei Befehle beschränkt und läuft als vorübergehendes Konto, das nur für den Aufruf existiert |

Das verbleibende Risiko ehrlich benannt: Wer das SSS-Dienstkonto besitzt, kann auf den VDAs mit dem Endpunkt die Prozesse **jedes** Benutzers beenden (ein Denial of Service) sowie Prozess- und Ordnernamen lesen. Code ausführen, Dateien lesen oder das System verändern kann er darüber nicht. Die Angriffsfläche verkleinerst du mit:

- `-AllowedFrom` auf dem VDA, damit sich nur der oder die SSS-Server verbinden können;
- `Sss:Citrix:Processes:Audience = Helpdesk`, wenn normale Benutzer keine Prozesse selbst beenden sollen;
- einer **gMSA** als Dienstkonto (kein Kennwort zum Stehlen) und einer kleinen, beobachteten Helpdesk-Gruppe;
- Testkonten nach dem Test wieder aus `-Principal` zu nehmen;
- den JEA-Transkripten und dem Audit-Protokoll von SSS (`end-process`, `profile-info`) in eurem Log-System.

## Sicherheitshinweise

- Diese Funktion gibt dem SSS-Dienstkonto einen Weg zu den VDAs. Beschränke ihn auf die drei gelieferten Befehle; erweitere die Rollenfähigkeit nicht.
- Die JEA-Transkripte zeigen jeden Aufruf. Sammle sie mit euren üblichen Werkzeugen ein, wenn du neben der Audit-Datei von SSS einen zweiten Nachweis brauchst.
- Die Zielgruppe `Helpdesk` nimmt die Funktion normalen Benutzern weg und lässt sie dem Support.
- Prozessnamen und Fenstertitel eines Benutzers können Privates enthalten. Sie werden nur diesem Benutzer und dem Helpdesk gezeigt und nicht im Audit-Protokoll gespeichert (dort steht nur die Prozess-ID).
