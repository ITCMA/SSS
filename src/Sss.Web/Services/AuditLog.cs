using System.Text.Json;
using Sss.Web.Models;

namespace Sss.Web.Services;

/// <summary>One audit line. <c>User</c> is the owner of the affected session; <c>Actor</c> is set when someone else
/// (the helpdesk) acted on it, <c>Reason</c> carries the helpdesk's free-text reason or search text.</summary>
public sealed record AuditEntry(string Action, UserIdentity User, string? SessionUid, string Result, string? RemoteIp,
    UserIdentity? Actor = null, string? Reason = null);

/// <summary>Append-only JSON-lines audit trail, one file per day.</summary>
public sealed class AuditLog(string directory, TimeProvider clock, bool perServerFiles = false)
{
    private readonly object _gate = new();

    public void Write(AuditEntry e)
    {
        var now = clock.GetUtcNow();
        var line = JsonSerializer.Serialize(new
        {
            time = now,
            action = e.Action,
            user = e.User.Name,
            sid = e.User.Sid,
            session = e.SessionUid,
            result = e.Result,
            ip = e.RemoteIp,
            actor = e.Actor?.Name,
            actorSid = e.Actor?.Sid,
            reason = e.Reason,
            server = Environment.MachineName,
        });
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, perServerFiles ? $"audit-{now:yyyyMMdd}-{Environment.MachineName}.jsonl" : $"audit-{now:yyyyMMdd}.jsonl"), line + Environment.NewLine);
        }
    }
}
