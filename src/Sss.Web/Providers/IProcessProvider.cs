using Sss.Web.Models;

namespace Sss.Web.Providers;

/// <summary>Processes of a session, shown and ended on the machine the session runs on. Only reached through
/// <c>ProcessService</c>, which has already checked that the session belongs to the user.</summary>
public interface IProcessProvider
{
    Task<ProcessListResult> ListProcessesAsync(string userSid, string uid, CancellationToken ct);

    /// <summary>closed, killed, no-window, not-closed, not-owned, not-found or protected.</summary>
    Task<string> EndProcessAsync(string userSid, string uid, int processId, bool force, CancellationToken ct);
}
