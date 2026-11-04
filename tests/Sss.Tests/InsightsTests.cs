using Sss.Web;
using Sss.Web.Citrix;
using Sss.Web.Models;
using Sss.Web.Services;

namespace Sss.Tests;

public class InsightsTests
{
    private static InsightsService Make(bool demo = false) => new(new SssOptions(), null, null, TimeProvider.System, demo);

    private static MonitorSession S(string key, string? group = "DG1", int? logon = null, int? rtt = null, int? kbps = null, long? user = 1, string machine = "m1.corp.local") =>
        new(key, machine, group, true, null, null, logon, "PC", "10.0.0.1", rtt, kbps, UserId: user);

    [Fact]
    public void Group_statistics_use_the_logon_durations_of_the_group()
    {
        var sessions = Enumerable.Range(1, 10).Select(i => S($"k{i}", logon: i * 10)).Append(S("none", logon: null)).Append(S("other", "DG2", 5)).ToList();
        var g = InsightsService.Groups(sessions).Single(x => x.Group == "DG1");
        Assert.Equal((10, 55, 50, 90), (g.Sessions, g.AvgSeconds, g.MedianSeconds, g.P90Seconds));
        Assert.Equal(["DG1", "DG2"], InsightsService.Groups(sessions).Select(x => x.Group));   // slowest group first
    }

    [Fact]
    public void Slow_connections_and_logons_are_findings_and_the_user_ids_are_collected()
    {
        var svc = Make();
        var live = new[] { S("a", rtt: 200, user: 7), S("b", rtt: 20, user: 8), S("c", rtt: 100, kbps: 100000, user: 9) };
        var recent = new[] { S("d", logon: 120, user: 10), S("e", logon: 20, user: 11) };
        var f = svc.Findings(live, recent, [], [], out var users);

        Assert.Equal("crit", f[0].Severity);                                                     // most severe first
        Assert.Contains(f, x => x.Kind == "connection" && x.Subject == "a" && x.Severity == "crit");
        Assert.Contains(f, x => x.Kind == "connection" && x.Subject == "c" && x.Severity == "warn");
        Assert.DoesNotContain(f, x => x.Subject == "b");
        Assert.Contains(f, x => x.Kind == "logon" && x.Subject == "d" && x.Severity == "crit");
        Assert.DoesNotContain(f, x => x.Subject == "e");
        Assert.Equal(10, users["d"]);
    }

    [Fact]
    public void A_logon_is_only_conspicuous_when_it_is_also_slow_for_its_group()
    {
        var groups = new[] { new GroupLogon("DG1", 50, 80, 75, 120) };                           // this group is always slow
        var f = Make().Findings([], [S("slow", logon: 90)], groups, [], out _);
        Assert.DoesNotContain(f, x => x.Kind == "logon");
        Assert.Contains(f, x => x.Kind == "group" && x.Group == "DG1");                          // the group itself is the finding
    }

    [Fact]
    public void Machines_are_rated_and_the_problems_become_findings()
    {
        var svc = Make();
        MachineInfo M(string name, int reg, int power, bool maint, string id) => new(name, "DG1", "2507.0", "Windows", reg, power, maint, 3, null, null, id);
        var rows = svc.MachineRows([M("a", 0, 3, false, "1"), M("b", 1, 3, false, "2"), M("c", 1, 3, false, "3"), M("d", 1, 3, true, "4"), M("e", 0, 2, false, "5")],
            new Dictionary<string, int> { ["2"] = 9500, ["3"] = 1000, ["4"] = 100 });
        Assert.Equal(["crit", "crit", "good", "info", "off"], rows.Select(r => r.Health));        // unregistered but on, overloaded, ok, maintenance, off

        var f = svc.Findings([], [], [], rows, out _);
        Assert.Equal(2, f.Count(x => x.Severity == "crit"));
        Assert.Contains(f, x => x.Kind == "unregistered" && x.Machine == "a");
        Assert.Contains(f, x => x.Kind == "load" && x.Machine == "b");
        Assert.Contains(f, x => x.Kind == "maintenance");
        Assert.DoesNotContain(f, x => x.Machine == "e");                                         // a machine that is simply off is not an alarm
    }

    [Theory]
    [InlineData(10, 0, 0, 30, 30, "ok")]
    [InlineData(10, 2, 3, 30, 30, "attention")]                                                   // five problems
    [InlineData(10, 10, 6, 30, 30, "poor")]
    [InlineData(10, 0, 0, 80, 40, "attention")]                                                   // slow for the group
    public void The_verdict_of_a_user_summary(int sessions, int failures, int reconnects, int logon, int groupAvg, string verdict)
    {
        var s = Make().Summary(30, sessions, [logon, logon], "DG1", groupAvg, failures, reconnects);
        Assert.Equal(verdict, s.Verdict);
        Assert.Equal(logon, s.AvgLogonSeconds);
    }

    [Fact]
    public void Group_statistics_carry_the_average_of_each_of_the_last_seven_days()
    {
        var now = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        MonitorSession At(string key, int days, int logon) => S(key, logon: logon) with { Start = now.AddDays(-days) };
        var g = InsightsService.Groups([At("a", 0, 40), At("b", 0, 60), At("c", 2, 30), At("old", 9, 99)], now).Single();
        Assert.Equal(7, g.Daily!.Count);
        Assert.Equal([null, null, null, null, 30, null, 50], g.Daily);                              // oldest day first, the last entry is today
    }

    [Fact]
    public void Long_disconnected_sessions_and_long_running_machines_are_listed()
    {
        HelpdeskSession H(string user, SessionState state, int? idle) => new(new SessionInfo("1", "SAP", "Application", "DG1", "m1", DateTimeOffset.UtcNow, null, state, idle, null, null,
            new Sss.Web.Models.SessionQuality(null, null, null, null, Health.Unknown, Health.Unknown)), user, "S-1-5-21-1-1-1-1");
        var f = InsightsService.DisconnectedFindings([H("a", SessionState.Disconnected, 9 * 3600), H("b", SessionState.Disconnected, 600), H("c", SessionState.Active, 99999)], DateTimeOffset.UtcNow);
        Assert.Equal("a", f.Single().Subject);
        Assert.Equal(9, f.Single().Value);

        MachineHealthRow M(string n, int days, int reg = 1) => new(n, "DG1", reg, 3, false, 0, 100, "good", days);
        var u = InsightsService.UptimeFindings([M("old", 30), M("fresh", 2), M("unreg", 40, 0)]);
        Assert.Equal("old", u.Single().Machine);
    }

    [Fact]
    public void Records_are_counted_by_name_with_the_machine_that_has_most()
    {
        var names = new Dictionary<string, string> { ["m1"] = "vda1", ["m2"] = "vda2" };
        var rows = InsightsService.Count([new("outlook", "m1"), new("outlook", "m1"), new("outlook", "m2"), new("excel", null)], names);
        Assert.Equal(("outlook", 3, 2, "vda1"), (rows[0].Name, rows[0].Count, rows[0].Machines, rows[0].TopMachine));
        Assert.Equal(("excel", 1, 0, null), (rows[1].Name, rows[1].Count, rows[1].Machines, rows[1].TopMachine));
    }

    [Fact]
    public void Records_are_joined_with_session_user_and_machine_and_summarised_with_a_hint()
    {
        var m1 = new MachineInfo("vda1.corp.local", "DG1", "2507", "Windows", 1, 3, false, 1, null, null, "M1");
        var sessions = new Dictionary<string, MonitorSession> { ["k1"] = S("k1", user: 5) with { MachineId = "M1", ClientName = "PC9" }, ["k2"] = S("k2", user: 6) with { MachineId = "M1" }, ["k3"] = S("k3", user: 5) with { MachineId = "M1" } };
        var users = new Dictionary<long, MonitorUser> { [5] = new("CORP\\a", "S-1-5-21-1-1-1-5"), [6] = new("CORP\\b", "S-1-5-21-1-1-1-6") };
        var at = DateTimeOffset.Parse("2026-11-04T10:00:00Z");
        FaultRecord F(string name, string key, int min) => new(name, null, at.AddMinutes(min), key, null, [new DetailItem("Version", "1.2")]);
        var rows = InsightsService.Rows([F("outlook", "k1", 0), F("outlook", "k2", 5), F("outlook", "k3", 9), F("excel", "k1", 3)], sessions, users, [m1]);

        Assert.Equal("outlook", rows[0].Name);                                                   // newest first
        Assert.Equal(("CORP\\a", "vda1.corp.local", "DG1", "PC"), (rows[0].User, rows[0].Machine, rows[0].Group, rows[0].Client));
        Assert.Equal("PC9", rows.First(r => r.Name == "excel").Client);
        var sum = InsightsService.Summarize(rows);
        var o = sum.Single(x => x.Name == "outlook");
        Assert.Equal((3, 2, 1, "oneMachine"), (o.Count, o.Users, o.Machines, o.Hint));          // several users, one machine
        Assert.Equal(["1.2"], o.Versions);
        Assert.Equal("few", sum.Single(x => x.Name == "excel").Hint);
    }

    [Fact]
    public async Task Records_are_read_for_the_allowed_periods_only()
    {
        var svc = Make(demo: true);
        Assert.True((await svc.RecordsAsync("faults", 3, default)).Summary.Count > 0);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecordsAsync("faults", 5, default));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecordsAsync("users", 1, default));
    }

    [Fact]
    public void Disconnected_sessions_are_listed_longest_first_with_the_state_change_time()
    {
        var now = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
        HelpdeskSession H(string user, int? idle, DateTimeOffset? changed) => new(new SessionInfo("1", "SAP", "Application", "DG1", "m1", now, null, SessionState.Disconnected, idle, null, null,
            new Sss.Web.Models.SessionQuality(null, null, null, null, Health.Unknown, Health.Unknown), StateChangedAt: changed), user, "S-1-5-21-1-1-1-1");
        var rows = InsightsService.DisconnectedRows([H("recent", null, now.AddMinutes(-30)), H("old", null, now.AddHours(-7)), H("fromIdle", 5 * 3600, null)], now);
        Assert.Equal(["old", "fromIdle", "recent"], rows.Select(r => r.User));
        Assert.Equal(7, rows[0].Hours);
    }

    [Fact]
    public async Task The_demo_delivers_findings_groups_and_machines()
    {
        var v = await Make(demo: true).GetAsync(default);
        Assert.True(v.Demo);
        Assert.NotEmpty(v.Findings);
        Assert.NotEmpty(v.Groups);
        Assert.NotEmpty(v.Machines);
        Assert.Equal("attention", (await Make(demo: true).UserSummaryAsync("S-1-5-21-1-1-1-1", 30, default))!.Verdict);
    }
}

[Collection("Api")]
public class InsightsApiTests : IClassFixture<HelpdeskOnFactory>, IClassFixture<AuditorFactory>
{
    private readonly HttpClient _helpdesk, _auditor;
    private readonly InsightsService _insights = new(new SssOptions(), null, null, TimeProvider.System);
    public InsightsApiTests(HelpdeskOnFactory a, AuditorFactory b) { _helpdesk = a.NewClient(); _auditor = b.NewClient(); }

    [Fact]
    public async Task The_helpdesk_reads_insights_group_statistics_and_a_user_summary()
    {
        var ins = await (await _helpdesk.GetAsync("/api/helpdesk/insights")).Json();
        Assert.True(ins.GetProperty("findings").GetArrayLength() > 0);
        Assert.True(ins.GetProperty("groups").GetArrayLength() > 0);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await _helpdesk.GetAsync("/api/helpdesk/insights/group?name=DG-Apps")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _helpdesk.GetAsync("/api/helpdesk/insights/group?name=nope")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await _helpdesk.GetAsync("/api/helpdesk/insights/group")).StatusCode);
        var sum = await (await _helpdesk.GetAsync("/api/helpdesk/users/summary?sid=S-1-5-21-1-1-1-1001&days=30")).Json();
        Assert.True(sum.GetProperty("sessions").GetInt32() > 0);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await _helpdesk.GetAsync("/api/helpdesk/users/summary?sid=x%27")).StatusCode);
    }

    [Fact]
    public async Task The_map_lists_sessions_with_their_location_for_the_helpdesk_only()
    {
        var map = await (await _helpdesk.GetAsync("/api/helpdesk/insights/map")).Json();
        Assert.True(map.GetProperty("demo").GetBoolean());
        var points = map.GetProperty("points");
        Assert.True(points.GetArrayLength() > 20);
        Assert.Contains(points.EnumerateArray(), p => p.GetProperty("city").GetString() == "Berlin" && p.GetProperty("user").GetString()!.Length > 0);
        Assert.DoesNotContain("clientAddress", map.ToString());
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await _auditor.GetAsync("/api/helpdesk/insights/map")).StatusCode);
    }

    [Fact]
    public void Map_points_carry_the_user_name_and_location_without_the_address()
    {
        var live = new[] { new Sss.Web.Citrix.MonitorSession("k1", "vdi.demo.local", "DG", true, null, null, null, "PC1", "10.1.2.3", null, UserId: 7, ClientCountry: " Germany ", ClientCity: "Munich") };
        var users = new Dictionary<long, Sss.Web.Citrix.MonitorUser> { [7] = new("Alex Demo", "S-1-5-21-1-1-1-7") };
        var p = Assert.Single(InsightsService.MapPoints(live, users));
        Assert.Equal(("Alex Demo", "Germany", "Munich", "PC1"), (p.User, p.Country, p.City, p.Device));
    }

    private static Sss.Web.Citrix.MachineInfo Machine(string id, string name, string group = "DG") => new(name, group, "1", "Windows", 1, 3, false, 1, null, null, id, null);

    [Fact]
    public void A_record_without_a_session_key_gets_the_one_session_that_ran_on_the_machine_at_that_time()
    {
        var t0 = DateTimeOffset.UtcNow;
        MonitorSession S(string key, string machine, DateTimeOffset start, DateTimeOffset? end, long user) => new(key, "vdi", "DG", true, start, end, 5, "PC", "10.0.0.1", null, UserId: user, MachineId: machine);
        var byKey = new[] { S("k1", "m1", t0.AddHours(-3), null, 1), S("k2", "m2", t0.AddHours(-3), null, 2), S("k3", "m2", t0.AddHours(-2), null, 3) }.ToDictionary(x => x.SessionKey);
        Assert.Equal(1, InsightsService.SessionOf(new FaultRecord("x", "m1", t0.AddHours(-1)), byKey)?.UserId);   // one session on m1
        Assert.Null(InsightsService.SessionOf(new FaultRecord("x", "m2", t0.AddHours(-1)), byKey));   // two sessions on m2: not told apart
        Assert.Equal(3, InsightsService.SessionOf(new FaultRecord("x", "m2", t0.AddHours(-1), "k3"), byKey)?.UserId);   // the key wins
    }

    [Fact]
    public void A_crash_is_matched_to_the_launch_that_was_running_and_hours_are_counted()
    {
        var t0 = DateTimeOffset.UtcNow;
        var launches = new List<LaunchUser>
        {
            new("OUTLOOK.EXE", "m1", t0.AddHours(-5), t0.AddHours(-3), "DEMO\\anna", null),    // ended before the crash
            new("OUTLOOK.EXE", "m1", t0.AddHours(-2), null, "DEMO\\ben", null),                 // still running: this one
            new("OUTLOOK.EXE", "m2", t0.AddHours(-2), null, "DEMO\\carl", null),                // other machine
        };
        Assert.Equal("DEMO\\ben", InsightsService.LaunchOf(new FaultRecord("outlook.exe", "m1", t0.AddHours(-1)), launches)?.User);
        Assert.Null(InsightsService.LaunchOf(new FaultRecord("excel.exe", "m1", t0.AddHours(-1)), launches));
        var sessions = new[] { new MonitorSession("k", "vdi", "DG", true, t0.AddMinutes(-90), null, 20, "PC", "10.0.0.1", null), new MonitorSession("l", "vdi", "DG", true, t0.AddMinutes(-10), null, 40, "PC", "10.0.0.1", null) };
        var hours = InsightsService.Hourly(sessions, t0);
        Assert.Equal(24, hours.Count);
        Assert.Equal(2, hours.Sum(h => h.Logons));
    }

    [Fact]
    public void Connection_paths_tell_direct_gateway_and_unknown_apart()
    {
        MonitorSession S(string? via, string client = "PC1") => new("k" + via + client, "vdi-01.demo.local", "DG", true, null, null, null, client, null, null, ConnectedVia: via);
        var rows = EnvironmentService.Paths([S("gw.demo.local"), S("gw.demo.local", "PC2"), S("PC3", "PC3"), S("vdi-01.demo.local"), S("127.0.0.1"), S(null)]);
        Assert.Equal([("gw.demo.local", 2, "gateway"), ("", 3, "direct"), ("", 1, "unknown")], rows.Select(r => (r.Name, r.Sessions, r.Kind)).ToArray());
    }

    [Fact]
    public void Load_strips_average_the_samples_per_hour_and_leave_gaps()
    {
        var now = new DateTimeOffset(2026, 11, 4, 12, 30, 0, TimeSpan.Zero);
        var machines = new Dictionary<string, MachineInfo> { ["m1"] = Machine("m1", "rds-01") };
        var s = new[] { new UsageSample("m1", 40, 50, null, null, 60, now.AddMinutes(-20)), new UsageSample("m1", 60, 70, null, null, 60, now.AddMinutes(-10)), new UsageSample("m1", 10, 20, null, null, 60, now.AddHours(-3)), new UsageSample("zz", 99, 99, null, null, 60, now) };
        var strip = Assert.Single(InsightsService.Strips(s, machines, now));
        Assert.Equal(24, strip.Cpu.Count);
        Assert.Equal(50.0, strip.Cpu[23]);   // the current hour: average of 40 and 60
        Assert.Equal(10.0, strip.Cpu[20]);
        Assert.Null(strip.Cpu[0]);
    }

    [Fact]
    public async Task One_machine_has_its_own_page_and_unknown_machines_are_not_found()
    {
        var ok = await (await _helpdesk.GetAsync("/api/helpdesk/insights/machine?name=rds-01")).Json();
        Assert.Equal("rds-01.demo.local", ok.GetProperty("row").GetProperty("name").GetString());
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _helpdesk.GetAsync("/api/helpdesk/insights/machine?name=nope")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await _helpdesk.GetAsync("/api/helpdesk/insights/machine?name=a%27b")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await _auditor.GetAsync("/api/helpdesk/insights/machine?name=rds-01")).StatusCode);
    }

    [Fact]
    public void Usage_uses_the_most_common_granularity_and_rates_the_load()
    {
        var machines = new Dictionary<string, Sss.Web.Citrix.MachineInfo> { ["m1"] = Machine("m1", "rds-01"), ["m2"] = Machine("m2", "rds-02") };
        var samples = new[]
        {
            new Sss.Web.Citrix.UsageSample("m1", 90, 99, 80, 97, 60), new Sss.Web.Citrix.UsageSample("m1", 80, 95, 70, 90, 60), new Sss.Web.Citrix.UsageSample("m1", 10, 10, 10, 10, 1440),
            new Sss.Web.Citrix.UsageSample("m2", 20, 40, 30, 50, 60), new Sss.Web.Citrix.UsageSample("zz", 20, 40, 30, 50, 60),
        };
        var rows = _insights.Usage(samples, machines);
        Assert.Equal(["rds-01", "rds-02"], rows.Select(r => r.Machine).ToArray());   // unknown machine left out, worst first
        Assert.Equal(("crit", 85.0, 99.0), (rows[0].Health, rows[0].Cpu, rows[0].PeakCpu));
        Assert.Equal("good", rows[1].Health);
    }

    [Fact]
    public void Application_start_times_name_the_machine_where_it_is_slow()
    {
        var machines = new Dictionary<string, Sss.Web.Citrix.MachineInfo> { ["m1"] = Machine("m1", "rds-01"), ["m2"] = Machine("m2", "rds-02") };
        var launches = Enumerable.Range(0, 4).Select(_ => new Sss.Web.Citrix.AppLaunch("sap.exe", "m1", 9000))
            .Concat(Enumerable.Range(0, 4).Select(_ => new Sss.Web.Citrix.AppLaunch("sap.exe", "m2", 3000)))
            .Concat([new Sss.Web.Citrix.AppLaunch("rare.exe", "m1", 100)]).ToList();
        var row = Assert.Single(InsightsService.AppStarts(launches, machines));   // fewer than 3 starts: not listed
        Assert.Equal(("sap.exe", 8, 6000, "rds-01", 9000), (row.Name, row.Launches, row.AvgMs, row.SlowMachine, row.SlowMachineMs));
    }

    [Fact]
    public void Costs_add_up_per_delivery_group_without_double_counting_granularities()
    {
        var machines = new Dictionary<string, Sss.Web.Citrix.MachineInfo> { ["m1"] = Machine("m1", "a", "DG-A"), ["m2"] = Machine("m2", "b", "DG-A") };
        var rows = InsightsService.Costs([new("m1", "EUR", 10, 2, 1, 1440), new("m2", "EUR", 5, 1, 0.5, 1440), new("m1", "EUR", 999, 999, 999, 43200)], machines);
        Assert.Equal(("DG-A", 15.0, 3.0, 1.5), (rows.Single().Group, rows.Single().Compute, rows.Single().Storage, rows.Single().Savings));
    }

    [Fact]
    public void Machine_events_list_registration_changes_and_power_on_of_the_last_week()
    {
        var now = DateTimeOffset.UtcNow;
        var m = Machine("m1", "rds-01") with { RegistrationChanged = now.AddHours(-2), PoweredOn = now.AddHours(-3) };
        var old = Machine("m2", "rds-02") with { RegistrationChanged = now.AddDays(-30), PoweredOn = now.AddDays(-30) };
        Assert.Equal(["registered", "poweredOn"], InsightsService.MachineEvents([m, old], now).Select(e => e.Kind).ToArray());
    }

    [Theory]
    [InlineData(0, null, null, 0, 0, null)]
    [InlineData(10, 30, 30, 0, 0, 100)]
    [InlineData(10, 30, 30, 2, 2, 74)]
    [InlineData(10, 70, 30, 9, 9, 15)]
    public void The_user_score_follows_failures_reconnects_and_logon_time(int sessions, int? avg, int? group, int failures, int reconnects, int? expected) =>
        Assert.Equal(expected, InsightsService.UserScore(sessions, avg, group, failures, reconnects));

    [Fact]
    public async Task More_insights_are_available_to_the_helpdesk_and_the_demo_has_data()
    {
        var more = await (await _helpdesk.GetAsync("/api/helpdesk/insights/more")).Json();
        Assert.True(more.GetProperty("usage").GetArrayLength() > 0);
        Assert.True(more.GetProperty("appStarts").GetArrayLength() > 0);
        Assert.True(more.GetProperty("costs").GetArrayLength() > 0);
        Assert.True(more.GetProperty("timeline").GetArrayLength() > 0);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await _auditor.GetAsync("/api/helpdesk/insights/more")).StatusCode);
    }

    [Fact]
    public async Task The_auditor_has_no_insights()
    {
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await _auditor.GetAsync("/api/helpdesk/insights")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await _auditor.GetAsync("/api/helpdesk/users/summary?sid=S-1-5-21-1-1-1-1001")).StatusCode);
    }
}
