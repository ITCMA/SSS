# Contributing

Thanks for helping. SSS is small on purpose: fast, light, secure, simple to run. Changes that keep it that way are very welcome.

## Ground rules

- **Security first.** A user may only ever see and affect their own sessions (helpdesk members, only through the separate helpdesk endpoints). Read [SECURITY.md](SECURITY.md) before touching identity, log-off or the PowerShell worker. Never log or return secrets, exception details or other users' data.
- **No new dependencies** in the web application unless there is a strong reason. The front end has no libraries and writes text only with `textContent`.
- Keep the **five languages** in sync: add every new UI text to all `src/Sss.Web/wwwroot/i18n/*.json`.
- The worker script must stay compatible with **Windows PowerShell 5.1**.
- Add or update tests. `dotnet test` must pass; it runs the real worker script against fake Broker cmdlets when `pwsh` is installed.

## Development

```bash
dotnet test
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5080 dotnet run --project src/Sss.Web
```

Development mode uses a fixed demo user and sample data, so no Citrix is needed. Set `Sss__Auth__DevIsHelpdesk=true` to see the helpdesk features.

## Script checks

`tests/scripts/Check-Scripts.ps1` checks the PowerShell scripts without a Citrix site and, on Windows, runs the code of the JEA commands for the current user. CI runs it on `windows-latest` in Windows PowerShell 5.1 and PowerShell 7. On a Mac or Linux machine run `pwsh -File tests/scripts/Check-Scripts.ps1` (the Windows part is skipped).

## Screenshots

README screenshots come only from the built-in demo mode, never from a real site. With the development server running (`ASPNETCORE_ENVIRONMENT=Development`), headless Chrome reproduces them: `?lang=en&theme=light` for the overview and `&shot=details`, `&shot=profile` or `&shot=processes` for the expert dialogs (these parameters work in demo mode only). Look at every image before committing: it must show demo data (`DEMO\demo.user`, `10.20.x.x`, `*.demo.local`) and nothing else.

## Reporting a problem with a real Citrix site

Run `Test-SssSetup.ps1` and `Test-SssCitrix.ps1` on the server and paste the output into the issue (run both with `-Redact`, which hides user, domain, host names, SIDs and IP addresses). Please name your Citrix version.

## Pull requests

Small, focused changes with a description of the *why*. Do not reformat unrelated code. Security problems: do **not** open a public issue, see [SECURITY.md](SECURITY.md).
