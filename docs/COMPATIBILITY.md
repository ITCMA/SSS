# Versioning and compatibility

[Deutsch](COMPATIBILITY.de.md)

SSS uses [Semantic Versioning](https://semver.org): `MAJOR.MINOR.PATCH`. Until 1.0.0 the project is in beta; this page says what will be stable from 1.0.0 on, and what is allowed to change before.

## What is part of the stable surface

| Surface | Promise from 1.0.0 |
|---|---|
| Keys, defaults and meaning in `sss.config.json` ([configuration reference](CONFIGURATION.md)) | A key is not renamed, removed or given a different meaning in a minor or patch release. Removal happens only in a major release, after a deprecation notice in the changelog and a warning in the Status tab. |
| Switches of `install.ps1`, `Install-SssJea.ps1`, `Test-SssSetup.ps1`, `Test-SssCitrix.ps1` | Same rule as configuration keys. |
| The audit trail (`audit-*.jsonl`): field names and the meaning of `action` and `result` | Existing fields stay. New fields and new `action` values may be added in minor releases; consumers must ignore fields they do not know. |
| `GET /healthz` (status values `ok`, `degraded`, `unavailable`, HTTP 200 or 503) | Stable. |
| Upgrading with `install.ps1 -Update` from any 1.x release to a later 1.x release, with rollback | Supported and tested. |

## What is not stable

- The web interface API under `/api/...`. It belongs to the bundled interface and may change in any release. If you need to integrate, open an issue so a supported endpoint can be added.
- Translations, wording and layout of the interface.
- The structure of the expert view (groups, series, tables), because it follows what Citrix delivers.
- The JEA endpoint's command names and parameters. After an SSS update that changes them the release notes say so, and `Install-SssJea.ps1` has to be run again on the VDAs. Older endpoints keep working for the features they support.
- Internal file names, log texts and the PowerShell worker protocol.

## Before 1.0.0

Minor versions (`0.x.0`) may change configuration keys or switches. Every such change is listed under *Changed* or *Removed* in the [changelog](../CHANGELOG.md), and unknown keys are reported so a rename does not go unnoticed. Patch versions (`0.x.y`) only fix bugs.

## Supported environments

| Component | Supported |
|---|---|
| Citrix Virtual Apps and Desktops | 2203, 2402, 2507, 2607 (LTSR) and later current releases; field names are checked with `Test-SssCitrix.ps1` |
| SSS server | Windows Server 2016 or later with IIS, ASP.NET Core Module V2 |
| VDAs (process control only) | Windows Server 2016 or later, Windows PowerShell 5.1, WinRM |
| Browsers | current Chrome, Edge, Firefox, Safari |

A release is supported until the next minor release is out; security fixes go to the latest release. See [SECURITY.md](../SECURITY.md).

## How to update

1. Read the changelog entry of the new release.
2. Download the zip and check the `.sha256` file.
3. `.\install.ps1 -Update` on every SSS server, one after the other. The previous version stays in `<install folder>.previous` and is restored by itself if the new one does not answer.
4. `.\Test-SssSetup.ps1`. If the release notes say the VDA endpoint changed, run `Install-SssJea.ps1` again on the VDAs.
