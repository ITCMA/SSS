using System.Text.Json;
using System.Text.Json.Serialization;
using Sss.Web.Audit;
using Sss.Web.Models;

namespace Sss.Web.Services;

/// <summary>One audit line. <c>User</c> is the owner of the affected session; <c>Actor</c> is set when someone else
/// (the helpdesk) acted on it, <c>Reason</c> carries the ticket number or reason the helpdesk entered, <c>Detail</c> what was
/// asked for (search text, process id, message text).</summary>
public sealed record AuditEntry(string Action, UserIdentity User, string? SessionUid, string Result, string? RemoteIp,
    UserIdentity? Actor = null, string? Reason = null, string? Detail = null);

/// <summary>Audit trail. Every line says who did what to whom, when, from where, on which server and in which request; see
/// <see cref="JsonlStream"/> for the file format. The local file is always written first; <see cref="AuditForwarder"/> copies
/// it to the central sinks, so nothing is lost while a sink is unreachable.</summary>
public sealed class AuditLog(JsonlStream stream, TimeProvider clock, Func<(string? RequestId, string? Role)>? context = null)
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public AuditLog(string directory, TimeProvider clock, bool perServerFiles = false)
        : this(new JsonlStream(directory, "audit", clock, perServerFiles), clock) { }

    public JsonlStream Stream => stream;

    public void Write(AuditEntry e)
    {
        var (requestId, role) = context?.Invoke() ?? (null, null);
        var who = e.Actor ?? e.User;   // always the person who clicked, also for the user's own actions
        var line = JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString("N")[..16],
            time = clock.GetUtcNow(),
            stream = "audit",
            action = e.Action,
            user = e.User.Name,
            sid = e.User.Sid,
            session = e.SessionUid,
            result = e.Result,
            ip = e.RemoteIp,
            actor = who.Name,
            actorSid = who.Sid,
            role = role ?? (e.Actor is null ? "User" : null),
            reason = e.Reason,
            detail = e.Detail,
            server = Environment.MachineName,
            requestId,
        }, Json);
        stream.Append(line);
        Metrics.Count("sss_audit_events_total", "action", e.Action.Split(':')[0]);
    }
}
