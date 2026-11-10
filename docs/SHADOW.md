# Remote support: shadowing a session

[Deutsche Version](SHADOW.de.md)

The helpdesk (every level by default) can look at a user's session, and with the user's agreement take over mouse and keyboard, straight from SSS: the button **Remote support** in the user's view. It uses the shadowing of Windows (`mstsc /shadow`), which works for Citrix HDX sessions as well.

## How it works

```
Browser (SSS) --1--> SSS server: rights, reason, owner of the session, audit entry "shadow"
      |                 reads the Windows session id on the VDA (JEA endpoint)
      | <--2-- link  sss-shadow://<vda>/<session id>?control=1
      v
Starter on the helpdesk PC --3--> mstsc /v:<vda> /shadow:<id> /control  --> the user is asked
```

1. SSS accepts the request only from helpdesk roles whose level is allowed (`Sss:Shadow:AllowLevel1/2/3`, all on by default), with the reason (if you require one), and only for a **live session of that user**. The VDA and the session id come from the VDA through the read-only JEA command `Get-SssHdx` (so this needs the [JEA endpoint](PROCESSES.md)). If the user has several sessions on that VDA and SSS cannot tell which one it is, it refuses instead of guessing.
2. The answer is a link of the form `sss-shadow://<host>/<id>?control=0|1`.
3. A small **starter** on the helpdesk PC receives the link from the browser, checks it strictly (host name and numbers only) and starts `mstsc.exe /v:<host> /shadow:<id> [/control]`. **The consent prompt for the user always stays on**; SSS never uses `/noConsentPrompt`.

SSS does **not** grant the right to shadow. Windows on the VDA decides who may, and asks the user.

## Set up

1. **VDAs**: the shadowing rules. Allow remote control with the user's permission by group policy *Set rules for remote control of Remote Desktop Services user sessions* (Computer Configuration > Administrative Templates > Windows Components > Remote Desktop Services > Remote Desktop Session Host > Connections), value *Full Control with user's permission* or *View Session with user's permission*. The helpdesk group needs the permission to shadow on the VDA (for example through the session permissions of the VDA). Test it first without SSS: `mstsc /v:<vda> /shadow:<id> /control`. If that works, SSS can start it.
2. **VDAs**: run the current `Install-SssJea.ps1` (the endpoint needs `Get-SssHdx`, see [HDX](HDX.md)).
3. **SSS server**, `sss.config.json`:

   ```json
   { "Sss": { "Shadow": { "Enabled": true, "AllowLevel1": true, "AllowLevel2": true, "AllowLevel3": true, "Control": true }, "Citrix": { "Processes": { "Enabled": true } } } }
   ```

   The shadow settings are on by default; set `Enabled` or one `AllowLevel` to `false` to switch it off, `Control: false` makes it view only. The button needs `Processes:Enabled` as well. **Restart the application** (recycle the app pool) after changing the file. `https://<server>/api/me` shows `"shadowState"`: `ok`, `disabled`, `processes-off`, `cloud`, `level-off` or `no-role` tells why a button is missing.
4. **Every helpdesk PC** (once per Windows user, no administrator rights): copy `Register-SssShadow.ps1` and `Start-SssShadow.ps1` from the release, then

   ```powershell
   .\Register-SssShadow.ps1 -AllowedHostSuffix 'corp.local'
   ```

   `-AllowedHostSuffix` limits the starter to VDAs of your domain (recommended). `-Remove` undoes it. Test without SSS: `.\Start-SssShadow.ps1 -DryRun "sss-shadow://vda01.corp.local/3?control=1"` prints the command it would run.

The first click makes the browser ask whether it may open the link; tick "always allow". With Edge or Chrome you can allow it for the SSS address by policy (`AutoLaunchProtocolsFromOrigins`).

## Security

- The starter runs only `mstsc.exe` and puts only the checked host name and numbers on its command line. It does not accept other links, and with the allowed host suffix it refuses VDAs outside your domain.
- Any web page could try to open an `sss-shadow://` link on a PC where the starter is registered. The worst case is that `mstsc` asks to shadow a session in your domain; Windows requires the right to do it and the user must agree. Keep the host suffix set.
- The audit entry `shadow` records who asked, for which user and session, on which VDA and why. It records the **request**; whether the connection was made and accepted is visible only in the event logs of the VDA (Remote Desktop Services).

## Limits

- Needs Windows PCs for the helpdesk (mstsc and the registry link type).
- Citrix Cloud (read-only mode) has no VDA access; the button is not offered there.
- If nothing happens after the click, the starter is not registered on that PC (the message in SSS says so), or the browser blocked the link.
