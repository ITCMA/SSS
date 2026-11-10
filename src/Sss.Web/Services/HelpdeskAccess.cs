using System.Security.Principal;

namespace Sss.Web.Services;

/// <summary>What a caller may do beyond their own sessions. Level 1 only looks, level 2 may also log off and send messages,
/// level 3 may also read processes and profiles of other users and end processes. Audit and Admin stand apart: a helpdesk
/// member sees neither unless that role is granted too.</summary>
[Flags]
public enum Cap
{
    None = 0,
    View = 1,
    Act = 2,
    Deep = 4,
    Audit = 8,
    Admin = 16,
}

/// <summary>Decides which roles the caller has. Each group of <c>Sss:Roles</c> (and the legacy <c>Sss:Helpdesk:Group</c>) is
/// resolved to a SID once at startup and compared with the group SIDs in the caller's Windows token. Fails closed: a group
/// that is not configured or cannot be resolved grants nothing.</summary>
public sealed class HelpdeskAccess
{
    private readonly SssOptions _options;
    private readonly (string? Sid, Cap Caps)[] _groups;
    private readonly bool _adminGroup;

    public HelpdeskAccess(SssOptions options, ILogger<HelpdeskAccess> log)
    {
        _options = options;
        var r = options.Roles;
        _adminGroup = !string.IsNullOrWhiteSpace(r.AdminGroup);
        _groups =
        [
            (Resolve(options.Helpdesk.Group, "Sss:Helpdesk:Group", log), Cap.View | Cap.Act | Cap.Deep),
            (Resolve(r.Level1Group, "Sss:Roles:Level1Group", log), Cap.View),
            (Resolve(r.Level2Group, "Sss:Roles:Level2Group", log), Cap.View | Cap.Act),
            (Resolve(r.Level3Group, "Sss:Roles:Level3Group", log), Cap.View | Cap.Act | Cap.Deep),
            (Resolve(r.AuditorGroup, "Sss:Roles:AuditorGroup", log), Cap.Audit),
            (Resolve(r.AdminGroup, "Sss:Roles:AdminGroup", log), Cap.Admin),
        ];
    }

    private static string? Resolve(string group, string key, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(group)) return null;
        if (!OperatingSystem.IsWindows())
        {
            log.LogWarning("{Key} is only evaluated on Windows; the role is off.", key);
            return null;
        }
        try
        {
            return ((SecurityIdentifier)new NTAccount(group).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            log.LogError(ex, "{Key} '{Group}' could not be resolved; the role is off.", key, group);
            return null;
        }
    }

    private bool IsDev => _options.Auth.Mode.Equals("Dev", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when at least one role group is configured (or, in Dev mode, demo roles are on).</summary>
    public bool Enabled => IsDev ? DevCaps() != Cap.None : _groups.Any(g => g.Sid is not null);

    public Cap Caps(HttpContext http)
    {
        var caps = IsDev ? DevCaps() : WindowsCaps(http);
        // Without a dedicated admin group, every helpdesk member keeps the status page, as before roles existed.
        if (!_adminGroup && caps.HasFlag(Cap.View)) caps |= Cap.Admin;
        return caps;
    }

    public bool Has(HttpContext http, Cap cap) => (Caps(http) & cap) == cap;

    /// <summary>True for every role that sees other users (level 1 and up).</summary>
    public bool IsHelpdesk(HttpContext http) => Has(http, Cap.View);

    /// <summary>Helpdesk level of the caller: 3, 2, 1, or 0 for no helpdesk role.</summary>
    public int Level(HttpContext http)
    {
        var c = Caps(http);
        return c.HasFlag(Cap.Deep) ? 3 : c.HasFlag(Cap.Act) ? 2 : c.HasFlag(Cap.View) ? 1 : 0;
    }

    /// <summary>Highest role name of the caller for the audit log.</summary>
    public string RoleName(HttpContext http)
    {
        var c = Caps(http);
        var parts = new List<string>();
        if (c.HasFlag(Cap.Deep)) parts.Add("L3");
        else if (c.HasFlag(Cap.Act)) parts.Add("L2");
        else if (c.HasFlag(Cap.View)) parts.Add("L1");
        if (c.HasFlag(Cap.Audit)) parts.Add("Auditor");
        if (_adminGroup && c.HasFlag(Cap.Admin)) parts.Add("Admin");
        return parts.Count == 0 ? "User" : string.Join('+', parts);
    }

    // Dev mode: Sss:Auth:DevRoles = "L1,L2,L3,Auditor,Admin" (any); the old DevIsHelpdesk flag means level 3.
    private Cap DevCaps()
    {
        var caps = Cap.None;
        foreach (var role in _options.Auth.DevRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            caps |= role.ToUpperInvariant() switch
            {
                "L1" => Cap.View,
                "L2" => Cap.View | Cap.Act,
                "L3" => Cap.View | Cap.Act | Cap.Deep,
                "AUDITOR" => Cap.Audit,
                "ADMIN" => Cap.Admin,
                _ => Cap.None,
            };
        if (_options.Auth.DevIsHelpdesk) caps |= Cap.View | Cap.Act | Cap.Deep;
        return caps;
    }

    private Cap WindowsCaps(HttpContext http)
    {
        if (!OperatingSystem.IsWindows() || http.User.Identity is not WindowsIdentity { IsAuthenticated: true } wi) return Cap.None;
        return Token(wi);
    }

    // The token of a Windows logon lists all (also nested) group SIDs of the caller.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private Cap Token(WindowsIdentity wi)
    {
        var sids = wi.Groups?.Select(g => g.Value).ToHashSet() ?? [];
        var caps = Cap.None;
        foreach (var (sid, c) in _groups)
            if (sid is not null && sids.Contains(sid)) caps |= c;
        return caps;
    }
}
