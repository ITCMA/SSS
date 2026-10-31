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
}
