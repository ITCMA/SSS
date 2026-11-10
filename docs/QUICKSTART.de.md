# Schnellstart

[English version](QUICKSTART.md)

Was wo installiert wird, mit welchen Parametern, und eine solide Grundkonfiguration. Alle Namen unten (`corp.local`, `CORP\...`, `ddc01`) sind Platzhalter: ersetze sie durch deine eigenen. Details zu jedem Schritt stehen in der [Einrichtungsanleitung](SETUP.de.md).

## Was kommt wohin

| # | Wo | Was | Befehl | Nötig für |
|---|----|-----|--------|-----------|
| 1 | Active Directory | gMSA und Sicherheitsgruppen | siehe [Einrichtungsanleitung](SETUP.de.md) | alles |
| 2 | **IIS-Server** (z. B. der Web-Studio-Server) | SSS selbst | `.\install.ps1 -Interactive` | alles |
| 3 | **IIS-Server** | `sss.config.json` bearbeiten, Anwendungspool recyceln | siehe [Grundkonfiguration](#grundkonfiguration) | alles |
| 4 | **Jeder VDA** (oder das Master-Image) | eingeschränkter PowerShell-Endpunkt (JEA) | `.\Install-SssJea.ps1 -Principal 'CORP\svc-sss$' -AllowedFrom sss01.corp.local` | Prozesse, Profil, Richtlinien, **HDX-Tab**, **Fernunterstützung** |
| 5 | **Jeder Helpdesk-PC**, einmal je Windows-Benutzer | Starter für die Fernunterstützung | `.\Register-SssShadow.ps1 -AllowedHostSuffix 'corp.local'` | Knopf Fernunterstützung |
| 6 | VDAs, per Gruppenrichtlinie | Shadowing mit Zustimmung des Benutzers erlauben | siehe [Fernunterstützung](SHADOW.de.md) | Fernunterstützung |

> ### Schritt 5 nicht vergessen
> **`Register-SssShadow.ps1 -AllowedHostSuffix '<deine Domäne>'` muss auf jedem Helpdesk-PC von jedem Windows-Benutzer ausgeführt werden, der die Fernunterstützung nutzt.** Ohne das ist der Knopf sichtbar, aber der Klick bewirkt nichts. Das Suffix beschränkt den Starter auf VDAs deiner eigenen Domäne: immer setzen. Test ohne SSS: Win+R, dann `sss-shadow://<vda>.<deine Domäne>/<Sitzungs-ID>?control=1`; `mstsc` muss sich öffnen.

## Aktualisieren

| Wo | Befehl |
|----|--------|
| IIS-Server | `.\install.ps1 -Update`, danach Anwendungspool recyceln |
| VDAs (nach einem Release, das den Endpunkt ändert, siehe [Changelog](../CHANGELOG.md)) | `.\Install-SssJea.ps1 -Principal 'CORP\svc-sss$' -AllowedFrom sss01.corp.local` erneut |
| Helpdesk-PCs | neues `Start-SssShadow.ps1` kopieren, `Register-SssShadow.ps1 -AllowedHostSuffix '<deine Domäne>'` erneut ausführen |

## Grundkonfiguration

Ein solider Start für `sss.config.json` (neben der Anwendung; nach jeder Änderung den Anwendungspool recyceln, dann mit `.\Test-SssConfig.ps1` prüfen):

```json
{
  "$schema": "docs/sss.config.schema.json",
  "Sss": {
    "Provider": "Citrix",
    "Citrix": {
      "Controllers": [ "ddc01.corp.local", "ddc02.corp.local" ],
      "Processes": { "Enabled": true }
    },
    "Roles": {
      "Level1Group": "CORP\\SSS-Helpdesk-L1",
      "Level2Group": "CORP\\SSS-Helpdesk-L2",
      "Level3Group": "CORP\\SSS-Helpdesk-L3",
      "AuditorGroup": "CORP\\SSS-Auditors",
      "AdminGroup": "CORP\\SSS-Admins"
    }
  }
}
```

Das Beispiel zeigt nur, was du setzen musst. Alles andere hat einen Standard. Die Fernunterstützung (`Sss:Shadow`) ist **standardmäßig für alle drei Helpdesk-Stufen an** und braucht keinen Eintrag; trage sie nur ein, um etwas abzuschalten, z. B. `"Shadow": { "AllowLevel1": false }` oder `"Control": false` für nur ansehen. `StoreFrontUrl` ist optional (sie fügt nur einen Link „StoreFront öffnen" in die Oberfläche ein) und fehlt hier.

Empfehlungen:

- SSS unter einem **gMSA** betreiben, mit schreibgeschützter Citrix-Rolle plus dem Recht, Sitzungen abzumelden; nie unter einem persönlichen Konto.
- **Helpdesk-Gruppen klein halten** und die Stufen trennen (L1 ansehen, L2 handeln, L3 tief). Dazu eine Auditor- und eine Admin-Gruppe.
- **Fernunterstützung eingeschaltet lassen** (Standard) und einzelne Stufen bei Bedarf mit `AllowLevel1/2/3` abschalten. Der Benutzer wird immer um Zustimmung gefragt.
- `ReasonRequired` setzen, damit jede Helpdesk-Aktion einen Grund im Audit-Protokoll hat.
- Auf den VDAs `-AllowedFrom` verwenden, damit nur der SSS-Server den Endpunkt erreicht.
- Das Audit-Protokoll auf eine Freigabe schreiben (`-AuditShare` oder `Sss:Audit:Sinks`), damit es einen Serververlust übersteht.
- Nach der Installation `Test-SssConfig.ps1`, `Test-SssSetup.ps1` und `Test-SssCitrix.ps1` ausführen; sie nennen für jeden Befund den passenden Befehl.

## Prüfen, ob es läuft

`https://<SSS-Server>/sss/api/me` (auf dem SSS-Server in einem Browser öffnen, in dem du angemeldet bist) zeigt deine Rolle und für die Fernunterstützung `"shadow": true` und `"shadowState": "ok"`. Jeder andere Wert bei `shadowState` nennt den Grund, warum der Knopf fehlt (siehe [Fernunterstützung](SHADOW.de.md)).

## Eigene Daten gehören nicht ins Repository

Echte Domänen-, Server- und Benutzernamen, Sitzungen und Konfiguration gehören nur auf deine Server, nie in Issues, Pull Requests, Dokumentation, Screenshots oder das Repository. Die Screenshots dieses Projekts stammen aus dem Demo-Modus mit erfundenen Daten.
