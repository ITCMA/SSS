# Rollen

[English](ROLES.md)

Jeder, der sich anmelden kann, sieht und beendet **seine eigenen** Sitzungen. Darüber kennt SSS fünf Rollen, jede an eine Active-Directory-Gruppe gebunden. Wer in einer höheren Helpdesk-Stufe ist, hat die niedrigeren mit. **Auditor** und **Admin** stehen daneben: Ein Helpdesk-Mitarbeiter sieht das Audit-Protokoll und die Systemseiten nur, wenn er diese Rolle zusätzlich hat.

| Rolle | Gruppen-Einstellung | Darf |
|---|---|---|
| Stufe 1 | `Sss:Roles:Level1Group` | ansehen: Benutzer suchen, deren Sitzungen, die Experten-Ansicht, die Farm-Übersicht und den Reiter [Insights](INSIGHTS.de.md) sehen. Ändert nichts. |
| Stufe 2 | `Sss:Roles:Level2Group` | Stufe 1, dazu Sitzungen abmelden und Nachrichten senden. |
| Stufe 3 | `Sss:Roles:Level3Group` | Stufe 2, dazu Prozesse und Profil anderer Benutzer lesen und Prozesse beenden (braucht den [JEA-Endpunkt](PROCESSES.de.md)). |
| Auditor | `Sss:Roles:AuditorGroup` | das [Audit-Protokoll](AUDIT.de.md) lesen, auswerten, exportieren und seine Integrität prüfen. Sonst nichts. |
| Admin | `Sss:Roles:AdminGroup` | der Reiter **Status** (Prüfungen, Delivery Controller, Audit-Ziele, Liste der SSS-Server, wirksame Konfiguration) und der Reiter **Umgebung** ([Client-Versionen und Standorte](ENVIRONMENT.de.md)). |

```json
"Roles": {
  "Level1Group": "CORP\\SSS-Helpdesk-L1",
  "Level2Group": "CORP\\SSS-Helpdesk-L2",
  "Level3Group": "CORP\\SSS-Helpdesk-L3",
  "AuditorGroup": "CORP\\SSS-Auditors",
  "AdminGroup": "CORP\\SSS-Admins"
}
```

- Die Gruppen werden beim Start in SIDs aufgelöst und mit den Gruppen im Windows-Token des Aufrufers verglichen (verschachtelte Gruppen zählen). Eine leere oder nicht auflösbare Gruppe gibt nichts frei (Status-Reiter und Log sagen es).
- **Bestehende Installationen laufen weiter.** `Sss:Helpdesk:Group` bedeutet weiterhin *alles von Stufe 3*. Ist `AdminGroup` leer, behält jeder Helpdesk-Mitarbeiter den Status-Reiter wie bisher. Das Audit-Protokoll gehört **nie** zu einer älteren Einrichtung: `AuditorGroup` setzen, um es zu nutzen.
- Die Oberfläche zeigt nur, was die Rollen erlauben, und der Server prüft jeden Aufruf noch einmal (ein API-Aufruf ohne das Recht bekommt `403`).
- Jede Audit-Zeile trägt die Rolle dessen, der gehandelt hat (`L2`, `L3+Auditor`, ...).

## Ticketnummer oder Grund (optional, auf Wunsch Pflicht)

Das Feld oben in der Benutzeransicht nimmt eine Ticketnummer oder einen beliebigen Grund auf; was eingetragen wird, steht im Audit-Protokoll. Standardmäßig ist es **optional**. Mit `Sss:Helpdesk:ReasonRequired` = `true` braucht jede Helpdesk-Aktion, die etwas ändert oder Prozesse und Profile liest, eine **Ticketnummer oder einen Grund**: Abmelden, Nachricht, Prozessliste, Profil, Prozess beenden. SSS prüft den Inhalt nicht (beliebiger Text bis 200 Zeichen), es stellt nur sicher, dass einer da ist, und schreibt ihn in das Audit-Protokoll. Der Helpdesk trägt ihn einmal im Feld oben in der Benutzeransicht ein. Suchen und Sitzungen ansehen braucht keinen. Wieder ausgeschaltet ist er optional.

## Hinweis bei Massenaktionen

Eine Helpdesk-Anfrage, die `Sss:Helpdesk:BulkAlertThreshold` Sitzungen oder mehr abmeldet (Standard 5), schreibt zusätzlich das Ereignis `alert:bulk-logoff`. Gib einem Chat- oder SNMP-[Audit-Ziel](AUDIT.de.md#ziele) den Filter `"Include": ["alert:"]`, dann werden die richtigen Leute sofort informiert.

## Entwicklungsmodus

Für Tests auf einem Entwicklerrechner nennt `Sss:Auth:DevRoles` die Rollen des Demo-Benutzers, zum Beispiel `"L2,Auditor"` (`L1`, `L2`, `L3`, `Auditor`, `Admin`). Es gilt nur mit `ASPNETCORE_ENVIRONMENT=Development`.
