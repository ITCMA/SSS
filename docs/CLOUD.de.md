# Citrix Cloud (DaaS)

[English](CLOUD.md)

SSS kann statt eines lokalen Delivery Controllers eine **Citrix-Cloud**-Umgebung lesen. Dieser erste Schritt ist **nur lesend** und **noch nicht gegen einen echten Mandanten getestet** (er folgt der dokumentierten Citrix-Cloud-API; bitte melde, was du siehst).

| Funktioniert | Funktioniert (noch) nicht |
|---|---|
| Eigene Sitzungen und Verlauf, Qualitäts-Ampeln, Expertenansicht | Abmelden, Nachrichten an eine Sitzung |
| Helpdesk-Liste, Insights, Karte, Umgebung (alles aus dem Monitor-Dienst der Cloud) | Prozesse, Profilanalyse, Richtlinien (sie brauchen den lokalen Broker und den JEA-Endpunkt auf den VDAs) |
| Status-Seite (Monitor-Prüfung) | Controller-Erkennung (in der Cloud nicht nötig) |

Die Knöpfe zum Abmelden und für Nachrichten sind ausgeblendet, ein Hinweis nennt den Nur-Lese-Modus. Der Server beantwortet solche Anfragen mit HTTP 501.

## Einrichtung

1. In Citrix Cloud unter **Identity and Access Management > API Access** einen **Secure Client** anlegen. Notiere die **Customer-ID** (über der Liste), die **Client-ID** und das **Secret** (wird nur einmal angezeigt). Gib dem Client nur die Rechte, die zum Lesen der Monitor-Daten nötig sind; Administratorrechte braucht der Nur-Lese-Modus nicht.
2. Die Einstellungen in `sss.config.json` eintragen, **ohne das Secret**:

```json
{ "Sss": { "Provider": "Citrix", "Citrix": { "Cloud": { "Enabled": true, "Region": "eu", "CustomerId": "<Customer-ID>", "ClientId": "<Client-ID>" } } } }
```

3. Das Secret über die Umgebung des Anwendungspools geben, nicht über eine Datei im Repository: `Sss__Citrix__Cloud__ClientSecret`. Die Status-Seite und die wirksame Konfiguration maskieren es.
4. `Region` ist `eu` (api-eu.cloud.com), `us` (api.cloud.com), `ap-s` (api-ap-s.cloud.com) oder `jp` (api.citrixcloud.jp). Der IIS-Server braucht nur ausgehendes HTTPS zu diesem Host.

Mit `Enabled` werden `Controllers`, `Discovery` und `Monitor:BaseUrl` ignoriert und die Prozess-Funktionen abgeschaltet. Es wird kein PowerShell-Worker gestartet.

## Wie es arbeitet und was es schützt

SSS meldet sich bei `cctrustoauth2/<Customer-ID>/tokens/clients` mit den Client-Zugangsdaten an, hält das Bearer-Token im Speicher bis kurz vor Ablauf und sendet es mit dem Header `Citrix-CustomerId` ausschließlich an `https://<API-Host>/monitorodata/`. Eine Anfrage an einen anderen Host wird abgelehnt, bevor das Token hinzukommt. Eine abgelehnte Anmeldung zeigt im Browser „Dienst nicht verfügbar“; der Grund (ohne das Secret) steht im Log.

## Bekannte Grenzen

- `scripts/Test-SssCitrix.ps1` prüft eine lokale Umgebung. Für die Cloud nimm `scripts/Test-SssCloud.ps1 -CustomerId ... -ClientId ... -Region eu` (fragt das Secret ohne Anzeige ab, liest und zeigt nur Zahlen), danach die Status-Seite.
- Die Live-Liste liest den Monitor-Dienst, der dem Broker kurz hinterherhinken kann; die Zustände sind Aktiv und Getrennt.
- Aktionen über die DaaS-REST-API (Abmelden, Nachricht) sind als zweiter Schritt geplant.
