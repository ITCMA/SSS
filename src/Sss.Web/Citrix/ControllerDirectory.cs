using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sss.Web.Citrix;

public sealed record ControllerInfo(string Name, string Source, string? State = null, string? Version = null, string? Zone = null, DateTimeOffset? LastSeen = null);

public sealed record DiscoveredController(string Name, string? State, string? Version, string? Zone);

public sealed class ControllerHealth
{
    public double? EwmaMs { get; set; }
    public int Failures { get; set; }
    public DateTimeOffset? OpenUntil { get; set; }
    public long Ok { get; set; }
    public long Failed { get; set; }
    public DateTimeOffset? LastOk { get; set; }
    public DateTimeOffset? LastFail { get; set; }
}

/// <summary>The controllers SSS talks to and how healthy each one is. The list is the configured controllers plus the discovered
/// ones (cached in the data folder, so a restart works even when the bootstrap controller is down). <see cref="Order"/> says which
/// controller to try first for the next request; the rest follow as fallbacks. A controller that keeps failing is skipped for a while.</summary>
public sealed partial class ControllerDirectory
{
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,253}$")] private static partial Regex NameRegex();

    private readonly CitrixOptions _o;
    private readonly string _cacheFile;
    private readonly TimeProvider _clock;
    private readonly ILogger? _log;
    private readonly Func<int, int> _random;
    private readonly object _gate = new();
    private readonly Dictionary<string, ControllerHealth> _health = new(StringComparer.OrdinalIgnoreCase);
    private List<ControllerInfo> _discovered = [];
    private string? _active;
    private int _round;

    public ControllerDirectory(CitrixOptions o, string dataDirectory, TimeProvider clock, ILogger? log = null, Func<int, int>? random = null)
    {
        _o = o;
        _cacheFile = Path.Combine(dataDirectory, "controllers.json");
        _clock = clock;
        _log = log;
        _random = random ?? (n => Random.Shared.Next(n));
        foreach (var c in o.Controllers) if (!NameRegex().IsMatch(c))
            throw new InvalidOperationException("Sss:Citrix:Controllers contains an invalid host name (allowed: letters, digits, '.', '_', '-').");
        if (o.Discovery.Bootstrap.Length > 0 && !NameRegex().IsMatch(o.Discovery.Bootstrap))
            throw new InvalidOperationException("Sss:Citrix:Discovery:Bootstrap is not a valid host name.");
        if (!new[] { "failover", "roundrobin", "leastlatency" }.Contains(o.Balancing.Strategy.ToLowerInvariant()))
            throw new InvalidOperationException("Sss:Citrix:Balancing:Strategy must be Failover, RoundRobin or LeastLatency.");
        if (Bootstrap is null)
            throw new InvalidOperationException("Sss:Citrix:Controllers must list at least one Delivery Controller (or set Sss:Citrix:Discovery:Bootstrap).");
        LoadCache();
    }

    public string? Bootstrap => _o.Discovery.Bootstrap.Length > 0 ? _o.Discovery.Bootstrap : _o.Controllers.FirstOrDefault();
    public string Strategy => _o.Balancing.Strategy;
    public bool DiscoveryEnabled => _o.Discovery.Enabled;

    /// <summary>Configured controllers first (in the order given), then the discovered ones.</summary>
    public IReadOnlyList<ControllerInfo> All
    {
        get
        {
            lock (_gate) return Merge();
        }
    }

    private List<ControllerInfo> Merge()
    {
        var list = _o.Controllers.Select(n => _discovered.FirstOrDefault(d => d.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } d
            ? d with { Source = "config" } : new ControllerInfo(n, "config")).ToList();
        foreach (var d in _discovered)
            if (!list.Any(x => x.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase))) list.Add(d);
        if (list.Count == 0 && Bootstrap is { } b) list.Add(new ControllerInfo(b, "bootstrap"));
        return list;
    }

    public IReadOnlyList<string> Names => All.Select(c => c.Name).ToList();

    public ControllerHealth HealthOf(string name)
    {
        lock (_gate) return Snapshot(name);
    }

    private ControllerHealth Snapshot(string name) =>
        _health.TryGetValue(name, out var h)
            ? new ControllerHealth { EwmaMs = h.EwmaMs, Failures = h.Failures, OpenUntil = h.OpenUntil, Ok = h.Ok, Failed = h.Failed, LastOk = h.LastOk, LastFail = h.LastFail }
            : new ControllerHealth();

    private ControllerHealth Get(string name) => _health.TryGetValue(name, out var h) ? h : _health[name] = new ControllerHealth();

    public void RecordSuccess(string name, double? latencyMs = null)
    {
        lock (_gate)
        {
            var h = Get(name);
            h.Ok++;
            h.Failures = 0;
            h.OpenUntil = null;
            h.LastOk = _clock.GetUtcNow();
            if (latencyMs is { } ms) h.EwmaMs = h.EwmaMs is { } e ? 0.7 * e + 0.3 * ms : ms;
            _active = name;
        }
    }

    public void RecordFailure(string name)
    {
        lock (_gate)
        {
            var h = Get(name);
            h.Failed++;
            h.Failures++;
            h.LastFail = _clock.GetUtcNow();
            if (h.Failures >= Math.Max(1, _o.Balancing.FailureThreshold)) h.OpenUntil = _clock.GetUtcNow().AddSeconds(Math.Max(1, _o.Balancing.OpenSeconds));
        }
    }

    private bool IsOpen(string name, DateTimeOffset now) => _health.TryGetValue(name, out var h) && h.OpenUntil is { } until && until > now;

    /// <summary>The controllers in the order they should be tried: first the strategy's choice among the healthy preferred ones,
    /// then the other healthy ones, and the skipped (circuit open) ones last, so a request is never refused only because all are marked bad.</summary>
    public IReadOnlyList<string> Order()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var all = Merge();
            var healthy = all.Where(c => !IsOpen(c.Name, now)).ToList();
            var skipped = all.Where(c => IsOpen(c.Name, now)).Select(c => c.Name).ToList();

            bool Preferred(ControllerInfo c) => _o.Balancing.Preferred.Length > 0 && _o.Balancing.Preferred.Any(p =>
                p.Equals(c.Name, StringComparison.OrdinalIgnoreCase) || (c.Zone is not null && p.Equals(c.Zone, StringComparison.OrdinalIgnoreCase)));
            var groups = new[] { healthy.Where(Preferred).ToList(), healthy.Where(c => !Preferred(c)).ToList() };

            var result = new List<string>();
            foreach (var g in groups) result.AddRange(Arrange(g));
            result.AddRange(skipped);
            return result;
        }
    }

    private IEnumerable<string> Arrange(List<ControllerInfo> group)
    {
        if (group.Count <= 1) return group.Select(c => c.Name);
        switch (_o.Balancing.Strategy.ToLowerInvariant())
        {
            case "roundrobin":
                var start = _round++ % group.Count;
                return group.Skip(start).Concat(group.Take(start)).Select(c => c.Name);
            case "leastlatency":
                double Cost(ControllerInfo c) => _health.TryGetValue(c.Name, out var h) && h.EwmaMs is { } e ? e : 0;   // unknown = 0: try it once
                var a = group[_random(group.Count)];
                var b = group[_random(group.Count)];
                var first = Cost(a) <= Cost(b) ? a : b;
                return new[] { first.Name }.Concat(group.Where(c => c != first).OrderBy(Cost).Select(c => c.Name));
            default:   // failover: the one that answered last first, the rest in configured order
                var active = _active is not null ? group.FirstOrDefault(c => c.Name.Equals(_active, StringComparison.OrdinalIgnoreCase)) : null;
                return active is null ? group.Select(c => c.Name) : new[] { active.Name }.Concat(group.Where(c => c != active).Select(c => c.Name));
        }
    }

    /// <summary>Takes over the answer of <c>Get-BrokerController</c>. Names outside the allowed domain suffixes are ignored.</summary>
    public (int Added, int Removed, int Rejected) SetDiscovered(IReadOnlyList<DiscoveredController> found)
    {
        var suffixes = _o.Discovery.AllowedSuffixes.Length > 0 ? _o.Discovery.AllowedSuffixes
            : Bootstrap is { } b && b.Contains('.') ? ["." + b[(b.IndexOf('.') + 1)..]] : [];
        var rejected = 0;
        var list = new List<ControllerInfo>();
        foreach (var c in found)
        {
            var name = c.Name.Trim().TrimEnd('.');
            var okName = NameRegex().IsMatch(name)
                && (suffixes.Length == 0 || suffixes.Any(s => name.EndsWith(s.StartsWith('.') ? s : "." + s, StringComparison.OrdinalIgnoreCase)))
                && (_o.Discovery.IncludeInactive || string.IsNullOrEmpty(c.State) || c.State.Equals("Active", StringComparison.OrdinalIgnoreCase));
            if (!okName) { rejected++; continue; }
            list.Add(new ControllerInfo(name, "discovered", c.State, c.Version, c.Zone, _clock.GetUtcNow()));
        }
        // an empty answer would leave SSS without controllers: keep what is known
        if (list.Count == 0) return (0, 0, rejected);

        int added, removed;
        lock (_gate)
        {
            var before = Merge().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _discovered = list;
            var after = Merge().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            added = after.Except(before, StringComparer.OrdinalIgnoreCase).Count();
            removed = before.Except(after, StringComparer.OrdinalIgnoreCase).Count();
            SaveCache();
        }
        if (added + removed > 0) _log?.LogInformation("Delivery Controllers changed: {Added} added, {Removed} removed, {Total} known.", added, removed, All.Count);
        return (added, removed, rejected);
    }

    private void LoadCache()
    {
        try
        {
            if (!_o.Discovery.Enabled || !File.Exists(_cacheFile)) return;
            _discovered = (JsonSerializer.Deserialize<List<ControllerInfo>>(File.ReadAllText(_cacheFile)) ?? [])
                .Where(c => NameRegex().IsMatch(c.Name)).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { /* the cache is only a convenience */ }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var tmp = _cacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_discovered));
            File.Move(tmp, _cacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log?.LogWarning("The controller list could not be cached in the data folder: {Error}", ex.Message); }
    }
}

/// <summary>Refreshes the controller list from the site now and then. Failures keep the last known list.</summary>
public sealed class ControllerDiscoveryService(ControllerDirectory directory, IBrokerClient broker, CitrixOptions options, ILogger<ControllerDiscoveryService> log) : BackgroundService
{
    public DateTimeOffset? LastRun { get; private set; }
    public string? LastError { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Discovery.Enabled) return;
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        while (!ct.IsCancellationRequested)
        {
            await RefreshAsync(ct);
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(options.Discovery.RefreshMinutes, 1, 1440)), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One discovery round. The bootstrap controller is asked first, then the others known so far.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var asks = new[] { directory.Bootstrap }.Concat(directory.Order()).Where(n => n is not null).Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Exception? last = null;
        foreach (var via in asks)
        {
            try
            {
                var found = await broker.DiscoverAsync(via, ct);
                var (_, _, rejected) = directory.SetDiscovered(found);
                if (rejected > 0) log.LogWarning("{Count} controller name(s) from the Broker were ignored (outside the allowed suffixes or not active).", rejected);
                LastRun = DateTimeOffset.UtcNow;
                LastError = null;
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
        }
        LastError = last?.Message;
        log.LogWarning("Controller discovery failed, the last known list stays in use: {Error}", last?.Message);
    }
}
