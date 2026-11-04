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
    public async Task The_auditor_has_no_insights()
    {
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await _auditor.GetAsync("/api/helpdesk/insights")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await _auditor.GetAsync("/api/helpdesk/users/summary?sid=S-1-5-21-1-1-1-1001")).StatusCode);
    }
}
