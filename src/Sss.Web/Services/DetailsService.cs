using Sss.Web.Models;
using Sss.Web.Providers;

namespace Sss.Web.Services;

/// <summary>Expert view of one session. The provider only returns sessions owned by the given user, so asking for
/// somebody else's session key simply yields "not found".</summary>
public sealed class DetailsService(ISessionDetailsProvider provider)
{
    public Task<SessionDetails?> GetAsync(UserIdentity user, string sessionKey, bool full, CancellationToken ct) =>
        provider.GetDetailsAsync(user, sessionKey, full, ct);
}
