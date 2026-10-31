using System.Diagnostics;
using System.Net;
using System.Text;
using Sss.Web;
using Sss.Web.Citrix;
using Sss.Web.Models;
using Sss.Web.Providers;
using Sss.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sss.Tests;

public class CitrixSessionProviderTests
{
    private static readonly UserIdentity Me = new("S-1-5-21-1-1-1-1001", "DEMO\\alice");

    private sealed class FakeBroker(params BrokerSession[] sessions) : IBrokerClient
    {
        public List<string> LoggedOff { get; } = [];
        public Task<IReadOnlyList<BrokerSession>> ListAsync(string sid, CancellationToken ct) => Task.FromResult<IReadOnlyList<BrokerSession>>(sessions);
        public Task<(IReadOnlyList<BrokerSession> Sessions, bool Truncated)> ListAllAsync(string? userFilter, int max, CancellationToken ct) =>
            Task.FromResult<(IReadOnlyList<BrokerSession>, bool)>((sessions.Where(s => string.IsNullOrEmpty(userFilter) || (s.UserName ?? "").Contains(userFilter, StringComparison.OrdinalIgnoreCase)).Take(max).ToList(), sessions.Length > max));
        public List<string> Messaged { get; } = [];
        public Task<IReadOnlyList<string>> MessageAsync(string sid, IReadOnlyCollection<string> uids, string title, string text, CancellationToken ct)
        {
            Messaged.AddRange(uids);
            return Task.FromResult<IReadOnlyList<string>>(uids.ToList());
        }
        public Task PingAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<BrokerLogoffResult> LogoffAsync(string sid, IReadOnlyCollection<string> uids, CancellationToken ct)
        {
            LoggedOff.AddRange(uids);
            return Task.FromResult(new BrokerLogoffResult(uids.ToList(), []));
        }
    }

    private sealed class FakeMonitor(IReadOnlyList<MonitorSession>? live = null, IReadOnlyList<MonitorSession>? ended = null, bool fail = false) : IMonitorClient
    {
        public int Calls;
        public Task<IReadOnlyList<MonitorSession>> GetLiveAsync(string sid, CancellationToken ct)
        {
            Calls++;
            return fail ? throw new ProviderUnavailableException("down") : Task.FromResult(live ?? []);
        }
        public Task<IReadOnlyList<MonitorUser>> FindUsersAsync(string query, int max, CancellationToken ct) =>
            fail ? throw new ProviderUnavailableException("down") : Task.FromResult<IReadOnlyList<MonitorUser>>(Users);
        public List<MonitorUser> Users { get; } = [];
        public Task<SessionDetails?> GetDetailsAsync(string sid, string sessionKey, bool full, CancellationToken ct) => Task.FromResult<SessionDetails?>(null);
        public Task PingAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<MonitorSession>> GetEndedAsync(string sid, DateTimeOffset since, CancellationToken ct)
        {
            Calls++;
            return fail ? throw new ProviderUnavailableException("down") : Task.FromResult(ended ?? []);
        }
    }

    private static readonly BrokerSession Desktop = new("101", "11111111-1111-1111-1111-111111111111", "Active", "Desktop", "DG-Desktops",
        "vdi-01", DateTimeOffset.UtcNow.AddHours(-3), 90, "LAPTOP-A", "10.0.0.5", []);
    private static readonly BrokerSession Apps = new("102", null, "Disconnected", "Application", "DG-Apps",
        "rds-01", DateTimeOffset.UtcNow.AddHours(-20), null, "", "", ["Excel", "Word"]);

    private static CitrixSessionProvider Make(IBrokerClient b, IMonitorClient? m) =>
        new(b, m, new QualityEvaluator(new QualityThresholds()), TimeProvider.System, NullLogger<CitrixSessionProvider>.Instance);

    [Fact]
    public async Task Maps_live_sessions_and_enriches_with_monitor_metrics()
    {
        var mon = new FakeMonitor(live: [new MonitorSession("11111111-1111-1111-1111-111111111111", "vdi-01", "DG", true, null, null, 25, null, null, 170)]);
        var r = await Make(new FakeBroker(Desktop, Apps), mon).GetSessionsAsync(Me, TimeSpan.FromHours(24), default);

        var d = r.Sessions.Single(s => s.Uid == "101");
        Assert.Equal(SessionState.Active, d.State);
        Assert.Equal("Desktop", d.ResourceType);
        Assert.Equal(170, d.Quality.IcaRttMs);
        Assert.Equal(Health.Critical, d.Quality.Frontend);
        Assert.Equal(Health.Good, d.Quality.Backend);

        var a = r.Sessions.Single(s => s.Uid == "102");
        Assert.Equal(SessionState.Disconnected, a.State);
        Assert.Equal("Excel, Word", a.ResourceName);
        Assert.Equal("App", a.ResourceType);
        Assert.Null(a.ClientName);
        Assert.Equal(Health.Unknown, a.Quality.Frontend);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public async Task Ended_sessions_come_from_monitor()
    {
        var ended = new MonitorSession("k1", "vdi-09", "DG-Desktops", true, DateTimeOffset.UtcNow.AddHours(-9), DateTimeOffset.UtcNow.AddHours(-2), 40, "PC", "1.2.3.4", null);
        var r = await Make(new FakeBroker(), new FakeMonitor(ended: [ended])).GetSessionsAsync(Me, TimeSpan.FromDays(1), default);

        var s = Assert.Single(r.Sessions);
        Assert.Equal(SessionState.Ended, s.State);
        Assert.Equal("k1", s.Uid);
    }

    [Fact]
    public async Task Monitor_failure_degrades_to_warnings_but_keeps_live_sessions()
    {
        var r = await Make(new FakeBroker(Desktop), new FakeMonitor(fail: true)).GetSessionsAsync(Me, TimeSpan.FromDays(1), default);

        Assert.Single(r.Sessions);
        Assert.Contains(CitrixSessionProvider.WarnHistory, r.Warnings);
        Assert.Contains(CitrixSessionProvider.WarnQuality, r.Warnings);
    }

    [Fact]
    public async Task Zero_window_never_touches_monitor()
    {
        var mon = new FakeMonitor();
        await Make(new FakeBroker(Desktop), mon).GetSessionsAsync(Me, TimeSpan.Zero, default);
        Assert.Equal(0, mon.Calls);
    }

    [Fact]
    public async Task Works_without_monitor()
    {
        var r = await Make(new FakeBroker(Desktop), null).GetSessionsAsync(Me, TimeSpan.FromDays(1), default);
        Assert.Single(r.Sessions);
        Assert.Empty(r.Warnings);
    }
}

public class ODataMonitorClientTests
{
    private const string Sid = "S-1-5-21-1-1-1-1001";

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var body = respond(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static (ODataMonitorClient Client, StubHandler Handler) Make(Func<HttpRequestMessage, string> respond)
    {
        var o = new CitrixOptions { Controllers = ["ddc.demo.local"] };
        var h = new StubHandler(respond);
        return (new ODataMonitorClient(new HttpClient(h) { BaseAddress = ODataMonitorClient.BuildBaseAddress(o) }, o), h);
    }

    private static string Decoded(Uri u) => Uri.UnescapeDataString(u.Query);

    private const string Key = "11111111-1111-1111-1111-111111111111";

    private static string DetailsBody(Uri u) => u.AbsolutePath switch
    {
        var p when p.EndsWith("/Users") => """{"value":[{"Id":4320}]}""",
        var p when p.EndsWith("/Sessions") => """
            {"value":[{"SessionKey":"11111111-1111-1111-1111-111111111111","Id":"x","StartDate":"2026-10-29T08:00:00Z","LogOnDuration":38000,"ProfileLoadDuration":4200,"EndDate":null,
              "Machine":{"DnsName":"vdi-01.demo.local","OSType":"Windows 11","HypervisorConnectionName":"secret-hv","DesktopGroup":{"Name":"DG","DeliveryType":"DesktopsOnly","Uid":7}},
              "CurrentConnection":{"ClientName":"LAPTOP-A","ClientVersion":"2507.1"},
              "SessionMetrics":[{"CollectedDate":"2026-10-29T08:01:00Z","IcaRttMS":300,"OutputBandwidthAvailable":2000000,"DcLatency":0},
                                {"CollectedDate":"2026-10-29T08:03:00Z","IcaRttMS":42,"OutputBandwidthAvailable":105379594,"DcLatency":0}]}]}
            """,
        var p when p.EndsWith("/Connections") => """{"value":[{"Id":1,"ClientName":"LAPTOP-A","BrokeringDuration":850}]}""",
        _ => """{"value":[]}""",
    };

    [Fact]
    public async Task Details_are_looked_up_by_user_id_and_session_key_and_grouped()
    {
        var (c, h) = Make(r => DetailsBody(r.RequestUri!));

        var d = (await c.GetDetailsAsync(Sid, Key, full: false, default))!;

        var sessionQuery = Decoded(h.Requests.First(u => u.AbsolutePath.EndsWith("/Sessions")));
        Assert.Contains("UserId eq 4320 and SessionKey eq " + Key, sessionQuery);
        Assert.Contains(d.Groups, g => g.Id == "logon" && g.Items.Any(i => i.Key == "LogOnDuration"));
        Assert.Contains(d.Groups, g => g.Id == "client" && g.Items.Any(i => i.Key == "ClientVersion"));
        Assert.Contains(d.Groups, g => g.Id == "technical" && g.Items.Any(i => i.Key == "SessionKey"));
        Assert.Equal("42", d.Groups.Single(g => g.Id == "network").Items.Single(i => i.Key == "IcaRttMS").Value);   // latest sample
        Assert.Equal("105379", d.Groups.Single(g => g.Id == "network").Items.Single(i => i.Key == "OutputBandwidthAvailable").Value);
        Assert.Equal("connections", Assert.Single(d.Tables).Id);
    }

    [Fact]
    public async Task Details_series_are_ascending_converted_and_skip_all_zero_columns()
    {
        var (c, _) = Make(r => DetailsBody(r.RequestUri!));
        var d = (await c.GetDetailsAsync(Sid, Key, false, default))!;

        var rtt = d.Series.Single(x => x.Name == "IcaRttMS");
        Assert.Equal([300d, 42d], rtt.Points.Select(p => p[1]).ToArray());
        Assert.True(rtt.Points[0][0] < rtt.Points[1][0]);
        Assert.Equal("kbit/s", d.Series.Single(x => x.Name == "OutputBandwidthAvailable").Unit);
        Assert.DoesNotContain(d.Series, x => x.Name == "DcLatency");
    }

    [Fact]
    public async Task Details_hide_infrastructure_properties_unless_full()
    {
        var (c, _) = Make(r => DetailsBody(r.RequestUri!));
        string[] Keys(SessionDetails d) => d.Groups.Single(g => g.Id == "machine").Items.Select(i => i.Key).ToArray();

        Assert.DoesNotContain("HypervisorConnectionName", Keys((await c.GetDetailsAsync(Sid, Key, false, default))!));
        Assert.Contains("HypervisorConnectionName", Keys((await c.GetDetailsAsync(Sid, Key, true, default))!));
    }

    [Fact]
    public async Task Details_of_an_unknown_session_are_null_and_bad_keys_are_rejected()
    {
        var (c, _) = Make(r => r.RequestUri!.AbsolutePath.EndsWith("/Users") ? """{"value":[{"Id":4320}]}""" : """{"value":[]}""");
        Assert.Null(await c.GetDetailsAsync(Sid, Key, false, default));
        await Assert.ThrowsAsync<ArgumentException>(() => c.GetDetailsAsync(Sid, "1 or 1 eq 1", false, default));
    }

    private sealed class PartsMissingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var optional = request.RequestUri!.AbsolutePath.EndsWith("/ConnectionFailureLogs");
            return Task.FromResult(new HttpResponseMessage(optional ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            { Content = new StringContent(DetailsBody(request.RequestUri!), Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task A_missing_optional_part_is_reported_but_does_not_fail_the_details()
    {
        var o = new CitrixOptions { Controllers = ["ddc.demo.local"] };
        var c = new ODataMonitorClient(new HttpClient(new PartsMissingHandler()) { BaseAddress = ODataMonitorClient.BuildBaseAddress(o) }, o);
        var d = (await c.GetDetailsAsync(Sid, Key, false, default))!;
        Assert.Equal(["failures"], d.Unavailable);
        Assert.NotEmpty(d.Groups);
    }

    [Fact]
    public async Task Live_query_filters_by_resolved_user_id_and_picks_latest_rtt()
    {
        var (c, h) = Make(r => r.RequestUri!.AbsolutePath.EndsWith("/Users")
            ? """{"value":[{"Id":4320}]}"""
            : """
              {"value":[{"SessionKey":"AAAA-1","StartDate":"2026-10-29T08:00:00Z","EndDate":null,"LogOnDuration":38000,"SessionType":0,
                "Machine":{"DnsName":"vdi-01.demo.local","DesktopGroup":{"Name":"DG-Desktops"}},
                "CurrentConnection":{"ClientName":"LAPTOP-A","ClientAddress":"10.0.0.5"},
                "SessionMetrics":[{"CollectedDate":"2026-10-29T08:01:00Z","IcaRttMS":300},{"CollectedDate":"2026-10-29T09:30:00Z","IcaRttMS":42,"OutputBandwidthAvailable":105379594}]}]}
              """);

        var s = Assert.Single(await c.GetLiveAsync(Sid, default));

        Assert.Equal("AAAA-1", s.SessionKey);
        Assert.Equal(42, s.IcaRttMs);
        Assert.Equal(105379, s.BandwidthKbps);   // bit/s -> kbit/s
        Assert.Equal(38, s.LogonSeconds);
        Assert.True(s.IsDesktop);
        Assert.Equal("DG-Desktops", s.DesktopGroup);
        Assert.Equal("LAPTOP-A", s.ClientName);
        Assert.Contains($"Sid eq '{Sid}'", Decoded(h.Requests[0]));
        Assert.Contains("UserId eq 4320 and EndDate eq null", Decoded(h.Requests[1]));
    }

    [Fact]
    public async Task Unknown_user_yields_no_sessions_and_no_second_query()
    {
        var (c, h) = Make(_ => """{"value":[]}""");
        Assert.Empty(await c.GetEndedAsync(Sid, DateTimeOffset.UtcNow.AddDays(-1), default));
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Rejects_malformed_sid_before_any_request()
    {
        var (c, h) = Make(_ => "{}");
        await Assert.ThrowsAsync<ArgumentException>(() => c.GetLiveAsync("S-1-5' or 1 eq 1 or Sid eq '", default));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Follows_next_link_only_on_the_same_host()
    {
        var page = 0;
        var (c, h) = Make(r =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith("/Users")) return """{"value":[{"Id":1}]}""";
            page++;
            return page == 1
                ? """{"value":[{"SessionKey":"a"}],"@odata.nextLink":"http://ddc.demo.local/Citrix/Monitor/OData/v4/Data/Sessions?$skip=1"}"""
                : """{"value":[{"SessionKey":"b"}],"@odata.nextLink":"http://evil.example/steal"}""";
        });

        var list = await c.GetLiveAsync(Sid, default);

        Assert.Equal(["a", "b"], list.Select(s => s.SessionKey).ToArray());
        Assert.DoesNotContain(h.Requests, u => u.Host == "evil.example");
    }

    [Fact]
    public async Task Http_error_becomes_provider_unavailable()
    {
        var o = new CitrixOptions { Controllers = ["ddc"] };
        var client = new ODataMonitorClient(new HttpClient(new FailHandler()) { BaseAddress = ODataMonitorClient.BuildBaseAddress(o) }, o);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => client.GetLiveAsync(Sid, default));
    }

    private sealed class FailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }
}

/// <summary>Runs the real sss-broker-worker.ps1 under PowerShell 7 (pwsh) against fake Broker cmdlets.
/// Silently returns when pwsh is not installed; CI images ship it.</summary>
public class BrokerWorkerPoolTests
{
    private const string Alice = "S-1-5-21-1-1-1-1001";
    private const string Bob = "S-1-5-21-1-1-1-2002";

    private static bool PwshAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pwsh", "-NoLogo -NoProfile -Command exit") { UseShellExecute = false, CreateNoWindow = true });
            p!.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static BrokerWorkerPool Pool() => new(new CitrixOptions
    {
        Controllers = ["ddc1.demo.local", "ddc2.demo.local"],
        PowerShellPath = "pwsh",
        WorkerScript = Path.Combine(AppContext.BaseDirectory, "Citrix", "sss-broker-worker.ps1"),
        SnapinOverride = Path.Combine(AppContext.BaseDirectory, "Fixtures", "FakeBroker.ps1"),
        Workers = 1,
        RequestTimeoutSeconds = 30,
    }, NullLogger<BrokerWorkerPool>.Instance);

    [Fact]
    public async Task Lists_only_the_users_own_sessions_and_maps_fields()
    {
        if (!PwshAvailable()) return;
        using var pool = Pool();

        var list = await pool.ListAsync(Alice, default);

        Assert.Equal(["101", "102"], list.Select(s => s.Uid).Order().ToArray());
        var d = list.Single(s => s.Uid == "101");
        Assert.Equal("Active", d.State);
        Assert.Equal("Desktop", d.Type);
        Assert.Equal("vdi-01.demo.local", d.Machine);
        Assert.Equal(90, d.IdleSeconds);
        Assert.Equal("11111111-1111-1111-1111-111111111111", d.SessionKey);
        var a = list.Single(s => s.Uid == "102");
        Assert.Equal(["Excel", "Word"], a.Apps);
        Assert.Equal("Zürich-PC", a.ClientName); // non-ASCII survives the ASCII-escaped stream
    }

    [Fact]
    public async Task Worker_rejects_logoff_of_foreign_and_malformed_uids()
    {
        if (!PwshAvailable()) return;
        using var pool = Pool();

        var res = await pool.LogoffAsync(Alice, ["101", "201", "1; Remove-Item *"], default);

        Assert.Equal(["101"], res.Accepted);
        Assert.Contains("201", res.Rejected);
        Assert.Contains("1; Remove-Item *", res.Rejected);
    }

    [Fact]
    public async Task Message_reaches_only_the_users_own_sessions()
    {
        if (!PwshAvailable()) return;
        using var pool = Pool();

        var sent = await pool.MessageAsync(Alice, ["101", "201", "1; calc"], "IT Helpdesk", "Please save your work", default);

        Assert.Equal(["101"], sent);
    }

    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("")]
    public async Task Message_text_with_control_characters_or_empty_is_refused(string text)
    {
        using var pool = Pool();
        await Assert.ThrowsAsync<ArgumentException>(() => pool.MessageAsync(Alice, ["101"], "IT Helpdesk", text, default));
    }

    [Fact]
    public async Task ListAll_returns_every_users_sessions_filters_by_name_part_and_reports_truncation()
    {
        if (!PwshAvailable()) return;
        using var pool = Pool();

        var (all, truncated) = await pool.ListAllAsync(null, 100, default);
        Assert.Equal(3, all.Count);
        Assert.False(truncated);
        Assert.Contains(all, s => s.UserSid == Bob && s.UserName == "DEMO\\bob");

        var (bobOnly, _) = await pool.ListAllAsync("BOB", 100, default);   // case-insensitive part of the name
        Assert.Equal("201", Assert.Single(bobOnly).Uid);

        var (capped, wasTruncated) = await pool.ListAllAsync(null, 2, default);
        Assert.Equal(2, capped.Count);
        Assert.True(wasTruncated);
    }

    [Theory]
    [InlineData("x'; Remove-Item *")]
    [InlineData("a|b")]
    public async Task ListAll_rejects_filters_with_special_characters(string filter)
    {
        using var pool = Pool();
        await Assert.ThrowsAsync<ArgumentException>(() => pool.ListAllAsync(filter, 10, default));
    }

    [Fact]
    public async Task Invalid_sid_is_rejected_on_the_dotnet_side()
    {
        using var pool = Pool();
        await Assert.ThrowsAsync<ArgumentException>(() => pool.ListAsync("not-a-sid", default));
    }
}

public class ConfigValidationTests
{
    [Theory]
    [InlineData("ddc; calc")]
    [InlineData("ddc01 -Foo")]
    [InlineData("")]
    public void Controller_names_that_are_not_host_names_are_rejected(string name) =>
        Assert.Throws<InvalidOperationException>(() =>
            new BrokerWorkerPool(new CitrixOptions { Controllers = [name] }, NullLogger<BrokerWorkerPool>.Instance));

    [Fact]
    public void Monitor_field_names_must_be_plain_identifiers()
    {
        var o = new CitrixOptions { Controllers = ["ddc"], Monitor = new MonitorOptions { MetricsDateField = "x desc;$top=1" } };
        Assert.Throws<InvalidOperationException>(() =>
            new ODataMonitorClient(new HttpClient { BaseAddress = new Uri("http://ddc/") }, o));
    }

    [Fact]
    public void Monitor_base_url_must_be_http_or_https()
    {
        var o = new CitrixOptions { Controllers = ["ddc"], Monitor = new MonitorOptions { BaseUrl = "file:///etc/passwd" } };
        Assert.Throws<InvalidOperationException>(() =>
            new ODataMonitorClient(new HttpClient { BaseAddress = new Uri("http://ddc/") }, o));
    }

    [Fact]
    public void Monitor_address_defaults_to_https_and_http_is_an_option()
    {
        var o = new CitrixOptions { Controllers = ["ddc"] };
        Assert.Equal("https://ddc/Citrix/Monitor/OData/v4/Data/", ODataMonitorClient.BuildBaseAddress(o).ToString());
        o.Monitor.UseHttps = false;
        Assert.Equal("http://ddc/Citrix/Monitor/OData/v4/Data/", ODataMonitorClient.BuildBaseAddress(o).ToString());
        o.Monitor.BaseUrl = "https://other/odata";
        Assert.Equal("https://other/odata/", ODataMonitorClient.BuildBaseAddress(o).ToString());
    }

    [Fact]
    public void One_monitor_address_per_controller_unless_configured()
    {
        var o = new CitrixOptions { Controllers = ["ddc1", "ddc2"] };
        Assert.Equal(["https://ddc1/Citrix/Monitor/OData/v4/Data/", "https://ddc2/Citrix/Monitor/OData/v4/Data/"],
            ODataMonitorClient.BuildBaseAddresses(o).Select(u => u.ToString()).ToArray());
        o.Monitor.BaseUrls = ["https://a/odata", "https://b/odata/"];
        Assert.Equal(["https://a/odata/", "https://b/odata/"], ODataMonitorClient.BuildBaseAddresses(o).Select(u => u.ToString()).ToArray());
    }

    [Fact]
    public async Task Monitor_fails_over_to_the_next_controller_and_remembers_it()
    {
        var o = new CitrixOptions { Controllers = ["ddc1", "ddc2"], Monitor = new MonitorOptions { UseHttps = false } };
        var seen = new List<string>();
        var c = new ODataMonitorClient(new HttpClient(new HostHandler(seen, down: "ddc1")) { BaseAddress = ODataMonitorClient.BuildBaseAddress(o) }, o);

        await c.PingAsync(default);
        await c.PingAsync(default);

        Assert.Equal(["ddc1", "ddc2", "ddc2"], seen);
    }

    private sealed class HostHandler(List<string> seen, string down) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            seen.Add(r.RequestUri!.Host);
            return Task.FromResult(r.RequestUri.Host == down
                ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : new HttpResponseMessage { Content = new StringContent("""{"value":[]}""", System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
