using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sss.Web.Services;

/// <summary>What one SSS server reports about itself.</summary>
public sealed record InstanceInfo(
    string Server, string Version, DateTimeOffset StartedAt, DateTimeOffset LastSeen, string ConfigHash,
    IReadOnlyDictionary<string, string> Sections, IReadOnlyList<string> LocalKeys, string Health, bool Draining);

/// <summary>One line of the server list with the verdict.</summary>
public sealed record InstanceView(string Server, string State, string? Version, DateTimeOffset? LastSeen, bool? InSync, IReadOnlyList<string> DiffSections,
    IReadOnlyList<string> LocalKeys, string Health, bool Draining, bool VersionDiffers);

/// <summary>Fingerprint of the shared configuration file. The sections under "Sss" are hashed separately so a difference can be
/// named. Whitespace and the order of the keys do not matter. Settings that belong to one server only go into sss.local.json,
/// which is not part of the fingerprint (its key names are reported, not the values).</summary>
public static class ConfigFingerprint
{
    public static (string Hash, IReadOnlyDictionary<string, string> Sections) Of(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ("none", new Dictionary<string, string>());
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        var sections = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var sss = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Sss", out var s) && s.ValueKind == JsonValueKind.Object ? s : root;
        if (sss.ValueKind == JsonValueKind.Object)
            foreach (var p in sss.EnumerateObject()) sections[p.Name] = Hash(Canonical(p.Value));
        return (Hash(Canonical(root)), sections);
    }

    public static string Canonical(JsonElement e)
    {
        var sb = new StringBuilder();
        Write(e, sb);
        return sb.ToString();
    }

    private static void Write(JsonElement e, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append(JsonSerializer.Serialize(p.Name.ToLowerInvariant())).Append(':');
                    Write(p.Value, sb);
                    sb.Append(',');
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                foreach (var i in e.EnumerateArray()) { Write(i, sb); sb.Append(','); }
                sb.Append(']');
                break;
            case JsonValueKind.String:
                sb.Append(JsonSerializer.Serialize(e.GetString()));
                break;
            default:
                sb.Append(e.GetRawText());
                break;
        }
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();

    /// <summary>Key paths (Section:Key) of a configuration file, without values.</summary>
    public static IReadOnlyList<string> Keys(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        var keys = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            void Walk(JsonElement e, string path)
            {
                if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) Walk(p.Value, path.Length == 0 ? p.Name : path + ":" + p.Name);
                else if (path.Length > 0) keys.Add(path);
            }
            Walk(doc.RootElement, "");
        }
        catch (JsonException) { /* the configuration check reports a broken file */ }
        return keys;
    }
}

/// <summary>Writes the heartbeat of this server and reads those of the others, so every server can show the complete list.
/// A draining server (flag file in the data folder) reports 503 on /healthz so the load balancer takes it out before an update.</summary>
public sealed class InstanceRegistry(SssOptions options, string contentRoot, string dataDirectory, TimeProvider clock, ILogger<InstanceRegistry> log,
    Func<string>? health = null) : BackgroundService
{
    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public bool Enabled => options.Instances.Directory.Length > 0;
    public string DrainFlag => Path.Combine(dataDirectory, "drain.flag");
    public bool Draining => File.Exists(DrainFlag);

    public InstanceInfo Self()
    {
        string? Read(string name) { try { var p = Path.Combine(contentRoot, name); return File.Exists(p) ? File.ReadAllText(p) : null; } catch (IOException) { return null; } }
        var (hash, sections) = ConfigFingerprint.Of(Read("sss.config.json"));
        return new InstanceInfo(Environment.MachineName, AppInfo.Version, Started, clock.GetUtcNow(), hash, sections, ConfigFingerprint.Keys(Read("sss.local.json")),
            health?.Invoke() ?? "ok", Draining);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { File.Delete(DrainFlag); } catch (IOException) { }   // a restart ends a drain: the recycle after an update brings the server back
        if (!Enabled) return;
        var failed = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Directory.CreateDirectory(options.Instances.Directory);
                var path = Path.Combine(options.Instances.Directory, $"sss-{Environment.MachineName}.json");
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Self(), Json));
                File.Move(tmp, path, overwrite: true);
                failed = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!failed) log.LogWarning("The shared folder for the server list is not writable ({Folder}): {Error}", options.Instances.Directory, ex.Message);
                failed = true;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.Instances.HeartbeatSeconds, 10, 3600)), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>All servers that reported, plus the expected ones that did not. The reference configuration is the one most
    /// online servers have; a server with another one shows which sections differ.</summary>
    public IReadOnlyList<InstanceView> View()
    {
        var o = options.Instances;
        var found = new List<InstanceInfo>();
        if (Enabled && Directory.Exists(o.Directory))
            foreach (var f in Directory.GetFiles(o.Directory, "sss-*.json"))
            {
                try { if (new FileInfo(f).Length > 256 * 1024) continue;   // a status file is a few KB; ignore anything that is not
                      if (JsonSerializer.Deserialize<InstanceInfo>(File.ReadAllText(f), Json) is { } i) found.Add(i); }
                catch (Exception ex) when (ex is IOException or JsonException) { /* half-written or foreign file */ }
            }
        found.RemoveAll(i => i.Server.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase));
        found.Add(Self());   // this server is always listed, with its current values

        var now = clock.GetUtcNow();
        bool Online(InstanceInfo i) => (now - i.LastSeen).TotalSeconds <= o.StaleSeconds;
        var online = found.Where(Online).ToList();
        var reference = online.GroupBy(i => i.ConfigHash).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(i => i.LastSeen)).FirstOrDefault()?.First();
        var topVersion = online.GroupBy(i => i.Version).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;

        var views = new List<InstanceView>();
        foreach (var i in found.OrderBy(i => i.Server, StringComparer.OrdinalIgnoreCase))
        {
            var isOnline = Online(i);
            var expected = o.Expected.Length == 0 || o.Expected.Contains(i.Server, StringComparer.OrdinalIgnoreCase);
            var diff = reference is null || i.ConfigHash == reference.ConfigHash ? []
                : reference.Sections.Keys.Union(i.Sections.Keys).Where(k => !(reference.Sections.TryGetValue(k, out var a) && i.Sections.TryGetValue(k, out var b) && a == b)).Order().ToList();
            views.Add(new InstanceView(i.Server, !expected ? "unexpected" : isOnline ? (i.Draining ? "draining" : "online") : "stale", i.Version, i.LastSeen,
                isOnline && reference is not null ? i.ConfigHash == reference.ConfigHash : null, diff, i.LocalKeys, i.Health, i.Draining,
                isOnline && topVersion is not null && i.Version != topVersion));
        }
        foreach (var name in o.Expected)
            if (!found.Any(i => i.Server.Equals(name, StringComparison.OrdinalIgnoreCase)))
                views.Add(new InstanceView(name, "missing", null, null, null, [], [], "unknown", false, false));
        return views;
    }
}
