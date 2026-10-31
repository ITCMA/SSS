using Sss.Web.Models;

namespace Sss.Web.Citrix;

public sealed record BrokerSession(
    string Uid,
    string? SessionKey,
    string State,
    string Type,
    string? DesktopGroup,
    string? Machine,
    DateTimeOffset? StartTime,
    int? IdleSeconds,
    string? ClientName,
    string? ClientAddress,
    IReadOnlyList<string> Apps,
    string? UserName = null,
    string? UserSid = null);

public sealed record BrokerProcess(int Id, string Name, int SessionId, DateTimeOffset? Started, int MemoryMb, string? Window, bool IsApp = false);

public sealed record BrokerProcessList(string? Machine, IReadOnlyList<BrokerProcess> Processes);

public sealed record BrokerLogoffResult(IReadOnlyList<string> Accepted, IReadOnlyList<string> Rejected);

/// <summary>Live view and log-off through the Citrix Broker SDK.</summary>
public interface IBrokerClient
{
    Task<IReadOnlyList<BrokerSession>> ListAsync(string userSid, CancellationToken ct);

    /// <summary>All sessions of the farm (helpdesk overview), optionally only users whose name contains
    /// <paramref name="userFilter"/>. <c>Truncated</c> is true when the farm has more than <paramref name="max"/> sessions.</summary>
    Task<(IReadOnlyList<BrokerSession> Sessions, bool Truncated)> ListAllAsync(string? userFilter, int max, CancellationToken ct);

    /// <summary>Starts the log-off (Stop-BrokerSession is asynchronous). Sessions not owned by
    /// <paramref name="userSid"/> are rejected again inside the worker.</summary>
    Task<BrokerLogoffResult> LogoffAsync(string userSid, IReadOnlyCollection<string> uids, CancellationToken ct);

    /// <summary>Shows a message in the given sessions; sessions not owned by <paramref name="userSid"/> are skipped by the worker.
    /// Returns the uids that were sent the message.</summary>
    Task<IReadOnlyList<string>> MessageAsync(string userSid, IReadOnlyCollection<string> uids, string title, string text, CancellationToken ct);

    /// <summary>Processes of the user on the machine of the given session (through the JEA endpoint on the VDA).
    /// The worker checks again that the session belongs to <paramref name="userSid"/>.</summary>
    Task<BrokerProcessList> ProcessesAsync(string userSid, string uid, CancellationToken ct);

    /// <summary>Ends one process of that session: closes its main window, or kills it with <paramref name="force"/>.
    /// Returns closed, killed, no-window, not-closed, not-owned, not-found or protected.</summary>
    Task<string> EndProcessAsync(string userSid, string uid, int processId, bool force, CancellationToken ct);

    /// <summary>Profile type and size of the user on the machine of the given session (JEA command Get-SssProfile).</summary>
    Task<ProfileInfo> ProfileAsync(string userSid, string uid, int scanSeconds, CancellationToken ct);

    /// <summary>Round trip to a worker (status page).</summary>
    Task PingAsync(CancellationToken ct);
}
