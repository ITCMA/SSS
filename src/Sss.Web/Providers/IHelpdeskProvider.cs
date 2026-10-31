using Sss.Web.Models;

namespace Sss.Web.Providers;

/// <summary>What the helpdesk needs beyond <see cref="ISessionProvider"/>: finding users and an overview of all
/// live sessions. Only ever reached through <c>HelpdeskService</c>, which checks the caller's group membership.</summary>
public interface IHelpdeskProvider
{
    /// <summary>All live sessions of the farm (optionally only users whose name contains <paramref name="userFilter"/>).</summary>
    Task<HelpdeskLiveResult> ListLiveAsync(string? userFilter, int max, CancellationToken ct);

    /// <summary>Users whose name contains <paramref name="query"/>: those with a live session and, if available, users that only have history.</summary>
    Task<IReadOnlyList<HelpdeskUser>> FindUsersAsync(string query, int max, CancellationToken ct);

    /// <summary>Shows a message in the given sessions (already verified as owned by <paramref name="userSid"/>).
    /// Returns the uids that accepted it.</summary>
    Task<IReadOnlyList<string>> SendMessageAsync(string userSid, IReadOnlyCollection<string> uids, string title, string text, CancellationToken ct);
}
