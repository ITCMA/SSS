using System.Text.RegularExpressions;
using Sss.Web.Models;
using Sss.Web.Providers;

namespace Sss.Web.Services;

/// <summary>Lists and ends the processes of a session. A session is only accepted when it is one of the target's live
/// sessions (the worker checks it a second time), and every attempt to end a process is audited.</summary>
public sealed partial class ProcessService(IProcessProvider provider, SessionService sessions, AuditLog audit, SssOptions options)
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$")] private static partial Regex UidRegex();

    /// <summary>Processes may be handled for users (own sessions) when the audience is "All"; the helpdesk may always.</summary>
    public bool Available(bool isHelpdesk) =>
        options.Citrix.Processes.Enabled || options.Provider.Equals("Mock", StringComparison.OrdinalIgnoreCase)
            ? isHelpdesk || !options.Citrix.Processes.Audience.Equals("Helpdesk", StringComparison.OrdinalIgnoreCase)
            : false;

    public bool ProfileAvailable(bool isHelpdesk) =>
        Available(isHelpdesk) && (options.Citrix.Processes.ProfileInfo || options.Provider.Equals("Mock", StringComparison.OrdinalIgnoreCase));

    public async Task<ProfileInfo?> ProfileAsync(UserIdentity target, string uid, UserIdentity? actor, string? reason, string? ip, CancellationToken ct)
    {
        if (!await OwnsAsync(target, uid, ct)) return null;
        var info = await provider.GetProfileAsync(target.Sid, uid, options.Citrix.Processes.ProfileScanSeconds, ct);
        audit.Write(new AuditEntry("profile-info", target, uid, "success", ip, actor, reason));
        return info with { Hints = ProfileAdvisor.Analyze(info) };
    }

    public async Task<PolicyListResult?> PoliciesAsync(UserIdentity target, string uid, UserIdentity? actor, string? reason, string? ip, CancellationToken ct)
    {
        if (!await OwnsAsync(target, uid, ct)) return null;
        var list = await provider.GetPoliciesAsync(target.Sid, uid, ct);
        audit.Write(new AuditEntry("list-policies", target, uid, "success", ip, actor, reason));
        return list;
    }

    /// <summary>The HDX view of one live session: the channel groups from the policies in force, and the live values of the VDA when it delivers
    /// them (an older JEA endpoint without Get-SssHdx is reported as a note, the rest still works). Null when the session is not the target's.</summary>
    public async Task<HdxView?> HdxAsync(UserIdentity target, string uid, UserIdentity? actor, string? reason, string? ip, CancellationToken ct)
    {
        if (!await OwnsAsync(target, uid, ct)) return null;
        var policies = await provider.GetPoliciesAsync(target.Sid, uid, ct);
        BrokerHdx? live = null; string? note = null;
        try { live = await provider.GetHdxAsync(target.Sid, uid, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { note = "unavailable"; }
        // several sessions of the user on that machine: take the one the live counters name, else the one with the most policy values
        var sessionId = live?.Sessions.FirstOrDefault(s => s.Counters.Count > 0)?.SessionId
            ?? policies.Entries.GroupBy(e => e.SessionId).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault();
        var one = live?.Sessions.FirstOrDefault(s => s.SessionId == sessionId) ?? live?.Sessions.FirstOrDefault();
        if (live is not null && one is null) note = "none";
        if (one is not null && one.Counters.Count == 0 && note is null) note = "nocounters";
        var channels = HdxAdvisor.Channels(policies.Entries.Where(e => sessionId is null || e.SessionId == sessionId));
        audit.Write(new AuditEntry("hdx-info", target, uid, "success", ip, actor, reason));
        return new HdxView(policies.Machine ?? live?.Machine, sessionId, channels, one, note, HdxAdvisor.Hints(channels, one));
    }

    /// <summary>Whether the caller's helpdesk level may use the shadow button (Sss:Shadow:AllowLevel1/2/3).</summary>
    public bool ShadowAllowedFor(int level) => level switch
    {
        1 => options.Shadow.AllowLevel1,
        2 => options.Shadow.AllowLevel2,
        3 => options.Shadow.AllowLevel3,
        _ => false,
    };

    /// <summary>Why the button is or is not offered (for /api/me and diagnosis): ok, disabled, processes-off, cloud, level-off or no-role.</summary>
    public string ShadowState(int level) =>
        !options.Shadow.Enabled ? "disabled"
        : options.Provider.Equals("Mock", StringComparison.OrdinalIgnoreCase) ? (level == 0 ? "no-role" : ShadowAllowedFor(level) ? "ok" : "level-off")
        : options.Citrix.Cloud.Enabled ? "cloud"
        : !options.Citrix.Processes.Enabled ? "processes-off"
        : level == 0 ? "no-role" : ShadowAllowedFor(level) ? "ok" : "level-off";

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$")] private static partial Regex HostRegex();

    /// <summary>Works out where to shadow the session: the VDA and the Windows session id on it (read through the JEA endpoint; with several
    /// sessions of the user the one whose first process started closest to the Broker session). The request is audited; the link goes to the
    /// starter on the helpdesk PC. Null when the session is not the target's.</summary>
    public async Task<ShadowResult?> ShadowAsync(UserIdentity target, string uid, UserIdentity actor, string? reason, string? ip, CancellationToken ct)
    {
        var live = (await sessions.GetAsync(target, TimeSpan.Zero, ct)).Live.FirstOrDefault(s => s.Uid == uid);
        if (live is null || !UidRegex().IsMatch(uid))
        {
            audit.Write(new AuditEntry("shadow", target, uid, "rejected-not-owned-or-not-live", ip, actor, reason));
            return null;
        }
        BrokerHdx found;
        try { found = await provider.GetHdxAsync(target.Sid, uid, ct, idsOnly: true); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            audit.Write(new AuditEntry("shadow", target, uid, "failed", ip, actor, reason, "session id not readable"));
            return new ShadowResult(null, null, null, "unavailable");
        }
        var host = found.Machine;
        HdxLive? pick = found.Sessions.Count == 1 ? found.Sessions[0]
            : found.Sessions.Where(s => s.Started is not null).OrderBy(s => Math.Abs((s.Started!.Value - live.StartTime).TotalSeconds)).FirstOrDefault();
        // several sessions and no start time to tell them apart: refuse instead of shadowing the wrong one
        if (found.Sessions.Count > 1 && found.Sessions.Count(s => s.Started is null) > 0) pick = null;
        if (pick is null || host is null || !HostRegex().IsMatch(host))
        {
            audit.Write(new AuditEntry("shadow", target, uid, "failed", ip, actor, reason, found.Sessions.Count == 0 ? "no session found on the VDA" : "session not unique"));
            return new ShadowResult(null, host, null, found.Sessions.Count == 0 ? "notfound" : "ambiguous");
        }
        var url = $"sss-shadow://{host}/{pick.SessionId}?control={(options.Shadow.Control ? 1 : 0)}";
        audit.Write(new AuditEntry("shadow", target, uid, "success", ip, actor, reason, $"{host} session {pick.SessionId}{(options.Shadow.Control ? " control" : " view")}"));
        return new ShadowResult(url, host, pick.SessionId, null);
    }

    public async Task<ProcessListResult?> ListAsync(UserIdentity target, string uid, UserIdentity? actor, string? reason, string? ip, CancellationToken ct)
    {
        if (!await OwnsAsync(target, uid, ct)) return null;
        var list = await provider.ListProcessesAsync(target.Sid, uid, ct);
        audit.Write(new AuditEntry("list-processes", target, uid, "success", ip, actor, reason));
        return list;
    }

    /// <summary>Returns the outcome (closed, killed, no-window, ...) or null when the session is not one of the target's live sessions.</summary>
    public async Task<string?> EndAsync(UserIdentity target, string uid, int processId, bool force, UserIdentity? actor, string? reason, string? ip, CancellationToken ct)
    {
        if (processId < 1) return null;
        if (!await OwnsAsync(target, uid, ct))
        {
            audit.Write(new AuditEntry("end-process", target, uid, "rejected-not-owned-or-not-live", ip, actor, reason, $"pid {processId}"));
            return null;
        }
        string result;
        try { result = await provider.EndProcessAsync(target.Sid, uid, processId, force, ct); }
        catch
        {
            audit.Write(new AuditEntry("end-process", target, uid, "failed", ip, actor, reason, $"pid {processId}{(force ? " force" : "")}"));
            throw;
        }
        audit.Write(new AuditEntry("end-process", target, uid, result, ip, actor, reason, $"pid {processId}{(force ? " force" : "")}"));
        return result;
    }

    private async Task<bool> OwnsAsync(UserIdentity target, string uid, CancellationToken ct) =>
        UidRegex().IsMatch(uid) && (await sessions.GetAsync(target, TimeSpan.Zero, ct)).Live.Any(s => s.Uid == uid);
}
