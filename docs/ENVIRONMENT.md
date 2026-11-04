# Environment: client versions and locations

[Deutsch](ENVIRONMENT.de.md)

The **Environment** tab (role *Admin*) shows, for all live sessions of the site, which Workspace app versions and platforms connect and from which of your locations. It answers questions such as "how many devices still run an old Workspace app?" or "where do our users connect from?" without opening Director. It shows **counts only**, nothing per person.

The top of the tab shows **charts** (sessions by platform and by location, VDA machines by state, the most started applications); the tables below hold the details. Sessions without a client version or location are listed as *unknown*, so the numbers always add up.

## Workspace app versions

SSS reads the client version and platform of every live connection from the Citrix Monitor (`Connection.ClientVersion`, `Connection.ClientPlatform`) and counts devices and sessions per version and platform, with a bar for the share. The fields are asked for only if the site's `$metadata` lists them. If your site does not deliver them, the tab says so; `Test-SssCitrix.ps1` lists what the Monitor offers (entity sets and properties for client, platform, location, policy or score) so you can see what is available. The property names can be changed in `Sss:Citrix:Monitor:ClientVersionField` and `ClientPlatformField`.

The counts come from the **live sessions** and are cached for a minute. Devices are counted by client name, users by the Monitor's user id.

### Hint for outdated clients

Set the oldest acceptable version per platform:

```json
"Environment": { "MinClientVersion": { "Windows": "26.3.0", "*": "26.3.0" } }
```

Versions are compared by their numbers (`26.10.0` is newer than `26.9.0`, `26.09.0.17` equals `26.9.0.17`). `*` applies to every platform without its own entry. Clients below the minimum are marked **outdated** in the tab, and the **user** sees a short hint in the own session list ("Your Workspace app is outdated. Please update it.") together with the version. Switch the hint off with `"HintOutdatedClient": false`; without a minimum nothing is marked.

## Locations

An on-premises site delivers no location: Citrix' cloud derives it from the client address with a geolocation service, and internal addresses cannot be located at all. SSS therefore lets you assign **your** networks to names:

```json
"Environment": {
  "Locations": [
    { "Name": "Head office",       "Networks": [ "10.20.0.0/16" ] },
    { "Name": "Branch office",     "Networks": [ "10.30.0.0/16", "192.0.2.0/24" ] },
    { "Name": "Home office (VPN)", "Networks": [ "10.99.0.0/16" ] }
  ]
}
```

A session belongs to the location with the **most specific** matching network (a `/24` wins over a `/16`); everything else is listed as "not assigned". The table shows sessions, devices and users per location. There is deliberately no world map: for internal networks it would show nothing true, and a table answers the question faster.

### Locations reported by Citrix

If the site delivers `Connection.ClientLocationCountry` and `ClientLocationCity` (the Monitor of a site with Gateway or cloud telemetry may), the tab also lists **country and city as Citrix reports them** with sessions, devices and users. Connections without a location show as "unknown". This list needs no configuration; the networks above remain the way to name your own offices, including internal addresses.

## Applications inside the sessions

The Monitor of current sites also keeps the applications started **inside the desktops** (the data behind Director's "local apps" view, collected by the uberAgent part of the VDA agent). The tab lists the **most used applications of the last 7 days** with launches, the highest number started at once, users per day (highest day) and machines, with the real application names. If the site delivers no such data the tab says so. In the expert view of one session the same data appears as the table **Applications started in the session** (name, version, program path, start time, start duration), together with **application crashes** of that session.

### VDA machines by agent version

Below the counts a list shows every machine (delivery group, registration, power state, maintenance, sessions, last registration change), followed by the Local Host Cache report if the site delivers one. Sessions for which Citrix reports no location form an **unknown** row in the Citrix location table; the table of *own locations* is optional and only helps for those.

The tab also counts the **VDA machines by agent version and operating system** (from the Monitor), with those in maintenance and those not registered, so old agents show up like old Workspace apps do.

### Short application names

The Monitor stores the names of locally started applications in a shortened form (for example "GglChrm" for Google Chrome). SSS shows the name as Citrix delivers it and, below it, the **program file** of one launch of that application (`chrome`), which makes the short names readable. A readable product name comes from the VDA endpoint when you open the processes of a session ([Processes](PROCESSES.md)).

## Privacy

Locations and device lists are personal data in the sense of data protection law, even when shown as counts. The tab shows only totals, requires the Admin role and does not store anything. Agree the use with your data protection officer and, where one exists, the works council before you switch it on, in particular the outdated-client hint, which tells a user about their own device.

## What this does not do

- Citrix Cloud features that come from cloud telemetry (device trust and posture policies, geolocation maps) are not available for an on-premises site and are not built.
- SSS does not collect history: it shows the situation of the live sessions at the time of the request. A statistics over days needs a database and is a different product (use Director or uberAgent for that).
