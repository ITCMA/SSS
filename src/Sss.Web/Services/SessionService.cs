using Sss.Web.Models;
using Sss.Web.Providers;

namespace Sss.Web.Services;

/// <summary>Business rules: a user only ever sees and affects their own sessions, and every
/// log-off target is re-verified against the user's own live sessions before the provider is called.</summary>
public sealed class SessionService(ISessionProvider provider, AuditLog audit, TimeProvider clock)
{
    public static readonly IReadOnlyDictionary<string, TimeSpan> Ranges = new Dictionary<string, TimeSpan>
    {
        ["1h"] = TimeSpan.FromHours(1),
        ["24h"] = TimeSpan.FromHours(24),
        ["7d"] = TimeSpan.FromDays(7),
        ["30d"] = TimeSpan.FromDays(30),
    };

    public async Task<SessionsResponse> GetAsync(UserIdentity user, TimeSpan window, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var result = await provider.GetSessionsAsync(user, window, ct);
        var live = result.Sessions.Where(s => s.State != SessionState.Ended).OrderByDescending(s => s.StartTime).ToList();
        var history = result.Sessions.Where(s => s.State == SessionState.Ended && s.EndTime >= now - window)
            .OrderByDescending(s => s.EndTime).ToList();
        return new SessionsResponse(live, history, now, result.Warnings);
    }

    public async Task<LogoffResult> LogoffAsync(UserIdentity user, LogoffScope scope,
        IReadOnlyCollection<string>? requested, string? remoteIp, CancellationToken ct,
        UserIdentity? actor = null, string? reason = null)
    {
        var prefix = actor is null ? "logoff" : "helpdesk-logoff";
        var live = (await provider.GetSessionsAsync(user, TimeSpan.Zero, ct)).Sessions
            .Where(s => s.State != SessionState.Ended).ToList();

        List<string> targets;
        var rejected = new List<string>();
        switch (scope)
        {
            case LogoffScope.Selected:
                var liveIds = live.Select(s => s.Uid).ToHashSet();
                targets = (requested ?? []).Distinct().Where(liveIds.Contains).ToList();
                rejected = (requested ?? []).Distinct().Where(u => !liveIds.Contains(u)).ToList();
                break;
            case LogoffScope.Disconnected:
                targets = live.Where(s => s.State == SessionState.Disconnected).Select(s => s.Uid).ToList();
                break;
            default:
                targets = live.Select(s => s.Uid).ToList();
                break;
        }

        var done = targets.Count == 0 ? [] : await provider.LogoffAsync(user, targets, ct);

        foreach (var uid in targets)
            audit.Write(new AuditEntry($"{prefix}:{scope.ToString().ToLowerInvariant()}", user, uid,
                done.Contains(uid) ? "success" : "failed", remoteIp, actor, reason));
        foreach (var uid in rejected)
            audit.Write(new AuditEntry($"{prefix}:selected", user, uid, "rejected-not-owned-or-not-live", remoteIp, actor, reason));

        return new LogoffResult(targets.Count + rejected.Count, done, rejected);
    }
}
