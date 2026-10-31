# Anwendungen und Prozesse beenden

[English](PROCESSES.md)

Benutzer sehen die Anwendungen und Prozesse ihrer eigenen Sitzungen und können sie schließen (zum Beispiel ein hängendes Excel), ohne sich abzumelden. Das Helpdesk kann dasselbe für die Sitzung jedes Benutzers. Die Funktion ist **standardmäßig aus** und braucht auf jedem VDA einen kleinen Endpunkt.

## So funktioniert es

Citrix bietet keinen unterstützten Broker- oder Monitor-Befehl, um einen einzelnen Prozess zu beenden (Director nutzt einen eigenen Kanal zum VDA). SSS verwendet deshalb **Just Enough Administration (JEA)**, einen eingeschränkten PowerShell-Endpunkt von Windows:

```
Browser --> SSS (IIS) --> PowerShell-Worker --WinRM--> JEA-Endpunkt "SSS.Processes" auf dem VDA
                          (Dienstkonto)                 Get-SssProcess / Stop-SssProcess
```

- Der JEA-Endpunkt bietet genau **zwei Befehle**. Das Dienstkonto kann darüber nichts anderes auf dem VDA ausführen.
- Er läuft als vorübergehender lokaler Administrator (ein *virtuelles Konto*, das nur für diesen Aufruf existiert); das Dienstkonto selbst braucht auf den VDAs keine Administratorrechte.
- **Nur die eigenen Prozesse des Benutzers werden angefasst.** Beide Befehle erhalten die SID des Benutzers und vergleichen sie mit dem Besitzer jedes Prozesses; Systemprozesse, Prozesse anderer Benutzer und unbekannte IDs werden abgelehnt (`not-owned`, `not-found`). Die Citrix-Sitzungsprozesse `wfshell.exe`, `ctxgfx.exe` und `cmstart.exe` sind geschützt (`protected`).
- **SSS prüft die Sitzung doppelt.** Die Webanwendung akzeptiert eine Sitzung nur, wenn sie eine Live-Sitzung des Ziels ist; der PowerShell-Worker sucht sie noch einmal im Broker und lehnt sie ab, wenn sie nicht diesem Benutzer gehört. Nur der Rechner dieser Sitzung wird kontaktiert.
- Jeder Aufruf wird **protokolliert** (`list-processes`, `end-process`, mit dem Helpdesk-Handelnden, der Prozess-ID und ob erzwungen), und JEA schreibt auf dem VDA ein eigenes **Transkript**.

## Einrichten

1. **Auf jedem VDA** (oder im Master-Image oder per Startskript): `Install-SssJea.ps1` aus dem Release als Administrator ausführen.

   ```powershell
   .\Install-SssJea.ps1 -Principal 'CORP\svc-sss$'
   ```

   Es aktiviert WinRM (TCP 5985), registriert den Endpunkt `SSS.Processes` für genau dieses Konto und schreibt die Transkripte nach `C:\ProgramData\SSS\jea-transcripts`. Sonst ändert es nichts. `-ConfigurationName` und `-TranscriptDirectory` ändern die Standardwerte. `.\Install-SssJea.ps1 -Remove` hebt die Registrierung des Endpunkts wieder auf.

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

   Das Skript verbindet sich mit dem Endpunkt, listet die Prozesse des Benutzers auf und prüft, dass eine nicht vorhandene Prozess-ID abgelehnt wird. Führe es möglichst als Dienstkonto aus (`runas`), sonst mit deinem eigenen Konto, nachdem du es über `-Principal` in die JEA-Rolle aufgenommen hast.

## Verwenden

Die **Expertenansicht** einschalten (Knopf im Kopf). Jede Live-Sitzung hat nun neben *Details* einen Knopf **Prozesse**. Er öffnet eine Liste der Prozesse des Benutzers auf dem Rechner dieser Sitzung:

- Standardmäßig werden nur **Anwendungen mit Fenster** gezeigt (Filter im Dialog); ohne Haken erscheinen auch Hintergrundprozesse.
- **Schließen** fordert die Anwendung auf, ihr Hauptfenster zu schließen, wie ein Klick auf das X. Nicht gespeicherte Arbeit kann der Benutzer noch sichern. Anwendungen ohne Fenster antworten „kein Fenster“.
- **Sofort beenden** beendet den Prozess hart. Nicht gespeicherte Arbeit geht verloren. Beides fragt vorher nach.
- Das Helpdesk findet denselben Knopf, wenn es einen Benutzer geöffnet hat (Reiter Helpdesk).

Auf einem Multi-Session-Host (RDS-Server) zeigt die Liste die Prozesse des Benutzers dieses Rechners; hat er dort mehrere Sitzungen, erscheinen alle.

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

## Sicherheitshinweise

- Diese Funktion gibt dem SSS-Dienstkonto einen Weg zu den VDAs. Beschränke ihn auf die beiden gelieferten Befehle; erweitere die Rollenfähigkeit nicht.
- Die JEA-Transkripte zeigen jeden Aufruf. Sammle sie mit euren üblichen Werkzeugen ein, wenn du neben der Audit-Datei von SSS einen zweiten Nachweis brauchst.
- Die Zielgruppe `Helpdesk` nimmt die Funktion normalen Benutzern weg und lässt sie dem Support.
- Prozessnamen und Fenstertitel eines Benutzers können Privates enthalten. Sie werden nur diesem Benutzer und dem Helpdesk gezeigt und nicht im Audit-Protokoll gespeichert (dort steht nur die Prozess-ID).
