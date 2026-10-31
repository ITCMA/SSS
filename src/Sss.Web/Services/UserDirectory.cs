using System.Collections.Concurrent;
using System.Security.Principal;

namespace Sss.Web.Services;

/// <summary>Turns a SID into a readable DOMAIN\name for the audit trail and the helpdesk UI.</summary>
public interface IUserDirectory
{
    string NameOf(string sid);
}

/// <summary>Windows account lookup (Active Directory). Falls back to the SID when a name cannot be resolved.</summary>
public sealed class WindowsUserDirectory : IUserDirectory
{
    private readonly ConcurrentDictionary<string, string> _cache = new();

    public string NameOf(string sid) => _cache.GetOrAdd(sid, Lookup);

    private static string Lookup(string sid)
    {
        if (!OperatingSystem.IsWindows()) return sid;
        try { return ((NTAccount)new SecurityIdentifier(sid).Translate(typeof(NTAccount))).Value; }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException or ArgumentException) { return sid; }
    }
}
