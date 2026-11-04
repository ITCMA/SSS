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
    int? BandwidthKbps = null,
    DateTimeOffset? MeasuredAt = null,
    string? ClientVersion = null,
    string? ClientPlatform = null,
    long? UserId = null,
    string? ClientCountry = null,
    string? ClientCity = null,
    string? ClientNetwork = null,
    string? MachineId = null,
    int? LoadIndex = null);

/// <summary>One locally started application as the Monitor counts it (uberAgent data): launches, peak and users per day over the last days.</summary>
public sealed record AppUsage(string Name, int Launches, int PeakConcurrent, int UsersPerDay, int Machines, string? Process = null);

/// <summary>VDA machines with the same agent version and operating system.</summary>
public sealed record MachineGroup(string AgentVersion, string Os, int Machines, int InMaintenance, int Unregistered, int PoweredOff = 0, string DeliveryGroup = "");

/// <summary>One VDA machine as the Monitor sees it. The registration and power states are Citrix' own codes (registration 1 = registered).</summary>
public sealed record MachineInfo(string Name, string Group, string AgentVersion, string Os, int? Registration, int? Power, bool Maintenance, int? Sessions,
    DateTimeOffset? RegistrationChanged, string? Fault, string? Id = null, DateTimeOffset? PoweredOn = null);

/// <summary>One application crash (process name and machine id) or one connection failure (reason as the Monitor reports it and machine id).</summary>
public sealed record FaultRecord(string Name, string? MachineId);

public sealed record MonitorUser(string Name, string Sid);

/// <summary>History and quality metrics from the Citrix Monitor Service (the data source of Director).</summary>
public interface IMonitorClient
{
    /// <summary>Sessions of the user that have not ended yet, including their latest metrics.</summary>
    Task<IReadOnlyList<MonitorSession>> GetLiveAsync(string userSid, CancellationToken ct);

    /// <summary>Sessions of the user that ended at or after <paramref name="since"/>, newest first.</summary>
    Task<IReadOnlyList<MonitorSession>> GetEndedAsync(string userSid, DateTimeOffset since, CancellationToken ct);

    /// <summary>Most used locally started applications of the site over the last days (empty when the site does not deliver them).</summary>
    Task<IReadOnlyList<AppUsage>> GetAppUsageAsync(int days, int top, CancellationToken ct) => Task.FromResult<IReadOnlyList<AppUsage>>([]);

    /// <summary>The VDA machines of the site (empty when the site does not deliver them).</summary>
    Task<IReadOnlyList<MachineInfo>> GetMachinesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MachineInfo>>([]);

    /// <summary>The Local Host Cache report of the site's controllers as rows of properties (empty when the site does not deliver it).</summary>
    Task<IReadOnlyList<IReadOnlyList<DetailItem>>> GetLhcAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IReadOnlyList<DetailItem>>>([]);

    /// <summary>All sessions that have not ended yet, with their latest metrics and load index (Insights).</summary>
    Task<IReadOnlyList<MonitorSession>> GetFarmLiveAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MonitorSession>>([]);

    /// <summary>Sessions of all users that started at or after <paramref name="since"/>, with logon duration, machine and delivery group.</summary>
    Task<IReadOnlyList<MonitorSession>> GetRecentAsync(DateTimeOffset since, CancellationToken ct) => Task.FromResult<IReadOnlyList<MonitorSession>>([]);

    /// <summary>Users by the numeric id Monitor uses (name and SID).</summary>
    Task<IReadOnlyDictionary<long, MonitorUser>> GetUsersByIdAsync(IEnumerable<long> ids, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<long, MonitorUser>>(new Dictionary<long, MonitorUser>());

    /// <summary>Application crashes reported since <paramref name="since"/> (empty when the site does not deliver them).</summary>
    Task<IReadOnlyList<FaultRecord>> GetFaultsAsync(DateTimeOffset since, CancellationToken ct) => Task.FromResult<IReadOnlyList<FaultRecord>>([]);

    /// <summary>Connection failures since <paramref name="since"/> with the reason as the site reports it (empty when the site does not deliver them).</summary>
    Task<IReadOnlyList<FaultRecord>> GetFailuresAsync(DateTimeOffset since, CancellationToken ct) => Task.FromResult<IReadOnlyList<FaultRecord>>([]);

    /// <summary>The newest load index (0-10000) of each machine id.</summary>
    Task<IReadOnlyDictionary<string, int>> GetLoadIndexesAsync(IEnumerable<string> machineIds, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

    /// <summary>How many rows of a record set (for example ConnectionFailureLogs) exist for each session key.</summary>
    Task<IReadOnlyDictionary<string, int>> CountBySessionAsync(string recordSet, IEnumerable<string> sessionKeys, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

    /// <summary>All sessions that have not ended yet, without metrics (Environment view: client versions and addresses).</summary>
    Task<IReadOnlyList<MonitorSession>> GetAllLiveAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MonitorSession>>([]);

    /// <summary>Users known to Monitor whose name or UPN contains <paramref name="query"/> (helpdesk search).</summary>
    Task<IReadOnlyList<MonitorUser>> FindUsersAsync(string query, int max, CancellationToken ct);

    /// <summary>Everything Monitor collected about one session of the user (null if it is not the user's).</summary>
    Task<SessionDetails?> GetDetailsAsync(string userSid, string sessionKey, bool full, CancellationToken ct);

    /// <summary>Cheap round trip for the status page. Throws <see cref="ProviderUnavailableException"/> when Monitor fails.</summary>
    Task PingAsync(CancellationToken ct);
}

public sealed partial class ODataMonitorClient(HttpClient http, CitrixOptions options, ControllerDirectory? directory = null) : IMonitorClient
{
    [GeneratedRegex(@"^S-1-\d+(-\d+)+$")] private static partial Regex SidRegex();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,63}$")] private static partial Regex FieldRegex();
    [GeneratedRegex(@"^[\p{L}\p{N} ._@-]{2,64}$")] private static partial Regex SearchRegex();

    // Field names from the configuration are placed into the query ($orderby), so they must be plain identifiers.
    private readonly bool _configChecked = CheckConfig(options);

    private static bool CheckConfig(CitrixOptions o)
    {
        foreach (var f in new[] { o.Monitor.MetricsDateField, o.Monitor.IcaRttField, o.Monitor.BandwidthField, o.Monitor.ClientVersionField, o.Monitor.ClientPlatformField })
            if (!FieldRegex().IsMatch(f))
                throw new InvalidOperationException($"Sss:Citrix:Monitor field name '{f}' is not a valid OData property name.");
        foreach (var url in BuildBaseAddresses(o))
            if (url.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("Sss:Citrix:Monitor:BaseUrl(s) must be http:// or https:// addresses.");
        return true;
    }

    // $metadata is read once (and again after a few hours): which optional properties and record sets this site has, and their types.
    private Dictionary<string, Dictionary<string, string>>? _meta;
    private DateTimeOffset _metaUntil;

    private async Task<Dictionary<string, Dictionary<string, string>>> MetaAsync(CancellationToken ct)
    {
        if (_meta is not null && DateTimeOffset.UtcNow < _metaUntil) return _meta;
        try
        {
            var xml = await http.GetStringAsync(new Uri(Candidates()[0].Uri, "$metadata"), ct);
            var meta = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var et in System.Xml.Linq.XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "EntityType"))
                meta[(string?)et.Attribute("Name") ?? ""] = et.Elements().Where(p => p.Name.LocalName == "Property")
                    .ToDictionary(p => (string?)p.Attribute("Name") ?? "", p => (string?)p.Attribute("Type") ?? "", StringComparer.OrdinalIgnoreCase);
            _metaUntil = DateTimeOffset.UtcNow.AddHours(6);
            return _meta = meta;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or TaskCanceledException or ProviderUnavailableException)
        {
            _metaUntil = DateTimeOffset.UtcNow.AddMinutes(5);   // without it the rest keeps working; asked again soon
            return _meta ??= new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<string?> PropTypeAsync(string prop, CancellationToken ct) =>
        (await MetaAsync(ct)).Values.FirstOrDefault(e => e.ContainsKey(prop))?[prop];

    /// <summary>A GUID as a filter literal: bare for Edm.Guid, quoted for text properties.</summary>
    private async Task<string> GuidLiteralAsync(string prop, string guid, CancellationToken ct) =>
        await PropTypeAsync(prop, ct) is "Edm.String" ? $"'{guid}'" : guid;

    // Optional properties of the connection (version, platform, location, network type), asked for only when this site lists them: an unknown property would fail the whole query.
    private async Task<string> ClientFieldsAsync(CancellationToken ct)
    {
        var meta = await MetaAsync(ct);
        if (!meta.TryGetValue("Connection", out var props)) return "";
        string[] wanted = [options.Monitor.ClientVersionField, options.Monitor.ClientPlatformField, "ClientLocationCountry", "ClientLocationCity", "ClientNetworkInterfaceType"];
        return string.Concat(wanted.Distinct().Where(props.ContainsKey).Select(n => "," + n));
    }

    private async Task<string> ExpandAsync(CancellationToken ct) =>
        $"Machine($select=DnsName;$expand=DesktopGroup($select=Name)),CurrentConnection($select=ClientName,ClientAddress{await ClientFieldsAsync(ct)})";

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
                    $"&$expand={Esc($"{await ExpandAsync(ct)},SessionMetrics($orderby={dateField} desc;$top={MetricSamples})")}";
        return await WithLoadIndexAsync(await PagedAsync(query, ct), ct);
    }

    // Monitor writes several kinds of rows; the newest one does not always carry every metric, so a few samples are read.
    private const int MetricSamples = 10;

    // The load index of the machine (0-10000, like Director's) from the newest LoadIndexes row of each machine the live sessions run on. Best effort.
    private async Task<IReadOnlyList<MonitorSession>> WithLoadIndexAsync(IReadOnlyList<MonitorSession> sessions, CancellationToken ct)
    {
        var latest = await GetLoadIndexesAsync(sessions.Select(m => m.MachineId).OfType<string>(), ct);
        return latest.Count == 0 ? sessions : sessions.Select(m => m.MachineId is not null && latest.TryGetValue(m.MachineId, out var idx) ? m with { LoadIndex = idx } : m).ToList();
    }

    public async Task<IReadOnlyDictionary<string, int>> GetLoadIndexesAsync(IEnumerable<string> machineIds, CancellationToken ct)
    {
        var ids = machineIds.Where(i => Guid.TryParse(i, out _)).Select(i => Guid.Parse(i).ToString("D")).Distinct().Take(300).ToList();
        var latest = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var batch in ids.Chunk(10))
            {
                using var doc = await GetJsonAsync($"LoadIndexes?$filter={Esc(string.Join(" or ", batch.Select(i => $"MachineId eq {i}")))}&$orderby=CreatedDate%20desc&$top={batch.Length * 6}&$select=MachineId,EffectiveLoadIndex", ct);
                if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                foreach (var r in arr.EnumerateArray())
                    if (Str(r, "MachineId") is { } mid && !latest.ContainsKey(mid) && TryGet(r, "EffectiveLoadIndex", out var li) && li.TryGetInt32(out var idx)) latest[mid] = idx;
            }
        }
        catch (ProviderUnavailableException) { /* best effort */ }
        return latest;
    }

    public async Task<IReadOnlyList<FaultRecord>> GetFaultsAsync(DateTimeOffset since, CancellationToken ct)
    {
        var iso = since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        try
        {
            if ((await MetaAsync(ct)).FirstOrDefault(e => e.Key.StartsWith("ApplicationFault", StringComparison.OrdinalIgnoreCase)).Value is not { } props || !props.ContainsKey("FaultReportedDate")) return [];
            return (await PagedJsonAsync($"ApplicationFaults?$filter={Esc($"FaultReportedDate ge {iso}")}&$orderby=FaultReportedDate%20desc&$top=500", ct))
                .Select(r => new FaultRecord(Short(Str(r, "ProcessName") ?? Str(r, "FaultingApplicationPath")?.Split('\\', '/').Last()) ?? "?", Str(r, "MachineId"))).ToList();
        }
        catch (ProviderUnavailableException) { return []; }
    }

    // The failure records differ between Citrix versions: the date, reason and machine are taken from whichever of the usual properties the site lists.
    public async Task<IReadOnlyList<FaultRecord>> GetFailuresAsync(DateTimeOffset since, CancellationToken ct)
    {
        var iso = since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        try
        {
            if ((await MetaAsync(ct)).FirstOrDefault(e => e.Key.StartsWith("ConnectionFailureLog", StringComparison.OrdinalIgnoreCase)).Value is not { } props) return [];
            var date = new[] { "FailureDate", "CreatedDate" }.FirstOrDefault(props.ContainsKey);
            if (date is null) return [];
            var reasonProp = new[] { "FailureReason", "ConnectionFailureEnumValue", "FailureId", "FailureCategory" }.FirstOrDefault(props.ContainsKey);
            return (await PagedJsonAsync($"ConnectionFailureLogs?$filter={Esc($"{date} ge {iso}")}&$orderby={date}%20desc&$top=500", ct))
                .Select(r => new FaultRecord(reasonProp is null || !TryGet(r, reasonProp, out var v) ? "?" : v.ValueKind == JsonValueKind.String ? Short(v.GetString())! : v.GetRawText(),
                    props.ContainsKey("MachineId") ? Str(r, "MachineId") : null)).ToList();
        }
        catch (ProviderUnavailableException) { return []; }
    }

    public async Task<IReadOnlyList<MonitorSession>> GetFarmLiveAsync(CancellationToken ct)
    {
        var dateField = options.Monitor.MetricsDateField;
        var query = $"Sessions?$filter={Esc("EndDate eq null")}&$expand={Esc($"{await ExpandAsync(ct)},SessionMetrics($orderby={dateField} desc;$top=3)")}";
        return await WithLoadIndexAsync(await PagedAsync(query, ct), ct);
    }

    public async Task<IReadOnlyList<MonitorSession>> GetRecentAsync(DateTimeOffset since, CancellationToken ct)
    {
        var iso = since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var expand = "Machine($select=DnsName;$expand=DesktopGroup($select=Name))";
        return await PagedAsync($"Sessions?$filter={Esc($"StartDate ge {iso}")}&$select=SessionKey,SessionType,StartDate,EndDate,LogOnDuration,UserId,MachineId&$expand={Esc(expand)}&$orderby=StartDate%20desc", ct);
    }

    public async Task<IReadOnlyDictionary<long, MonitorUser>> GetUsersByIdAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var result = new Dictionary<long, MonitorUser>();
        var list = ids.Distinct().Take(60).ToList();
        if (list.Count == 0) return result;
        try
        {
            foreach (var r in await PagedJsonAsync($"Users?$filter={Esc(string.Join(" or ", list.Select(i => $"Id eq {i.ToString(CultureInfo.InvariantCulture)}")))}&$select=Id,Sid,UserName,Upn,Domain&$top=60", ct))
            {
                if (!TryGet(r, "Id", out var idEl) || !idEl.TryGetInt64(out var id)) continue;
                var sid = Str(r, "Sid"); var sam = Str(r, "UserName") ?? Str(r, "Upn");
                if (sid is null || sam is null || !SidRegex().IsMatch(sid)) continue;
                var domain = Str(r, "Domain");
                result[id] = new MonitorUser(string.IsNullOrEmpty(domain) ? sam : $"{domain}\\{sam}", sid);
            }
        }
        catch (ProviderUnavailableException) { /* names are optional */ }
        return result;
    }

    public async Task<IReadOnlyDictionary<string, int>> CountBySessionAsync(string recordSet, IEnumerable<string> sessionKeys, CancellationToken ct)
    {
        // only a fixed list of record sets and GUIDs go into the query
        if (recordSet is not ("ConnectionFailureLogs" or "SessionAutoReconnects")) throw new ArgumentException("Unknown record set.", nameof(recordSet));
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var keys = sessionKeys.Where(k => Guid.TryParse(k, out _)).Select(k => Guid.Parse(k).ToString("D")).Distinct().Take(300).ToList();
        try
        {
            if (!(await MetaAsync(ct)).ContainsKey(recordSet)) return counts;
            foreach (var batch in keys.Chunk(15))
                foreach (var r in await PagedJsonAsync($"{recordSet}?$filter={Esc(string.Join(" or ", batch.Select(k => $"SessionKey eq {k}")))}&$select=SessionKey&$top=500", ct))
                    if (Str(r, "SessionKey") is { } key) counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        catch (ProviderUnavailableException) { /* optional */ }
        return counts;
    }

    public async Task<IReadOnlyList<MachineInfo>> GetMachinesAsync(CancellationToken ct)
    {
        try
        {
            var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var g in await PagedJsonAsync("DesktopGroups?$select=Id,Name&$top=500", ct))
                    if (Str(g, "Id") is { } gid && Str(g, "Name") is { } gn) groups[gid] = Short(gn)!;
            }
            catch (ProviderUnavailableException) { /* the group is then shown by its id */ }

            int? Int(JsonElement r, string n) => TryGet(r, n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : null;
            return (await PagedJsonAsync("Machines?$top=500", ct)).Select(r => new MachineInfo(
                Short(Str(r, "DnsName") ?? Str(r, "HostedMachineName") ?? Str(r, "Name")) ?? (Str(r, "Id") is { Length: >= 8 } mid ? $"({mid[..8]})" : "?"), Str(r, "DesktopGroupId") is { } dg ? groups.GetValueOrDefault(dg) ?? dg : "",
                Short(Str(r, "AgentVersion")) ?? "?", Short(Str(r, "OSType")) ?? "?", Int(r, "CurrentRegistrationState"), Int(r, "CurrentPowerState"),
                TryGet(r, "IsInMaintenanceMode", out var m) && m.ValueKind == JsonValueKind.True, Int(r, "CurrentSessionCount"),
                Date(r, "RegistrationStateChangeDate"), TryGet(r, "FaultState", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetRawText() : null, Str(r, "Id"), Date(r, "PoweredOnDate"))).ToList();
        }
        catch (ProviderUnavailableException) { return []; }
    }

    public async Task<IReadOnlyList<IReadOnlyList<DetailItem>>> GetLhcAsync(CancellationToken ct)
    {
        try
        {
            return (await PagedJsonAsync("LhcReport?$top=100", ct))
                .Select(r => (IReadOnlyList<DetailItem>)Props(r).Select(p => Format(p.Name, p.Value)).OfType<DetailItem>().ToList()).Where(r => r.Count > 0).ToList();
        }
        catch (ProviderUnavailableException) { return []; }
    }

    // A few pages of plain records (no MonitorSession mapping).
    private async Task<List<JsonElement>> PagedJsonAsync(string firstRelative, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        Uri? next = null;
        var baseUri = http.BaseAddress!;
        for (var page = 0; page == 0 || (next is not null && page < options.Monitor.MaxPages); page++)
        {
            JsonDocument doc;
            if (next is null) (doc, baseUri) = await GetJsonWithBaseAsync(firstRelative, ct);
            else doc = await GetJsonAsync(next, ct);
            using var _doc = doc;
            if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array) result.AddRange(arr.EnumerateArray().Select(e => e.Clone()));
            next = null;
            if (doc.RootElement.TryGetProperty("@odata.nextLink", out var nl) && nl.GetString() is { } link && Uri.TryCreate(baseUri, link, out var candidate)
                && string.Equals(candidate.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)) next = new Uri(baseUri, candidate.PathAndQuery);
        }
        return result;
    }

    public async Task<IReadOnlyList<MonitorSession>> GetEndedAsync(string userSid, DateTimeOffset since, CancellationToken ct)
    {
        if (await ResolveUserIdAsync(userSid, ct) is not { } uid) return [];
        var iso = since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var query = $"Sessions?$filter={Esc($"UserId eq {uid.ToString(CultureInfo.InvariantCulture)} and EndDate ge {iso}")}" +
                    $"&$expand={Esc(await ExpandAsync(ct))}&$orderby=EndDate%20desc&$top=100";
        return await PagedAsync(query, ct);
    }

    public async Task<IReadOnlyList<MonitorSession>> GetAllLiveAsync(CancellationToken ct)
    {
        var connection = $"CurrentConnection($select=ClientName,ClientAddress{await ClientFieldsAsync(ct)})";
        return await PagedAsync($"Sessions?$filter={Esc("EndDate eq null")}&$select=SessionKey,SessionType,StartDate,UserId&$expand={Esc(connection)}", ct);
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

    // Delivery Controllers are tried in order. With a controller directory the order comes from the balancing strategy and the
    // answer times and failures feed its statistics; with explicit Monitor:BaseUrl(s) the fixed list is used, last answering one first.
    private Uri[]? _bases;
    private int _active;

    private Uri[] FixedBases => _bases ??= (http.BaseAddress is { } b && !BuildBaseAddresses(options).Any(u => u == b)
        ? [b, .. BuildBaseAddresses(options)]
        : BuildBaseAddresses(options).ToArray());

    private bool UsesDirectory => directory is not null && options.Monitor.BaseUrls.Length == 0 && options.Monitor.BaseUrl is null;

    private IReadOnlyList<(Uri Uri, string? Controller)> Candidates()
    {
        if (!UsesDirectory) return FixedBases.Select(u => (u, (string?)null)).ToList();
        var scheme = options.Monitor.UseHttps ? "https" : "http";
        return directory!.Order().Select(n => (new Uri($"{scheme}://{n}/Citrix/Monitor/OData/v4/Data/"), (string?)n)).ToList();
    }

    private async Task<(JsonDocument Doc, Uri Base)> GetJsonWithBaseAsync(string relative, CancellationToken ct)
    {
        var bases = Candidates();
        ProviderUnavailableException? last = null;
        for (var n = 0; n < bases.Count; n++)
        {
            var i = UsesDirectory ? n : (Volatile.Read(ref _active) + n) % bases.Count;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var doc = await GetJsonAsync(new Uri(bases[i].Uri, relative), ct);
                Volatile.Write(ref _active, i);
                if (bases[i].Controller is { } ok) directory!.RecordSuccess(ok, sw.Elapsed.TotalMilliseconds);
                return (doc, bases[i].Uri);
            }
            catch (ProviderUnavailableException ex)
            {
                last = ex;
                if (bases[i].Controller is { } bad) directory!.RecordFailure(bad);
            }
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

    // Version and platform end up in tables and counts; a few dozen characters are plenty.
    private static string? Short(string? v) => v is null ? null : v.Length > 40 ? v[..40] : v;

    private MonitorSession? Parse(JsonElement s)
    {
        var key = Str(s, "SessionKey");
        if (key is null) return null;

        string? machine = null, group = null, client = null, address = null, version = null, platform = null, country = null, city = null, network = null;
        if (TryGet(s, "Machine", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            machine = Str(m, "DnsName");
            if (TryGet(m, "DesktopGroup", out var dg) && dg.ValueKind == JsonValueKind.Object) group = Str(dg, "Name");
        }
        if (TryGet(s, "CurrentConnection", out var c) && c.ValueKind == JsonValueKind.Object)
        {
            client = Str(c, "ClientName");
            address = Str(c, "ClientAddress");
            version = Short(Str(c, options.Monitor.ClientVersionField));
            platform = Short(Str(c, options.Monitor.ClientPlatformField));
            country = Short(Str(c, "ClientLocationCountry"));
            city = Short(Str(c, "ClientLocationCity"));
            network = Short(Str(c, "ClientNetworkInterfaceType"));
        }

        int? logon = TryGet(s, "LogOnDuration", out var ld) && ld.ValueKind == JsonValueKind.Number && ld.TryGetInt64(out var ms)
            ? (int)Math.Clamp(ms / 1000, 0, int.MaxValue) : null;
        var isDesktop = TryGet(s, "SessionType", out var st) && st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out var t) && t == 0;

        var (rtt, bandwidth, at) = LatestMetrics(s);
        long? userId = TryGet(s, "UserId", out var uidEl) && uidEl.ValueKind == JsonValueKind.Number && uidEl.TryGetInt64(out var uidValue) ? uidValue : null;
        return new MonitorSession(key, machine, group, isDesktop, Date(s, "StartDate"), Date(s, "EndDate"),
            logon, client, address, rtt, bandwidth, at, version, platform, userId, country, city, network, Str(s, "MachineId"));
    }

    /// <summary>Newest usable value per metric. 0 means "not measured" in Monitor, so it never counts as a (perfect or
    /// terrible) value; the sample time is that of the newest value used.</summary>
    private (int? RttMs, int? BandwidthKbps, DateTimeOffset? At) LatestMetrics(JsonElement session)
    {
        if (!TryGet(session, "SessionMetrics", out var metrics) || metrics.ValueKind != JsonValueKind.Array) return (null, null, null);
        var samples = metrics.EnumerateArray()
            .Select(m => (Date: Date(m, options.Monitor.MetricsDateField) ?? DateTimeOffset.MinValue, E: m))
            .OrderByDescending(x => x.Date).ToList();

        int? rtt = null, kbps = null;
        DateTimeOffset? at = null;
        foreach (var (date, m) in samples)
        {
            if (rtt is null && TryGet(m, options.Monitor.IcaRttField, out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetDouble(out var rv) && rv > 0)
            { rtt = (int)Math.Round(rv); at ??= date; }
            if (kbps is null && TryGet(m, options.Monitor.BandwidthField, out var bw) && bw.ValueKind == JsonValueKind.Number && bw.TryGetDouble(out var bv) && bv > 0)
            { kbps = (int)Math.Min(int.MaxValue, bv / Math.Max(1, options.Monitor.BandwidthDivisor)); at ??= date; }
        }
        return (rtt, kbps, at is { } a && a > DateTimeOffset.MinValue ? a : null);
    }

    // ---- expert view ----------------------------------------------------------------------------

    /// <summary>Optional record sets of a session. A site that lacks one (older versions, other schema) just reports it as unavailable.</summary>
    private static readonly (string Id, string Set)[] OptionalParts =
    [
        ("connections", "Connections"),
        ("failures", "ConnectionFailureLogs"),
        ("applications", "ApplicationInstances"),
        ("faults", "ApplicationFaults"),
        ("reconnects", "SessionAutoReconnects"),
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
                if (id == "applications") rows = await WithApplicationNamesAsync(rows, ct);
                if (rows.Count > 0) tables.Add(new DetailTable(id, rows));
            }
            catch (ProviderUnavailableException) { unavailable.Add(id); }
        }

        // Logon details the Monitor keeps apart from the connection: user initialisation and AppX steps (merged into the logon steps), profile and group
        // policy figures, the HDX protocol details of the latest sample and the gateway location.
        if (await FirstRowAsync("LogOnMetrics", $"SessionKey eq {key}", ct) is { } lom)
            foreach (var rec in connectionRecords)
                foreach (var (k, val) in LogonWaterfallBuilder.Flatten(lom)) rec.TryAdd(k, val);
        if (await FirstRowAsync("SessionLogonMetrics", $"SessionKey eq {key}", ct) is { } slm) AddLogonMetrics(slm, buckets, tables);
        if (await FirstRowAsync("SessionMetricsLatest", $"SessionKey eq {key}", ct) is { } sml)
        {
            foreach (var p in Props(sml).Where(p => !p.Name.EndsWith("Date", StringComparison.Ordinal) && !p.Name.EndsWith("Key", StringComparison.Ordinal) && !p.Name.EndsWith("Latency", StringComparison.Ordinal)))
                Add("metrics", p.Name, p.Value);
            if (Str(sml, "GatewayPopName") is { } pop && PopRegex().IsMatch(pop) && await FirstRowAsync("GatewayPopInfo", $"GatewayPopName eq '{pop}'", ct) is { } info)
                foreach (var p in Props(info).Where(p => p.Name != "GatewayPopName")) Add("metrics", p.Name, p.Value);
        }

        // Applications started inside the session (uberAgent data in the Monitor), the client device and the VDA over time
        try { if (await LaunchRowsAsync(key, ct) is { Count: > 0 } launches) tables.Add(new DetailTable("launches", launches)); }
        catch (ProviderUnavailableException) { /* the site does not deliver them: no table */ }
        series = [.. series, .. await ExtraSeriesAsync("EndpointMetrics", $"SessionKey eq {key}", "Endpoint.", null, ct),
            .. (Str(s, "MachineId") is { } mid && Guid.TryParse(mid, out var mg)
                ? await ExtraSeriesAsync("ResourceUtilization", $"MachineId eq {mg:D}", "Vda.", VdaMap, ct) : [])];

        string[] order = ["session", "logon", "client", "network", "machine", "technical"];
        var groups = order.Where(buckets.ContainsKey).Select(id => new DetailGroup(id, buckets[id])).ToList();
        double? logOnMs = TryGet(s, "LogOnDuration", out var lod) && lod.ValueKind == JsonValueKind.Number ? lod.GetDouble() : null;
        var waterfall = LogonWaterfallBuilder.Build(connectionRecords, logOnMs);
        if (waterfall is not null && await UserLogonAveragesAsync(uid, key, ct) is { Count: > 0 } averages)
            waterfall = waterfall with { Steps = waterfall.Steps.Select(st => averages.TryGetValue(st.Id, out var avg) ? st with { AvgUserMs = avg } : st).ToList() };
        return new SessionDetails(key, groups, series, tables, unavailable, waterfall);
    }

    [GeneratedRegex(@"^[\p{L}\p{N} ._-]{1,80}$")] private static partial Regex PopRegex();

    // The application records name the application only by its id: put the published name in front.
    private async Task<List<IReadOnlyList<DetailItem>>> WithApplicationNamesAsync(List<IReadOnlyList<DetailItem>> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.FirstOrDefault(i => i.Key == "ApplicationId")?.Value).Where(v => v is not null && long.TryParse(v, out _)).Distinct().Take(40).ToList();
        if (ids.Count == 0) return rows;
        try
        {
            using var doc = await GetJsonAsync($"Applications?$filter={Esc(string.Join(" or ", ids.Select(i => $"Id eq {i}")))}&$top=40", ct);
            var names = new Dictionary<string, string>();
            if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var a in arr.EnumerateArray())
                    if (TryGet(a, "Id", out var id) && (Str(a, "PublishedName") ?? Str(a, "Name")) is { Length: > 0 } n) names[id.ToString()] = Short(n)!;
            return rows.Select(r => r.FirstOrDefault(i => i.Key == "ApplicationId")?.Value is { } aid && names.TryGetValue(aid, out var n)
                ? (IReadOnlyList<DetailItem>)[new DetailItem("Application", n), .. r] : r).ToList();
        }
        catch (ProviderUnavailableException) { return rows; }
    }

    private async Task<JsonElement?> FirstRowAsync(string set, string filter, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync($"{set}?$filter={Esc(filter)}&$top=1", ct);
            return doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0 ? arr[0].Clone() : null;
        }
        catch (ProviderUnavailableException) { return null; }
    }

    // Profile and group-policy figures of the logon (what Director shows under "profile load" and "group policies").
    private void AddLogonMetrics(JsonElement row, Dictionary<string, List<DetailItem>> buckets, List<DetailTable> tables)
    {
        void AddItem(DetailItem item)
        {
            if (!buckets.TryGetValue("logon", out var list)) buckets["logon"] = list = [];
            list.Add(item);
        }
        foreach (var p in Props(row).Where(p => !p.Name.EndsWith("Date", StringComparison.Ordinal) && !p.Name.EndsWith("Key", StringComparison.Ordinal)
                     && p.Name is not ("GPORecords" or "AppxLoadPackages") && Format(p.Name, p.Value) is not null))
        {
            // sizes arrive in bytes
            if (p.Name is "ProfileSize" or "DiskCapacity" or "DiskSizeUsed" && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var bytes))
                AddItem(new DetailItem(p.Name, Math.Round(bytes / 1073741824.0, 1).ToString(CultureInfo.InvariantCulture), "GB"));
            else AddItem(Format(p.Name, p.Value)!);
        }
        if (TryGet(row, "LargeFilesCount", out var large) && TryGet(row, "FilesCount", out var all) && large.TryGetInt64(out var l) && all.TryGetInt64(out var a))
            AddItem(new DetailItem("LargeFiles", $"{l} / {a}"));
        foreach (var name in new[] { "GPORecords", "AppxLoadPackages" })
        {
            if (Str(row, name) is not { Length: > 0 } text) continue;
            var slow = SlowRows(text);
            if (slow.Count > 0) tables.Add(new DetailTable(name == "GPORecords" ? "gpo" : "appx", slow));
            else AddItem(new DetailItem(name, text.Length > 300 ? text[..300] + "…" : text));
        }
    }

    // The text is a JSON list of records (name and a duration); the slowest ten become table rows. Anything else stays raw text.
    private static List<IReadOnlyList<DetailItem>> SlowRows(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var rows = new List<(double Ms, IReadOnlyList<DetailItem> Row)>();
            foreach (var e in doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
            {
                var nameProp = e.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.String && Regex.IsMatch(p.Name, "name|cse|extension|package", RegexOptions.IgnoreCase));
                var durProp = e.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Number && Regex.IsMatch(p.Name, "dur|elapsed|ms$", RegexOptions.IgnoreCase));
                if (nameProp.Value.ValueKind != JsonValueKind.String) continue;
                double ms;
                if (durProp.Value.ValueKind == JsonValueKind.Number) ms = durProp.Value.GetDouble();
                else
                {
                    // no duration: the records carry a start and an end as milliseconds since 1970
                    var st = e.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Number && Regex.IsMatch(p.Name, "start", RegexOptions.IgnoreCase));
                    var en = e.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Number && Regex.IsMatch(p.Name, "end", RegexOptions.IgnoreCase));
                    if (st.Value.ValueKind != JsonValueKind.Number || en.Value.ValueKind != JsonValueKind.Number) continue;
                    ms = en.Value.GetDouble() - st.Value.GetDouble();
                    if (ms < 0) continue;
                }
                rows.Add((ms, [new DetailItem("Name", nameProp.Value.GetString() ?? ""), new DetailItem("Duration", ms.ToString(CultureInfo.InvariantCulture), "ms")]));
            }
            return rows.OrderByDescending(r => r.Ms).Take(10).Select(r => r.Row).ToList();
        }
        catch (JsonException) { return []; }
    }

    // Columns shown for an application start; the rest of the record (ids, user name) stays out of the table.
    private static readonly string[] LaunchColumns = ["ProcessName", "AppVersion", "ExecutablePath", "StartDate", "EndDate", "StartupTimeMs", "ProcessParentName", "IsPublishedApp"];

    private async Task<List<IReadOnlyList<DetailItem>>> LaunchRowsAsync(string key, CancellationToken ct)
    {
        var literal = await GuidLiteralAsync("SessionGUID", key, ct);
        var rows = new List<(DateTimeOffset? Start, IReadOnlyList<DetailItem> Row)>();
        var seen = new HashSet<string>();
        var failed = 0;
        foreach (var set in new[] { "ActiveApplicationLaunches", "HistoricalApplicationLaunches" })
        {
            try
            {
                using var doc = await GetJsonAsync($"{set}?$filter={Esc($"SessionGUID eq {literal}")}&$top=200", ct);
                if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                foreach (var r in arr.EnumerateArray())
                {
                    if (Str(r, "ProcessGUID") is { } pg && !seen.Add(pg)) continue;
                    var items = LaunchColumns.Select(c => TryGet(r, c, out var v) ? Format(c, v) : null).OfType<DetailItem>().ToList();
                    if (items.Count > 0) rows.Add((Date(r, "StartDate"), items));
                }
            }
            catch (ProviderUnavailableException) { failed++; }
        }
        if (failed == 2) throw new ProviderUnavailableException("Application launches are not delivered by this site.");
        return rows.OrderByDescending(x => x.Start).Select(x => x.Row).Take(200).ToList();
    }

    // Memory of the VDA in percent next to its CPU load and session count.
    private static IEnumerable<(string Name, double Value)> VdaMap(JsonElement r)
    {
        if (TryGet(r, "PercentCpu", out var cpu) && cpu.TryGetDouble(out var c)) yield return ("PercentCpu", c);
        if (TryGet(r, "UsedMemory", out var um) && um.TryGetDouble(out var u) && TryGet(r, "TotalMemory", out var tm) && tm.TryGetDouble(out var t) && t > 0) yield return ("MemoryPercent", Math.Round(u / t * 100, 1));
        if (TryGet(r, "SessionCount", out var sc) && sc.TryGetDouble(out var n)) yield return ("SessionCount", n);
    }

    /// <summary>Numeric columns of a record set as series, oldest first. <paramref name="map"/> overrides the columns; without it every numeric column
    /// except ids and keys is used. Series that are always 0 are left out. A site without the set simply gets no series.</summary>
    private async Task<List<DetailSeries>> ExtraSeriesAsync(string set, string filter, string prefix, Func<JsonElement, IEnumerable<(string Name, double Value)>>? map, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync($"{set}?$filter={Esc(filter)}&$orderby={options.Monitor.MetricsDateField}%20desc&$top=240", ct);
            if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
            var byName = new Dictionary<string, List<double[]>>();
            foreach (var r in arr.EnumerateArray().Select(e => (Date: Date(e, options.Monitor.MetricsDateField), E: e)).Where(x => x.Date is not null).OrderBy(x => x.Date))
            {
                var values = map is not null ? map(r.E)
                    : Props(r.E).Where(p => p.Value.ValueKind == JsonValueKind.Number && p.Name != "Id" && !p.Name.EndsWith("Id", StringComparison.Ordinal) && !p.Name.EndsWith("Key", StringComparison.Ordinal))
                        .Select(p => (p.Name, p.Value.GetDouble()));
                foreach (var (name, value) in values)
                {
                    if (!byName.TryGetValue(name, out var l)) byName[name] = l = [];
                    if (value < 0) continue;   // -1 means "not available" (for example the Wi-Fi signal of a wired device)
                    l.Add([r.Date!.Value.ToUnixTimeMilliseconds(), value]);
                }
            }
            return byName.Where(kv => kv.Value.Any(p => p[1] != 0)).Select(kv => new DetailSeries(prefix + kv.Key, UnitOf(kv.Key), kv.Value)).ToList();
        }
        catch (ProviderUnavailableException) { return []; }
    }

    private static string UnitOf(string name) => name.Contains("Percent", StringComparison.OrdinalIgnoreCase) ? "%" : name.StartsWith("Throughput", StringComparison.OrdinalIgnoreCase) ? "kbit/s" : "";

    // Earlier logons of the same user (a week): the average of each step is the comparison value next to the current one. Best effort.
    private readonly ConcurrentDictionary<long, (Dictionary<string, double> Averages, DateTimeOffset Until)> _logonAverages = new();

    private async Task<Dictionary<string, double>> UserLogonAveragesAsync(long uid, string excludeKey, CancellationToken ct)
    {
        if (_logonAverages.TryGetValue(uid, out var hit) && hit.Until > DateTimeOffset.UtcNow) return hit.Averages;
        var result = new Dictionary<string, double>();
        try
        {
            var since = DateTimeOffset.UtcNow.AddDays(-7).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            using var sessions = await GetJsonAsync($"Sessions?$filter={Esc($"UserId eq {uid.ToString(CultureInfo.InvariantCulture)} and StartDate ge {since}")}&$select=SessionKey,LogOnDuration&$orderby=StartDate%20desc&$top=30", ct);
            var rows = sessions.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(e => (Key: Str(e, "SessionKey"), Logon: TryGet(e, "LogOnDuration", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() : (double?)null))
                    .Where(x => x.Key is not null && Guid.TryParse(x.Key, out _) && !x.Key!.Equals(excludeKey, StringComparison.OrdinalIgnoreCase)).ToList()
                : [];
            if (rows.Count >= 2)
            {
                var filter = string.Join(" or ", rows.Select(x => $"SessionKey eq {Guid.Parse(x.Key!):D}"));
                using var conns = await GetJsonAsync($"Connections?$filter={Esc(filter)}&$top=300", ct);
                var perSession = conns.RootElement.TryGetProperty("value", out var cv) && cv.ValueKind == JsonValueKind.Array
                    ? cv.EnumerateArray().Select(e => (Key: Str(e, "SessionKey") ?? "", Rec: LogonWaterfallBuilder.Flatten(e))).GroupBy(x => x.Key).ToList() : [];
                var waterfalls = perSession.Select(g => LogonWaterfallBuilder.Build(g.Select(x => x.Rec), rows.FirstOrDefault(r => r.Key!.Equals(g.Key, StringComparison.OrdinalIgnoreCase)).Logon)).OfType<LogonWaterfall>().ToList();
                result = LogonWaterfallBuilder.AverageSteps(waterfalls);
            }
        }
        catch (ProviderUnavailableException) { /* no comparison values: the waterfall is shown without them */ }
        _logonAverages[uid] = (result, DateTimeOffset.UtcNow.AddMinutes(10));
        return result;
    }

    // ---- fleet: locally started applications ---------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<AppUsage>> GetAppUsageAsync(int days, int top, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.Date.AddDays(-Math.Clamp(days, 1, 30)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var usage = new Dictionary<string, (long Launches, int Peak, int Users, int Machines)>();
        try
        {
            using var doc = await GetJsonAsync($"ApplicationsUsageSummaryData?$filter={Esc($"SummaryDate ge {since}")}&$select=AppId,UniqueUserCount,TotalMachinesCount,PeakConcurrentLaunches,TotalLaunches&$top=2000", ct);
            if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
            foreach (var r in arr.EnumerateArray())
            {
                var id = TryGet(r, "AppId", out var a) ? a.ToString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                int I(string n) => TryGet(r, n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : 0;
                var cur = usage.TryGetValue(id, out var u) ? u : default;
                usage[id] = (cur.Launches + I("TotalLaunches"), Math.Max(cur.Peak, I("PeakConcurrentLaunches")), Math.Max(cur.Users, I("UniqueUserCount")), Math.Max(cur.Machines, I("TotalMachinesCount")));
            }
        }
        catch (ProviderUnavailableException) { return []; }

        var names = new Dictionary<string, string>();
        try
        {
            using var doc = await GetJsonAsync("ApplicationNameIds?$select=AppId,AppName&$top=2000", ct);
            if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var r in arr.EnumerateArray())
                    if (TryGet(r, "AppId", out var a) && Str(r, "AppName") is { Length: > 0 } n) names[a.ToString()] = n;
        }
        catch (ProviderUnavailableException) { /* the ids are shown instead */ }

        var best = usage.OrderByDescending(kv => kv.Value.Launches).Take(Math.Clamp(top, 1, 50)).ToList();

        // the program behind each id (the names in the Monitor are shortened), from one launch of each
        var processes = new Dictionary<string, string>();
        try
        {
            string Literal(string id) => Guid.TryParse(id, out var g) ? g.ToString("D") : long.TryParse(id, out _) ? id : $"'{id.Replace("'", "''")}'";
            if (best.Count > 0 && best.All(kv => kv.Key.Length <= 80))
            {
                using var doc = await GetJsonAsync($"HistoricalApplicationLaunches?$filter={Esc(string.Join(" or ", best.Select(kv => $"AppId eq {Literal(kv.Key)}")))}&$select=AppId,ProcessName&$top=400", ct);
                if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var r in arr.EnumerateArray())
                        if (TryGet(r, "AppId", out var a) && Str(r, "ProcessName") is { Length: > 0 } pn) processes.TryAdd(a.ToString(), pn);
            }
        }
        catch (ProviderUnavailableException) { /* the shortened names are shown alone */ }

        return best.Select(kv => new AppUsage(Short(names.GetValueOrDefault(kv.Key) ?? kv.Key)!, (int)Math.Min(int.MaxValue, kv.Value.Launches), kv.Value.Peak, kv.Value.Users,
            kv.Value.Machines, Short(processes.GetValueOrDefault(kv.Key)))).ToList();
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
            // for these metrics 0 means "not measured": leave the sample out, so the chart shows a gap and not a dive to zero
            var zeroIsGap = bandwidth || Regex.IsMatch(name, "Rtt|Latency", RegexOptions.IgnoreCase);
            var points = new List<double[]>();
            foreach (var (date, e) in samples)
                if (TryGet(e, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var dv) && !(zeroIsGap && dv == 0))
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
