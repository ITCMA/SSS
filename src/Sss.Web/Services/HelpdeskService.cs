using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Sss.Web.Models;
using Sss.Web.Providers;

namespace Sss.Web.Services;

public sealed record HelpdeskUserView(UserIdentity User, SessionsResponse Sessions);

/// <summary>Helpdesk use cases. The caller's group membership is checked by the endpoint filter before any of this
/// runs; this class makes sure everything a helpdesk user does is audited with who did it, to whom, and why.
/// Log-off reuses <see cref="SessionService"/>, so the same ownership re-check applies to the chosen user.</summary>
public sealed partial class HelpdeskService(
    IHelpdeskProvider provider, SessionService sessions, IUserDirectory directory,
    AuditLog audit, TimeProvider clock, SssOptions options, DetailsService details)
{
    [GeneratedRegex(@"^[\p{L}\p{N} ._@\\-]{0,64}$")] public static partial Regex QueryRegex();
    [GeneratedRegex(@"^S-1-\d+(-\d+)+$")] public static partial Regex SidRegex();

    public const int MaxReasonLength = 200;
    public const int MaxMessageLength = 300;
    private static readonly TimeSpan AuditViewEvery = TimeSpan.FromMinutes(5);

    // Auto-refresh would otherwise write an audit line every few seconds for the same lookup.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastViewAudit = new();

    public static bool IsValidQuery(string? q) => q is not null && QueryRegex().IsMatch(q);

    /// <summary>Removes control characters and caps the length; the audit log is JSON, so this is for readability.</summary>
    public static string? NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var clean = new string(reason.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? null : clean[..Math.Min(clean.Length, MaxReasonLength)];
    }

    /// <summary>Removes control characters and caps the length of a message shown to the user.</summary>
    public static string? NormalizeMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? null : clean[..Math.Min(clean.Length, MaxMessageLength)];
    }

    public async Task<IReadOnlyList<HelpdeskUser>> FindUsersAsync(UserIdentity actor, string query, string? ip, CancellationToken ct)
    {
        var users = await provider.FindUsersAsync(query, 50, ct);
        audit.Write(new AuditEntry("helpdesk:search", actor, null, $"{users.Count} hit(s)", ip, actor, query));
        return users;
    }

    public async Task<HelpdeskLiveResult> ListLiveAsync(UserIdentity actor, string? filter, string? ip, CancellationToken ct)
    {
        var result = await provider.ListLiveAsync(filter, Math.Clamp(options.Helpdesk.MaxSessions, 1, 2000), ct);
        AuditViewOnce(actor, actor, "helpdesk:list-all", ip, filter);
        return result;
    }

    public async Task<HelpdeskUserView> GetUserAsync(UserIdentity actor, string sid, TimeSpan window, string? ip, CancellationToken ct)
    {
        var target = new UserIdentity(sid, directory.NameOf(sid));
        var data = await sessions.GetAsync(target, window, ct);
        AuditViewOnce(actor, target, "helpdesk:view", ip, null);
        return new HelpdeskUserView(target, data);
    }

    public Task<LogoffResult> LogoffAsync(UserIdentity actor, string sid, LogoffScope scope,
        IReadOnlyCollection<string>? uids, string? reason, string? ip, CancellationToken ct)
    {
        var target = new UserIdentity(sid, directory.NameOf(sid));
        return sessions.LogoffAsync(target, scope, uids, ip, ct, actor, reason);
    }

    public async Task<SessionDetails?> GetDetailsAsync(UserIdentity actor, string sid, string sessionKey, string? ip, CancellationToken ct)
    {
        var target = new UserIdentity(sid, directory.NameOf(sid));
        var result = await details.GetAsync(target, sessionKey, full: true, ct);
        AuditViewOnce(actor, target, "helpdesk:details", ip, null);
        return result;
    }

    /// <summary>Shows a message in the chosen live sessions of the user. The uids are checked against the user's own
    /// live sessions here and once more inside the worker.</summary>
    public async Task<LogoffResult> SendMessageAsync(UserIdentity actor, string sid, IReadOnlyCollection<string> uids,
        string text, string? ip, CancellationToken ct)
    {
        var target = new UserIdentity(sid, directory.NameOf(sid));
        var live = (await sessions.GetAsync(target, TimeSpan.Zero, ct)).Live.Select(s => s.Uid).ToHashSet();
        var allowed = uids.Distinct().Where(live.Contains).ToList();
        var rejected = uids.Distinct().Where(u => !live.Contains(u)).ToList();

        var sent = allowed.Count == 0 ? [] : await provider.SendMessageAsync(sid, allowed, options.Helpdesk.MessageTitle, text, ct);
        foreach (var uid in allowed)
            audit.Write(new AuditEntry("helpdesk-message", target, uid, sent.Contains(uid) ? "success" : "failed", ip, actor, text));
        foreach (var uid in rejected)
            audit.Write(new AuditEntry("helpdesk-message", target, uid, "rejected-not-owned-or-not-live", ip, actor, text));
        return new LogoffResult(allowed.Count + rejected.Count, sent, rejected);
    }

    private void AuditViewOnce(UserIdentity actor, UserIdentity target, string action, string? ip, string? note)
    {
        var key = $"{actor.Sid}|{target.Sid}|{action}|{note}";
        var now = clock.GetUtcNow();
        if (_lastViewAudit.TryGetValue(key, out var last) && now - last < AuditViewEvery) return;
        _lastViewAudit[key] = now;
        audit.Write(new AuditEntry(action, target, null, "success", ip, actor, note));
    }
}
