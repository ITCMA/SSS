using Sss.Web.Models;

namespace Sss.Web.Providers;

/// <summary>Expert view: all data Citrix collected about one session.</summary>
public interface ISessionDetailsProvider
{
    /// <summary>Returns null when the session does not exist or does not belong to <paramref name="user"/>.
    /// <paramref name="full"/> (helpdesk) also includes infrastructure details such as hosting and desktop group properties.</summary>
    Task<SessionDetails?> GetDetailsAsync(UserIdentity user, string sessionKey, bool full, CancellationToken ct);
}
