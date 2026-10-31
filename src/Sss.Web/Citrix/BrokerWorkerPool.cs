using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Sss.Web.Models;
using System.Text.RegularExpressions;
using Sss.Web.Providers;

namespace Sss.Web.Citrix;

/// <summary>Broker client backed by a small pool of warm Windows PowerShell workers.</summary>
public sealed partial class BrokerWorkerPool : IBrokerClient, IHostedService, IDisposable
{
    [GeneratedRegex(@"^S-1-\d+(-\d+)+$")] private static partial Regex SidRegex();
    [GeneratedRegex(@"^\d{1,18}$")] private static partial Regex UidRegex();
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,253}$")] private static partial Regex ControllerRegex();
    [GeneratedRegex(@"^[\p{L}\p{N} ._@\\-]{0,64}$")] private static partial Regex FilterRegex();
    [GeneratedRegex(@"^[^\p{Cc}]{1,300}$")] private static partial Regex MessageRegex();

    private readonly ConcurrentQueue<PowerShellWorker> _free = new();
    private readonly List<PowerShellWorker> _all = [];
    private readonly SemaphoreSlim _slots;
    private readonly ILogger<BrokerWorkerPool> _log;

    private readonly CitrixOptions _options;

    public BrokerWorkerPool(CitrixOptions options, ILogger<BrokerWorkerPool> log)
    {
        _options = options;
        _log = log;
        if (options.Controllers.Length == 0) throw new InvalidOperationException("Sss:Citrix:Controllers must list at least one Delivery Controller.");
        // The names end up on the PowerShell command line; the worker validates them again.
        if (options.Controllers.Any(c => !ControllerRegex().IsMatch(c)))
            throw new InvalidOperationException("Sss:Citrix:Controllers contains an invalid host name (allowed: letters, digits, '.', '_', '-').");
        var script = options.WorkerScript ?? Path.Combine(AppContext.BaseDirectory, "Citrix", "sss-broker-worker.ps1");
        var n = Math.Clamp(options.Workers, 1, 16);
        _slots = new SemaphoreSlim(n, n);
        for (var i = 0; i < n; i++)
        {
            var w = new PowerShellWorker(options, script, log);
            _all.Add(w);
            _free.Enqueue(w);
        }
    }

    /// <summary>Starts every worker once so the first user does not pay the snap-in load time.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        try { await Task.WhenAll(_all.Select(w => w.RequestAsync(new() { ["op"] = "ping" }, ct))); }
        catch (Exception ex) { _log.LogWarning(ex, "Citrix worker warm-up failed; it will be retried on the first request."); }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task<JsonElement> RunAsync(Dictionary<string, object?> request, CancellationToken ct, int? timeoutSeconds = null)
    {
        await _slots.WaitAsync(ct);
        _free.TryDequeue(out var worker);
        try { return await worker!.RequestAsync(request, ct, timeoutSeconds); }
        finally { _free.Enqueue(worker!); _slots.Release(); }
    }

    public async Task<IReadOnlyList<BrokerSession>> ListAsync(string userSid, CancellationToken ct)
    {
        if (!SidRegex().IsMatch(userSid)) throw new ArgumentException("Invalid SID.", nameof(userSid));
        var res = await RunAsync(new() { ["op"] = "list", ["sid"] = userSid }, ct);
        return ParseSessions(res);
    }

    public async Task<(IReadOnlyList<BrokerSession> Sessions, bool Truncated)> ListAllAsync(string? userFilter, int max, CancellationToken ct)
    {
        userFilter ??= "";
        if (!FilterRegex().IsMatch(userFilter)) throw new ArgumentException("Invalid filter.", nameof(userFilter));
        var res = await RunAsync(new() { ["op"] = "listall", ["q"] = userFilter, ["max"] = Math.Clamp(max, 1, 2000) }, ct);
        var truncated = res.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True;
        return (ParseSessions(res), truncated);
    }

    private static List<BrokerSession> ParseSessions(JsonElement res)
    {
        var list = new List<BrokerSession>();
        foreach (var s in res.GetProperty("sessions").EnumerateArray())
        {
            var apps = s.TryGetProperty("apps", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : [];
            list.Add(new BrokerSession(
                Str(s, "uid") ?? "", Str(s, "sessionKey"), Str(s, "state") ?? "", Str(s, "type") ?? "",
                Str(s, "desktopGroup"), Str(s, "machine"), Date(s, "startTime"), Int(s, "idleSeconds"),
                Str(s, "clientName"), Str(s, "clientAddress"), apps, Str(s, "userName"), Str(s, "userSid")));
        }
        return list;
    }

    public async Task<BrokerLogoffResult> LogoffAsync(string userSid, IReadOnlyCollection<string> uids, CancellationToken ct)
    {
        if (!SidRegex().IsMatch(userSid)) throw new ArgumentException("Invalid SID.", nameof(userSid));
        var rejected = uids.Where(u => !UidRegex().IsMatch(u)).ToList();
        var valid = uids.Where(u => UidRegex().IsMatch(u)).ToList();
        if (valid.Count == 0) return new BrokerLogoffResult([], rejected);

        var res = await RunAsync(new() { ["op"] = "logoff", ["sid"] = userSid, ["uids"] = valid }, ct);
        var accepted = Strings(res, "accepted");
        rejected.AddRange(Strings(res, "rejected"));
        return new BrokerLogoffResult(accepted, rejected);
    }

    public async Task<IReadOnlyList<string>> MessageAsync(string userSid, IReadOnlyCollection<string> uids, string title, string text, CancellationToken ct)
    {
        if (!SidRegex().IsMatch(userSid)) throw new ArgumentException("Invalid SID.", nameof(userSid));
        if (!MessageRegex().IsMatch(title) || !MessageRegex().IsMatch(text)) throw new ArgumentException("Invalid message.", nameof(text));
        var valid = uids.Where(u => UidRegex().IsMatch(u)).ToList();
        if (valid.Count == 0) return [];
        var res = await RunAsync(new() { ["op"] = "message", ["sid"] = userSid, ["uids"] = valid, ["title"] = title, ["text"] = text }, ct);
        return Strings(res, "accepted");
    }

    public async Task<BrokerProcessList> ProcessesAsync(string userSid, string uid, CancellationToken ct)
    {
        if (!SidRegex().IsMatch(userSid)) throw new ArgumentException("Invalid SID.", nameof(userSid));
        if (!UidRegex().IsMatch(uid)) throw new ArgumentException("Invalid session id.", nameof(uid));
        var res = await RunAsync(new() { ["op"] = "processes", ["sid"] = userSid, ["uid"] = uid }, ct, Math.Max(_options.RequestTimeoutSeconds, _options.Processes.TimeoutSeconds));
        var list = new List<BrokerProcess>();
        if (res.TryGetProperty("processes", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var p in arr.EnumerateArray())
                list.Add(new BrokerProcess(
                    p.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                    Str(p, "name") ?? "",
                    p.TryGetProperty("sessionId", out var sid) ? sid.GetInt32() : 0,
                    DateTimeOffset.TryParse(Str(p, "started"), out var st) ? st : null,
                    p.TryGetProperty("memoryMb", out var mem) ? mem.GetInt32() : 0,
                    Str(p, "window") is { Length: > 0 } w ? w : null,
                    p.TryGetProperty("isApp", out var ia) && ia.ValueKind == JsonValueKind.True));
        return new BrokerProcessList(Str(res, "machine"), list);
    }

    public async Task<ProfileInfo> ProfileAsync(string userSid, string uid, int scanSeconds, CancellationToken ct)
    {
        if (!SidRegex().IsMatch(userSid)) throw new ArgumentException("Invalid SID.", nameof(userSid));
        if (!UidRegex().IsMatch(uid)) throw new ArgumentException("Invalid session id.", nameof(uid));
        scanSeconds = Math.Clamp(scanSeconds, 0, 120);
        var res = await RunAsync(new() { ["op"] = "profile", ["sid"] = userSid, ["uid"] = uid, ["scanSeconds"] = scanSeconds }, ct,
            Math.Max(_options.RequestTimeoutSeconds, _options.Processes.TimeoutSeconds) + scanSeconds);
        int? Mb(string name) => res.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;
        bool Flag(string name) => res.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
        string? Text(string name) => Str(res, name) is { Length: > 0 } s ? s : null;
        var top = new List<ProfileFolder>();
        if (res.TryGetProperty("top", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var t in arr.EnumerateArray())
                top.Add(new ProfileFolder(Str(t, "name") ?? "", t.TryGetProperty("mb", out var mb) && mb.ValueKind == JsonValueKind.Number ? mb.GetInt32() : 0));
        return new ProfileInfo(Text("machine"), Text("type") ?? "Local", Flag("loaded"), Text("localPath"), Text("storePath"), Text("container"), Mb("containerMb"), Mb("containerMaxMb"),
            Flag("roaming"), Flag("temporary"), Flag("mandatory"), Mb("sizeMb"), Flag("sizePartial"), Mb("scanSeconds") ?? 0, Strings(res, "notes"), top);
    }

    public async Task<string> EndProcessAsync(string userSid, string uid, int processId, bool force, CancellationToken ct)
    {
        if (!SidRegex().IsMatch(userSid)) throw new ArgumentException("Invalid SID.", nameof(userSid));
        if (!UidRegex().IsMatch(uid)) throw new ArgumentException("Invalid session id.", nameof(uid));
        if (processId < 1) throw new ArgumentException("Invalid process id.", nameof(processId));
        var res = await RunAsync(new() { ["op"] = "endprocess", ["sid"] = userSid, ["uid"] = uid, ["pid"] = processId, ["force"] = force }, ct,
            Math.Max(_options.RequestTimeoutSeconds, _options.Processes.TimeoutSeconds));
        return Str(res, "result") ?? "failed";
    }

    public async Task PingAsync(CancellationToken ct) => await RunAsync(new() { ["op"] = "ping" }, ct);

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : [];

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public void Dispose()
    {
        foreach (var w in _all) w.Dispose();
        _slots.Dispose();
    }
}
