using System.Net;
using Sss.Web.Citrix;
using Sss.Web.Models;

namespace Sss.Web.Services;

/// <summary>Comparison of Workspace app versions such as 26.7.1.4, 26.09.0.17 or 25.11.10.50 (numeric parts, missing parts count as 0).</summary>
public static class ClientVersions
{
    public static int[]? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var parts = new List<int>();
        foreach (var p in version.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var digits = new string(p.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0 || !int.TryParse(digits, out var n)) break;
            parts.Add(n);
        }
        return parts.Count == 0 ? null : parts.ToArray();
    }

    public static int Compare(int[] a, int[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }

    /// <summary>True when the version is below the minimum for its platform ("*" is the fallback). Unknown versions or platforms without a minimum are not outdated.</summary>
    public static bool IsOutdated(string? platform, string? version, IReadOnlyDictionary<string, string> minimums)
    {
        if (minimums.Count == 0 || Parse(version) is not { } v) return false;
        string? min = null;
        foreach (var (k, m) in minimums)
            if (k.Equals(platform ?? "", StringComparison.OrdinalIgnoreCase)) { min = m; break; }
        min ??= minimums.FirstOrDefault(x => x.Key == "*").Value;
        return Parse(min) is { } mv && Compare(v, mv) < 0;
    }
}

public sealed record VersionRow(string Version, string Platform, int Devices, int Sessions, bool Outdated);
public sealed record LocationRow(string Name, int Sessions, int Devices, int Users);
/// <summary>Country and city as Citrix itself reports them for the connection (empty parts: unknown).</summary>
public sealed record CitrixLocationRow(string Country, string City, int Sessions, int Devices, int Users);
public sealed record EnvironmentView(DateTimeOffset At, int Sessions, int Devices, int Users, int Outdated, int WithoutVersion,
    bool VersionAvailable, bool LocationsConfigured, IReadOnlyList<VersionRow> Versions, IReadOnlyList<LocationRow> Locations, bool Demo,
    IReadOnlyList<CitrixLocationRow>? CitrixLocations = null, IReadOnlyList<AppUsage>? Apps = null, IReadOnlyList<MachineGroup>? Machines = null,
    IReadOnlyList<MachineInfo>? MachineList = null, IReadOnlyList<IReadOnlyList<DetailItem>>? Lhc = null);

/// <summary>Aggregates the live sessions of the whole site by Workspace app version and platform and by location (networks you assigned to a
/// name). Only counts are produced, nothing per person. The result is cached for a minute because it reads every live session.</summary>
public sealed class EnvironmentService(SssOptions options, IMonitorClient? monitor, TimeProvider clock, bool demo = false)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, EnvironmentView View)? _cache;

    private bool Demo => demo;

    public async Task<EnvironmentView> GetAsync(CancellationToken ct)
    {
        if (_cache is { } c && clock.GetUtcNow() - c.At < CacheFor) return c.View;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is { } c2 && clock.GetUtcNow() - c2.At < CacheFor) return c2.View;
            var sessions = demo ? DemoSessions() : monitor is null ? [] : await monitor.GetAllLiveAsync(ct);
            var view = Build(sessions, Demo ? DemoLocations() : options.Environment.Locations);
            IReadOnlyList<AppUsage> apps = demo ? DemoApps() : monitor is null ? [] : await monitor.GetAppUsageAsync(7, 15, ct);
            IReadOnlyList<MachineInfo> machines = demo ? DemoMachines() : monitor is null ? [] : await monitor.GetMachinesAsync(ct);
            IReadOnlyList<IReadOnlyList<DetailItem>> lhc = demo ? [] : monitor is null ? [] : await monitor.GetLhcAsync(ct);
            view = view with { Apps = apps, Machines = GroupMachines(machines), MachineList = machines, Lhc = lhc };
            _cache = (clock.GetUtcNow(), view);
            return view;
        }
        finally { _gate.Release(); }
    }

    public EnvironmentView Build(IReadOnlyList<MonitorSession> sessions, LocationOptions[] locations)
    {
        var minimums = demo && options.Environment.MinClientVersion.Count == 0 ? new Dictionary<string, string> { ["Windows"] = "26.3.0" } : options.Environment.MinClientVersion;   // the demo shows what the check looks like
        string Device(MonitorSession s) => (s.ClientName ?? s.ClientAddress ?? s.SessionKey).ToLowerInvariant();
        string User(MonitorSession s) => s.UserId?.ToString() ?? s.SessionKey;

        var versions = sessions.Where(s => !string.IsNullOrWhiteSpace(s.ClientVersion))
            .GroupBy(s => (Version: s.ClientVersion!.Trim(), Platform: string.IsNullOrWhiteSpace(s.ClientPlatform) ? "?" : s.ClientPlatform!.Trim()))
            .Select(g => new VersionRow(g.Key.Version, g.Key.Platform, g.Select(Device).Distinct().Count(), g.Count(), ClientVersions.IsOutdated(g.Key.Platform, g.Key.Version, minimums)))
            .OrderByDescending(v => v.Devices).ThenBy(v => v.Version, StringComparer.Ordinal).ToList();
        // sessions for which Citrix reports no version are listed too (empty version = "unknown"), last
        if (versions.Count > 0) versions.AddRange(sessions.Where(s => string.IsNullOrWhiteSpace(s.ClientVersion))
            .GroupBy(s => string.IsNullOrWhiteSpace(s.ClientPlatform) ? "?" : s.ClientPlatform!.Trim())
            .Select(g => new VersionRow("", g.Key, g.Select(Device).Distinct().Count(), g.Count(), false)).OrderByDescending(v => v.Devices));

        var nets = locations.Where(l => l.Name.Length > 0)
            .SelectMany(l => l.Networks.Select(n => (l.Name, Net: ParseNetwork(n)))).Where(x => x.Net is not null)
            .Select(x => (x.Name, Net: x.Net!.Value)).OrderByDescending(x => x.Net.PrefixLength).ToList();
        string Location(MonitorSession s)
        {
            if (IPAddress.TryParse(s.ClientAddress, out var ip))
            {
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                foreach (var (name, net) in nets) if (net.Contains(ip)) return name;
            }
            return "";   // not assigned
        }
        var locs = nets.Count == 0 ? [] : sessions.GroupBy(Location)
            .Select(g => new LocationRow(g.Key, g.Count(), g.Select(Device).Distinct().Count(), g.Select(User).Distinct().Count()))
            .OrderBy(l => l.Name.Length == 0).ThenByDescending(l => l.Sessions).ToList();

        // Citrix reports no location for every connection (internal addresses, older clients): those form one "unknown" row, so the rows add up to all sessions.
        var anyCitrix = sessions.Any(x => !string.IsNullOrWhiteSpace(x.ClientCountry) || !string.IsNullOrWhiteSpace(x.ClientCity));
        var citrixLocs = !anyCitrix ? [] : sessions.GroupBy(x => (Country: (x.ClientCountry ?? "").Trim(), City: (x.ClientCity ?? "").Trim()))
            .Select(g => new CitrixLocationRow(g.Key.Country, g.Key.City, g.Count(), g.Select(Device).Distinct().Count(), g.Select(User).Distinct().Count()))
            .OrderBy(l => l.Country.Length == 0 && l.City.Length == 0).ThenByDescending(l => l.Sessions).Take(31).ToList();

        return new EnvironmentView(clock.GetUtcNow(), sessions.Count, sessions.Select(Device).Distinct().Count(), sessions.Select(User).Distinct().Count(),
            versions.Where(v => v.Outdated).Sum(v => v.Sessions), sessions.Count(s => string.IsNullOrWhiteSpace(s.ClientVersion)),
            sessions.Any(s => !string.IsNullOrWhiteSpace(s.ClientVersion)), nets.Count > 0, versions, locs, Demo, citrixLocs);
    }

    /// <summary>"10.20.0.0/16" or a single address.</summary>
    public static IPNetwork? ParseNetwork(string text)
    {
        text = text.Trim();
        if (text.Contains('/')) return IPNetwork.TryParse(text, out var n) ? n : null;
        return IPAddress.TryParse(text, out var a) ? new IPNetwork(a, a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32) : null;
    }

    /// <summary>Counts the machines by agent version and operating system; a registration state other than 1 counts as not registered.</summary>
    public static IReadOnlyList<MachineGroup> GroupMachines(IReadOnlyList<MachineInfo> machines) =>
        // machines that never reported an agent version (never registered, switched off) are summed per delivery group instead of by version
        machines.GroupBy(m => m.AgentVersion == "?" ? ("", "", m.Group) : (m.AgentVersion, m.Os, ""))
            .Select(g => new MachineGroup(g.Key.Item1, g.Key.Item2, g.Count(), g.Count(m => m.Maintenance), g.Count(m => m.Registration is { } r && r != 1), g.Count(m => m.Power == 2), g.Key.Item3))
            .OrderBy(x => x.AgentVersion.Length == 0).ThenByDescending(x => x.Machines).ThenBy(x => x.AgentVersion, StringComparer.Ordinal).ToList();

    private static IReadOnlyList<MachineInfo> DemoMachines()
    {
        var now = DateTimeOffset.UtcNow;
        MachineInfo M(string n, string g, string v, string os, int reg, bool maint, int sessions) => new($"{n}.demo.local", g, v, os, reg, 3, maint, sessions, now.AddHours(-9), "0");
        return [M("vdi-01", "DG-Desktops", "2507.0.0.1", "Windows 11", 1, false, 6), M("vdi-02", "DG-Desktops", "2507.0.0.1", "Windows 11", 1, false, 4), M("vdi-03", "DG-Desktops", "2503.0.100.339", "Windows 11", 1, true, 0),
            M("rds-01", "DG-Apps", "2402.0.0.8", "Windows Server 2022", 1, false, 12), M("rds-02", "DG-Apps", "2402.0.0.8", "Windows Server 2022", 0, false, 0)];
    }

    private static IReadOnlyList<AppUsage> DemoApps() =>
    [
        new("MsEdg", 412, 38, 52, 14, "msedge"), new("MsOtlk", 389, 41, 47, 14, "OUTLOOK"), new("MsExcl", 301, 29, 38, 13, "EXCEL"), new("MsTms", 288, 33, 36, 14, "ms-teams"),
        new("Adobe Acrobat Reader", 174, 12, 21, 11), new("Notepad++", 96, 6, 9, 7), new("7-Zip", 41, 3, 6, 5),
    ];

    // ---- demo data (the built-in demo mode has no Citrix site) --------------------------------------------------------------------------------------------
    private static LocationOptions[] DemoLocations() =>
    [
        new() { Name = "Berlin", Networks = ["10.20.0.0/16"] },
        new() { Name = "Munich", Networks = ["10.30.0.0/16"] },
        new() { Name = "Home office (VPN)", Networks = ["10.99.0.0/16"] },
    ];

    private static IReadOnlyList<MonitorSession> DemoSessions()
    {
        var rnd = new Random(42);
        (string Version, string Platform, int Count)[] mix =
        [
            ("26.7.1.4", "Windows", 22), ("26.3.20.31", "Windows", 20), ("26.10.0.31", "Windows", 14), ("26.7.0.269", "Windows", 6), ("26.3.11.10", "Windows", 3),
            ("26.9.0", "iOS", 3), ("26.09.0.17", "Mac", 2), ("25.11.10.50", "Windows", 2),
        ];
        string[] nets = ["10.20", "10.30", "10.99", "172.16"];
        var list = new List<MonitorSession>();
        var n = 0;
        foreach (var (version, platform, count) in mix)
            for (var i = 0; i < count; i++, n++)
                list.Add(new MonitorSession($"demo-{n}", "vdi-01.demo.local", "DG-Desktops", true, null, null, null, $"DEMO-PC-{n:000}",
                    $"{nets[rnd.Next(nets.Length)]}.{rnd.Next(1, 250)}.{rnd.Next(1, 250)}", null, ClientVersion: version, ClientPlatform: platform, UserId: 1000 + n % 60,
                    ClientCountry: n % 7 == 0 ? null : "Germany", ClientCity: n % 7 == 0 ? null : n % 3 == 0 ? "Munich" : "Berlin"));
        return list;
    }
}
