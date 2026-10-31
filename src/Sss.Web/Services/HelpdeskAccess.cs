using System.Security.Principal;

namespace Sss.Web.Services;

/// <summary>Decides whether the caller belongs to the helpdesk group. The group is resolved to a SID once at
/// startup and compared with the group SIDs in the caller's Windows token. Fails closed: no group configured or
/// not resolvable means nobody is helpdesk.</summary>
public sealed class HelpdeskAccess
{
    private readonly SssOptions _options;
    private readonly string? _groupSid;

    public HelpdeskAccess(SssOptions options, ILogger<HelpdeskAccess> log)
    {
        _options = options;
        if (string.IsNullOrWhiteSpace(options.Helpdesk.Group)) return;
        if (!OperatingSystem.IsWindows())
        {
            log.LogWarning("Sss:Helpdesk:Group is only evaluated on Windows; the helpdesk feature is off.");
            return;
        }
        try
        {
            _groupSid = ((SecurityIdentifier)new NTAccount(options.Helpdesk.Group).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            log.LogError(ex, "Sss:Helpdesk:Group '{Group}' could not be resolved; the helpdesk feature is off.", options.Helpdesk.Group);
        }
    }

    public bool Enabled => IsDev ? _options.Auth.DevIsHelpdesk : _groupSid is not null;

    private bool IsDev => _options.Auth.Mode.Equals("Dev", StringComparison.OrdinalIgnoreCase);

    public bool IsHelpdesk(HttpContext http)
    {
        if (IsDev) return _options.Auth.DevIsHelpdesk;
        return _groupSid is not null
            && OperatingSystem.IsWindows()
            && http.User.Identity is WindowsIdentity { IsAuthenticated: true } wi
            && InGroup(wi, _groupSid);
    }

    // The token of a Windows logon lists all (also nested) group SIDs of the caller.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool InGroup(WindowsIdentity wi, string groupSid) =>
        wi.Groups?.Any(g => g.Value == groupSid) == true;
}
