using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sss.Web.Models;
using Sss.Web.Providers;

namespace Sss.Web.Citrix;

public sealed record MonitorSession(
    string SessionKey,
    string? Machine,
    string? DesktopGroup,
    bool IsDesktop,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    int? LogonSeconds,
    string? ClientName,
    string? ClientAddress,
    int? IcaRttMs,
    int? BandwidthKbps = null);

public sealed record MonitorUser(string Name, string Sid);

/// <summary>History and quality metrics from the Citrix Monitor Service (the data source of Director).</summary>
public interface IMonitorClient
{
    /// <summary>Sessions of the user that have not ended yet, including their latest metrics.</summary>
    Task<IReadOnlyList<MonitorSession>> GetLiveAsync(string userSid, CancellationToken ct);

    /// <summary>Sessions of the user that ended at or after <paramref name="since"/>, newest first.</summary>
    Task<IReadOnlyList<MonitorSession>> GetEndedAsync(string userSid, DateTimeOffset since, CancellationToken ct);

    /// <summary>Users known to Monitor whose name or UPN contains <paramref name="query"/> (helpdesk search).</summary>
    Task<IReadOnlyList<MonitorUser>> FindUsersAsync(string query, int max, CancellationToken ct);

    /// <summary>Everything Monitor collected about one session of the user (null if it is not the user's).</summary>
    Task<SessionDetails?> GetDetailsAsync(string userSid, string sessionKey, bool full, CancellationToken ct);

    /// <summary>Cheap round trip for the status page. Throws <see cref="ProviderUnavailableException"/> when Monitor fails.</summary>
    Task PingAsync(CancellationToken ct);
}

public sealed partial class ODataMonitorClient(HttpClient http, CitrixOptions options) : IMonitorClient
{
    [GeneratedRegex(@"^S-1-\d+(-\d+)+$")] private static partial Regex SidRegex();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,63}$")] private static partial Regex FieldRegex();
    [GeneratedRegex(@"^[\p{L}\p{N} ._@-]{2,64}$")] private static partial Regex SearchRegex();

    // Field names from the configuration are placed into the query ($orderby), so they must be plain identifiers.
    private readonly bool _configChecked = CheckConfig(options);

    private static bool CheckConfig(CitrixOptions o)
    {
        foreach (var f in new[] { o.Monitor.MetricsDateField, o.Monitor.IcaRttField, o.Monitor.BandwidthField })
            if (!FieldRegex().IsMatch(f))
                throw new InvalidOperationException($"Sss:Citrix:Monitor field name '{f}' is not a valid OData property name.");
        foreach (var url in BuildBaseAddresses(o))
            if (url.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("Sss:Citrix:Monitor:BaseUrl(s) must be http:// or https:// addresses.");
        return true;
    }

    private const string Expand =
        "Machine($select=DnsName;$expand=DesktopGroup($select=Name)),CurrentConnection($select=ClientName,ClientAddress)";

    private readonly ConcurrentDictionary<string, (long? Id, DateTimeOffset Expires)> _userIds = new();

    /// <summary>One address per Delivery Controller (or the explicit BaseUrl / BaseUrls). The first is tried first.</summary>
    public static IReadOnlyList<Uri> BuildBaseAddresses(CitrixOptions o)
    {
        var scheme = o.Monitor.UseHttps ? "https" : "http";
        IEnumerable<string> urls = o.Monitor.BaseUrls is { Length: > 0 } list ? list
            : o.Monitor.BaseUrl is { } one ? [one]
            : (o.Controllers.Length > 0 ? o.Controllers : ["localhost"]).Select(c => $"{scheme}://{c}/Citrix/Monitor/OData/v4/Data/");
        return urls.Select(u => new Uri(u.EndsWith('/') ? u : u + "/")).DistinctBy(u => u.AbsoluteUri).ToArray();
    }

    public static Uri BuildBaseAddress(CitrixOptions o) => BuildBaseAddresses(o)[0];

    public async Task<IReadOnlyList<MonitorSession>> GetLiveAsync(string userSid, CancellationToken ct)
    {
        if (await ResolveUserIdAsync(userSid, ct) is not { } uid) return [];
        var dateField = options.Monitor.MetricsDateField;
        var query = $"Sessions?$filter={Esc($"UserId eq {uid.ToString(CultureInfo.InvariantCulture)} and EndDate eq null")}" +
                    $"&$expand={Esc($"{Expand},SessionMetrics($orderby={dateField} desc;$top=1)")}";
        return await PagedAsync(query, ct);
    }

    public async Task<IReadOnlyList<MonitorSession>> GetEndedAsync(string userSid, DateTimeOffset since, CancellationToken ct)
    {
        if (await ResolveUserIdAsync(userSid, ct) is not { } uid) return [];
        var iso = since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var query = $"Sessions?$filter={Esc($"UserId eq {uid.ToString(CultureInfo.InvariantCulture)} and EndDate ge {iso}")}" +
                    $"&$expand={Esc(Expand)}&$orderby=EndDate%20desc&$top=100";
        return await PagedAsync(query, ct);
    }

    public async Task<IReadOnlyList<MonitorUser>> FindUsersAsync(string query, int max, CancellationToken ct)
    {
        // The text goes into an OData string literal: only plain characters are allowed (no quotes at all).
        if (!SearchRegex().IsMatch(query)) throw new ArgumentException("Invalid search text.", nameof(query));
        var q = query.ToLowerInvariant();
        var filter = $"contains(tolower(UserName),'{q}') or contains(tolower(Upn),'{q}')";
        using var doc = await GetJsonAsync(
            $"Users?$filter={Esc(filter)}&$select=Sid,UserName,Upn,Domain&$top={Math.Clamp(max, 1, 100)}", ct);

        var users = new List<MonitorUser>();
        if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var u in arr.EnumerateArray())
            {
                var sid = Str(u, "Sid");
                var sam = Str(u, "UserName") ?? Str(u, "Upn");
                if (sid is null || sam is null || !SidRegex().IsMatch(sid)) continue;
                var domain = Str(u, "Domain");
                users.Add(new MonitorUser(string.IsNullOrEmpty(domain) ? sam : $"{domain}\\{sam}", sid));
            }
        return users;
    }

    private static string Esc(string s) => Uri.EscapeDataString(s);

    /// <summary>The Monitor database keys users by a numeric id; the SID is looked up once and cached.</summary>
    private async Task<long?> ResolveUserIdAsync(string sid, CancellationToken ct)
    {
        if (!SidRegex().IsMatch(sid)) throw new ArgumentException("Invalid SID.", nameof(sid));
        if (_userIds.TryGetValue(sid, out var hit) && hit.Expires > DateTimeOffset.UtcNow) return hit.Id;

        using var doc = await GetJsonAsync($"Users?$filter={Esc($"Sid eq '{sid}'")}&$select=Id&$top=1", ct);
        long? id = null;
        if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0
            && TryGet(arr[0], "Id", out var idEl) && idEl.TryGetInt64(out var v))
            id = v;
        _userIds[sid] = (id, DateTimeOffset.UtcNow.AddMinutes(id is null ? 1 : 30));
        return id;
    }

    private async Task<IReadOnlyList<MonitorSession>> PagedAsync(string firstRelative, CancellationToken ct)
    {
        var result = new List<MonitorSession>();
        Uri? next = null;
        Uri baseUri = http.BaseAddress!;
        for (var page = 0; page == 0 || (next is not null && page < options.Monitor.MaxPages); page++)
        {
            JsonDocument doc;
            if (next is null) (doc, baseUri) = await GetJsonWithBaseAsync(firstRelative, ct);
            else doc = await GetJsonAsync(next, ct);
            using var _doc = doc;
            if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                result.AddRange(arr.EnumerateArray().Select(Parse).Where(s => s is not null)!);

            next = null;
            if (doc.RootElement.TryGetProperty("@odata.nextLink", out var nl) && nl.GetString() is { } link
                && Uri.TryCreate(baseUri, link, out var candidate)
                && string.Equals(candidate.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))   // never follow links to other hosts
                next = new Uri(baseUri, candidate.PathAndQuery);   // keep scheme and port of the configured address (Monitor may advertise http://)
        }
        return result;
    }

    private async Task<JsonDocument> GetJsonAsync(string relative, CancellationToken ct) => (await GetJsonWithBaseAsync(relative, ct)).Doc;

    // Delivery Controllers are tried in order; the one that answered last goes first, like in the Broker worker.
    private Uri[]? _bases;
    private int _active;

    private Uri[] Bases => _bases ??= (http.BaseAddress is { } b && !BuildBaseAddresses(options).Any(u => u == b)
        ? [b, .. BuildBaseAddresses(options)]
        : BuildBaseAddresses(options).ToArray());

    private async Task<(JsonDocument Doc, Uri Base)> GetJsonWithBaseAsync(string relative, CancellationToken ct)
    {
        var bases = Bases;
        ProviderUnavailableException? last = null;
        for (var n = 0; n < bases.Length; n++)
        {
            var i = (Volatile.Read(ref _active) + n) % bases.Length;
            try
            {
                var doc = await GetJsonAsync(new Uri(bases[i], relative), ct);
                Volatile.Write(ref _active, i);
                return (doc, bases[i]);
            }
            catch (ProviderUnavailableException ex) { last = ex; }
        }
        throw last!;
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                throw new ProviderUnavailableException($"Monitor OData returned {(int)resp.StatusCode} for {uri.AbsolutePath}.");
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException("Monitor OData not reachable: " + ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderUnavailableException("Monitor OData timed out.", ex);
        }
    }

    private MonitorSession? Parse(JsonElement s)
    {
        var key = Str(s, "SessionKey");
        if (key is null) return null;

        string? machine = null, group = null, client = null, address = null;
        if (TryGet(s, "Machine", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            machine = Str(m, "DnsName");
            if (TryGet(m, "DesktopGroup", out var dg) && dg.ValueKind == JsonValueKind.Object) group = Str(dg, "Name");
        }
        if (TryGet(s, "CurrentConnection", out var c) && c.ValueKind == JsonValueKind.Object)
        {
            client = Str(c, "ClientName");
            address = Str(c, "ClientAddress");
        }

        int? logon = TryGet(s, "LogOnDuration", out var ld) && ld.ValueKind == JsonValueKind.Number && ld.TryGetInt64(out var ms)
            ? (int)Math.Clamp(ms / 1000, 0, int.MaxValue) : null;
        var isDesktop = TryGet(s, "SessionType", out var st) && st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out var t) && t == 0;

        var (rtt, bandwidth) = LatestMetrics(s);
        return new MonitorSession(key, machine, group, isDesktop, Date(s, "StartDate"), Date(s, "EndDate"),
            logon, client, address, rtt, bandwidth);
    }

    private (int? RttMs, int? BandwidthKbps) LatestMetrics(JsonElement session)
    {
        if (!TryGet(session, "SessionMetrics", out var metrics) || metrics.ValueKind != JsonValueKind.Array) return (null, null);
        JsonElement? best = null;
        DateTimeOffset bestDate = DateTimeOffset.MinValue;
        foreach (var m in metrics.EnumerateArray())
        {
            var d = Date(m, options.Monitor.MetricsDateField) ?? DateTimeOffset.MinValue;
            if (best is null || d >= bestDate) { best = m; bestDate = d; }
        }
        if (best is not { } b) return (null, null);

        int? rtt = TryGet(b, options.Monitor.IcaRttField, out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetDouble(out var rv)
            ? (int)Math.Round(rv) : null;
        // 0 means "not measured" in Monitor, so it never counts as a (terrible) bandwidth
        int? kbps = TryGet(b, options.Monitor.BandwidthField, out var bw) && bw.ValueKind == JsonValueKind.Number && bw.TryGetDouble(out var bv) && bv > 0
            ? (int)Math.Min(int.MaxValue, bv / Math.Max(1, options.Monitor.BandwidthDivisor)) : null;
        return (rtt, kbps);
    }

    // ---- expert view ----------------------------------------------------------------------------

    /// <summary>Optional record sets of a session. A site that lacks one (older versions, other schema) just reports it as unavailable.</summary>
    private static readonly (string Id, string Set)[] OptionalParts =
    [
        ("connections", "Connections"),
        ("failures", "ConnectionFailureLogs"),
        ("applications", "ApplicationInstances"),
    ];

    private static readonly string[] MachineBasics = ["DnsName", "OSType", "OSVersion", "AgentVersion", "RegistrationState"];
    private static readonly string[] GroupBasics = ["Name", "DeliveryType", "SessionSupport"];

    [GeneratedRegex(@"(Duration|Logon|Brokering|Authentication|Profile|Gpo|GroupPolicy|Script|Interactive)", RegexOptions.IgnoreCase)]
    private static partial Regex LogonRegex();

    public async Task PingAsync(CancellationToken ct)
    {
        using var _ = await GetJsonAsync("Users?$select=Id&$top=1", ct);
    }

    public async Task<SessionDetails?> GetDetailsAsync(string userSid, string sessionKey, bool full, CancellationToken ct)
    {
        // Only a GUID is ever placed into the query, and the session is looked up by the caller's own user id.
        if (!Guid.TryParse(sessionKey, out var guid)) throw new ArgumentException("Invalid session key.", nameof(sessionKey));
        if (await ResolveUserIdAsync(userSid, ct) is not { } uid) return null;
        var key = guid.ToString("D");
        var dateField = options.Monitor.MetricsDateField;

        var filter = Esc($"UserId eq {uid.ToString(CultureInfo.InvariantCulture)} and SessionKey eq {key}");
        var expand = Esc($"Machine($expand=DesktopGroup),CurrentConnection,SessionMetrics($orderby={dateField} desc;$top=240)");
        using var doc = await GetJsonAsync($"Sessions?$filter={filter}&$expand={expand}&$top=1", ct);
        if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return null;
        var s = arr[0];

        var buckets = new Dictionary<string, List<DetailItem>>();
        void Add(string source, string name, JsonElement v, string? prefix = null)
        {
            if (Format(name, v) is not { } item) return;
            var id = Classify(source, name);
            if (!buckets.TryGetValue(id, out var list)) buckets[id] = list = [];
            list.Add(prefix is null ? item : item with { Key = prefix + item.Key });
        }

        foreach (var p in s.EnumerateObject())
            if (p.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) Add("session", p.Name, p.Value);

        if (TryGet(s, "CurrentConnection", out var cc))
            foreach (var p in Props(cc)) Add("connection", p.Name, p.Value);

        if (TryGet(s, "Machine", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in Props(m)) if (full || MachineBasics.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) Add("machine", p.Name, p.Value);
            if (TryGet(m, "DesktopGroup", out var dg))
                foreach (var p in Props(dg)) if (full || GroupBasics.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) Add("machine", p.Name, p.Value, "DesktopGroup.");
        }

        var series = BuildSeries(s, out var latest);
        if (latest is { } l)
            foreach (var p in Props(l)) Add("metrics", p.Name, p.Value);

        var tables = new List<DetailTable>();
        var unavailable = new List<string>();
        var connectionRecords = new List<Dictionary<string, object?>>();
        if (TryGet(s, "CurrentConnection", out var curConn)) connectionRecords.Add(LogonWaterfallBuilder.Flatten(curConn));
        foreach (var (id, set) in OptionalParts)
        {
            try
            {
                using var part = await GetJsonAsync($"{set}?$filter={Esc($"SessionKey eq {key}")}&$top=50", ct);
                var rows = new List<IReadOnlyList<DetailItem>>();
                if (part.RootElement.TryGetProperty("value", out var pv) && pv.ValueKind == JsonValueKind.Array)
                    foreach (var r in pv.EnumerateArray())
                    {
                        if (id == "connections") connectionRecords.Add(LogonWaterfallBuilder.Flatten(r));
                        rows.Add(Props(r).Select(p => Format(p.Name, p.Value)).OfType<DetailItem>().ToList());
                    }
                if (rows.Count > 0) tables.Add(new DetailTable(id, rows));
            }
            catch (ProviderUnavailableException) { unavailable.Add(id); }
        }

        string[] order = ["session", "logon", "client", "network", "machine", "technical"];
        var groups = order.Where(buckets.ContainsKey).Select(id => new DetailGroup(id, buckets[id])).ToList();
        double? logOnMs = TryGet(s, "LogOnDuration", out var lod) && lod.ValueKind == JsonValueKind.Number ? lod.GetDouble() : null;
        return new SessionDetails(key, groups, series, tables, unavailable, LogonWaterfallBuilder.Build(connectionRecords, logOnMs));
    }

    private static IEnumerable<JsonProperty> Props(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object
            ? e.EnumerateObject().Where(p => p.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array) && !p.Name.Contains('@'))
            : [];

    private static string Classify(string source, string name)
    {
        if (source == "machine") return "machine";
        if (name == "Id" || name.EndsWith("Id", StringComparison.Ordinal) || name.EndsWith("Key", StringComparison.Ordinal)) return "technical";
        return source switch
        {
            "connection" => "client",
            "metrics" => "network",
            _ => LogonRegex().IsMatch(name) ? "logon" : "session",
        };
    }

    /// <summary>Turns one JSON value into a display item; nulls are skipped. Bandwidth fields are shown in kbit/s.</summary>
    private DetailItem? Format(string name, JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.String: return v.GetString() is { Length: > 0 } str ? new DetailItem(name, str) : null;
            case JsonValueKind.True: return new DetailItem(name, "true");
            case JsonValueKind.False: return new DetailItem(name, "false");
            case JsonValueKind.Number:
                var d = v.GetDouble();
                if (name.Contains("Bandwidth", StringComparison.OrdinalIgnoreCase))
                    return new DetailItem(name, Math.Floor(d / Math.Max(1, options.Monitor.BandwidthDivisor)).ToString(CultureInfo.InvariantCulture), "kbit/s");
                return new DetailItem(name, v.GetRawText(), name.EndsWith("MS", StringComparison.OrdinalIgnoreCase) ? "ms" : null);
            default: return null;
        }
    }

    /// <summary>One series per numeric metric column (oldest first); columns that are always 0 are left out.</summary>
    private List<DetailSeries> BuildSeries(JsonElement session, out JsonElement? latest)
    {
        latest = null;
        var result = new List<DetailSeries>();
        if (!TryGet(session, "SessionMetrics", out var metrics) || metrics.ValueKind != JsonValueKind.Array) return result;

        var samples = metrics.EnumerateArray()
            .Select(e => (Date: Date(e, options.Monitor.MetricsDateField), E: e))
            .Where(x => x.Date is not null)
            .OrderBy(x => x.Date).ToList();
        if (samples.Count == 0) return result;
        latest = samples[^1].E;

        var names = samples.SelectMany(x => Props(x.E)).Where(p => p.Value.ValueKind == JsonValueKind.Number)
            .Select(p => p.Name).Distinct()
            .Where(n => n != "Id" && !n.EndsWith("Id", StringComparison.Ordinal) && !n.EndsWith("Key", StringComparison.Ordinal));
        foreach (var name in names)
        {
            var bandwidth = name.Contains("Bandwidth", StringComparison.OrdinalIgnoreCase);
            var points = new List<double[]>();
            foreach (var (date, e) in samples)
                if (TryGet(e, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var dv))
                    points.Add([date!.Value.ToUnixTimeMilliseconds(), bandwidth ? Math.Floor(dv / Math.Max(1, options.Monitor.BandwidthDivisor)) : dv]);
            if (points.Any(p => p[1] != 0))
                result.Add(new DetailSeries(name, bandwidth ? "kbit/s" : name.EndsWith("MS", StringComparison.OrdinalIgnoreCase) ? "ms" : "", points));
        }
        return result;
    }

    private static bool TryGet(JsonElement e, string name, out JsonElement value)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    private static string? Str(JsonElement e, string name) =>
        TryGet(e, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
