# Ending applications and processes

[Deutsch](PROCESSES.de.md)

Users can see the applications and processes of their own sessions and close them (for example a frozen Excel) without logging off. The helpdesk can do the same for any user's session. The feature is **off by default** and needs a small endpoint on every VDA.

## How it works

Citrix offers no supported Broker or Monitor command to end a single process (Director uses its own channel to the VDA). SSS therefore uses **Just Enough Administration (JEA)**, a restricted PowerShell endpoint of Windows:

```
Browser --> SSS (IIS) --> PowerShell worker --WinRM--> JEA endpoint "SSS.Processes" on the VDA
                          (service account)             Get-SssProcess / Stop-SssProcess
```

- The JEA endpoint offers exactly **three commands**: `Get-SssProcess` and `Stop-SssProcess` for this feature, and the read-only `Get-SssProfile` for the [profile type and size](DATA-SOURCES.md#profile-type-and-size) in the expert view. The service account cannot run anything else on the VDA through it.
- It runs as a temporary local administrator (a *virtual account* that exists only for that call), so the service account itself needs no administrator rights on the VDAs.
- **Only the user's own processes are touched.** Both commands take the user's SID and compare it with the owner of every process; system processes, other users' processes and unknown ids are refused (`not-owned`, `not-found`). The Citrix session processes `wfshell.exe`, `ctxgfx.exe` and `cmstart.exe` are protected (`protected`).
- **SSS checks the session twice.** The web application accepts a session only when it is one of the target's live sessions; the PowerShell worker looks it up again in the Broker and refuses it unless the session belongs to that user. Only the machine of that session is contacted.
- Every call is **audited** (`list-processes`, `end-process`, with the helpdesk actor, the process id and whether it was forced) and JEA writes its own **transcript** on the VDA.

## Set up

1. **On every VDA** (or in the master image, or by startup script): run `Install-SssJea.ps1` from the release as administrator.

   ```powershell
   .\Install-SssJea.ps1 -Principal 'CORP\svc-sss$'
   ```

   (several accounts are possible: `-Principal 'a','b'`.) Add `-AllowedFrom sss01.corp.local` (host names or IP ranges of your SSS servers) to limit the firewall rule for WinRM to them; without it every computer that can reach the VDA may try to connect. The script enables WinRM (TCP 5985), registers the endpoint `SSS.Processes` for exactly the given accounts and writes the transcripts to `C:\ProgramData\SSS\jea-transcripts` (readable only by administrators and the system). It changes nothing else. `-ConfigurationName` and `-TranscriptDirectory` change the defaults. `.\Install-SssJea.ps1 -Remove` unregisters the endpoint again.

2. **Open the network path:** TCP 5985 (WinRM over Kerberos, encrypted at message level) from the SSS server to the VDAs.

3. **On the SSS server**, in `sss.config.json`:

   ```json
   { "Sss": { "Citrix": { "Processes": { "Enabled": true } } } }
   ```

   Further keys: `Configuration` (endpoint name, default `SSS.Processes`), `Audience` (`All` or `Helpdesk`), `TimeoutSeconds` (default 60). See the [configuration reference](CONFIGURATION.md#processes-ssscitrixprocesses). Restart the app pool.

4. **Test it** (nothing is ended):

   ```powershell
   .\Test-SssCitrix.ps1 -Controller ddc01 -UserName CORP\alice -Vda vda01.corp.local
   ```

   The script connects to the endpoint, lists the user's processes and checks that a process id that does not exist is refused. Only the accounts given with `-Principal` may connect, so run it as the service account (`runas`), or register your own administrator account for the test as well and take it out afterwards by running the script again with the service account only:

   ```powershell
   .\Install-SssJea.ps1 -Principal 'CORP\svc-sss$','CORP\admin.alice'   # for the test
   .\Install-SssJea.ps1 -Principal 'CORP\svc-sss$'                      # afterwards
   ```

   "Access is denied" in the test means the account running it is not in that list.

## Using it

Switch on the **expert view** (button in the header). Every live session now has a **Processes** button next to *Details*. It opens a list of the user's processes on the machine of that session:

- By default only **applications** are shown (filter in the dialog); untick it to see system and background processes as well. The endpoint runs in session 0, where Windows reports no window titles of other sessions, so "application" is a best guess from the program path: everything outside the Windows folder and the Citrix/FSLogix infrastructure, plus well-known Windows programs such as Explorer or Notepad.
- **Close** asks the application to close its main window, like clicking the X, so the user can still save. This works only where Windows exposes the window to the endpoint; for another session it usually answers "no window" and **End now** is the way.
- **End now** kills the process. Unsaved work is lost. Both ask for confirmation first.
- The helpdesk finds the same button when it has opened a user (Helpdesk tab).

On a multi-session host (RDS server) the list shows the user's processes of that machine; if the user has more than one session on it, they are all listed.

## Updating the endpoint

New SSS releases can add commands to the endpoint (version 0.4 added `Get-SssProfile`). Run `Install-SssJea.ps1 -Principal ...` again on the VDAs after such an update; it replaces the registration. Until then the other features keep working and the profile button explains that the endpoint is out of date.

## What can go wrong

| Message or result | Cause |
|---|---|
| "The machine of the session cannot be reached for process control" | WinRM blocked, the endpoint is not registered on that VDA, or the VDA is a different machine than expected. Check with `Test-SssCitrix.ps1 -Vda`, and `Get-PSSessionConfiguration SSS.Processes` on the VDA |
| `Access is denied` in the test | the account is not the `-Principal` of `Install-SssJea.ps1` |
| `no-window` | the process has no window to close; use *End now* if it must stop |
| `not-closed` | the application ignored the close request (for example a dialog is open) |
| `protected` | a Citrix session process; ending it would break the session |
| `not-owned` / `not-found` | the process already ended, or it is not the user's |
| The button is missing | feature off, audience `Helpdesk`, expert view off, or the session is not live |

## What this exposes, and what it does not

The endpoint is the only new door on the VDA. What someone who controlled the SSS service account could do through it, and what stops them:

| Capability | Limit |
|---|---|
| list processes, window titles and program paths of a user's session | read only; a user SID must be passed in; only that user's processes are returned |
| end a process | only processes owned by the SID passed in; never system processes; never `wfshell`, `ctxgfx`, `cmstart`; no commands or scripts can be sent |
| read the profile type, sizes, folder names, FSLogix and UPM settings | read only; file **contents** are never read, only names and sizes |
| anything else on the VDA | not possible: the session is restricted to these three commands and runs as a temporary account that exists only for the call |

Remaining risk, honestly: an attacker who owns the SSS service account can end the processes of **any** user on the VDAs that run the endpoint (a denial of service) and read process and folder names. They cannot run code, read files or change the system through it. Reduce the exposure with:

- `-AllowedFrom` on the VDA, so only the SSS server(s) can connect;
- `Sss:Citrix:Processes:Audience = Helpdesk`, if ordinary users should not end processes themselves;
- a **gMSA** as service account (no password to steal) and a small, watched helpdesk group;
- keeping test accounts out of `-Principal` after testing;
- collecting the JEA transcripts and the SSS audit trail (`end-process`, `profile-info`) in your log system.

## Security notes

- This feature gives the SSS service account a path to the VDAs. Keep it limited to the three commands as shipped; do not widen the role capability.
- JEA transcripts show every call. Collect them with your usual tools if you need a second trail next to SSS's audit file.
- Audience `Helpdesk` removes the feature for ordinary users and keeps it for support staff.
- The process names and window titles of a user can contain private information. They are shown only to that user and to the helpdesk, and are not written to the audit trail (only the process id).
