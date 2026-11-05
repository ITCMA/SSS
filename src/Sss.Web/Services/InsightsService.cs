using System.Globalization;
using Sss.Web.Citrix;
using Sss.Web.Models;
using Sss.Web.Providers;

namespace Sss.Web.Services;

/// <summary>One thing the helpdesk should look at. Severity is "crit", "warn" or "info"; the user (when there is one) lets the interface open the user's sessions.</summary>
/// <summary>One live session on the world map: who, from which device, where Citrix locates the client and where the session runs.</summary>
public sealed record MapPoint(string User, string? Sid, string? Device, string Country, string City, string? Machine, string? Group, string? Platform, bool Desktop);
/// <summary>The live sessions for the map. NoUserId = sessions for which the Monitor names no user; Unresolved = sessions whose user id the user table did not return.</summary>
public sealed record MapView(DateTimeOffset At, IReadOnlyList<MapPoint> Points, bool Demo, int NoUserId = 0, int Unresolved = 0);

/// <summary>Average and peak load of a machine over the last 7 days, and how it rates against the load limits.</summary>
public sealed record UsageRow(string Machine, string Group, double Cpu, double PeakCpu, double Memory, double PeakMemory, string Health);
/// <summary>How long an application takes to start: average and 90th percentile, and the machine where it starts slowest (when it differs).</summary>
public sealed record AppStartRow(string Name, int Launches, int AvgMs, int P90Ms, string? SlowMachine, int SlowMachineMs);
/// <summary>Cost and savings of a delivery group over the last 30 days (in the currency the Monitor reports).</summary>
public sealed record CostRow(string Group, string Currency, double Compute, double Storage, double Savings);
/// <summary>One thing that changed: a machine registered or lost its registration, was switched on, or an administrative action (audit).</summary>
public sealed record TimelineEvent(DateTimeOffset At, string Kind, string Subject, string? Detail = null, string? Actor = null);
public sealed record MoreView(DateTimeOffset At, IReadOnlyList<UsageRow> Usage, IReadOnlyList<AppStartRow> AppStarts, IReadOnlyList<CostRow> Costs, IReadOnlyList<TimelineEvent> Timeline, bool Demo, int AppLaunchesRead = 0, int UsageSamples = 0, IReadOnlyList<HourCount>? Hourly = null, IReadOnlyList<LoadStrip>? Strips = null, HeatView? Heat = null);
/// <summary>The load of a machine hour by hour over the last 24 hours (average CPU in percent; null = no sample).</summary>
public sealed record LoadStrip(string Machine, string Group, IReadOnlyList<double?> Cpu);
/// <summary>Everything about one machine on one page: state, load, the last 24 hours, crashes and failures there, and what changed.</summary>
public sealed record MachineDetail(MachineHealthRow? Row, MachineInfo? Info, UsageRow? Usage, LoadStrip? Strip, IReadOnlyList<RecordRow> Faults, IReadOnlyList<RecordRow> Failures, IReadOnlyList<TimelineEvent> Events, IReadOnlyList<MachineOutage>? Outages = null);

/// <summary>Logons per hour over the last 7 days: 168 values, the first one for the hour starting at Start (UTC). The browser sorts them into weekday and hour of its own time zone.</summary>
public sealed record HeatView(DateTimeOffset Start, IReadOnlyList<int> Logons, IReadOnlyList<int?> AvgSeconds);

/// <summary>Logons that started in one hour, and their average logon time.</summary>
public sealed record HourCount(DateTimeOffset At, int Logons, int? AvgSeconds);

public sealed record InsightFinding(string Severity, string Kind, string Subject, string Detail, string? UserName = null, string? UserSid = null,
    string? Machine = null, string? Group = null, int? Value = null);

/// <summary>Logon durations of the sessions started in a delivery group over the last days.</summary>
public sealed record GroupLogon(string Group, int Sessions, int AvgSeconds, int MedianSeconds, int P90Seconds, IReadOnlyList<int?>? Daily = null);

/// <summary>Crashes of one application or failures with one reason, counted over the last 24 hours.</summary>
public sealed record CountRow(string Name, int Count, int Machines, string? TopMachine);

/// <summary>Crashes of one application or failures with one reason: how many, how many users and machines, when, and what the pattern suggests
/// (Hint: few, oneMachine, oneUser, oneBoth, widespread).</summary>
public sealed record RecordSummary(string Name, int Count, int Users, int Machines, string? TopMachine, string? TopUser, DateTimeOffset? First, DateTimeOffset? Last, string Hint, IReadOnlyList<string> Versions);

/// <summary>One crash or failure with the user, machine and delivery group it happened to and everything else the Monitor knows about it.</summary>
public sealed record RecordRow(DateTimeOffset? At, string Name, string? User, string? UserSid, string? Machine, string? Group, string? Client, IReadOnlyList<DetailItem> Items);

public sealed record RecordsView(DateTimeOffset At, int Hours, string Kind, IReadOnlyList<RecordSummary> Summary, IReadOnlyList<RecordRow> Rows, bool Demo, int Machines = 0);

/// <summary>A disconnected session: who, what, where and since when.</summary>
public sealed record DisconnectedRow(string User, string UserSid, string Application, string Machine, string Group, DateTimeOffset? Since, int Hours);

public sealed record MachineHealthRow(string Name, string Group, int? Registration, int? Power, bool Maintenance, int? Sessions, int? LoadIndex, string Health, int? UptimeDays = null, int? Score = null);

public sealed record InsightsView(DateTimeOffset At, int LiveSessions, IReadOnlyList<InsightFinding> Findings, IReadOnlyList<GroupLogon> Groups,
    IReadOnlyList<MachineHealthRow> Machines, bool Demo, IReadOnlyList<DisconnectedRow>? Disconnected = null, int ActiveSessions = 0, int DisconnectedSessions = 0);

/// <summary>How one user's last days looked: number of sessions, logon time (against the delivery group), failures and automatic reconnects.
/// Verdict is "ok", "attention" or "poor".</summary>
public sealed record UserSummary(int Days, int Sessions, int? AvgLogonSeconds, int? MedianLogonSeconds, string? Group, int? GroupAvgSeconds, int Failures, int Reconnects, string Verdict, int? Score = null);

/// <summary>The helpdesk's "what is conspicuous right now": slow connections and logons, machines that are not registered or busy, the logon time of each
/// delivery group, and a short history of one user. Everything comes from the Citrix Monitor; the farm-wide result is cached for a minute.</summary>
public sealed class InsightsService(SssOptions options, IMonitorClient? monitor, IHelpdeskProvider? helpdesk, TimeProvider clock, bool demo = false)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60), GroupCacheFor = TimeSpan.FromMinutes(10);
    private const int MaxPerKind = 8;
    /// <summary>A disconnected session that has been so for this long is listed (it still holds a licence and memory).</summary>
    private const int DisconnectedHours = 4;
    /// <summary>A registered machine that has run this long without a restart is listed.</summary>
    private const int UptimeDaysLimit = 14;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, InsightsView View)? _cache;
    private (DateTimeOffset At, IReadOnlyList<GroupLogon> Groups)? _groups;

    private QualityThresholds Q => options.Quality;

    public async Task<InsightsView> GetAsync(CancellationToken ct)
    {
        if (_cache is { } c && clock.GetUtcNow() - c.At < CacheFor) return c.View;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is { } c2 && clock.GetUtcNow() - c2.At < CacheFor) return c2.View;
            var view = demo ? Demo() : monitor is null ? new InsightsView(clock.GetUtcNow(), 0, [], [], [], false, []) : await BuildAsync(monitor, ct);
            _cache = (clock.GetUtcNow(), view);
            return view;
        }
        finally { _gate.Release(); }
    }

    private (DateTimeOffset At, MapView View)? _map;

    /// <summary>All live sessions of the site with the location Citrix reports for the client, for the world map (cached for a minute). The coordinates are
    /// looked up in the browser from a bundled place list, so no address leaves the server.</summary>
    public async Task<MapView> MapAsync(CancellationToken ct)
    {
        if (_map is { } c && clock.GetUtcNow() - c.At < CacheFor) return c.View;
        await _gate.WaitAsync(ct);
        try
        {
            if (_map is { } c2 && clock.GetUtcNow() - c2.At < CacheFor) return c2.View;
            var now = clock.GetUtcNow();
            MapView view;
            if (demo) view = new MapView(now, DemoMapPoints(), true);
            else if (monitor is null) view = new MapView(now, [], false);
            else
            {
                var live = await monitor.GetFarmLiveAsync(ct);
                var users = await monitor.GetUsersByIdAsync(live.Select(x => x.UserId).OfType<long>().Distinct(), ct);
                // the Broker names the user of every session it knows (also when the Monitor's user table does not): matched by the session key
                Dictionary<string, (string Name, string Sid)> byKey = new(StringComparer.OrdinalIgnoreCase);
                if (helpdesk is not null)
                {
                    try
                    {
                        foreach (var h in (await helpdesk.ListLiveAsync(null, 3000, ct)).Sessions)
                            if (h.Session.SessionKey is { Length: > 0 } key && !string.IsNullOrWhiteSpace(h.UserName)) byKey[key] = (h.UserName, h.UserSid);
                    }
                    catch (ProviderUnavailableException) { /* the Broker is optional here */ }
                }
                var points = MapPoints(live, users, byKey);
                view = new MapView(now, points, false, live.Count(x => x.UserId is null), live.Count(x => x.UserId is { } id && !users.ContainsKey(id)));
            }
            _map = (now, view);
            return view;
        }
        finally { _gate.Release(); }
    }

    /// <summary>One point per live session (at most 3000), the sessions of one user next to each other.</summary>
    public static IReadOnlyList<MapPoint> MapPoints(IReadOnlyList<MonitorSession> live, IReadOnlyDictionary<long, MonitorUser> users, IReadOnlyDictionary<string, (string Name, string Sid)>? byKey = null) => live
        .Select(s => (S: s, U: s.UserId is { } id && users.TryGetValue(id, out var u) ? (Name: u.Name, Sid: (string?)u.Sid) : byKey is not null && byKey.TryGetValue(s.SessionKey, out var b) ? (Name: b.Name, Sid: (string?)b.Sid) : (Name: "", Sid: (string?)null)))
        .Select(x => new MapPoint(x.U.Name, x.U.Sid,
            x.S.ClientName, (x.S.ClientCountry ?? "").Trim(), (x.S.ClientCity ?? "").Trim(), x.S.Machine, x.S.DesktopGroup, x.S.ClientPlatform, x.S.IsDesktop))
        .OrderBy(p => p.User, StringComparer.OrdinalIgnoreCase).Take(3000).ToList();

    private static IReadOnlyList<MapPoint> DemoMapPoints()
    {
        (string Country, string City, int Count)[] spots =
        [
            ("Germany", "Berlin", 9), ("Germany", "Munich", 6), ("Germany", "Hamburg", 4), ("Germany", "Frankfurt", 5), ("United Kingdom", "London", 5), ("France", "Paris", 3),
            ("Poland", "Warsaw", 3), ("Spain", "Madrid", 2), ("United States", "New York", 4), ("United States", "Chicago", 2), ("United States", "San Francisco", 2),
            ("Brazil", "Sao Paulo", 2), ("India", "Mumbai", 3), ("India", "Bangalore", 2), ("China", "Shanghai", 2), ("Japan", "Tokyo", 2), ("Singapore", "Singapore", 2),
            ("Australia", "Sydney", 2), ("South Africa", "Johannesburg", 1), ("United Arab Emirates", "Dubai", 1), ("Canada", "Toronto", 1), ("Mexico", "", 1),
            ("Switzerland", "Zurich", 4), ("Switzerland", "Basel", 2), ("Netherlands", "Amsterdam", 4), ("United Kingdom", "Manchester", 2), ("France", "Lyon", 2), ("Austria", "Vienna", 2),
        ];
        string[] first = ["Alex", "Sam", "Robin", "Kim", "Jo", "Max", "Lee", "Chris", "Pat", "Mika", "Dana", "Eli"], last = ["Example", "Sample", "Demo", "Test", "Muster", "Probe"];
        var list = new List<MapPoint>();
        var n = 0;
        foreach (var (country, city, count) in spots)
            for (var i = 0; i < count; i++, n++)
                list.Add(new MapPoint($"{first[n % first.Length]} {last[n / first.Length % last.Length]}", $"S-1-5-21-0-0-0-{1000 + n}", $"DEMO-PC-{n:000}", country, city,
                    $"vdi-{1 + n % 4:00}.demo.local", n % 3 == 0 ? "DG-Apps" : "DG-Desktops", n % 9 == 0 ? "Mac" : "Windows", n % 3 != 0));
        return list.OrderBy(p => p.User, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private (DateTimeOffset At, MoreView View)? _more;
    private static readonly TimeSpan MoreCacheFor = TimeSpan.FromMinutes(10);

    /// <summary>Machine utilisation, application start times and costs of the last days, and the machine events of the last 7 days (cached for 10 minutes).</summary>
    public async Task<MoreView> MoreAsync(CancellationToken ct)
    {
        if (_more is { } c && clock.GetUtcNow() - c.At < MoreCacheFor) return c.View;
        await _gate.WaitAsync(ct);
        try
        {
            if (_more is { } c2 && clock.GetUtcNow() - c2.At < MoreCacheFor) return c2.View;
            var now = clock.GetUtcNow();
            MoreView view;
            if (demo) view = DemoMore(now);
            else if (monitor is null) view = new MoreView(now, [], [], [], [], false);
            else
            {
                var machines = await monitor.GetMachinesAsync(ct);
                var byId = machines.Where(x => x.Id is not null).ToDictionary(x => x.Id!, StringComparer.OrdinalIgnoreCase);
                var usage = await monitor.GetUsageAsync(now.AddDays(-7), ct);
                var launches = await monitor.GetAppLaunchesAsync(now.AddDays(-7), ct);
                var recent = await RecentAsync(monitor, ct);
                view = new MoreView(now, Usage(usage, byId), AppStarts(launches, byId), Costs(await monitor.GetCostsAsync(now.AddDays(-30), ct), byId), MachineEvents(machines, now), false, launches.Count, usage.Count, Hourly(recent, now),
                    Strips(await monitor.GetHourlyUsageAsync(now.AddHours(-24), ct), byId, now), Heat(recent, now));
            }
            _more = (now, view);
            return view;
        }
        finally { _gate.Release(); }
    }

    /// <summary>One row per machine with 24 hourly CPU averages (the hour of <paramref name="now"/> is the last), the busiest machines first.</summary>
    public static IReadOnlyList<LoadStrip> Strips(IReadOnlyList<UsageSample> samples, IReadOnlyDictionary<string, MachineInfo> machines, DateTimeOffset now)
    {
        var top = new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, now.UtcDateTime.Day, now.UtcDateTime.Hour, 0, 0, TimeSpan.Zero).AddHours(-23);
        return samples.Where(x => x.At is not null && x.Cpu is not null && machines.ContainsKey(x.MachineId))
            .GroupBy(x => x.MachineId, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var cpu = new double?[24];
                foreach (var h in g.GroupBy(x => (int)Math.Floor((x.At!.Value - top).TotalHours)).Where(h => h.Key is >= 0 and < 24)) cpu[h.Key] = Math.Round(h.Average(x => x.Cpu!.Value), 1);
                var m = machines[g.Key];
                return new LoadStrip(m.Name, m.Group, cpu);
            })
            .OrderByDescending(r => r.Cpu.Max() ?? 0).Take(60).ToList();
    }

    /// <summary>Everything about one machine (the name may be given without the domain), or null when the Monitor does not know it.</summary>
    public async Task<MachineDetail?> MachineAsync(string name, CancellationToken ct)
    {
        static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.Split('.')[0].Equals(b.Split('.')[0], StringComparison.OrdinalIgnoreCase);
        var view = await GetAsync(ct);
        var row = view.Machines.FirstOrDefault(m => Same(m.Name, name));
        if (row is null) return null;
        var more = await MoreAsync(ct);
        var faults = (await RecordsAsync("faults", 24, ct)).Rows.Where(r => r.Machine is not null && Same(r.Machine, row.Name)).ToList();
        var failures = (await RecordsAsync("failures", 24, ct)).Rows.Where(r => r.Machine is not null && Same(r.Machine, row.Name)).ToList();
        MachineInfo? info = demo ? null : monitor is null ? null : (await monitor.GetMachinesAsync(ct)).FirstOrDefault(m => Same(m.Name, row.Name));
        var now = clock.GetUtcNow();
        // the failure log of the Monitor holds the history: when the machine had lost its registration in the last 7 days (nothing of our own is stored)
        IReadOnlyList<MachineOutage> outages = demo ? DemoOutages(row.Name, now)
            : monitor is null || info?.Id is null ? []
            : (await monitor.GetMachineOutagesAsync(now.AddDays(-7), ct)).Where(o => o.MachineId.Equals(info.Id, StringComparison.OrdinalIgnoreCase)).OrderBy(o => o.Start).ToList();
        return new MachineDetail(row, info, more.Usage.FirstOrDefault(u => Same(u.Machine, row.Name)), more.Strips?.FirstOrDefault(u => Same(u.Machine, row.Name)),
            faults, failures, more.Timeline.Where(e => Same(e.Subject, row.Name)).ToList(), outages);
    }

    private static IReadOnlyList<MachineOutage> DemoOutages(string name, DateTimeOffset now) => name.Split('.')[0] switch
    {
        "rds-02" => [new("demo", now.AddDays(-2.4), now.AddDays(-2.4).AddMinutes(25), 1), new("demo", now.AddMinutes(-35), null, 1)],
        "vdi-02" => [new("demo", now.AddHours(-3.5), now.AddHours(-3), 1), new("demo", now.AddDays(-5), now.AddDays(-5).AddHours(2), 1)],
        _ => [],
    };

    /// <summary>Logons per hour over the last 24 hours (the current hour included), with the average logon time of that hour.</summary>
    public static IReadOnlyList<HourCount> Hourly(IReadOnlyList<MonitorSession> sessions, DateTimeOffset now)
    {
        var top = new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, now.UtcDateTime.Day, now.UtcDateTime.Hour, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, 24).Select(i => top.AddHours(i - 23)).Select(h =>
        {
            var inHour = sessions.Where(s => s.Start is { } st && st >= h && st < h.AddHours(1)).ToList();
            var logons = inHour.Where(s => s.LogonSeconds is > 0).Select(s => s.LogonSeconds!.Value).ToList();
            return new HourCount(h, inHour.Count, logons.Count == 0 ? null : (int)Math.Round(logons.Average()));
        }).ToList();
    }

    /// <summary>The logons of the last 7 days hour by hour (168 values) with the average logon time of each hour.</summary>
    public static HeatView Heat(IReadOnlyList<MonitorSession> sessions, DateTimeOffset now)
    {
        var top = new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, now.UtcDateTime.Day, now.UtcDateTime.Hour, 0, 0, TimeSpan.Zero);
        var start = top.AddHours(-167);
        var counts = new int[168]; var sums = new long[168]; var n = new int[168];
        foreach (var s in sessions)
        {
            if (s.Start is not { } st) continue;
            var i = (int)Math.Floor((st - start).TotalHours);
            if (i is < 0 or >= 168) continue;
            counts[i]++;
            if (s.LogonSeconds is > 0) { sums[i] += s.LogonSeconds.Value; n[i]++; }
        }
        return new HeatView(start, counts, Enumerable.Range(0, 168).Select(i => n[i] == 0 ? (int?)null : (int)Math.Round((double)sums[i] / n[i])).ToList());
    }

    /// <summary>The granularity that has most rows is used, so rows of several granularities are not counted twice.</summary>
    private static IEnumerable<T> Dominant<T>(IReadOnlyList<T> rows, Func<T, int?> granularity) =>
        rows.GroupBy(granularity).OrderByDescending(g => g.Count()).FirstOrDefault() is { } g ? g : [];

    public IReadOnlyList<UsageRow> Usage(IReadOnlyList<UsageSample> samples, IReadOnlyDictionary<string, MachineInfo> machines) => Dominant(samples, x => x.Granularity)
        .GroupBy(x => x.MachineId, StringComparer.OrdinalIgnoreCase)
        .Where(g => machines.ContainsKey(g.Key))
        .Select(g =>
        {
            var m = machines[g.Key];
            double Avg(Func<UsageSample, double?> f) => g.Select(f).OfType<double>().DefaultIfEmpty(0).Average();
            double Max(Func<UsageSample, double?> f) => g.Select(f).OfType<double>().DefaultIfEmpty(0).Max();
            var cpu = Avg(x => x.Cpu);
            var health = cpu >= 85 || Max(x => x.PeakMemory) >= 95 ? "crit" : cpu >= 65 || Avg(x => x.Memory) >= 85 ? "warn" : "good";
            return new UsageRow(m.Name, m.Group, Math.Round(cpu, 1), Math.Round(Max(x => x.PeakCpu), 1), Math.Round(Avg(x => x.Memory), 1), Math.Round(Max(x => x.PeakMemory), 1), health);
        })
        .OrderBy(r => r.Health switch { "crit" => 0, "warn" => 1, _ => 2 }).ThenByDescending(r => r.Cpu).Take(100).ToList();

    public static IReadOnlyList<AppStartRow> AppStarts(IReadOnlyList<AppLaunch> launches, IReadOnlyDictionary<string, MachineInfo> machines) => launches
        .GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 3)
        .Select(g =>
        {
            var ms = g.Select(l => l.StartupMs).Order().ToArray();
            var avg = (int)Math.Round(ms.Average());
            var slow = g.Where(l => l.MachineId is not null).GroupBy(l => l.MachineId!, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() >= 3)
                .Select(x => (Id: x.Key, Avg: (int)Math.Round(x.Average(l => l.StartupMs)))).OrderByDescending(x => x.Avg).FirstOrDefault();
            var slowName = slow.Id is not null && slow.Avg >= avg * 1.5 && machines.TryGetValue(slow.Id, out var mi) ? mi.Name : null;
            return new AppStartRow(g.Key, ms.Length, avg, Percentile(ms, 0.9), slowName, slowName is null ? 0 : slow.Avg);
        })
        .OrderByDescending(r => r.AvgMs).Take(15).ToList();

    public static IReadOnlyList<CostRow> Costs(IReadOnlyList<CostSample> samples, IReadOnlyDictionary<string, MachineInfo> machines) => Dominant(samples, x => x.Granularity)
        .GroupBy(x => (Group: machines.TryGetValue(x.MachineId, out var m) && m.Group.Length > 0 ? m.Group : "?", x.Currency))
        .Select(g => new CostRow(g.Key.Group, g.Key.Currency, Math.Round(g.Sum(x => x.Compute), 2), Math.Round(g.Sum(x => x.Storage), 2), Math.Round(g.Sum(x => x.Savings), 2)))
        .OrderByDescending(r => r.Compute + r.Storage).Take(30).ToList();

    /// <summary>What changed on the machines in the last 7 days: registered, lost the registration, switched on.</summary>
    public static IReadOnlyList<TimelineEvent> MachineEvents(IReadOnlyList<MachineInfo> machines, DateTimeOffset now) => machines
        .SelectMany(m => new[]
        {
            m.RegistrationChanged is { } r && r > now.AddDays(-7) && m.Registration is not null
                ? new TimelineEvent(r, m.Registration == 1 ? "registered" : "unregistered", m.Name, m.Group) : null,
            m.PoweredOn is { } p && p > now.AddDays(-7) && m.Power == 3 ? new TimelineEvent(p, "poweredOn", m.Name, m.Group) : null,
        })
        .OfType<TimelineEvent>().OrderByDescending(e => e.At).Take(100).ToList();

    private MoreView DemoMore(DateTimeOffset now) => new(now,
        [new("rds-01.demo.local", "DG-Apps", 71.4, 96.0, 83.2, 94.1, "warn"), new("rds-02.demo.local", "DG-Apps", 52.0, 88.5, 64.0, 80.3, "good"), new("vdi-01.demo.local", "DG-Desktops", 34.2, 72.0, 48.7, 66.0, "good"),
         new("vdi-02.demo.local", "DG-Desktops", 29.8, 65.4, 44.1, 61.5, "good")],
        [new("sapgui.exe", 412, 6200, 11800, "rds-01.demo.local", 9400), new("outlook.exe", 389, 4100, 7300, null, 0), new("excel.exe", 301, 2300, 4100, null, 0), new("chrome.exe", 288, 1500, 2600, null, 0)],
        [new("DG-Desktops", "EUR", 1840.50, 312.20, 420.00), new("DG-Apps", "EUR", 960.00, 150.75, 88.40)],
        [new(now.AddMinutes(-35), "unregistered", "rds-02.demo.local", "DG-Apps"), new(now.AddHours(-3), "registered", "vdi-02.demo.local", "DG-Desktops"),
         new(now.AddHours(-3.2), "poweredOn", "vdi-02.demo.local", "DG-Desktops"), new(now.AddDays(-1), "registered", "rds-01.demo.local", "DG-Apps")], true, 0, 0,
        Enumerable.Range(0, 24).Select(i => new HourCount(now.AddHours(i - 23), new[] { 1, 0, 0, 0, 1, 2, 4, 9, 22, 31, 18, 12, 14, 19, 11, 8, 6, 4, 3, 2, 1, 1, 1, 0 }[(i + now.Hour + 1) % 24], 25 + (i * 7 % 30))).ToList(),
        [DemoStrip("rds-01.demo.local", "DG-Apps", 70, 3), DemoStrip("rds-02.demo.local", "DG-Apps", 45, 5), DemoStrip("vdi-01.demo.local", "DG-Desktops", 30, 2), DemoStrip("vdi-02.demo.local", "DG-Desktops", 25, 7)],
        DemoHeat(now));

    /// <summary>A working week: busy at 7 to 9 and a little after lunch, quiet at the weekend.</summary>
    private static HeatView DemoHeat(DateTimeOffset now)
    {
        var top = new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, now.UtcDateTime.Day, now.UtcDateTime.Hour, 0, 0, TimeSpan.Zero);
        var start = top.AddHours(-167);
        var logons = new int[168]; var avg = new int?[168];
        for (var i = 0; i < 168; i++)
        {
            var t = start.AddHours(i).ToLocalTime();
            var h = t.Hour; var weekend = t.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var v = weekend ? 2.5 * Math.Exp(-Math.Pow((h - 11) / 3.0, 2)) : 28 * Math.Exp(-Math.Pow((h - 8.3) / 1.4, 2)) + 13 * Math.Exp(-Math.Pow((h - 13) / 1.3, 2)) + 4 * Math.Exp(-Math.Pow((h - 17) / 2.0, 2));
            logons[i] = (int)Math.Round(v + (i * 7 % 3)); avg[i] = logons[i] == 0 ? null : 24 + (h == 8 ? 12 : 0) + (i * 5 % 9);
        }
        return new HeatView(start, logons, avg);
    }

    private static LoadStrip DemoStrip(string name, string group, int peak, int shift) => new(name, group,
        Enumerable.Range(0, 24).Select(i => name.StartsWith("rds-02") && i is > 18 and < 22 ? (double?)null : Math.Round(Math.Max(2, peak * Math.Exp(-Math.Pow((i - 10 - shift % 3) / 4.5, 2))), 1)).ToList());

    public static readonly int[] Periods = [1, 3, 6, 12, 24];
    private readonly Dictionary<(string, int), (DateTimeOffset At, RecordsView View)> _records = [];

    /// <summary>Application crashes ("faults") or connection failures of the last 1 to 24 hours, counted by application or reason, with the single records
    /// (who, when, where) for the detail view.</summary>
    public async Task<RecordsView> RecordsAsync(string kind, int hours, CancellationToken ct)
    {
        if (kind is not ("faults" or "failures") || !Periods.Contains(hours)) throw new ArgumentException("Unknown kind or period.");
        var now = clock.GetUtcNow();
        if (_records.TryGetValue((kind, hours), out var c) && now - c.At < CacheFor) return c.View;
        await _gate.WaitAsync(ct);
        try
        {
            if (_records.TryGetValue((kind, hours), out c) && now - c.At < CacheFor) return c.View;
            var view = demo ? DemoRecords(kind, hours) : monitor is null ? new RecordsView(now, hours, kind, [], [], false) : await BuildRecordsAsync(monitor, kind, hours, ct);
            _records[(kind, hours)] = (now, view);
            return view;
        }
        finally { _gate.Release(); }
    }

    private async Task<RecordsView> BuildRecordsAsync(IMonitorClient m, string kind, int hours, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var records = kind == "faults" ? await m.GetFaultsAsync(now.AddHours(-hours), ct) : await m.GetFailuresAsync(now.AddHours(-hours), ct);
        var machines = await m.GetMachinesAsync(ct);
        // the session of a record: first from the sessions of the last days and the live ones (one cheap request each, cached), only then one by one
        var sessions = new Dictionary<string, MonitorSession>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in await RecentAsync(m, ct)) sessions[x.SessionKey] = x;
        var wanted = records.Select(r => r.SessionKey).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Any(k => !sessions.ContainsKey(k)))
            foreach (var x in await m.GetFarmLiveAsync(ct)) sessions[x.SessionKey] = x;
        foreach (var x in await m.GetSessionsByKeyAsync(wanted.Where(k => !sessions.ContainsKey(k)), ct)) sessions[x.SessionKey] = x;
        var users = await m.GetUsersByIdAsync(records.Select(r => r.UserId ?? SessionOf(r, sessions)?.UserId).OfType<long>(), ct);
        // crashes still without a user: who ran that program on that machine at that time (the launch records of the Monitor name the user)
        Func<FaultRecord, MonitorUser?>? fallback = null;
        var open = kind == "faults" ? records.Where(r => (r.UserId ?? SessionOf(r, sessions)?.UserId) is null).ToList() : [];
        if (open.Count > 0)
        {
            var launches = await m.GetLaunchUsersAsync(open.Select(r => r.Name), now.AddHours(-hours).AddDays(-7), ct);
            var found = new Dictionary<string, MonitorUser>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in launches.Select(l => l.User).Distinct(StringComparer.OrdinalIgnoreCase).Take(20))
            {
                var hit = (await m.FindUsersAsync(name.Contains('\\') ? name[(name.IndexOf('\\') + 1)..] : name, 5, ct)).FirstOrDefault(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                found[name] = hit ?? new MonitorUser(name, "");
            }
            fallback = r => LaunchOf(r, launches) is { } l && found.TryGetValue(l.User, out var u) ? u : null;
        }
        var rows = Rows(records, sessions, users, machines, fallback);
        return new RecordsView(now, hours, kind, Summarize(rows), rows.Take(300).ToList(), false, machines.Count);
    }

    /// <summary>The session of a record: by its session key; without a usable key (or when the Monitor no longer lists the session) the one session that ran
    /// on that machine at that time, if there is exactly one (on a multi-session machine it cannot be told apart).</summary>
    public static MonitorSession? SessionOf(FaultRecord r, IReadOnlyDictionary<string, MonitorSession> byKey)
    {
        if (r.SessionKey is not null && byKey.TryGetValue(r.SessionKey, out var s)) return s;
        if (r.MachineId is null || r.At is not { } at) return null;
        var c = byKey.Values.Where(x => string.Equals(x.MachineId, r.MachineId, StringComparison.OrdinalIgnoreCase) && x.Start <= at && (x.End is null || x.End >= at)).Take(2).ToList();
        return c.Count == 1 ? c[0] : null;
    }

    /// <summary>The launch that was running when the program crashed: same program on the same machine, started before the crash and not yet ended (the newest one).</summary>
    public static LaunchUser? LaunchOf(FaultRecord r, IReadOnlyList<LaunchUser> launches) => r.At is not { } at || r.MachineId is null ? null
        : launches.Where(l => string.Equals(l.Process, r.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(l.MachineId, r.MachineId, StringComparison.OrdinalIgnoreCase)
                && l.Start <= at && (l.End is null || l.End >= at.AddMinutes(-1))).OrderByDescending(l => l.Start).FirstOrDefault();

    /// <summary>Joins each record with the session (user, client, delivery group) and the machine it belongs to.</summary>
    public static List<RecordRow> Rows(IReadOnlyList<FaultRecord> records, IReadOnlyDictionary<string, MonitorSession> sessions, IReadOnlyDictionary<long, MonitorUser> users, IReadOnlyList<MachineInfo> machines,
        Func<FaultRecord, MonitorUser?>? fallbackUser = null)
    {
        var byId = machines.Where(x => x.Id is not null).ToDictionary(x => x.Id!, StringComparer.OrdinalIgnoreCase);
        return records.OrderByDescending(r => r.At ?? DateTimeOffset.MinValue).Select(r =>
        {
            var s = SessionOf(r, sessions);
            var uid = r.UserId ?? s?.UserId;
            var user = uid is { } id && users.TryGetValue(id, out var u) ? u : fallbackUser?.Invoke(r);
            var machineId = r.MachineId ?? s?.MachineId;
            var machine = machineId is not null && byId.TryGetValue(machineId, out var mi) ? mi : null;
            return new RecordRow(r.At, r.Name, user?.Name, user?.Sid, machine?.Name ?? s?.Machine ?? (r.MachineId is { Length: >= 8 } mid ? $"({mid[..8]})" : null),
                machine?.Group ?? s?.DesktopGroup, s?.ClientName, r.Items ?? []);
        }).ToList();
    }

    /// <summary>Counts the records by name with users, machines, first and last time, the versions involved and a hint on the pattern.</summary>
    public static IReadOnlyList<RecordSummary> Summarize(IReadOnlyList<RecordRow> rows) => rows
        .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .Select(g =>
        {
            string? Top(IEnumerable<string?> v) => v.Where(x => !string.IsNullOrEmpty(x)).GroupBy(x => x!).OrderByDescending(x => x.Count()).FirstOrDefault()?.Key;
            var users = g.Select(r => r.User).Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var machines = g.Select(r => r.Machine).Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var hint = g.Count() < 3 ? "few" : machines == 1 && users <= 1 ? "oneBoth" : machines == 1 ? "oneMachine" : users == 1 ? "oneUser" : "widespread";
            var versions = g.SelectMany(r => r.Items).Where(i => i.Key is "Version" or "AppVersion").Select(i => i.Value).Distinct().Take(4).ToList();
            return new RecordSummary(g.Key, g.Count(), users, machines, Top(g.Select(r => r.Machine)), Top(g.Select(r => r.User)),
                g.Min(r => r.At), g.Max(r => r.At), hint, versions);
        })
        .OrderByDescending(s => s.Count).Take(20).ToList();

    private RecordsView DemoRecords(string kind, int hours)
    {
        var now = clock.GetUtcNow();
        var items = kind == "faults"
            ? new[] { ("outlook.exe", "demo.anna", "rds-01.demo.local", "16.0.17"), ("outlook.exe", "demo.ben", "rds-01.demo.local", "16.0.17"), ("outlook.exe", "demo.carla", "rds-01.demo.local", "16.0.16"), ("sapgui.exe", "demo.dirk", "vdi-02.demo.local", "8.0.1"), ("excel.exe", "demo.anna", "vdi-01.demo.local", "16.0.17") }
            : new[] { ("Session limit reached", "demo.anna", "rds-01.demo.local", ""), ("Session limit reached", "demo.ben", "rds-01.demo.local", ""), ("Session limit reached", "demo.carla", "rds-02.demo.local", ""), ("No available capacity", "demo.dirk", "rds-02.demo.local", "") };
        var rows = items.Select((x, i) => new RecordRow(now.AddMinutes(-7 - i * 11), x.Item1, x.Item2, $"S-1-5-21-1-1-1-11{i:00}", x.Item3, x.Item3.StartsWith("rds") ? "DG-Apps" : "DG-Desktops", $"PC-{i + 1:00}",
            kind == "faults" ? [new DetailItem("Version", x.Item4), new DetailItem("FaultingApplicationPath", $"C:\\Program Files\\App\\{x.Item1}"), new DetailItem("Description", "Faulting module ntdll.dll, exception 0xc0000005")]
                             : [new DetailItem("FailureReason", x.Item1)])).ToList();
        return new RecordsView(now, hours, kind, Summarize(rows), rows, true);
    }

    private async Task<InsightsView> BuildAsync(IMonitorClient m, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var live = await m.GetFarmLiveAsync(ct);
        var recent = await m.GetRecentAsync(now.AddHours(-24), ct);
        var groups = await GroupsAsync(m, ct);
        var machines = await m.GetMachinesAsync(ct);
        var loads = await m.GetLoadIndexesAsync(machines.Where(x => x.Registration == 1).Select(x => x.Id).OfType<string>(), ct);
        var rows = MachineRows(machines, loads);
        var findings = Findings(live, recent, groups, rows, out var userIds);
        findings.AddRange(UptimeFindings(rows));
        IReadOnlyList<DisconnectedRow> disconnected = [];
        if (helpdesk is not null)
        {
            try
            {
                var brokerSessions = (await helpdesk.ListLiveAsync(null, 2000, ct)).Sessions;
                disconnected = DisconnectedRows(brokerSessions, now);
                findings.AddRange(DisconnectedFindings(brokerSessions, now));
            }
            catch (ProviderUnavailableException) { /* the Broker is optional here */ }
        }
        findings = findings.OrderBy(f => f.Severity switch { "crit" => 0, "warn" => 1, _ => 2 }).ToList();
        // names for the sessions in the findings (the Monitor knows users by a number)
        var users = await m.GetUsersByIdAsync(userIds.Values, ct);
        findings = findings.Select(f => f.Kind is "connection" or "logon" && userIds.TryGetValue(f.Subject, out var uid) && users.TryGetValue(uid, out var u)
            ? f with { Subject = u.Name, UserName = u.Name, UserSid = u.Sid } : f).ToList();
        return new InsightsView(now, live.Count, findings, groups, rows, false, disconnected, live.Count(x => x.ConnectionState != 2), live.Count(x => x.ConnectionState == 2));
    }

    private static DateTimeOffset? Since(SessionInfo s, DateTimeOffset now) => s.StateChangedAt ?? (s.IdleSeconds is { } idle ? now.AddSeconds(-idle) : null);

    /// <summary>All disconnected sessions, the longest disconnected first (the time comes from the Broker's state change, or its idle time).</summary>
    public static IReadOnlyList<DisconnectedRow> DisconnectedRows(IReadOnlyList<HelpdeskSession> sessions, DateTimeOffset now) => sessions
        .Where(h => h.Session.State == SessionState.Disconnected)
        .Select(h => (H: h, Since: Since(h.Session, now)))
        .OrderBy(x => x.Since ?? DateTimeOffset.MaxValue).Take(100)
        .Select(x => new DisconnectedRow(x.H.UserName, x.H.UserSid, x.H.Session.ResourceName, x.H.Session.Machine, x.H.Session.DeliveryGroup, x.Since,
            x.Since is { } s ? (int)(now - s).TotalHours : 0)).ToList();

    /// <summary>Sessions that have been disconnected for hours: they hold a licence and memory without being used.</summary>
    public static List<InsightFinding> DisconnectedFindings(IReadOnlyList<HelpdeskSession> sessions, DateTimeOffset now) => DisconnectedRows(sessions, now)
        .Where(r => r.Hours >= DisconnectedHours).Take(MaxPerKind)
        .Select(r => new InsightFinding("info", "disconnected", r.User, r.Application, r.User, r.UserSid, r.Machine, r.Group, r.Hours)).ToList();

    /// <summary>Registered machines that have not been restarted for a long time.</summary>
    public static List<InsightFinding> UptimeFindings(IReadOnlyList<MachineHealthRow> machines) => machines
        .Where(x => x.Registration == 1 && x.Power == 3 && x.UptimeDays >= UptimeDaysLimit)
        .OrderByDescending(x => x.UptimeDays).Take(MaxPerKind)
        .Select(x => new InsightFinding("info", "uptime", x.Name, $"{x.UptimeDays}", Machine: x.Name, Group: x.Group, Value: x.UptimeDays)).ToList();

    /// <summary>Counts the records by name, with the number of machines and the machine that has most of them.</summary>
    public static IReadOnlyList<CountRow> Count(IReadOnlyList<FaultRecord> records, IReadOnlyDictionary<string, string> machineNames) => records
        .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .Select(g =>
        {
            var byMachine = g.Where(r => r.MachineId is not null).GroupBy(r => r.MachineId!, StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Count()).ToList();
            return new CountRow(g.Key, g.Count(), byMachine.Count, byMachine.Count == 0 ? null : machineNames.GetValueOrDefault(byMachine[0].Key) ?? byMachine[0].Key);
        })
        .OrderByDescending(r => r.Count).Take(10).ToList();

    /// <summary>The findings, most severe first. Pure function of its inputs, so it can be tested.</summary>
    public List<InsightFinding> Findings(IReadOnlyList<MonitorSession> live, IReadOnlyList<MonitorSession> recent, IReadOnlyList<GroupLogon> groups, IReadOnlyList<MachineHealthRow> machines, out Dictionary<string, long> userIds)
    {
        var list = new List<InsightFinding>();
        var ids = userIds = [];

        foreach (var s in live.Where(s => s.IcaRttMs is not null || s.BandwidthKbps is not null)
                     .Select(s => (S: s, Rtt: QualityEvaluator.HigherIsWorse(s.IcaRttMs, Q.RttWarnMs, Q.RttCritMs), Bw: QualityEvaluator.LowerIsWorse(s.BandwidthKbps, Q.BandwidthWarnKbps, Q.BandwidthCritKbps)))
                     .Where(x => x.Rtt >= Health.Warning || x.Bw >= Health.Warning)
                     .OrderByDescending(x => (int)QualityEvaluator.Combine(x.Rtt, x.Bw)).ThenByDescending(x => x.S.IcaRttMs ?? 0).Take(MaxPerKind))
        {
            var crit = QualityEvaluator.Combine(s.Rtt, s.Bw) == Health.Critical;
            var parts = new List<string>();
            if (s.Rtt >= Health.Warning) parts.Add($"ICA RTT {s.S.IcaRttMs} ms");
            if (s.Bw >= Health.Warning) parts.Add($"{(s.S.BandwidthKbps ?? 0) / 1000.0:0.0} Mbit/s");
            list.Add(Make(ids, crit, "connection", s.S, string.Join(" · ", parts), s.S.IcaRttMs));
        }

        var threshold = Q.LogonWarnSeconds;
        var avg = groups.ToDictionary(g => g.Group, g => g, StringComparer.OrdinalIgnoreCase);
        foreach (var s in recent.Where(s => s.LogonSeconds is { } l && l >= threshold)
                     .Where(s => s.DesktopGroup is null || !avg.TryGetValue(s.DesktopGroup, out var g) || g.Sessions < 5 || s.LogonSeconds >= g.AvgSeconds * 1.5)
                     .OrderByDescending(s => s.LogonSeconds).Take(MaxPerKind))
        {
            var g = s.DesktopGroup is not null && avg.TryGetValue(s.DesktopGroup, out var gg) ? $" · Ø {gg.AvgSeconds} s" : "";
            list.Add(Make(ids, s.LogonSeconds >= Q.LogonCritSeconds, "logon", s, $"{s.LogonSeconds} s{g}", s.LogonSeconds));
        }

        foreach (var g in groups.Where(g => g.Sessions >= 5 && g.AvgSeconds >= Q.LogonWarnSeconds))
            list.Add(new InsightFinding(g.AvgSeconds >= Q.LogonCritSeconds ? "crit" : "warn", "group", g.Group, $"Ø {g.AvgSeconds} s ({g.Sessions})", Group: g.Group, Value: g.AvgSeconds));

        foreach (var x in machines.Where(x => x.Health is "crit" or "warn"))
            list.Add(new InsightFinding(x.Health, x.Registration != 1 ? "unregistered" : "load", x.Name, x.Registration != 1 ? "" : (x.LoadIndex ?? 0).ToString(CultureInfo.InvariantCulture), Machine: x.Name, Group: x.Group, Value: x.LoadIndex));

        var inMaint = machines.Count(x => x.Maintenance);
        if (inMaint > 0) list.Add(new InsightFinding("info", "maintenance", inMaint.ToString(CultureInfo.InvariantCulture), "", Value: inMaint));

        return list.OrderBy(f => f.Severity switch { "crit" => 0, "warn" => 1, _ => 2 }).ThenBy(f => f.Kind, StringComparer.Ordinal).ToList();
    }

    private static InsightFinding Make(Dictionary<string, long> ids, bool crit, string kind, MonitorSession s, string detail, int? value)
    {
        if (s.UserId is { } uid) ids[s.SessionKey] = uid;
        // the subject is the session key until the user's name is known
        return new InsightFinding(crit ? "crit" : "warn", kind, s.SessionKey, detail, Machine: s.Machine, Group: s.DesktopGroup, Value: value);
    }

    public IReadOnlyList<MachineHealthRow> MachineRows(IReadOnlyList<MachineInfo> machines, IReadOnlyDictionary<string, int> loads) => machines
        .Select(x =>
        {
            int? load = x.Id is not null && loads.TryGetValue(x.Id, out var l) ? l : null;
            var registered = x.Registration == 1;
            var health = x.Maintenance ? "info" : !registered ? (x.Power == 3 ? "crit" : "off")
                : load >= Q.LoadCrit ? "crit" : load >= Q.LoadWarn ? "warn" : "good";
            int? uptime = x.PoweredOn is { } on && x.Power == 3 ? (int)(clock.GetUtcNow() - on).TotalDays : null;
            return new MachineHealthRow(x.Name, x.Group, x.Registration, x.Power, x.Maintenance, x.Sessions, load, health, uptime, MachineScore(health, load, uptime));
        })
        .OrderBy(r => r.Health switch { "crit" => 0, "warn" => 1, "good" => 2, "info" => 3, _ => 4 }).ThenBy(r => r.Group, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .Take(300).ToList();

    /// <summary>One number from 0 (bad) to 100 (fine) per machine: not registered while switched on costs 60 points, a load above the critical limit 40, above the
    /// warning limit 20, and a running time beyond the limit 10. Machines in maintenance or switched off have no score.</summary>
    public int? MachineScore(string health, int? load, int? uptimeDays)
    {
        if (health is "info" or "off") return null;
        var score = 100;
        if (health == "crit" && load is null) score -= 60;
        if (load >= Q.LoadCrit) score -= 40; else if (load >= Q.LoadWarn) score -= 20;
        if (uptimeDays >= UptimeDaysLimit) score -= 10;
        return Math.Max(0, score);
    }

    /// <summary>One number from 0 (bad) to 100 (fine) per user: connection failures (10 each, at most 40), automatic reconnects (3 each, at most 15) and a logon
    /// time well above the group's average (20 beyond 1.5 times, 30 beyond twice).</summary>
    public static int? UserScore(int sessions, int? avgLogon, int? groupAvg, int failures, int reconnects)
    {
        if (sessions == 0) return null;
        var score = 100 - Math.Min(40, failures * 10) - Math.Min(15, reconnects * 3);
        if (avgLogon is { } a && groupAvg is { } g && g > 0) score -= a >= g * 2 ? 30 : a >= g * 1.5 ? 20 : 0;
        return Math.Max(0, score);
    }

    private (DateTimeOffset At, IReadOnlyList<MonitorSession> Sessions)? _recent;

    /// <summary>The sessions of the last 7 days (cached for 10 minutes): for the logon statistics and to find the user of a crash or failure.</summary>
    private async Task<IReadOnlyList<MonitorSession>> RecentAsync(IMonitorClient m, CancellationToken ct)
    {
        if (_recent is { } r && clock.GetUtcNow() - r.At < GroupCacheFor) return r.Sessions;
        var sessions = await m.GetRecentAsync(clock.GetUtcNow().AddDays(-7), ct);
        _recent = (clock.GetUtcNow(), sessions);
        return sessions;
    }

    private async Task<IReadOnlyList<GroupLogon>> GroupsAsync(IMonitorClient m, CancellationToken ct)
    {
        if (_groups is { } g && clock.GetUtcNow() - g.At < GroupCacheFor) return g.Groups;
        var sessions = await RecentAsync(m, ct);
        var result = Groups(sessions, clock.GetUtcNow());
        _groups = (clock.GetUtcNow(), result);
        return result;
    }

    /// <summary>Average, median and 90th percentile of the logon durations per delivery group (sessions without a logon duration are left out).</summary>
    public static IReadOnlyList<GroupLogon> Groups(IReadOnlyList<MonitorSession> sessions, DateTimeOffset? now = null) => sessions
        .Where(s => s.LogonSeconds is > 0 && !string.IsNullOrWhiteSpace(s.DesktopGroup))
        .GroupBy(s => s.DesktopGroup!, StringComparer.OrdinalIgnoreCase)
        .Select(g =>
        {
            var v = g.Select(s => s.LogonSeconds!.Value).Order().ToArray();
            var today = DateOnly.FromDateTime((now ?? DateTimeOffset.UtcNow).UtcDateTime);
            var daily = Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6)).Select(day =>
            {
                var d = g.Where(s => s.Start is { } st && DateOnly.FromDateTime(st.UtcDateTime) == day).Select(s => s.LogonSeconds!.Value).ToList();
                return d.Count == 0 ? (int?)null : (int)Math.Round(d.Average());
            }).ToList();
            return new GroupLogon(g.Key, v.Length, (int)Math.Round(v.Average()), Percentile(v, 0.5), Percentile(v, 0.9), daily);
        })
        .OrderByDescending(g => g.AvgSeconds).ToList();

    private static int Percentile(int[] sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    /// <summary>The logon statistics of one delivery group (for the comparison in the expert view), or null when unknown.</summary>
    public async Task<GroupLogon?> GroupAsync(string group, CancellationToken ct)
    {
        if (demo) return DemoGroups().FirstOrDefault(g => string.Equals(g.Group, group, StringComparison.OrdinalIgnoreCase));
        if (monitor is null) return null;
        try { return (await GroupsAsync(monitor, ct)).FirstOrDefault(g => string.Equals(g.Group, group, StringComparison.OrdinalIgnoreCase)); }
        catch (ProviderUnavailableException) { return null; }
    }

    public async Task<UserSummary?> UserSummaryAsync(string sid, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 90);
        if (demo) return new UserSummary(days, 14, 38, 35, "DG-Desktops", 31, 1, 3, "attention", UserScore(14, 38, 31, 1, 3));
        if (monitor is null) return null;
        var since = clock.GetUtcNow().AddDays(-days);
        var sessions = (await monitor.GetEndedAsync(sid, since, ct)).Concat(await monitor.GetLiveAsync(sid, ct)).DistinctBy(s => s.SessionKey).ToList();
        var logons = sessions.Where(s => s.LogonSeconds is > 0).Select(s => s.LogonSeconds!.Value).Order().ToArray();
        var keys = sessions.Select(s => s.SessionKey).ToList();
        var failures = (await monitor.CountBySessionAsync("ConnectionFailureLogs", keys, ct)).Values.Sum();
        var reconnects = (await monitor.CountBySessionAsync("SessionAutoReconnects", keys, ct)).Values.Sum();
        var group = sessions.Where(s => !string.IsNullOrWhiteSpace(s.DesktopGroup)).GroupBy(s => s.DesktopGroup!).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        var ga = group is null ? null : await GroupAsync(group, ct);
        return Summary(days, sessions.Count, logons, group, ga?.AvgSeconds, failures, reconnects);
    }

    public UserSummary Summary(int days, int sessions, int[] sortedLogons, string? group, int? groupAvg, int failures, int reconnects)
    {
        int? avg = sortedLogons.Length > 0 ? (int)Math.Round(sortedLogons.Average()) : null;
        int? median = sortedLogons.Length > 0 ? Percentile(sortedLogons, 0.5) : null;
        var slow = avg is { } a && a >= Q.LogonWarnSeconds && groupAvg is { } g && a >= g * 1.5;
        var problems = failures + reconnects;
        var verdict = problems >= 15 || (avg >= Q.LogonCritSeconds && slow) ? "poor" : problems >= 5 || slow ? "attention" : "ok";
        return new UserSummary(days, sessions, avg, median, group, groupAvg, failures, reconnects, verdict, UserScore(sessions, avg, groupAvg, failures, reconnects));
    }

    // ---- demo ----------------------------------------------------------------------------------

    private static IReadOnlyList<GroupLogon> DemoGroups() =>
    [new("DG-Apps", 120, 52, 48, 81, [38, 41, 44, 47, 55, 61, 66]), new("DG-Desktops", 210, 31, 28, 49, [30, 33, 29, 31, null, 28, 32]), new("DG-Kiosk", 35, 19, 17, 28, [20, 19, 18, null, 19, 21, 19])];

    private InsightsView Demo() => DemoCore() with { ActiveSessions = 56, DisconnectedSessions = 16 };

    private InsightsView DemoCore() => new(clock.GetUtcNow(), 72,
    [
        new("crit", "unregistered", "rds-02.demo.local", "", Machine: "rds-02.demo.local", Group: "DG-Apps"),
        new("crit", "connection", "demo.anna", "ICA RTT 182 ms · 1.1 Mbit/s", "demo.anna", "S-1-5-21-1-1-1-1101", "rds-01.demo.local", "DG-Apps", 182),
        new("warn", "load", "rds-01.demo.local", "7600", Machine: "rds-01.demo.local", Group: "DG-Apps", Value: 7600),
        new("warn", "logon", "demo.ben", "74 s · Ø 31 s", "demo.ben", "S-1-5-21-1-1-1-1102", "vdi-02.demo.local", "DG-Desktops", 74),
        new("warn", "group", "DG-Apps", "Ø 52 s (120)", Group: "DG-Apps", Value: 52),
        new("info", "disconnected", "demo.carla", "SAP GUI", "demo.carla", "S-1-5-21-1-1-1-1103", "rds-01.demo.local", "DG-Apps", 9),
        new("info", "uptime", "rds-01.demo.local", "21", Machine: "rds-01.demo.local", Group: "DG-Apps", Value: 21),
        new("info", "maintenance", "1", "", Value: 1),
    ], DemoGroups(),
    [
        new("rds-02.demo.local", "DG-Apps", 0, 3, false, 0, null, "crit", null, 40), new("rds-01.demo.local", "DG-Apps", 1, 3, false, 12, 7600, "warn", 21, 70),
        new("vdi-01.demo.local", "DG-Desktops", 1, 3, false, 6, 2400, "good", null, 100), new("vdi-02.demo.local", "DG-Desktops", 1, 3, false, 4, 1900, "good", null, 100),
        new("vdi-03.demo.local", "DG-Desktops", 1, 3, true, 0, 300, "info"),
    ], true,
    [new("demo.carla", "S-1-5-21-1-1-1-1103", "SAP GUI", "rds-01.demo.local", "DG-Apps", clock.GetUtcNow().AddHours(-9), 9), new("demo.dirk", "S-1-5-21-1-1-1-1104", "Windows 11 Desktop", "vdi-02.demo.local", "DG-Desktops", clock.GetUtcNow().AddMinutes(-70), 1)]);
}
