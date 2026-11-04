using System.Net;
using Sss.Web.Citrix;
using Sss.Web.Models;
using Xunit;

namespace Sss.Tests;

public class CloudTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static CloudOptions Cloud(string region = "eu") => new() { Enabled = true, Region = region, CustomerId = "democust", ClientId = "demo-client", ClientSecret = "demo-secret" };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Theory]
    [InlineData("eu", "https://api-eu.cloud.com/monitorodata/")]
    [InlineData("us", "https://api.cloud.com/monitorodata/")]
    [InlineData("ap-s", "https://api-ap-s.cloud.com/monitorodata/")]
    [InlineData("jp", "https://api.citrixcloud.jp/monitorodata/")]
    public void The_region_selects_the_monitor_address(string region, string expected) => Assert.Equal(expected, Cloud(region).MonitorBase.AbsoluteUri);

    [Fact]
    public void Incomplete_or_unsafe_settings_are_reported()
    {
        Assert.Null(new CloudOptions().Problem());   // not enabled: nothing to check
        Assert.Null(Cloud().Problem());
        Assert.NotNull(new CloudOptions { Enabled = true }.Problem());
        Assert.NotNull(new CloudOptions { Enabled = true, CustomerId = "a", ClientId = "b", ClientSecret = "c", ApiBase = "http://api.cloud.com" }.Problem());
        Assert.NotNull(new CloudOptions { Enabled = true, CustomerId = "a/../b", ClientId = "b", ClientSecret = "c" }.Problem());
    }

    [Fact]
    public async Task Requests_carry_the_token_and_customer_id_and_the_token_is_fetched_once()
    {
        var stub = new Stub(r => r.RequestUri!.AbsolutePath.EndsWith("/tokens/clients") ? Json("""{"access_token":"tok123","expires_in":"3600"}""") : Json("{}"));
        using var http = new HttpClient(new CloudAuthHandler(Cloud(), stub, TimeProvider.System)) { BaseAddress = Cloud().MonitorBase };
        await http.GetAsync("Sessions");
        await http.GetAsync("Machines");
        var calls = stub.Seen.Where(r => !r.RequestUri!.AbsolutePath.EndsWith("/tokens/clients")).ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, r => Assert.Equal("CWSAuth", r.Headers.Authorization!.Scheme));
        Assert.All(calls, r => Assert.Equal("bearer=tok123", r.Headers.Authorization!.Parameter));
        Assert.All(calls, r => Assert.Equal("democust", r.Headers.GetValues("Citrix-CustomerId").Single()));
        Assert.Single(stub.Seen, r => r.RequestUri!.AbsolutePath.EndsWith("/tokens/clients"));
    }

    [Fact]
    public async Task A_refused_sign_in_is_a_backend_failure_without_the_secret_in_the_message()
    {
        var stub = new Stub(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var http = new HttpClient(new CloudAuthHandler(Cloud(), stub, TimeProvider.System)) { BaseAddress = Cloud().MonitorBase };
        var ex = await Assert.ThrowsAsync<Sss.Web.Providers.ProviderUnavailableException>(() => http.GetAsync("Sessions"));
        Assert.DoesNotContain("demo-secret", ex.Message);
    }

    [Fact]
    public async Task The_token_is_never_sent_to_another_host()
    {
        var stub = new Stub(_ => Json("""{"access_token":"t","expires_in":3600}"""));
        using var http = new HttpClient(new CloudAuthHandler(Cloud(), stub, TimeProvider.System));
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.GetAsync("http://localhost/Citrix/Monitor/OData/v4/Data/Sessions"));
        Assert.Empty(stub.Seen);
    }

    private sealed class FakeMonitor : IMonitorClient
    {
        public static readonly MonitorSession Active = new("k1", "vdi.demo.local", "DG", true, DateTimeOffset.Now, null, 5, "PC1", "10.0.0.1", null, UserId: 7, ConnectionState: 5);
        public static readonly MonitorSession Gone = new("k2", "vdi.demo.local", "DG", true, DateTimeOffset.Now, null, 5, "PC2", "10.0.0.2", null, UserId: 8, ConnectionState: 3);
        public static readonly MonitorSession Away = new("k3", "vdi.demo.local", "DG", false, DateTimeOffset.Now, null, 5, "PC3", "10.0.0.3", null, UserId: 8, ConnectionState: 2);
        public Task<IReadOnlyList<MonitorSession>> GetLiveAsync(string userSid, CancellationToken ct) => Task.FromResult<IReadOnlyList<MonitorSession>>([Active, Gone]);
        public Task<IReadOnlyList<MonitorSession>> GetFarmLiveAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MonitorSession>>([Active, Gone, Away]);
        public Task<IReadOnlyDictionary<long, MonitorUser>> GetUsersByIdAsync(IEnumerable<long> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<long, MonitorUser>>(new Dictionary<long, MonitorUser> { [7] = new("Alex Demo", "S-1-5-21-1-1-1-7"), [8] = new("Sam Demo", "S-1-5-21-1-1-1-8") });
        public Task<IReadOnlyList<MonitorSession>> GetEndedAsync(string userSid, DateTimeOffset since, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<MonitorUser>> FindUsersAsync(string query, int max, CancellationToken ct) => throw new NotSupportedException();
        public Task<SessionDetails?> GetDetailsAsync(string userSid, string sessionKey, bool full, CancellationToken ct) => throw new NotSupportedException();
        public Task PingAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task The_cloud_broker_lists_sessions_from_the_monitor_and_refuses_actions()
    {
        var broker = new CloudBrokerClient(new FakeMonitor());
        var own = await broker.ListAsync("S-1-5-21-1-1-1-7", CancellationToken.None);
        Assert.Equal("k1", Assert.Single(own).Uid);   // the terminated session is not listed

        var (all, truncated) = await broker.ListAllAsync(null, 100, CancellationToken.None);
        Assert.False(truncated);
        Assert.Equal(["Active", "Disconnected"], all.Select(s => s.State).ToArray());
        Assert.Equal("Sam Demo", all.Single(s => s.State == "Disconnected").UserName);
        Assert.Equal("Application", all.Single(s => s.State == "Disconnected").Type);
        Assert.Single((await broker.ListAllAsync("alex", 100, CancellationToken.None)).Sessions);

        await Assert.ThrowsAsync<ActionNotSupportedException>(() => broker.LogoffAsync("S-1", ["k1"], CancellationToken.None));
        await Assert.ThrowsAsync<ActionNotSupportedException>(() => broker.MessageAsync("S-1", ["k1"], "t", "x", CancellationToken.None));
        await Assert.ThrowsAsync<ActionNotSupportedException>(() => broker.ProcessesAsync("S-1", "k1", CancellationToken.None));
    }
}
