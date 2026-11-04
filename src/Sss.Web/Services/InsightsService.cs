using System.Globalization;
using Sss.Web.Citrix;
using Sss.Web.Models;
using Sss.Web.Providers;

namespace Sss.Web.Services;

/// <summary>One thing the helpdesk should look at. Severity is "crit", "warn" or "info"; the user (when there is one) lets the interface open the user's sessions.</summary>
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

public sealed record RecordsView(DateTimeOffset At, int Hours, string Kind, IReadOnlyList<RecordSummary> Summary, IReadOnlyList<RecordRow> Rows, bool Demo);

/// <summary>A disconnected session: who, what, where and since when.</summary>
public sealed record DisconnectedRow(string User, string UserSid, string Application, string Machine, string Group, DateTimeOffset? Since, int Hours);

public sealed record MachineHealthRow(string Name, string Group, int? Registration, int? Power, bool Maintenance, int? Sessions, int? LoadIndex, string Health, int? UptimeDays = null);

public sealed record InsightsView(DateTimeOffset At, int LiveSessions, IReadOnlyList<InsightFinding> Findings, IReadOnlyList<GroupLogon> Groups,
    IReadOnlyList<MachineHealthRow> Machines, bool Demo, IReadOnlyList<DisconnectedRow>? Disconnected = null);

/// <summary>How one user's last days looked: number of sessions, logon time (against the delivery group), failures and automatic reconnects.
/// Verdict is "ok", "attention" or "poor".</summary>
public sealed record UserSummary(int Days, int Sessions, int? AvgLogonSeconds, int? MedianLogonSeconds, string? Group, int? GroupAvgSeconds, int Failures, int Reconnects, string Verdict);

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
        var sessions = (await m.GetSessionsByKeyAsync(records.Select(r => r.SessionKey).OfType<string>(), ct)).ToDictionary(s => s.SessionKey, StringComparer.OrdinalIgnoreCase);
        var users = await m.GetUsersByIdAsync(records.Select(r => r.UserId ?? (r.SessionKey is not null && sessions.TryGetValue(r.SessionKey, out var s) ? s.UserId : null)).OfType<long>(), ct);
        var rows = Rows(records, sessions, users, machines);
        return new RecordsView(now, hours, kind, Summarize(rows), rows.Take(300).ToList(), false);
    }

    /// <summary>Joins each record with the session (user, client, delivery group) and the machine it belongs to.</summary>
    public static List<RecordRow> Rows(IReadOnlyList<FaultRecord> records, IReadOnlyDictionary<string, MonitorSession> sessions, IReadOnlyDictionary<long, MonitorUser> users, IReadOnlyList<MachineInfo> machines)
    {
        var byId = machines.Where(x => x.Id is not null).ToDictionary(x => x.Id!, StringComparer.OrdinalIgnoreCase);
        return records.OrderByDescending(r => r.At ?? DateTimeOffset.MinValue).Select(r =>
        {
            var s = r.SessionKey is not null && sessions.TryGetValue(r.SessionKey, out var ss) ? ss : null;
            var uid = r.UserId ?? s?.UserId;
            var user = uid is { } id && users.TryGetValue(id, out var u) ? u : null;
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
        return new InsightsView(now, live.Count, findings, groups, rows, false, disconnected);
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
            return new MachineHealthRow(x.Name, x.Group, x.Registration, x.Power, x.Maintenance, x.Sessions, load, health, uptime);
        })
        .OrderBy(r => r.Health switch { "crit" => 0, "warn" => 1, "good" => 2, "info" => 3, _ => 4 }).ThenBy(r => r.Group, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .Take(300).ToList();

    private async Task<IReadOnlyList<GroupLogon>> GroupsAsync(IMonitorClient m, CancellationToken ct)
    {
        if (_groups is { } g && clock.GetUtcNow() - g.At < GroupCacheFor) return g.Groups;
        var sessions = await m.GetRecentAsync(clock.GetUtcNow().AddDays(-7), ct);
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
        if (demo) return new UserSummary(days, 14, 38, 35, "DG-Desktops", 31, 1, 3, "attention");
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
        return new UserSummary(days, sessions, avg, median, group, groupAvg, failures, reconnects, verdict);
    }

    // ---- demo ----------------------------------------------------------------------------------

    private static IReadOnlyList<GroupLogon> DemoGroups() =>
    [new("DG-Apps", 120, 52, 48, 81, [38, 41, 44, 47, 55, 61, 66]), new("DG-Desktops", 210, 31, 28, 49, [30, 33, 29, 31, null, 28, 32]), new("DG-Kiosk", 35, 19, 17, 28, [20, 19, 18, null, 19, 21, 19])];

    private InsightsView Demo() => new(clock.GetUtcNow(), 72,
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
        new("rds-02.demo.local", "DG-Apps", 0, 3, false, 0, null, "crit"), new("rds-01.demo.local", "DG-Apps", 1, 3, false, 12, 7600, "warn", 21),
        new("vdi-01.demo.local", "DG-Desktops", 1, 3, false, 6, 2400, "good"), new("vdi-02.demo.local", "DG-Desktops", 1, 3, false, 4, 1900, "good"),
        new("vdi-03.demo.local", "DG-Desktops", 1, 3, true, 0, 300, "info"),
    ], true,
    [new("demo.carla", "S-1-5-21-1-1-1-1103", "SAP GUI", "rds-01.demo.local", "DG-Apps", clock.GetUtcNow().AddHours(-9), 9), new("demo.dirk", "S-1-5-21-1-1-1-1104", "Windows 11 Desktop", "vdi-02.demo.local", "DG-Desktops", clock.GetUtcNow().AddMinutes(-70), 1)]);
}
