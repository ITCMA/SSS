# Insights, cards and user history

[Deutsch](INSIGHTS.de.md)

![Insights tab](img/insights-light.png)

## Insights tab (all helpdesk levels)

The **Insights** tab answers "what is conspicuous right now?" without opening a single session. It reads the Citrix Monitor and needs no extra setup. The result is cached for a minute.

**Conspicuous now** lists, most severe first (each with a symbol and text, not only a colour):

| Finding | When | Button |
|---|---|---|
| Slow connection | a live session has an ICA round-trip time or available bandwidth beyond the limits of `Sss:Quality` | Open user |
| Slow logon | a session of the last 24 hours logged on slower than `LogonWarnSeconds` **and** at least 1.5 times slower than its delivery group's average | Open user |
| Slow delivery group | the group's average logon time of the last 7 days is beyond the limit | Show sessions |
| Machine not registered | a machine is switched on but not registered with the Delivery Controller (not in maintenance) | Show sessions |
| Machine busy | the load index of a registered machine is beyond `LoadWarn` or `LoadCrit` | Show sessions |
| Long disconnected | a session has been disconnected for 4 hours or more (it still holds a licence and memory), from the Broker's idle time | Open user |
| Long running | a registered machine has run 14 days or more without a restart | Show sessions |
| Maintenance | number of machines in maintenance mode (information) | |

*Open user* opens the user's sessions, where log off, message and the expert view are. *Show sessions* opens the helpdesk list filtered to that machine or delivery group.

**Logon time per delivery group** shows count, average, median and the time 90 % of the logons stay under (sessions of the last 7 days that have a logon duration). It is computed from the Monitor's session records, so it works even where the Monitor's own summary tables are empty.

The table also shows the **average of each of the last 7 days** as a small curve, so a slow deterioration is visible and not only the average. A group with fewer than 5 sessions is shown in grey and is not rated.

**Disconnected sessions** lists every disconnected session with user, application, machine, delivery group and since when (from the Broker's state change time), the longest first. Sessions disconnected for 4 hours or more are also named under *Conspicuous now*.

**Application crashes** and **connection failures** can be switched between the last **1, 3, 6, 12 and 24 hours**. Each row counts one application (or failure reason) with the number of users, the machines (and the machine with most), the first and last time. **Details** opens every single record: when, which user (a button to open the user's sessions), machine, delivery group, client device and everything else the Monitor delivers about it (for a crash the version, program path and error description). Above the records a hint names the pattern: one user on one machine, mostly one machine (look at the machine: patches, updates, disk), one user on several machines (look at the user: profile, settings) or widespread (look for a recent change). The failure reason is the text the Monitor records (`ConnectionFailureText`; the code if there is none); a site that does not deliver these records leaves the table out.

**Machines** lists every VDA with a state (problem, busy, OK, maintenance, off), its load index, the number of sessions and how long it has been running.

## Logon time against the delivery group

In the expert view of a session the helpdesk sees a line such as "Logon 38 s ▲ delivery group DG-Desktops: average 31 s, median 28 s, 90 % under 49 s". It tells whether this session (or user) is slow or the whole group is.

## History of a user

Above the sessions of the user the helpdesk sees the last 30 days in one line: number of sessions, average logon time (and the group's), connection failures and automatic reconnects. A frame in yellow or red marks users with many problems (five or more failures and reconnects, or a logon time clearly above the group's). The line uses the same permission as viewing the user's sessions and is cached for five minutes.

## Cards for users

A normal user (expert view off) sees the own sessions as **cards**: first one sentence on whether everything is fine, then per session its name, state, the two quality lights and the log-off button. The technical columns (delivery group, machine, start, duration, idle, client) are behind **Details**. The helpdesk and the expert view keep the dense table. The history period and CSV export now sit with the history.

## Data sources and limits

`Sessions`, `Users`, `Machines`, `LoadIndexes`, `ConnectionFailureLogs` and `SessionAutoReconnects` of the Monitor (see [DATA-SOURCES](DATA-SOURCES.md)). Nothing is stored by SSS. A farm with very many sessions is read up to `Sss:Citrix:Monitor:MaxPages` pages. A record set the site does not have simply leaves its part out.


## Health score, load, start times, costs and timeline

The lower part of the Insights tab reads more of the Monitor's own tables. Every part says so when your site delivers no data for it.

| Part | Source | What it shows |
|---|---|---|
| **Health** (machines) | registration, load index, running time | 0-100 per machine: not registered while switched on -60, load above the critical limit -40, above the warning limit -20, running 14 days or more -10. Machines in maintenance or switched off have no score. |
| **Health score** (user) | the user history card | 0-100: connection failures (10 each, at most 40), automatic reconnects (3 each, at most 15), logon time 1.5 times (-20) or twice (-30) the group's average. |
| **Machine load, last 7 days** | `ResourceUtilizationSummary` | CPU and memory, average and peak. Critical from 85 % CPU or 95 % memory peak, warning from 65 % CPU or 85 % average memory. |
| **Application start times** | `HistoricalApplicationLaunches` | per application average and 90 % under; if one machine takes 1.5 times longer than the average, it is named. Applications with fewer than 3 starts are left out. User names are not read. |
| **Costs, last 30 days** | `MachineCostAllocationSummaries` | compute, storage and savings per delivery group in the currency the Monitor reports. |
| **What changed, last 7 days** | machines, audit trail | machine registered, lost its registration, switched on; for the **auditor** also log-offs, messages and ended processes with who did them. |

Where the Monitor keeps the same figures in several granularities (minute, hour, day), the most common one is used, so nothing is counted twice. The result is cached for 10 minutes.


## Machines, tiles and charts

- **Machines**: the search field filters tiles and table together while you type; every word must be part of the machine name, the delivery group or the state ("rds busy"). "only conspicuous" keeps machines that are busy or have a problem. The tiles are grouped per delivery group (groups with a problem open on their own) and can be hidden with one button; on a site with more than 40 machines they start hidden, and the choice is remembered in the browser. A click on a tile or on "Show sessions" in the table opens the helpdesk list filtered to that machine.
- **The day at a glance** (top of the tab): five key figures and one chart of the last 24 hours. Bars are the logons per hour (scale left), the line is the average logon time (scale right, dashed = warning limit), and below the same time axis crashes (triangles) and connection failures (dots) are marked. Tooltips name time, count, machine and user. Use it to see at once whether crashes or failures coincide with a logon peak or a slow hour.
- **Logon time per delivery group**: lines over 7 days with the warning limit, for groups with at least 5 sessions.
- **User of a crash**: taken from the session of the record; without one, from the launch record of the program (same program on the same machine, running at the time), as a last resort from the one session that ran on that machine then. If several users shared the machine and nothing identifies one, the user stays empty.


## Delivery groups, load strips and the machine page

- **Delivery groups at a glance**: one card per group (sorted worst first). The light is the worst of four lines: average logon time against the limits, machines that are busy or have a problem, crashes and connection failures of the last 24 hours (a light from the first, red from 5). Each line names its reason; a click filters the machine list to the group.
- **Load of the machines over the day**: one row per machine and one cell per hour of the last 24 hours (the hourly utilisation of the Monitor): the darker, the more CPU (orange from 65 %, red from 85 %). A red cross marks an hour in which the machine lost its registration. Sixty busiest machines are shown. A click on a name opens the machine page.
- **Machine page**: state and health score, sessions, load index, running time, agent version and operating system, CPU and memory of the last 7 days, the load strip, the **availability of the last 7 days** (a band: green while registered, red where the failure log of the Monitor shows it had lost the registration, with the share registered; no database of our own is needed), crashes and connection failures on this machine in the last 24 hours, what changed, and the sessions running on it (each with *Open user*).
- **Hover**: pointing at an hour of the day chart (or tapping it) shows that hour with its logons, average logon time and every crash and failure with machine and user.
