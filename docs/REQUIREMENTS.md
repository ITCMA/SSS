# Requirements and dependencies

[Deutsch](REQUIREMENTS.de.md)

## SSS server (IIS)

| What | Detail |
|---|---|
| Operating system | Windows Server 2016 or later (tested on 2019) with IIS |
| IIS features | *Windows Authentication* and *Application Initialization* (`install.ps1` installs them on a server OS), a site with an HTTPS binding |
| ASP.NET Core Module V2 | from the ".NET Hosting Bundle" of any current .NET 10 release. The application itself is **self-contained**, no .NET runtime is needed |
| Windows PowerShell 5.1 | included in Windows. The Citrix snap-ins are .NET Framework based, so PowerShell 7 is not used |
| Citrix PowerShell snap-ins | `Citrix.Broker.Admin.V2` on the SSS server: installed with Studio, or the *Remote PowerShell SDK* from the Citrix download page. Required for sessions, log-off, messages |
| Service account | a group-managed service account (gMSA) as identity of the app pool, see [Permissions](PERMISSIONS.md). A normal domain account works too; the app pool identity (`IIS AppPool\SSS`) cannot reach the controller |
| Disk | about 150 MB for the application, plus small, growing audit files in `logs` |
| Memory | about 100 MB for the web process plus 60-100 MB per PowerShell worker (`Workers`, default 2) |

## Citrix site

| What | Detail |
|---|---|
| Versions | Citrix Virtual Apps and Desktops 2203, 2402, 2507 and 2607 (LTSR) tested. Other current releases should work; run `Test-SssCitrix.ps1` to check the Monitor field names |
| Editions | any; the depth of the Monitor data depends on your edition and the grooming settings of the site |
| Delivery Controller | reachable from the SSS server on TCP 80 or 443 (Broker SDK, Monitor OData) |
| Delegated administration | the service account is a Citrix administrator with the role *Help Desk Administrator* (or a custom role that may read sessions, log off, send messages and read Monitor data), scope *All* |
| Monitor service | reachable over `http://` or `https://`; see [Monitor over HTTPS](MULTI-SERVER.md#monitor-over-https) |
| Process control (optional) | WinRM (TCP 5985) from the SSS server to the VDAs and the JEA endpoint from `Install-SssJea.ps1` on each VDA, see [Applications and processes](PROCESSES.md) |

## Active Directory

| What | Detail |
|---|---|
| KDS root key | required once for gMSAs (`Add-KdsRootKey`) |
| Helpdesk group | optional, a security group whose members get the helpdesk features |
| Kerberos | correct time and DNS. Behind a load balancer alias an SPN `HTTP/<alias>` on the gMSA ([Several controllers and servers](MULTI-SERVER.md)) |
| Trusts | users from trusted domains work as long as their SID resolves; names are shown as `DOMAIN\user` |

## Client (browser)

Any current browser (Chrome, Edge, Firefox, Safari) with JavaScript. Windows authentication needs a browser that signs in automatically for intranet sites (Edge and Chrome do for sites in the *Local intranet* zone; add the URL through a GPO if needed). The interface has no external dependencies: no CDN, no fonts, no tracking.

## Network and ports

| From | To | Port | Purpose |
|---|---|---|---|
| Browser | SSS server | 443 | the web interface |
| SSS server | Delivery Controller | 80 or 443 | Broker SDK, Monitor OData |
| SSS server | VDA | 5985 (WinRM) | only with process control |
| SSS server | domain controller | usual AD ports | gMSA, Kerberos, group lookup |
| SSS server | file share | 445 | only with `DataProtection:KeyDirectory` or a shared audit folder |

No inbound connection from the controllers or VDAs to SSS is needed, and SSS needs no internet access.

## Third-party components

The web application has **no third-party NuGet packages**; it uses only what ships with .NET. The web interface is plain JavaScript without libraries. The test project uses xUnit and Microsoft.AspNetCore.Mvc.Testing. The build and CI actions are pinned to commit hashes and updated by Dependabot.

## Trademarks

Citrix, Citrix Virtual Apps and Desktops and Director are trademarks of Cloud Software Group, Inc. SSS is an independent project and not affiliated with or endorsed by Citrix.
