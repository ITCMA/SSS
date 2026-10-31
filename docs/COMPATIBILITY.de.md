# Versionierung und Kompatibilität

[English](COMPATIBILITY.md)

SSS nutzt [Semantic Versioning](https://semver.org): `MAJOR.MINOR.PATCH`. Bis 1.0.0 ist das Projekt in der Beta; diese Seite sagt, was ab 1.0.0 stabil sein wird und was sich vorher noch ändern darf.

## Was zur stabilen Oberfläche gehört

| Oberfläche | Zusage ab 1.0.0 |
|---|---|
| Schlüssel, Standardwerte und Bedeutung in `sss.config.json` ([Konfigurationsreferenz](CONFIGURATION.de.md)) | Ein Schlüssel wird in einem Minor- oder Patch-Release nicht umbenannt, entfernt oder anders belegt. Entfernt wird nur in einem Major-Release, nach einem Hinweis im Changelog und einer Warnung im Status-Reiter. |
| Schalter von `install.ps1`, `Install-SssJea.ps1`, `Test-SssSetup.ps1`, `Test-SssCitrix.ps1` | Dieselbe Regel wie bei Konfigurationsschlüsseln. |
| Das Audit-Protokoll (`audit-*.jsonl`): Feldnamen und Bedeutung von `action` und `result` | Bestehende Felder bleiben. Neue Felder und neue `action`-Werte dürfen in Minor-Releases hinzukommen; Auswertungen müssen unbekannte Felder ignorieren. |
| `GET /healthz` (Statuswerte `ok`, `degraded`, `unavailable`, HTTP 200 oder 503) | Stabil. |
| Aktualisieren mit `install.ps1 -Update` von jedem 1.x-Release auf ein späteres 1.x-Release, mit Rückfall | Unterstützt und getestet. |

## Was nicht stabil ist

- Die Programmierschnittstelle der Weboberfläche unter `/api/...`. Sie gehört zur mitgelieferten Oberfläche und kann sich in jedem Release ändern. Wer integrieren will, öffnet ein Issue, damit ein unterstützter Endpunkt entstehen kann.
- Übersetzungen, Texte und Layout der Oberfläche.
- Der Aufbau der Experten-Ansicht (Gruppen, Reihen, Tabellen), weil er dem folgt, was Citrix liefert.
- Befehlsnamen und Parameter des JEA-Endpunkts. Ändern sie sich mit einem SSS-Update, steht es in den Release-Hinweisen, und `Install-SssJea.ps1` muss auf den VDAs erneut laufen. Ältere Endpunkte funktionieren weiter für die Funktionen, die sie unterstützen.
- Interne Dateinamen, Protokolltexte und das Protokoll des PowerShell-Workers.

## Vor 1.0.0

Minor-Versionen (`0.x.0`) dürfen Konfigurationsschlüssel oder Schalter ändern. Jede solche Änderung steht im [Changelog](../CHANGELOG.md) unter *Changed* oder *Removed*, und unbekannte Schlüssel werden gemeldet, damit eine Umbenennung nicht unbemerkt bleibt. Patch-Versionen (`0.x.y`) beheben nur Fehler.

## Unterstützte Umgebungen

| Komponente | Unterstützt |
|---|---|
| Citrix Virtual Apps and Desktops | 2203, 2402, 2507, 2607 (LTSR) und spätere aktuelle Releases; Feldnamen prüft `Test-SssCitrix.ps1` |
| SSS-Server | Windows Server 2016 oder neuer mit IIS, ASP.NET Core Module V2 |
| VDAs (nur Prozesssteuerung) | Windows Server 2016 oder neuer, Windows PowerShell 5.1, WinRM |
| Browser | aktuelle Versionen von Chrome, Edge, Firefox, Safari |

Ein Release wird unterstützt, bis das nächste Minor-Release erscheint; Sicherheitskorrekturen gehen in das neueste Release. Siehe [SECURITY.md](../SECURITY.md).

## So aktualisierst du

1. Den Changelog-Eintrag des neuen Releases lesen.
2. Zip herunterladen und die `.sha256`-Datei prüfen.
3. `.\install.ps1 -Update` auf jedem SSS-Server, nacheinander. Die vorherige Version bleibt in `<Installationsordner>.previous` und wird von selbst zurückgeholt, wenn die neue nicht antwortet.
4. `.\Test-SssSetup.ps1`. Steht in den Release-Hinweisen, dass sich der VDA-Endpunkt geändert hat, `Install-SssJea.ps1` auf den VDAs erneut ausführen.
