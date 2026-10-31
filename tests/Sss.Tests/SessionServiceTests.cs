using Microsoft.Extensions.Configuration;
using Sss.Web;
using Sss.Web.Models;
using Sss.Web.Providers;
using Sss.Web.Services;

namespace Sss.Tests;

public class SessionServiceTests : IDisposable
{
    private readonly string _auditDir = Path.Combine(Path.GetTempPath(), "sss-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SessionService _svc;
    private static readonly UserIdentity Me = new(MockSessionProvider.DemoSid, "DEMO\\me");
    private static readonly UserIdentity Other = new(MockSessionProvider.OtherSid, "DEMO\\other");

    public SessionServiceTests()
    {
        var provider = new MockSessionProvider(TimeProvider.System, new QualityEvaluator(new QualityThresholds()));
        _svc = new SessionService(provider, new AuditLog(_auditDir, TimeProvider.System), TimeProvider.System);
    }

    public void Dispose()
    {
        if (Directory.Exists(_auditDir)) Directory.Delete(_auditDir, true);
    }

    [Fact]
    public async Task User_only_sees_own_sessions()
    {
        var mine = await _svc.GetAsync(Me, TimeSpan.FromDays(30), default);
        var theirs = await _svc.GetAsync(Other, TimeSpan.FromDays(30), default);

        Assert.Equal(3, mine.Live.Count);
        Assert.Equal(2, theirs.Live.Count);
        Assert.Empty(mine.Live.Select(s => s.Uid).Intersect(theirs.Live.Select(s => s.Uid)));
        Assert.Empty(mine.History.Select(s => s.Uid).Intersect(theirs.History.Select(s => s.Uid)));
    }

    [Fact]
    public async Task Logoff_of_foreign_session_is_rejected_and_has_no_effect()
    {
        var foreign = (await _svc.GetAsync(Other, TimeSpan.Zero, default)).Live[0].Uid;

        var result = await _svc.LogoffAsync(Me, LogoffScope.Selected, [foreign], "10.0.0.1", default);

        Assert.Empty(result.LoggedOff);
        Assert.Equal([foreign], result.Rejected);
        Assert.Equal(2, (await _svc.GetAsync(Other, TimeSpan.Zero, default)).Live.Count);
        Assert.Contains("rejected", File.ReadAllText(Directory.GetFiles(_auditDir).Single()));
    }

    [Fact]
    public async Task Logoff_own_session_moves_it_to_history()
    {
        var uid = (await _svc.GetAsync(Me, TimeSpan.Zero, default)).Live[0].Uid;

        var result = await _svc.LogoffAsync(Me, LogoffScope.Selected, [uid], null, default);

        Assert.Equal([uid], result.LoggedOff);
        var after = await _svc.GetAsync(Me, TimeSpan.FromHours(1), default);
        Assert.DoesNotContain(after.Live, s => s.Uid == uid);
        Assert.Contains(after.History, s => s.Uid == uid);
    }

    [Fact]
    public async Task Bulk_disconnected_only_touches_disconnected_sessions()
    {
        var result = await _svc.LogoffAsync(Me, LogoffScope.Disconnected, null, null, default);

        Assert.Single(result.LoggedOff);
        var after = await _svc.GetAsync(Me, TimeSpan.Zero, default);
        Assert.Equal(2, after.Live.Count);
        Assert.All(after.Live, s => Assert.Equal(SessionState.Active, s.State));
    }

    [Fact]
    public async Task Bulk_all_logs_off_everything_of_the_user_only()
    {
        await _svc.LogoffAsync(Me, LogoffScope.All, null, null, default);

        Assert.Empty((await _svc.GetAsync(Me, TimeSpan.Zero, default)).Live);
        Assert.Equal(2, (await _svc.GetAsync(Other, TimeSpan.Zero, default)).Live.Count);
    }

    [Fact]
    public async Task History_respects_range()
    {
        var hour = await _svc.GetAsync(Me, TimeSpan.FromHours(1), default);
        var month = await _svc.GetAsync(Me, TimeSpan.FromDays(30), default);
        Assert.True(month.History.Count >= hour.History.Count);
        Assert.NotEmpty(month.History);
    }
}

public class QualityEvaluatorTests
{
    private readonly QualityEvaluator _q = new(new QualityThresholds());

    [Theory]
    [InlineData(40, 5000, 20, 2000, Health.Good, Health.Good)]
    [InlineData(100, 5000, 20, 2000, Health.Warning, Health.Good)]
    [InlineData(40, 300, 20, 2000, Health.Critical, Health.Good)]
    [InlineData(40, 5000, 100, 2000, Health.Good, Health.Critical)]
    [InlineData(40, 5000, 20, 7500, Health.Good, Health.Warning)]
    public void Rates_frontend_and_backend(int rtt, int kbps, int logon, int load, Health fe, Health be)
    {
        var r = _q.Evaluate(rtt, kbps, logon, load);
        Assert.Equal(fe, r.Frontend);
        Assert.Equal(be, r.Backend);
    }

    [Fact]
    public void Missing_metrics_are_unknown_and_known_ones_still_count()
    {
        Assert.Equal(Health.Unknown, _q.Evaluate(null, null, null, null).Frontend);
        Assert.Equal(Health.Critical, _q.Evaluate(200, null, null, null).Frontend);
    }
}

public class CsvWriterTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    public void Escapes_values(string input, string expected) => Assert.Equal(expected, CsvWriter.Escape(input));

    [Fact]
    public void Audit_files_can_be_named_per_server_for_a_shared_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sss-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            new AuditLog(dir, TimeProvider.System, perServerFiles: true)
                .Write(new AuditEntry("logoff", new Sss.Web.Models.UserIdentity("CORP\\a", "S-1-5-21-1"), "1", "ok", null));
            var file = Assert.Single(Directory.GetFiles(dir));
            Assert.EndsWith($"-{Environment.MachineName}.jsonl", file);
            Assert.Contains($"\"server\":\"{Environment.MachineName}\"", File.ReadAllText(file));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Misplaced_configuration_keys_are_reported_with_a_hint()
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sss:Provider"] = "Citrix",
            ["Sss:UseHttps"] = "false",
            ["Sss:Citrix:Monitor:UseHttps"] = "false",
            ["Sss:Citrix:Controllers:0"] = "ddc",
            ["Sss:PathBase"] = "/sss",
            ["Sss:Citrix:Wokers"] = "3",
        }).Build();

        var unknown = Sss.Web.Services.ConfigCheck.UnknownKeys(cfg.GetSection("Sss"));

        Assert.Equal(2, unknown.Count);
        Assert.Contains(unknown, u => u.StartsWith("Sss:UseHttps (did you mean Sss:Citrix:Monitor:UseHttps?") );
        Assert.Contains(unknown, u => u == "Sss:Citrix:Wokers" || u.StartsWith("Sss:Citrix:Wokers"));
    }

    private static ProfileInfo Profile(string type = "FSLogix", int? size = 6000, int? used = null, int? max = null, ProfileNode[]? tree = null,
        ProfileRedirect[]? redirected = null, ProfileExclusion[]? exclusions = null, bool temporary = false, int? localProfiles = null) =>
        new("vda", type, true, null, null, null, used, max, false, temporary, false, size, false, 3, [],
            tree ?? [], [], redirected ?? [], [], exclusions ?? [], localProfiles, []);

    [Fact]
    public void Advisor_flags_a_nearly_full_container_and_a_temporary_profile()
    {
        var hints = ProfileAdvisor.Analyze(Profile(used: 29000, max: 30000, temporary: true));

        Assert.Contains(hints, h => h.Id == "containerFull" && h.Level == "crit" && h.Args!["pct"] == "97");
        Assert.Contains(hints, h => h.Id == "tempProfile" && h.Level == "crit");
    }

    [Fact]
    public void Advisor_points_at_heavy_caches_and_unredirected_user_data()
    {
        var hints = ProfileAdvisor.Analyze(Profile(size: 9000, tree:
        [
            new("AppData", 7000), new(@"AppData\Local\Microsoft\Outlook", 3000), new("Documents", 2500), new("Downloads", 100),
        ]));

        Assert.Contains(hints, h => h.Id == "cacheOutlook" && h.Args!["share"] == "33");
        Assert.Contains(hints, h => h.Id == "userDataLocal" && h.Args!["path"] == "Documents");
        Assert.Contains(hints, h => h.Id == "appdataDominates");
        Assert.DoesNotContain(hints, h => h.Args?.GetValueOrDefault("path") == "Downloads");
    }

    [Fact]
    public void Advisor_does_not_call_redirected_data_local_and_knows_roaming_and_upm_settings()
    {
        var redirected = ProfileAdvisor.Analyze(Profile(tree: [new("Documents", 3000)], redirected: [new("Documents", @"\\srv\home")]));
        Assert.DoesNotContain(redirected, h => h.Id == "userDataLocal");

        var roaming = ProfileAdvisor.Analyze(Profile("Roaming", size: 1500));
        Assert.Contains(roaming, h => h.Id == "roamingLarge");
        Assert.Contains(roaming, h => h.Id == "roamingNoRedirection");

        var upm = ProfileAdvisor.Analyze(Profile("Citrix UPM", size: 3000));
        Assert.Contains(upm, h => h.Id == "upmNoExclusions");
        var withExclusions = ProfileAdvisor.Analyze(Profile("Citrix UPM", size: 3000, exclusions: [new("Citrix UPM ExclusionListDir", "AppData")]));
        Assert.DoesNotContain(withExclusions, h => h.Id == "upmNoExclusions");

        Assert.Contains(ProfileAdvisor.Analyze(Profile(localProfiles: 25)), h => h.Id == "manyLocalProfiles");
    }
}
