# Roles

[Deutsch](ROLES.de.md)

Everybody who can sign in sees and logs off **their own** sessions. On top of that SSS knows five roles, each tied to an Active Directory group. A member of a higher helpdesk level has the lower levels as well. **Auditor** and **Admin** stand apart: a helpdesk member does not see the audit trail or the system pages unless that role is granted too.

| Role | Group setting | May |
|---|---|---|
| Level 1 | `Sss:Roles:Level1Group` | look: search users, see their sessions, the expert view, the farm overview and the [Insights](INSIGHTS.md) tab. Changes nothing. |
| Level 2 | `Sss:Roles:Level2Group` | level 1, plus log off sessions and send messages. |
| Level 3 | `Sss:Roles:Level3Group` | level 2, plus list processes and read the profile of other users, end processes (needs the [JEA endpoint](PROCESSES.md)). |
| Auditor | `Sss:Roles:AuditorGroup` | read, evaluate and export the [audit trail](AUDIT.md) and check its integrity. Nothing else. |
| Admin | `Sss:Roles:AdminGroup` | the **Status** tab (health checks, Delivery Controllers, audit destinations, list of SSS servers, effective configuration) and the **Environment** tab ([client versions and locations](ENVIRONMENT.md)). |

```json
"Roles": {
  "Level1Group": "CORP\\SSS-Helpdesk-L1",
  "Level2Group": "CORP\\SSS-Helpdesk-L2",
  "Level3Group": "CORP\\SSS-Helpdesk-L3",
  "AuditorGroup": "CORP\\SSS-Auditors",
  "AdminGroup": "CORP\\SSS-Admins"
}
```

- The groups are resolved to SIDs at startup and compared with the groups in the Windows token of the caller (nested groups count). A group that is empty or cannot be resolved grants nothing (the Status tab and the log say so).
- **Existing installations keep working.** `Sss:Helpdesk:Group` still means *everything of level 3*. If `AdminGroup` is empty, every helpdesk member keeps the Status tab, as before. The audit trail is **never** part of an older setup: set `AuditorGroup` to use it.
- The interface shows only what the roles allow, and the server checks every call again (an API call without the right gets `403`).
- Every audit line carries the role of the person who acted (`L2`, `L3+Auditor`, ...).

## Ticket number or reason (optional, can be made mandatory)

The field at the top of the user view takes a ticket number or any other reason; whatever is entered is written into the audit trail. By default it is **optional**. With `Sss:Helpdesk:ReasonRequired` = `true` every helpdesk action that changes something or reads processes or profiles needs a **ticket number or reason**: log off, message, process list, profile, end process. SSS does not check the content (any text up to 200 characters), it only makes sure that there is one and writes it into the audit trail. The helpdesk enters it once in the field at the top of the user view. Searching and looking at sessions does not need one. Switch the option off again to make it optional.

## Notice on bulk actions

A helpdesk request that logs off `Sss:Helpdesk:BulkAlertThreshold` sessions or more (default 5) writes an extra event `alert:bulk-logoff`. Give a chat or SNMP [audit destination](AUDIT.md#destinations) the filter `"Include": ["alert:"]` and the right people are told at once.

## Development mode

For tests on a developer machine `Sss:Auth:DevRoles` lists the roles of the demo user, for example `"L2,Auditor"` (`L1`, `L2`, `L3`, `Auditor`, `Admin`). It is only accepted when `ASPNETCORE_ENVIRONMENT=Development`.
