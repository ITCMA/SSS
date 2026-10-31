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

    public async Task<ProfileInfo?> ProfileAsync(UserIdentity target, string uid, UserIdentity? actor, string? ip, CancellationToken ct)
    {
        if (!await OwnsAsync(target, uid, ct)) return null;
        var info = await provider.GetProfileAsync(target.Sid, uid, options.Citrix.Processes.ProfileScanSeconds, ct);
        audit.Write(new AuditEntry("profile-info", target, uid, "success", ip, actor));
        return info;
    }

    public async Task<ProcessListResult?> ListAsync(UserIdentity target, string uid, UserIdentity? actor, string? ip, CancellationToken ct)
    {
        if (!await OwnsAsync(target, uid, ct)) return null;
        var list = await provider.ListProcessesAsync(target.Sid, uid, ct);
        audit.Write(new AuditEntry("list-processes", target, uid, "success", ip, actor));
        return list;
    }

    /// <summary>Returns the outcome (closed, killed, no-window, ...) or null when the session is not one of the target's live sessions.</summary>
    public async Task<string?> EndAsync(UserIdentity target, string uid, int processId, bool force, UserIdentity? actor, string? ip, CancellationToken ct)
    {
        if (processId < 1) return null;
        if (!await OwnsAsync(target, uid, ct))
        {
            audit.Write(new AuditEntry("end-process", target, uid, "rejected-not-owned-or-not-live", ip, actor, $"pid {processId}"));
            return null;
        }
        string result;
        try { result = await provider.EndProcessAsync(target.Sid, uid, processId, force, ct); }
        catch
        {
            audit.Write(new AuditEntry("end-process", target, uid, "failed", ip, actor, $"pid {processId}{(force ? " force" : "")}"));
            throw;
        }
        audit.Write(new AuditEntry("end-process", target, uid, result, ip, actor, $"pid {processId}{(force ? " force" : "")}"));
        return result;
    }

    private async Task<bool> OwnsAsync(UserIdentity target, string uid, CancellationToken ct) =>
        UidRegex().IsMatch(uid) && (await sessions.GetAsync(target, TimeSpan.Zero, ct)).Live.Any(s => s.Uid == uid);
}
