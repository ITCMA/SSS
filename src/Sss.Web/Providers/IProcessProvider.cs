using Sss.Web.Models;

namespace Sss.Web.Providers;

/// <summary>Processes of a session, shown and ended on the machine the session runs on. Only reached through
/// <c>ProcessService</c>, which has already checked that the session belongs to the user.</summary>
public interface IProcessProvider
{
    Task<ProcessListResult> ListProcessesAsync(string userSid, string uid, CancellationToken ct);

    Task<ProfileInfo> GetProfileAsync(string userSid, string uid, int scanSeconds, CancellationToken ct);

    /// <summary>The Citrix policy values in force in the user's sessions on that machine (read only).</summary>
    Task<PolicyListResult> GetPoliciesAsync(string userSid, string uid, CancellationToken ct) => throw new NotSupportedException();

    /// <summary>Live HDX values of the user's sessions on that machine: virtual-channel counters, redirected printers, client drives (read only).</summary>
    Task<BrokerHdx> GetHdxAsync(string userSid, string uid, CancellationToken ct, bool idsOnly = false) => throw new NotSupportedException();

    /// <summary>closed, killed, no-window, not-closed, not-owned, not-found or protected.</summary>
    Task<string> EndProcessAsync(string userSid, string uid, int processId, bool force, CancellationToken ct);
}
