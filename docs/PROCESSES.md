# Ending applications and processes

[Deutsch](PROCESSES.de.md)

Users can see the applications and processes of their own sessions and close them (for example a frozen Excel) without logging off. The helpdesk can do the same for any user's session. The feature is **off by default** and needs a small endpoint on every VDA.

## How it works

Citrix offers no supported Broker or Monitor command to end a single process (Director uses its own channel to the VDA). SSS therefore uses **Just Enough Administration (JEA)**, a restricted PowerShell endpoint of Windows:

```
Browser --> SSS (IIS) --> PowerShell worker --WinRM--> JEA endpoint "SSS.Processes" on the VDA
                          (service account)             Get-SssProcess / Stop-SssProcess
```

- The JEA endpoint offers exactly **two commands**. The service account cannot run anything else on the VDA through it.
- It runs as a temporary local administrator (a *virtual account* that exists only for that call), so the service account itself needs no administrator rights on the VDAs.
- **Only the user's own processes are touched.** Both commands take the user's SID and compare it with the owner of every process; system processes, other users' processes and unknown ids are refused (`not-owned`, `not-found`). The Citrix session processes `wfshell.exe`, `ctxgfx.exe` and `cmstart.exe` are protected (`protected`).
- **SSS checks the session twice.** The web application accepts a session only when it is one of the target's live sessions; the PowerShell worker looks it up again in the Broker and refuses it unless the session belongs to that user. Only the machine of that session is contacted.
- Every call is **audited** (`list-processes`, `end-process`, with the helpdesk actor, the process id and whether it was forced) and JEA writes its own **transcript** on the VDA.

## Set up

1. **On every VDA** (or in the master image, or by startup script): run `Install-SssJea.ps1` from the release as administrator.

   ```powershell
   .\Install-SssJea.ps1 -Principal 'CORP\svc-sss$'
   ```

   It enables WinRM (TCP 5985), registers the endpoint `SSS.Processes` for exactly that account and writes the transcripts to `C:\ProgramData\SSS\jea-transcripts`. It changes nothing else. `-ConfigurationName` and `-TranscriptDirectory` change the defaults. `.\Install-SssJea.ps1 -Remove` unregisters the endpoint again.

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

   The script connects to the endpoint, lists the user's processes and checks that a process id that does not exist is refused. Run it as the service account if you can (`runas`), otherwise with your own account after adding yourself to the JEA role (`-Principal`).

## Using it

Switch on the **expert view** (button in the header). Every live session now has a **Processes** button next to *Details*. It opens a list of the user's processes on the machine of that session:

- By default only **applications with a window** are shown (filter in the dialog); untick it to see background processes as well.
- **Close** asks the application to close its main window, like clicking the X. Unsaved work can still be saved by the user. Applications without a window answer "no window".
- **End now** kills the process. Unsaved work is lost. Both ask for confirmation first.
- The helpdesk finds the same button when it has opened a user (Helpdesk tab).

On a multi-session host (RDS server) the list shows the user's processes of that machine; if the user has more than one session on it, they are all listed.

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

## Security notes

- This feature gives the SSS service account a path to the VDAs. Keep it limited to the two commands as shipped; do not widen the role capability.
- JEA transcripts show every call. Collect them with your usual tools if you need a second trail next to SSS's audit file.
- Audience `Helpdesk` removes the feature for ordinary users and keeps it for support staff.
- The process names and window titles of a user can contain private information. They are shown only to that user and to the helpdesk, and are not written to the audit trail (only the process id).
