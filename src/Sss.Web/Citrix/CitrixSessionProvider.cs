using Sss.Web.Models;
using Sss.Web.Providers;
using Sss.Web.Services;

namespace Sss.Web.Citrix;

/// <summary>Live sessions and log-off come from the Broker SDK; history and quality metrics from the
/// Monitor Service. A Monitor failure degrades to warnings, a Broker failure is an error.
/// Also serves the helpdesk (farm-wide overview and user search).</summary>
public sealed class CitrixSessionProvider(
    IBrokerClient broker,
    IMonitorClient? monitor,
    QualityEvaluator quality,
    TimeProvider clock,
    ILogger<CitrixSessionProvider> log) : ISessionProvider, IHelpdeskProvider, ISessionDetailsProvider, IProcessProvider
{
    public const string WarnHistory = "history-unavailable";
    public const string WarnQuality = "quality-unavailable";

    public async Task<SessionQueryResult> GetSessionsAsync(UserIdentity user, TimeSpan historyWindow, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var live = await broker.ListAsync(user.Sid, ct);
        var warnings = new List<string>();

        IReadOnlyList<MonitorSession> monLive = [], monEnded = [];
        if (monitor is not null && historyWindow > TimeSpan.Zero)
        {
            var liveTask = TryAsync(() => monitor.GetLiveAsync(user.Sid, ct));
            var endedTask = TryAsync(() => monitor.GetEndedAsync(user.Sid, now - historyWindow, ct));
            await Task.WhenAll(liveTask, endedTask);
            if (liveTask.Result is { } l) monLive = l; else warnings.Add(WarnQuality);
            if (endedTask.Result is { } e) monEnded = e; else warnings.Add(WarnHistory);
        }

        var byKey = monLive.ToDictionary(m => m.SessionKey, StringComparer.OrdinalIgnoreCase);
        var sessions = new List<SessionInfo>();

        foreach (var b in live)
        {
            MonitorSession? m = b.SessionKey is not null && byKey.TryGetValue(b.SessionKey, out var found) ? found : null;
            sessions.Add(MapLive(b, m, now));
        }

        foreach (var m in monEnded.Where(m => m.End is not null))
        {
            sessions.Add(new SessionInfo(
                m.SessionKey,
                m.DesktopGroup ?? m.Machine ?? "Session",
                m.IsDesktop ? "Desktop" : "App",
                m.DesktopGroup ?? "",
                m.Machine ?? "",
                m.Start ?? m.End!.Value,
                m.End,
                SessionState.Ended,
                null,
                m.ClientName,
                m.ClientAddress,
                quality.Evaluate(m.IcaRttMs, null, m.LogonSeconds, null),
                m.SessionKey));
        }

        if (warnings.Count > 0) log.LogWarning("Monitor data incomplete: {Warnings}", string.Join(", ", warnings));
        return new SessionQueryResult(sessions, warnings);
    }

    public async Task<IReadOnlyList<string>> LogoffAsync(UserIdentity user, IReadOnlyCollection<string> uids, CancellationToken ct) =>
        (await broker.LogoffAsync(user.Sid, uids, ct)).Accepted;

    public Task<SessionDetails?> GetDetailsAsync(UserIdentity user, string sessionKey, bool full, CancellationToken ct) =>
        monitor is null ? Task.FromResult<SessionDetails?>(null) : monitor.GetDetailsAsync(user.Sid, sessionKey, full, ct);

    // ---- helpdesk -------------------------------------------------------------------------------

    public async Task<HelpdeskLiveResult> ListLiveAsync(string? userFilter, int max, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var (all, truncated) = await broker.ListAllAsync(userFilter, max, ct);
        // Quality metrics are not loaded for the overview (one Monitor call per user would be too expensive).
        var rows = all
            .Where(b => !string.IsNullOrEmpty(b.UserSid))
            .Select(b => new HelpdeskSession(MapLive(b, null, now), b.UserName ?? b.UserSid!, b.UserSid!))
            .ToList();
        return new HelpdeskLiveResult(rows, truncated);
    }

    public async Task<ProcessListResult> ListProcessesAsync(string userSid, string uid, CancellationToken ct)
    {
        var list = await broker.ProcessesAsync(userSid, uid, ct);
        return new ProcessListResult(list.Machine,
            list.Processes.Select(p => new ProcessEntry(p.Id, p.Name, p.Started, p.MemoryMb, p.Window)).ToList());
    }

    public Task<ProfileInfo> GetProfileAsync(string userSid, string uid, int scanSeconds, CancellationToken ct) =>
        broker.ProfileAsync(userSid, uid, scanSeconds, ct);

    public Task<string> EndProcessAsync(string userSid, string uid, int processId, bool force, CancellationToken ct) =>
        broker.EndProcessAsync(userSid, uid, processId, force, ct);

    public Task<IReadOnlyList<string>> SendMessageAsync(string userSid, IReadOnlyCollection<string> uids, string title, string text, CancellationToken ct) =>
        broker.MessageAsync(userSid, uids, title, text, ct);

    public async Task<IReadOnlyList<HelpdeskUser>> FindUsersAsync(string query, int max, CancellationToken ct)
    {
        var (live, _) = await broker.ListAllAsync(query, 2000, ct);
        var found = live
            .Where(b => !string.IsNullOrEmpty(b.UserSid))
            .GroupBy(b => b.UserSid!)
            .ToDictionary(g => g.Key, g => new HelpdeskUser(g.First().UserName ?? g.Key, g.Key, true));

        if (monitor is not null)
        {
            // Best effort: users that only have history. A failing Monitor must not break the search.
            try
            {
                foreach (var u in await monitor.FindUsersAsync(query, max, ct))
                    found.TryAdd(u.Sid, new HelpdeskUser(u.Name, u.Sid, false));
            }
            catch (Exception ex) when (ex is ProviderUnavailableException or ArgumentException)
            {
                log.LogWarning(ex, "Monitor user search failed; showing users with live sessions only.");
            }
        }
        return found.Values.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    // ---- helpers --------------------------------------------------------------------------------

    private SessionInfo MapLive(BrokerSession b, MonitorSession? m, DateTimeOffset now) => new(
        b.Uid,
        b.Apps.Count > 0 ? string.Join(", ", b.Apps) : b.DesktopGroup ?? b.Machine ?? "Session",
        b.Type.Equals("Desktop", StringComparison.OrdinalIgnoreCase) ? "Desktop" : "App",
        b.DesktopGroup ?? m?.DesktopGroup ?? "",
        b.Machine ?? m?.Machine ?? "",
        b.StartTime ?? m?.Start ?? now,
        null,
        b.State.Equals("Disconnected", StringComparison.OrdinalIgnoreCase) ? SessionState.Disconnected : SessionState.Active,
        b.IdleSeconds,
        NullIfEmpty(b.ClientName) ?? m?.ClientName,
        NullIfEmpty(b.ClientAddress) ?? m?.ClientAddress,
        quality.Evaluate(m?.IcaRttMs, m?.BandwidthKbps, m?.LogonSeconds, null),
        b.SessionKey ?? m?.SessionKey);

    private async Task<IReadOnlyList<MonitorSession>?> TryAsync(Func<Task<IReadOnlyList<MonitorSession>>> call)
    {
        try { return await call(); }
        catch (Exception ex) when (ex is ProviderUnavailableException or ArgumentException)
        {
            log.LogWarning(ex, "Monitor query failed.");
            return null;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
