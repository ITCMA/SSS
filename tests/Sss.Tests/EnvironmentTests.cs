using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Sss.Web;
using Sss.Web.Citrix;
using Sss.Web.Models;
using Sss.Web.Services;

namespace Sss.Tests;

public class ClientVersionTests
{
    [Theory]
    [InlineData("26.7.1.4", "26.3.20.31", 1)]
    [InlineData("26.09.0", "26.9.0", 0)]              // leading zero
    [InlineData("26.09.0.17", "26.9.0", 1)]           // a missing part counts as 0
    [InlineData("25.11.10.50", "26.3.0", -1)]
    [InlineData("26.10.0.31", "26.9.0", 1)]           // 10 is more than 9, not text order
    [InlineData("2507.1", "2402.4", 1)]
    public void Versions_compare_numerically(string a, string b, int expected) =>
        Assert.Equal(expected, ClientVersions.Compare(ClientVersions.Parse(a)!, ClientVersions.Parse(b)!));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    public void Unreadable_versions_are_never_outdated(string? v) =>
        Assert.False(ClientVersions.IsOutdated("Windows", v, new Dictionary<string, string> { ["*"] = "26.3.0" }));

    [Fact]
    public void The_minimum_is_per_platform_with_a_star_fallback_and_empty_means_no_check()
    {
        var min = new Dictionary<string, string> { ["windows"] = "26.3.0", ["*"] = "26.9.0" };
        Assert.True(ClientVersions.IsOutdated("Windows", "26.1.0", min));
        Assert.False(ClientVersions.IsOutdated("Windows", "26.3.20.31", min));
        Assert.True(ClientVersions.IsOutdated("iOS", "26.3.0", min));      // fallback minimum
        Assert.False(ClientVersions.IsOutdated("Mac", "26.09.0.17", min));
        Assert.False(ClientVersions.IsOutdated("Windows", "1.0", new Dictionary<string, string>()));
        Assert.False(ClientVersions.IsOutdated("Linux", "1.0", new Dictionary<string, string> { ["Windows"] = "26.3.0" }));   // no minimum for that platform
    }
}

public class EnvironmentServiceTests
{
    private static MonitorSession S(string client, string? ip, string? version, string? platform = "Windows", long user = 1) =>
        new(Guid.NewGuid().ToString(), null, null, true, null, null, null, client, ip, null, ClientVersion: version, ClientPlatform: platform, UserId: user);

    private static EnvironmentService Make(Dictionary<string, string>? min = null) =>
        new(new SssOptions { Environment = new EnvironmentOptions { MinClientVersion = min ?? [] } }, null, TimeProvider.System);

    [Fact]
    public void Versions_are_counted_per_version_and_platform_with_devices_apart_from_sessions()
    {
        var sessions = new[]
        {
            S("PC1", "10.20.1.1", "26.7.1.4"), S("PC1", "10.20.1.1", "26.7.1.4"), S("PC2", "10.20.1.2", "26.7.1.4", user: 2),
            S("PC3", "10.30.1.1", "25.11.10.50", user: 3), S("MAC1", "10.30.1.2", "26.09.0.17", "Mac", 4), S("PC4", "10.30.1.3", null, null, 5),
        };
        var v = Make(new() { ["Windows"] = "26.3.0" }).Build(sessions, []);

        Assert.Equal((6, 5, 5), (v.Sessions, v.Devices, v.Users));
        var top = v.Versions[0];
        Assert.Equal(("26.7.1.4", "Windows", 2, 3, false), (top.Version, top.Platform, top.Devices, top.Sessions, top.Outdated));
        Assert.True(v.Versions.Single(x => x.Version == "25.11.10.50").Outdated);
        Assert.False(v.Versions.Single(x => x.Platform == "Mac").Outdated);          // no minimum for the Mac
        Assert.Equal(1, v.Outdated);
        Assert.Equal(1, v.WithoutVersion);
        Assert.True(v.VersionAvailable);
        Assert.False(v.LocationsConfigured);
        Assert.Empty(v.Locations);
    }

    [Fact]
    public void Without_any_version_the_view_says_so()
    {
        var v = Make().Build([S("PC1", "10.0.0.1", null)], []);
        Assert.False(v.VersionAvailable);
        Assert.Empty(v.Versions);
    }

    [Fact]
    public void Locations_use_the_most_specific_network_and_keep_the_rest_unassigned()
    {
        LocationOptions[] locs =
        [
            new() { Name = "Berlin", Networks = ["10.20.0.0/16"] },
            new() { Name = "Berlin lab", Networks = ["10.20.5.0/24", "10.20.6.7"] },
            new() { Name = "Broken", Networks = ["not-a-network"] },
        ];
        var sessions = new[]
        {
            S("A", "10.20.1.1", "1"), S("B", "10.20.5.9", "1"), S("C", "10.20.6.7", "1", user: 2), S("D", "192.0.2.1", "1", user: 3), S("E", null, "1", user: 4),
            S("F", "::ffff:10.20.2.2", "1", user: 5),
        };
        var v = Make().Build(sessions, locs);

        Assert.True(v.LocationsConfigured);
        var byName = v.Locations.ToDictionary(l => l.Name);
        Assert.Equal(2, byName["Berlin"].Sessions);        // A and the IPv4-mapped F
        Assert.Equal(2, byName["Berlin lab"].Sessions);    // B (the /24 wins over the /16) and C (single address)
        Assert.Equal(2, byName[""].Sessions);              // D and E: not assigned
        Assert.Equal("", v.Locations[^1].Name);            // the unassigned row comes last
        Assert.DoesNotContain(v.Locations, l => l.Name == "Broken");
    }

    [Fact]
    public async Task The_demo_view_has_versions_and_locations_and_is_cached()
    {
        var svc = new EnvironmentService(new SssOptions(), null, TimeProvider.System, demo: true);
        var a = await svc.GetAsync(default);
        var b = await svc.GetAsync(default);
        Assert.Same(a, b);
        Assert.True(a.Demo);
        Assert.True(a.Versions.Count >= 5);
        Assert.True(a.LocationsConfigured);
    }
}

public class ClientFieldsMonitorTests
{
    private sealed class Handler(string metadata) : HttpMessageHandler
    {
        public List<string> Sessions { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("$metadata")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(metadata, Encoding.UTF8, "application/xml") });
            if (path.EndsWith("/Sessions")) Sessions.Add(Uri.UnescapeDataString(request.RequestUri.Query));
            var body = path.EndsWith("/Sessions")
                ? """{"value":[{"SessionKey":"k1","UserId":42,"SessionType":0,"CurrentConnection":{"ClientName":"PC1","ClientAddress":"10.20.1.1","ClientVersion":"26.7.1.4","ClientPlatform":"Windows"}},{"SessionKey":"k2","UserId":43,"CurrentConnection":{"ClientName":"PC2"}}]}"""
                : """{"value":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static string Metadata(string connectionProps) =>
        $"""<edmx:Edmx xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx" Version="4.0"><edmx:DataServices><Schema xmlns="http://docs.oasis-open.org/odata/ns/edm" Namespace="Citrix"><EntityType Name="Connection">{connectionProps}</EntityType></Schema></edmx:DataServices></edmx:Edmx>""";

    private static (ODataMonitorClient Client, Handler Handler) Make(string metadata)
    {
        var o = new CitrixOptions { Controllers = ["ddc.demo.local"] };
        var h = new Handler(metadata);
        return (new ODataMonitorClient(new HttpClient(h) { BaseAddress = ODataMonitorClient.BuildBaseAddress(o) }, o), h);
    }

    [Fact]
    public async Task Version_and_platform_are_requested_and_read_when_the_site_delivers_them()
    {
        var (c, h) = Make(Metadata("""<Property Name="ClientName" Type="Edm.String"/><Property Name="ClientVersion" Type="Edm.String"/><Property Name="ClientPlatform" Type="Edm.String"/>"""));
        var all = await c.GetAllLiveAsync(default);

        Assert.Contains("ClientName,ClientAddress,ClientVersion,ClientPlatform", h.Sessions.Single());
        Assert.Contains("EndDate eq null", h.Sessions.Single());
        var one = all.Single(s => s.SessionKey == "k1");
        Assert.Equal(("26.7.1.4", "Windows", 42L), (one.ClientVersion, one.ClientPlatform, one.UserId));
        Assert.Null(all.Single(s => s.SessionKey == "k2").ClientVersion);
    }

    [Fact]
    public async Task A_site_without_the_properties_is_not_asked_for_them()
    {
        var (c, h) = Make(Metadata("""<Property Name="ClientName" Type="Edm.String"/>"""));
        await c.GetAllLiveAsync(default);
        Assert.DoesNotContain("ClientVersion", h.Sessions.Single());
        Assert.DoesNotContain("ClientPlatform", h.Sessions.Single());
    }

    [Fact]
    public async Task An_unreadable_metadata_document_only_costs_the_extra_fields()
    {
        var (c, h) = Make("this is not xml");
        var all = await c.GetAllLiveAsync(default);
        Assert.Equal(2, all.Count);
        Assert.DoesNotContain("ClientVersion", h.Sessions.Single());
    }
}

public class EnvironmentConfigTests
{
    private static string[] Errors(string json)
    {
        var sss = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build().GetSection("Sss");
        return ConfigValidator.Validate(sss.Get<SssOptions>() ?? new SssOptions(), sss, false).Where(f => f.Level == "error").Select(f => f.Message).ToArray();
    }

    [Theory]
    [InlineData("""{"Sss":{"Environment":{"Locations":[{"Name":"","Networks":["10.0.0.0/8"]}]}}}""", "name")]
    [InlineData("""{"Sss":{"Environment":{"Locations":[{"Name":"HQ","Networks":[]}]}}}""", "no networks")]
    [InlineData("""{"Sss":{"Environment":{"Locations":[{"Name":"HQ","Networks":["10.0.0.0/40"]}]}}}""", "not a network")]
    [InlineData("""{"Sss":{"Environment":{"MinClientVersion":{"Windows":"latest"}}}}""", "not a version")]
    public void Wrong_environment_settings_are_errors(string json, string part) =>
        Assert.Contains(Errors(json), m => m.Contains(part, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Valid_environment_settings_pass() =>
        Assert.Empty(Errors("""{"Sss":{"Environment":{"Locations":[{"Name":"HQ","Networks":["10.0.0.0/8","192.0.2.7"]}],"MinClientVersion":{"Windows":"26.3.0","*":"26.9.0"}}}}"""));
}

public class OutdatedHintProviderTests
{
    private sealed class OneBroker : IBrokerClient
    {
        public Task<IReadOnlyList<DiscoveredController>> DiscoverAsync(string via, CancellationToken ct) => Task.FromResult<IReadOnlyList<DiscoveredController>>([]);
        public Task<IReadOnlyList<BrokerSession>> ListAsync(string sid, CancellationToken ct) => Task.FromResult<IReadOnlyList<BrokerSession>>(
            [new BrokerSession("101", "11111111-1111-1111-1111-111111111111", "Active", "Desktop", "DG", "vdi-01", DateTimeOffset.UtcNow, null, "PC1", "10.0.0.5", [])]);
        public Task<(IReadOnlyList<BrokerSession> Sessions, bool Truncated)> ListAllAsync(string? f, int max, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrokerLogoffResult> LogoffAsync(string sid, IReadOnlyCollection<string> uids, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> MessageAsync(string sid, IReadOnlyCollection<string> uids, string title, string text, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrokerProcessList> ProcessesAsync(string sid, string uid, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> EndProcessAsync(string sid, string uid, int processId, bool force, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProfileInfo> ProfileAsync(string sid, string uid, int scanSeconds, CancellationToken ct) => throw new NotSupportedException();
        public Task PingAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class OneMonitor(string version) : IMonitorClient
    {
        public Task<IReadOnlyList<MonitorSession>> GetLiveAsync(string sid, CancellationToken ct) => Task.FromResult<IReadOnlyList<MonitorSession>>(
            [new MonitorSession("11111111-1111-1111-1111-111111111111", "vdi-01", "DG", true, null, null, 20, "PC1", "10.0.0.5", 30, ClientVersion: version, ClientPlatform: "Windows")]);
        public Task<IReadOnlyList<MonitorSession>> GetEndedAsync(string sid, DateTimeOffset since, CancellationToken ct) => Task.FromResult<IReadOnlyList<MonitorSession>>([]);
        public Task<IReadOnlyList<MonitorUser>> FindUsersAsync(string query, int max, CancellationToken ct) => Task.FromResult<IReadOnlyList<MonitorUser>>([]);
        public Task<SessionDetails?> GetDetailsAsync(string sid, string key, bool full, CancellationToken ct) => Task.FromResult<SessionDetails?>(null);
        public Task PingAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static Task<SessionQueryResult> Run(string version, EnvironmentOptions env) =>
        new CitrixSessionProvider(new OneBroker(), new OneMonitor(version), new QualityEvaluator(new QualityThresholds()), TimeProvider.System,
            NullLogger<CitrixSessionProvider>.Instance, env).GetSessionsAsync(new UserIdentity("S-1-5-21-1-1-1-1001", "DEMO\\a"), TimeSpan.FromHours(1), default);

    [Fact]
    public async Task The_user_gets_a_hint_only_when_a_minimum_is_set_and_the_own_client_is_below_it()
    {
        var env = new EnvironmentOptions { MinClientVersion = new() { ["Windows"] = "26.3.0" } };
        var old = (await Run("25.11.10.50", env)).Sessions.Single();
        Assert.True(old.ClientOutdated);
        Assert.Equal("25.11.10.50", old.ClientVersion);
        Assert.False((await Run("26.7.1.4", env)).Sessions.Single().ClientOutdated);
        Assert.False((await Run("25.11.10.50", new EnvironmentOptions())).Sessions.Single().ClientOutdated);                       // no minimum
        Assert.False((await Run("25.11.10.50", new EnvironmentOptions { MinClientVersion = env.MinClientVersion, HintOutdatedClient = false })).Sessions.Single().ClientOutdated);
    }
}

[Collection("Api")]
public class EnvironmentApiTests : IClassFixture<HelpdeskOnFactory>, IClassFixture<AuditorFactory>, IClassFixture<Level1Factory>
{
    private readonly HttpClient _admin, _auditor;
    public EnvironmentApiTests(HelpdeskOnFactory a, AuditorFactory b, Level1Factory c) { _admin = a.NewClient(); _auditor = b.NewClient(); }

    [Fact]
    public async Task The_admin_reads_the_environment_and_the_auditor_does_not()
    {
        var env = await (await _admin.GetAsync("/api/helpdesk/environment")).Json();
        Assert.True(env.GetProperty("sessions").GetInt32() > 0);
        Assert.True(env.GetProperty("versions").GetArrayLength() > 3);
        Assert.True(env.GetProperty("demo").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await _auditor.GetAsync("/api/helpdesk/environment")).StatusCode);
    }

    [Fact]
    public async Task Sessions_carry_the_client_version_and_the_outdated_flag()
    {
        var mine = await (await _admin.GetAsync("/api/sessions?range=24h")).Json();
        var first = mine.GetProperty("live")[0];
        Assert.Equal("26.7.1.4", first.GetProperty("clientVersion").GetString());
        Assert.True(first.TryGetProperty("clientOutdated", out _));
    }
}
