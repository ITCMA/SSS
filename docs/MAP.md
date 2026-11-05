# Map tab

[Deutsch](MAP.de.md)

![Map tab](img/map-light.png)

The **Map** tab (every helpdesk level) shows all live sessions of the site on a world map. It needs no setup: the location comes from the Citrix Monitor (`ClientLocationCountry` and `ClientLocationCity` of the connection, the same data as the location table of the Environment tab).

- **Zoom and move**: mouse wheel, the + and − buttons, or the keys + − and the arrow keys with the map focused; drag to move. *Whole world* (or Esc, or the × of the place box) flies back to the whole world and clears the selection and the search.
- **Places**: a circle per place with the number of sessions and the name of the place (city, otherwise country); many devices behind one provider address are one place. A click on a cloud of devices flies in (about a second, zooming out a little on long trips) until every device is its own dot. Hovering a dot shows an info box with the place, the number of sessions and the users there. A click lists the sessions (user, device, place, machine, delivery group) with *Show on map* and *Open user*.
- **Search** (live filter, it reacts from the first letter): user, device, machine, delivery group or city. The matches turn red, the other places fade, and both lists under the map are filtered; *Show* flies to the first match.
- **Lists**: under the map, closed by default, are all sessions with a known location and all sessions without a usable one (while you search they open and show only the matches).
- **Approximate positions**: the position of a city is looked up in a bundled list of about 40,000 cities. A city that is not in the list gets its own dot named after the city, in the middle of its country (orange, marked as approximate). Sessions without a country (internal addresses, older clients) cannot be placed and are listed under the map.
- **User names** come from the Monitor and, where it has none, from the Broker session list. A line under the map says how many sessions still have no name.

## Privacy

The outlines and the place list are bundled with SSS (`wwwroot/geo/world.json`, derived from [Natural Earth](https://www.naturalearthdata.com/), public domain, and the city list of [GeoNames](https://www.geonames.org/), licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/): about 40,000 cities from 15,000 inhabitants). The browser finds the position itself, so **no address, name or location is sent to a map service**, and the map works without internet access. The client address is not part of the data the map receives. The data is read from the Monitor like the other Insights (cached for a minute) and shows names, so it is limited to the helpdesk roles.
