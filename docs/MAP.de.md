# Karte

[English](MAP.md)

![Karte](img/map-light.png)

Die **Karte** (alle Helpdesk-Stufen) zeigt alle Live-Sitzungen der Umgebung auf einer Weltkarte. Es ist keine Einrichtung nötig: Der Standort kommt aus dem Citrix Monitor (`ClientLocationCountry` und `ClientLocationCity` der Verbindung, dieselben Daten wie die Standorttabelle im Reiter Umgebung).

- **Zoomen und verschieben**: Mausrad, die Knöpfe + und −, oder die Tasten + − und die Pfeiltasten bei fokussierter Karte; zum Verschieben ziehen. *Ganze Welt* (oder Esc, oder das × der Orts-Box) fliegt zurück auf die ganze Welt und hebt Auswahl und Suche auf.
- **Orte**: ein Kreis je Ort mit der Zahl der Sitzungen und dem Namen des Orts (Stadt, sonst Land); viele Geräte hinter einer Provider-Adresse sind ein Ort. Ein Klick auf eine Geräte-Wolke fliegt hinein (etwa eine Sekunde, bei langen Strecken erst etwas heraus) bis jedes Gerät ein eigener Punkt ist. Der Mauszeiger auf einem Punkt zeigt eine Info-Box mit Ort, Zahl der Sitzungen und den Benutzern dort. Ein Klick listet die Sitzungen (Benutzer, Gerät, Ort, Maschine, Bereitstellungsgruppe) mit *Auf Karte zeigen* und *Benutzer öffnen*.
- **Suche** (Live-Filter, reagiert ab dem ersten Buchstaben): Benutzer, Gerät, Maschine, Bereitstellungsgruppe oder Ort. Treffer werden rot, die übrigen Orte verblassen, und beide Listen unter der Karte werden gefiltert; *Zeigen* fliegt zum ersten Treffer.
- **Listen**: Unter der Karte stehen, standardmäßig eingeklappt, alle Sitzungen mit bekanntem Standort und alle ohne verwertbaren Standort (bei einer Suche öffnen sie sich und zeigen nur die Treffer).
- **Ungefähre Positionen**: Die Position einer Stadt stammt aus einer mitgelieferten Liste mit rund 40.000 Städten. Eine Stadt, die nicht in der Liste steht, bekommt einen eigenen Punkt mit ihrem Namen in der Mitte ihres Landes (orange, als ungefähr gekennzeichnet). Sitzungen ohne Land (interne Adressen, ältere Clients) lassen sich nicht platzieren und stehen unter der Karte.
- **Benutzernamen** kommen aus dem Monitor und, wo er keinen kennt, aus der Sitzungsliste des Brokers. Eine Zeile unter der Karte sagt, bei wie vielen Sitzungen noch ein Name fehlt.

## Datenschutz

Umrisse und Ortsliste sind in SSS enthalten (`wwwroot/geo/world.json`, abgeleitet von [Natural Earth](https://www.naturalearthdata.com/), gemeinfrei, und der Städteliste von [GeoNames](https://www.geonames.org/), Lizenz [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/): rund 40.000 Städte ab 15.000 Einwohnern). Den Ort bestimmt der Browser selbst, daher **geht keine Adresse, kein Name und kein Standort an einen Kartendienst**, und die Karte funktioniert ohne Internetzugang. Die Client-Adresse gehört nicht zu den Daten der Karte. Die Daten werden wie die übrigen Insights aus dem Monitor gelesen (eine Minute zwischengespeichert) und enthalten Namen, deshalb sind sie auf die Helpdesk-Rollen beschränkt.
