# Berechtigungen auf einen Blick

[English version](PERMISSIONS.md)

SSS braucht nur wenige Rechte, und nur ein Konto trägt Citrix-Rechte: das **Dienstkonto** des App-Pools. Endbenutzer und Helpdesk-Mitarbeiter bekommen über SSS keine Citrix-Rechte.

## Wer braucht was

| Wer | Recht | Wo | Wozu | Gesetzt durch |
|---|---|---|---|---|
| **Dienstkonto** (gMSA, z. B. `CORP\svc-sss$`) | ist die Identität des App-Pools | IIS | SSS spricht mit Citrix unter diesem Konto | `install.ps1 -ServiceAccount` |
| | darf sein Kennwort auf dem IIS-Server abrufen | Active Directory | gMSA-Technik | `New-ADServiceAccount ... -PrincipalsAllowedToRetrieveManagedPassword` |
| | **Citrix-Administrator** mit Rolle *Help Desk Administrator*, Bereich *Alle* | Delivery Controller (Delegierte Verwaltung) | Sitzungen lesen (Broker), abmelden, Nachrichten senden, Monitor-Daten lesen | `New-AdminAdministrator` + `Add-AdminRight` |
| | Lesen im Anwendungsordner, **Ändern** in `logs` | Dateisystem des IIS-Servers | Audit-Log | `install.ps1` (icacls) |
| | Netzwerkzugriff zum Controller (Broker-SDK, Monitor-OData, meist TCP 80/443) | Firewall | Abfragen | Netzwerk-Team |
| **Endbenutzer** | darf die SSS-URL erreichen (Windows-Authentifizierung) | IIS | Anmeldung | nichts zu tun: alle Domänenbenutzer |
| | keine Citrix-Rechte | - | Benutzer sehen und beeinflussen nur ihre eigenen Sitzungen; das erzwingt SSS | - |
| **Helpdesk** | Mitglied der AD-Gruppe aus `Sss:Helpdesk:Group` | Active Directory | Benutzer finden, Sitzungen sehen und abmelden, Nachrichten senden, Experten-Ansicht | `New-ADGroup` + `Add-ADGroupMember`, danach neu anmelden |
| **Installierender Administrator** | lokaler Administrator auf dem IIS-Server | IIS-Server | `install.ps1` | - |
| | Domänen-Admin (einmalig) | Active Directory | KDS-Stammschlüssel und gMSA | - |
| | voller Citrix-Administrator (einmalig) | Delivery Controller | Administratoreintrag anlegen | - |

Helpdesk-Mitglieder brauchen keine eigene Citrix-Rolle: Der Helpdesk handelt über das Dienstkonto. Die Helpdesk-Gruppe klein halten, denn ihre Mitglieder sehen die Sitzungen aller Benutzer.

## Alle Schritte in einem Block

```powershell
# --- Active Directory (Domänen-Admin) --------------------------------------------------------
Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))      # nur, wenn die Domäne noch keinen KDS-Stammschlüssel hat
New-ADServiceAccount -Name svc-sss -DNSHostName svc-sss.corp.local `
  -PrincipalsAllowedToRetrieveManagedPassword 'IIS-SERVER$'
New-ADGroup -Name SSS-Helpdesk -GroupScope Global -GroupCategory Security   # optional
Add-ADGroupMember SSS-Helpdesk -Members helpdesk.user1, helpdesk.user2

# --- IIS-Server (PowerShell als Administrator) -----------------------------------------------
Install-ADServiceAccount svc-sss
Test-ADServiceAccount svc-sss                                  # muss True liefern

# --- Delivery Controller (PowerShell als Administrator) --------------------------------------
Add-PSSnapin Citrix.DelegatedAdmin.Admin.V1
New-AdminAdministrator -Name 'CORP\svc-sss$' -Enabled $true
Add-AdminRight -Administrator 'CORP\svc-sss$' -Role 'Help Desk Administrator' -All

# --- IIS-Server: installieren und prüfen -----------------------------------------------------
.\install.ps1 -Interactive
.\Test-SssSetup.ps1                                            # nennt zu jedem Befund die Lösung
```

`Test-SssSetup.ps1` und der Reiter **Status** (Helpdesk) prüfen jede Zeile der Tabelle und nennen den genauen Befehl für das, was fehlt.

## Warum *Help Desk Administrator*?

Es ist die kleinste eingebaute Rolle, die abdeckt, was SSS tut: Sitzungen auflisten, abmelden, Nachrichten senden, Monitor-Daten lesen. Sie kann mehr (zum Beispiel Maschinen ein- und ausschalten). Im Produktivbetrieb eine eigene Rolle mit genau den nötigen Berechtigungen anlegen und stattdessen zuweisen. Ein kompromittierter SSS-Prozess darf alles, was das Dienstkonto darf, deshalb zählt die Rolle.

## Anwendungen und Prozesse beenden (optional)

Standardmäßig aus. Es braucht ein weiteres Recht, und zwar auf den **VDAs** statt auf dem Controller: Das Dienstkonto darf sich mit einem eingeschränkten PowerShell-Endpunkt (JEA, `SSS.Processes`) verbinden, der nur „Prozesse dieses Benutzers auflisten“ und „einen Prozess dieses Benutzers beenden“ anbietet. `scripts/Install-SssJea.ps1 -Principal 'CORP\svc-sss$'` richtet ihn auf einem VDA ein; weitere Rechte werden nicht vergeben und das Konto erhält keinen allgemeinen Zugriff auf den Rechner. Details: [Anwendungen und Prozesse beenden](PROCESSES.de.md).

| Wer | Recht | Wo | Warum | Gesetzt durch |
|---|---|---|---|---|
| Dienstkonto | darf den JEA-Endpunkt `SSS.Processes` nutzen | jeder VDA | eigene Prozesse des Benutzers auflisten und beenden | `Install-SssJea.ps1` |
| | Netzwerkzugriff auf WinRM (TCP 5985) | Firewall, VDAs | die Verbindung | Netzwerkteam |
