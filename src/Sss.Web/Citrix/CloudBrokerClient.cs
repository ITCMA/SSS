using Sss.Web.Models;

namespace Sss.Web.Citrix;

/// <summary>The "Broker" of a Citrix Cloud site: with DaaS there is no snap-in to ask, so the live sessions are read from the Monitor service.
/// Everything that changes a session or needs the VDA (log off, message, processes, policies, profile) is not available in this read-only mode
/// and says so with <see cref="ActionNotSupportedException"/> (HTTP 501).</summary>
public sealed class CloudBrokerClient(IMonitorClient monitor) : IBrokerClient
{
    private const string ReadOnly = "Not available with Citrix Cloud: SSS only reads the Monitor data in this mode.";

    // Monitor connection states as Director lists them: 2 = disconnected, 3 = terminated; everything else counts as active.
    private static string State(int? s) => s == 2 ? "Disconnected" : "Active";

    private static BrokerSession Map(MonitorSession m, string? name = null, string? sid = null) => new(
        m.SessionKey, m.SessionKey, State(m.ConnectionState), m.IsDesktop ? "Desktop" : "Application", m.DesktopGroup, m.Machine, m.Start, null,
        m.ClientName, m.ClientAddress, [], name, sid, m.StateChanged);

    public async Task<IReadOnlyList<BrokerSession>> ListAsync(string userSid, CancellationToken ct) =>
        (await monitor.GetLiveAsync(userSid, ct)).Where(m => m.ConnectionState is not 3).Select(m => Map(m, null, userSid)).ToList();

    public async Task<(IReadOnlyList<BrokerSession> Sessions, bool Truncated)> ListAllAsync(string? userFilter, int max, CancellationToken ct)
    {
        var live = (await monitor.GetFarmLiveAsync(ct)).Where(m => m.ConnectionState is not 3).ToList();
        var users = await monitor.GetUsersByIdAsync(live.Select(m => m.UserId).OfType<long>().Distinct(), ct);
        var rows = live.Select(m => (M: m, U: m.UserId is { } id && users.TryGetValue(id, out var u) ? u : null))
            .Where(x => x.U is not null && (string.IsNullOrWhiteSpace(userFilter) || x.U.Name.Contains(userFilter.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Select(x => Map(x.M, x.U!.Name, x.U.Sid)).ToList();
        return (rows.Take(max).ToList(), rows.Count > max);
    }

    public Task<BrokerLogoffResult> LogoffAsync(string userSid, IReadOnlyCollection<string> uids, CancellationToken ct) => throw new ActionNotSupportedException(ReadOnly);
    public Task<IReadOnlyList<string>> MessageAsync(string userSid, IReadOnlyCollection<string> uids, string title, string text, CancellationToken ct) => throw new ActionNotSupportedException(ReadOnly);
    public Task<BrokerProcessList> ProcessesAsync(string userSid, string uid, CancellationToken ct) => throw new ActionNotSupportedException(ReadOnly);
    public Task<string> EndProcessAsync(string userSid, string uid, int processId, bool force, CancellationToken ct) => throw new ActionNotSupportedException(ReadOnly);
    public Task<BrokerPolicies> PoliciesAsync(string userSid, string uid, CancellationToken ct) => throw new ActionNotSupportedException(ReadOnly);
    public Task<ProfileInfo> ProfileAsync(string userSid, string uid, int scanSeconds, CancellationToken ct) => throw new ActionNotSupportedException(ReadOnly);
    public Task<IReadOnlyList<DiscoveredController>> DiscoverAsync(string via, CancellationToken ct) => Task.FromResult<IReadOnlyList<DiscoveredController>>([]);

    /// <summary>The cloud answers when the Monitor service does.</summary>
    public Task PingAsync(CancellationToken ct) => monitor.PingAsync(ct);
}
