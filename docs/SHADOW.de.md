# Fernunterstützung: eine Sitzung spiegeln

[English version](SHADOW.md)

Der Helpdesk (standardmäßig jede Stufe) kann direkt aus SSS eine Benutzersitzung ansehen und, mit Zustimmung des Benutzers, Maus und Tastatur übernehmen: der Knopf **Fernunterstützung** in der Benutzeransicht. Er nutzt das Spiegeln von Windows (`mstsc /shadow`), das auch bei Citrix-HDX-Sitzungen funktioniert.

## So funktioniert es

```
Browser (SSS) --1--> SSS-Server: Rechte, Grund, Besitzer der Sitzung, Audit-Eintrag "shadow"
      |                 liest die Windows-Sitzungsnummer auf dem VDA (JEA-Endpunkt)
      | <--2-- Link  sss-shadow://<vda>/<Sitzungsnummer>?control=1
      v
Starter auf dem Helpdesk-PC --3--> mstsc /v:<vda> /shadow:<Nr> /control  --> der Benutzer wird gefragt
```

1. SSS nimmt die Anfrage nur von Helpdesk-Rollen an, deren Stufe erlaubt ist (`Sss:Shadow:AllowLevel1/2/3`, standardmäßig alle an), mit dem Grund (wenn du ihn verlangst) und nur für eine **laufende Sitzung dieses Benutzers**. VDA und Sitzungsnummer kommen vom VDA über den nur lesenden JEA-Befehl `Get-SssHdx` (dafür braucht es den [JEA-Endpunkt](PROCESSES.de.md)). Hat der Benutzer mehrere Sitzungen auf dem VDA und SSS erkennt nicht, welche es ist, verweigert es, statt zu raten.
2. Die Antwort ist ein Link der Form `sss-shadow://<Host>/<Nr>?control=0|1`.
3. Ein kleiner **Starter** auf dem Helpdesk-PC bekommt den Link vom Browser, prüft ihn streng (nur Hostname und Zahlen) und startet `mstsc.exe /v:<Host> /shadow:<Nr> [/control]`. **Die Zustimmungsabfrage beim Benutzer bleibt immer an**; SSS verwendet nie `/noConsentPrompt`.

SSS vergibt **nicht** das Recht zum Spiegeln. Das entscheidet Windows auf dem VDA und fragt den Benutzer.

## Einrichten

1. **VDAs**: die Regeln zum Spiegeln. Per Gruppenrichtlinie *Regeln für die Remotesteuerung von Remotedesktopdienste-Benutzersitzungen festlegen* (Computerkonfiguration > Administrative Vorlagen > Windows-Komponenten > Remotedesktopdienste > Remotedesktopsitzungs-Host > Verbindungen) die Fernsteuerung mit Erlaubnis des Benutzers zulassen (*Vollzugriff mit Erlaubnis des Benutzers* oder *Sitzung anzeigen mit Erlaubnis des Benutzers*). Die Helpdesk-Gruppe braucht auf dem VDA das Recht zum Spiegeln (zum Beispiel über die Sitzungsberechtigungen des VDA). Teste es zuerst ohne SSS: `mstsc /v:<vda> /shadow:<Nr> /control`. Geht das, kann SSS es starten.
2. **VDAs**: das aktuelle `Install-SssJea.ps1` ausführen (der Endpunkt braucht `Get-SssHdx`, siehe [HDX](HDX.de.md)).
3. **SSS-Server**, `sss.config.json`:

   ```json
   { "Sss": { "Shadow": { "Enabled": true, "AllowLevel1": true, "AllowLevel2": true, "AllowLevel3": true, "Control": true }, "Citrix": { "Processes": { "Enabled": true } } } }
   ```

   Die Fernunterstützung ist standardmäßig an; mit `Enabled` oder einem `AllowLevel` auf `false` schaltest du sie ab, `Control: false` macht sie zum reinen Ansehen. Der Knopf braucht zusätzlich `Processes:Enabled`. **Starte die Anwendung neu** (Anwendungspool recyceln), nachdem du die Datei geändert hast. `https://<Server>/api/me` zeigt `"shadowState"`: `ok`, `disabled`, `processes-off`, `cloud`, `level-off` oder `no-role` sagt, warum ein Knopf fehlt.
4. **Jeder Helpdesk-PC** (einmal je Windows-Benutzer, keine Administratorrechte): `Register-SssShadow.ps1` und `Start-SssShadow.ps1` aus dem Release kopieren, dann

   ```powershell
   .\Register-SssShadow.ps1 -AllowedHostSuffix 'corp.local'
   ```

   `-AllowedHostSuffix` beschränkt den Starter auf VDAs deiner Domäne (empfohlen). `-Remove` macht alles rückgängig. Test ohne SSS: `.\Start-SssShadow.ps1 -DryRun "sss-shadow://vda01.corp.local/3?control=1"` zeigt den Befehl, den er ausführen würde.

Beim ersten Klick fragt der Browser, ob er den Link öffnen darf; „immer erlauben“ anhaken. Bei Edge oder Chrome kannst du es per Richtlinie für die SSS-Adresse erlauben (`AutoLaunchProtocolsFromOrigins`).

## Sicherheit

- Der Starter führt nur `mstsc.exe` aus und setzt nur den geprüften Hostnamen und Zahlen in dessen Befehlszeile. Andere Links akzeptiert er nicht, und mit dem erlaubten Host-Suffix verweigert er VDAs außerhalb deiner Domäne.
- Jede Webseite könnte versuchen, auf einem PC mit registriertem Starter einen `sss-shadow://`-Link zu öffnen. Im schlimmsten Fall fragt `mstsc` nach dem Spiegeln einer Sitzung in deiner Domäne; Windows verlangt dafür das Recht, und der Benutzer muss zustimmen. Lass das Host-Suffix gesetzt.
- Der Audit-Eintrag `shadow` hält fest, wer für welchen Benutzer und welche Sitzung auf welchem VDA und warum angefragt hat. Er zeigt die **Anfrage**; ob die Verbindung zustande kam und angenommen wurde, sieht man nur in den Ereignisprotokollen des VDA (Remotedesktopdienste).

## Grenzen

- Setzt Windows-PCs für den Helpdesk voraus (mstsc und der Registry-Linktyp).
- Citrix Cloud (schreibgeschützter Modus) hat keinen VDA-Zugriff; der Knopf wird dort nicht angeboten.
- Passiert nach dem Klick nichts, ist der Starter auf diesem PC nicht registriert (die Meldung in SSS weist darauf hin) oder der Browser hat den Link blockiert.
