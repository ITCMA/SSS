# Bedrohungsanalyse

[English](THREAT-MODEL.md)

Was SSS schützt, wer es angreifen könnte, wie, und was ihn aufhält. Sie ist aus dem Quelltext und der dokumentierten Einrichtung geschrieben und keine externe Prüfung. Probleme bitte vertraulich melden, siehe [SECURITY.md](../SECURITY.md).

## Was schützenswert ist

| Wert | Warum |
|---|---|
| Sitzungen anderer Benutzer | ein Benutzer darf nie in einer fremden Sitzung etwas sehen, abmelden, senden oder beenden |
| Das Dienstkonto des App-Pools | es hat Citrix-Rechte (Sitzungen lesen, abmelden, Nachrichten senden, Monitor-Daten lesen) und mit Prozesssteuerung einen Weg zu den VDAs |
| Sitzungs- und Profildaten | Namen, Client-Adressen, Rechnernamen, Prozessnamen und Pfade, Ordnernamen und Größen: personenbezogene Daten im Sinn des Datenschutzes |
| Audit-Protokoll | Nachweis, wer was getan hat |
| Verfügbarkeit | Helpdesk und Benutzer verlassen sich in Störungen darauf |

## Wer angreifen könnte

1. **Ein angemeldeter Benutzer**, der Daten oder Aktionen anderer Benutzer will.
2. **Ein böswilliges oder neugieriges Helpdesk-Mitglied**, das Helpdesk-Rechte missbraucht.
3. **Ein Angreifer von außen** ohne Konto, über das Netzwerk.
4. **Eine Webseite, die der Benutzer besucht** (seitenübergreifende Angriffe auf den angemeldeten Browser).
5. **Ein Angreifer, der einen VDA oder Server der Umgebung kontrolliert** und SSS oder das Dienstkonto erreichen will.
6. **Jemand, der das Dienstkonto stiehlt** (Kennwort oder Ticket).

## Vertrauensgrenzen

```
Browser ──HTTPS+Windows-Anmeldung──> IIS ──in-process──> SSS ──PowerShell-Worker──> Delivery Controller (Broker SDK)
                                                            ├──HTTP(S)+Windows-Anmeldung──> Monitor OData
                                                            └──WinRM (JEA, optional)──> VDA
```

Die Identität kommt immer aus der Windows-Authentifizierung des IIS. SSS übernimmt für die eigenen Ansichten des Benutzers nie einen Benutzer oder eine SID aus einer Anfrage. Das Helpdesk übergibt eine Ziel-SID, die nur für Mitglieder der Helpdesk-Gruppe akzeptiert wird und nur, wenn die Sitzung wirklich dieser SID gehört.

## Bedrohungen und Gegenmaßnahmen

| Bedrohung | Gegenmaßnahme | Rest |
|---|---|---|
| Ein Benutzer liest oder ändert fremde Sitzungen durch Raten von IDs | Identität vom IIS; jede Aktion prüft erneut, dass die Sitzung dem Aufrufer gehört (Weboberfläche **und** PowerShell-Worker, unabhängig voneinander); fremde IDs ergeben „nicht gefunden“ oder werden abgelehnt und protokolliert | keiner bekannt |
| Missbrauch durch das Helpdesk | getrennte Endpunkte hinter einer Gruppenprüfung; ein Grund kann verlangt werden; jede Suche, Ansicht, Nachricht, Abmeldung und Prozessaktion wird mit Handelndem, Ziel und Zeit protokolliert | ein Helpdesk-Mitglied sieht berechtigt alle Sitzungen: Gruppe klein halten, Audit-Protokoll weiterleiten |
| Einschleusen in Citrix-Abfragen oder PowerShell | jeder Wert, der ein Cmdlet oder eine OData-Abfrage erreicht, wird gegen ein strenges Muster geprüft (SIDs, numerische IDs, GUIDs, Hostnamen); Feldnamen aus der Konfiguration müssen einfache Bezeichner sein; kein Freitext erreicht eine Befehlszeile | keiner bekannt |
| Seitenübergreifende Anfragefälschung | Anti-Forgery-Token im Header plus Cookie mit `SameSite=Strict`, `HttpOnly`, `Secure` bei jeder ändernden Anfrage | keiner bekannt |
| Seitenübergreifendes Skripting | die Oberfläche schreibt Text nur mit `textContent`; strenge Content-Security-Policy (keine Inline-Skripte oder -Stile, keine externen Quellen); keine Bibliotheken von Dritten | keiner bekannt |
| Clickjacking, Sniffing, Referrer-Lecks | `frame-ancestors 'none'`, `X-Content-Type-Options`, kein Referrer, Same-Origin-Richtlinien, HSTS | keiner |
| Überlastung durch einen Benutzer | Ratenbegrenzung pro Benutzer (Abmelden, Lesen, Helpdesk), begrenzter Worker-Pool, begrenzte Antwortgrößen | ein entschlossener Angreifer mit vielen Konten kann den Controller trotzdem belasten |
| Details in Fehlermeldungen | der Browser bekommt nur Statuscodes (503 für die Citrix-Seite, 500 für Fehler); Details gehen ins Serverprotokoll | Protokolle enthalten Details: schützen |
| Anonymer Zugriff | Windows-Authentifizierung an, anonym aus für die ganze Anwendung. Einzige Ausnahme ist `GET /healthz`, auf Wunsch geöffnet, mit den Antworten `ok`, `degraded` oder `unavailable` und sonst nichts | keiner |
| Monitor-Verkehr im Netz | Windows-Authentifizierung (signiert, keine Klartext-Anmeldedaten); HTTPS, wo der Controller es kann | mit `UseHttps = false` sind die Daten selbst nicht verschlüsselt: im Verwaltungsnetz halten |
| Gestohlenes Dienstkonto | gMSA empfohlen (langes zufälliges Kennwort, vom AD gewechselt); das Konto hat nur die Citrix-Rolle, die du vergibst, und die JEA-Rechte | mit *Help Desk Administrator* ist die Rolle weiter als SSS braucht ([eigene Rolle](PERMISSIONS.de.md)); mit Prozesssteuerung kann ein Angreifer die Prozesse jedes Benutzers auf VDAs mit dem Endpunkt beenden und Prozess- und Ordnernamen lesen (keine Dateiinhalte, keine Codeausführung) |
| Ein VDA greift SSS an | SSS ruft den VDA nur an und nimmt keine Daten von ihm als Anweisung; Antworten werden als Daten gelesen und in der Größe begrenzt | ein kompromittierter VDA kann der Oberfläche falsche Prozess- und Profildaten liefern |
| Manipulation des Audit-Protokolls | aus Sicht der Anwendung nur anfügbar; eine Datei pro Tag und Server | ein Administrator auf dem Server kann Dateien ändern: Protokoll an ein System weiterleiten, das er nicht ändern kann |
| Verwundbare Abhängigkeiten | keine Pakete von Dritten in der Webanwendung; Build-Aktionen auf Commit-Hashes festgelegt; Dependabot; CodeQL | .NET selbst und Windows: aktuell halten |
| Lieferkette der Releases | Releases entstehen in einem festgelegten GitHub-Actions-Ablauf aus einem Tag; jedes Zip hat eine SHA-256-Datei | die Prüfsumme kommt vom selben Ort wie das Zip: bei hohem Schutzbedarf über einen zweiten Weg vergleichen |

## Datenschutz

SSS zeigt Benutzern nur eigene Daten und Helpdesk-Mitgliedern die Daten der Benutzer, die sie ansehen. Es speichert nichts außer dem Audit-Protokoll und der Konfiguration; es gibt keine Datenbank und keine Telemetrie. Das Audit-Protokoll enthält Benutzernamen, SIDs, Client-Adressen, Suchtexte, Helpdesk-Gründe und Nachrichtentexte. Legt eine Aufbewahrungsfrist fest und wer es lesen darf. Die Experten-Ansicht und die Profilanalyse zeigen Rechnernamen, Prozessnamen, Pfade und Ordnernamen, woraus man ablesen kann, woran jemand arbeitet: entscheidet, ob die Helpdesk-Gruppe das sehen darf.

## Nicht Teil dieser Analyse

Der Schutz der Citrix-Umgebung selbst, der Domäne, von Windows auf SSS-Server und VDAs und des Browsers des Benutzers. SSS verlässt sich darauf.
