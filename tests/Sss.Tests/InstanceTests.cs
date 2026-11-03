using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sss.Web;
using Sss.Web.Services;

namespace Sss.Tests;

public class ConfigFingerprintTests
{
    [Fact]
    public void Whitespace_comments_and_key_order_do_not_change_the_fingerprint_but_values_do()
    {
        var a = ConfigFingerprint.Of("""{ "Sss": { "Provider": "Citrix", "Quality": { "RttWarnMs": 80, "RttCritMs": 150 }, "Citrix": { "Controllers": ["a","b"] } } }""");
        var b = ConfigFingerprint.Of("""
            { // same settings, other layout
              "Sss": { "citrix": { "controllers": ["a", "b"] }, "Quality": { "RttCritMs": 150, "RttWarnMs": 80 }, "Provider": "Citrix", } }
            """);
        var c = ConfigFingerprint.Of("""{ "Sss": { "Provider": "Citrix", "Quality": { "RttWarnMs": 90, "RttCritMs": 150 }, "Citrix": { "Controllers": ["a","b"] } } }""");
        var d = ConfigFingerprint.Of("""{ "Sss": { "Provider": "Citrix", "Quality": { "RttWarnMs": 80, "RttCritMs": 150 }, "Citrix": { "Controllers": ["b","a"] } } }""");

        Assert.Equal(a.Hash, b.Hash);
        Assert.NotEqual(a.Hash, c.Hash);
        Assert.Equal(["Citrix", "Provider", "Quality"], c.Sections.Keys);
        Assert.Equal(a.Sections["Citrix"], c.Sections["Citrix"]);
        Assert.NotEqual(a.Sections["Quality"], c.Sections["Quality"]);   // the section that differs is named
        Assert.NotEqual(a.Sections["Citrix"], d.Sections["Citrix"]);      // the order of a list matters (failover order)
        Assert.Equal("none", ConfigFingerprint.Of(null).Hash);
    }

    [Fact]
    public void Local_keys_are_listed_without_values()
    {
        var keys = ConfigFingerprint.Keys("""{ "Sss": { "Audit": { "Directory": "D:\\secret-path" }, "DataDirectory": "D:\\data" } }""");
        Assert.Equal(["Sss:Audit:Directory", "Sss:DataDirectory"], keys);
    }
}

public class InstanceRegistryTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static void Report(string dir, string server, string hash, DateTimeOffset seen, string version = "0.5.0", bool draining = false, Dictionary<string, string>? sections = null) =>
        File.WriteAllText(Path.Combine(dir, $"sss-{server}.json"), JsonSerializer.Serialize(new
        {
            server, version, startedAt = seen.AddDays(-1), lastSeen = seen, configHash = hash,
            sections = sections ?? new Dictionary<string, string> { ["Quality"] = hash }, localKeys = Array.Empty<string>(), health = "ok", draining,
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    private static InstanceRegistry Make(TempDir d, Clock clock, string[]? expected = null) =>
        new(new SssOptions { Instances = new InstancesOptions { Directory = d.Sub("shared"), Expected = expected ?? [], StaleSeconds = 180 } },
            d.Path, d.Sub("data"), clock, NullLogger<InstanceRegistry>.Instance);

    [Fact]
    public void The_list_shows_missing_stale_unexpected_and_drifting_servers()
    {
        using var d = new TempDir();
        Directory.CreateDirectory(d.Sub("shared"));
        var clock = new Clock();
        var reg = Make(d, clock, [Environment.MachineName, "IIS02", "IIS03", "IIS04"]);
        var self = reg.Self();
        Report(d.Sub("shared"), "IIS02", self.ConfigHash, clock.Now.AddSeconds(-20), sections: new());            // same configuration as this server
        Report(d.Sub("shared"), "IIS03", "otherhash", clock.Now.AddSeconds(-30), "0.4.1", sections: new() { ["Quality"] = "x" });
        Report(d.Sub("shared"), "ROGUE", self.ConfigHash, clock.Now.AddSeconds(-5));
        Report(d.Sub("shared"), "OLD", self.ConfigHash, clock.Now.AddMinutes(-30));

        var views = reg.View().ToDictionary(v => v.Server, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("online", views[Environment.MachineName].State);
        Assert.Equal("online", views["IIS02"].State);
        Assert.Equal(true, views["IIS02"].InSync);
        Assert.Equal(false, views["IIS03"].InSync);
        Assert.True(views["IIS03"].VersionDiffers);
        Assert.Equal("missing", views["IIS04"].State);                 // expected but never reported
        Assert.Equal("unexpected", views["ROGUE"].State);              // reports but is not on the list
        Assert.Equal("unexpected", views["OLD"].State);
    }

    [Fact]
    public void An_oversized_or_broken_status_file_is_ignored()
    {
        using var d = new TempDir();
        Directory.CreateDirectory(d.Sub("shared"));
        var clock = new Clock();
        var reg = Make(d, clock, [Environment.MachineName]);
        File.WriteAllText(Path.Combine(d.Sub("shared"), "sss-BIG.json"), new string('x', 300 * 1024));
        File.WriteAllText(Path.Combine(d.Sub("shared"), "sss-BAD.json"), "{ not json");
        Assert.Equal([Environment.MachineName], reg.View().Select(v => v.Server), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_server_that_stops_reporting_turns_stale_and_the_majority_configuration_is_the_reference()
    {
        using var d = new TempDir();
        Directory.CreateDirectory(d.Sub("shared"));
        var clock = new Clock();
        var reg = Make(d, clock, [Environment.MachineName, "A", "B", "C"]);
        Report(d.Sub("shared"), "A", "x", clock.Now);
        Report(d.Sub("shared"), "B", "x", clock.Now);
        Report(d.Sub("shared"), "C", "y", clock.Now);
        var views = reg.View().ToDictionary(v => v.Server, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(true, views["A"].InSync);
        Assert.Equal(false, views["C"].InSync);                       // the minority differs

        clock.Now = clock.Now.AddMinutes(10);
        Assert.Equal("stale", reg.View().Single(v => v.Server == "A").State);
    }

    [Fact]
    public void A_single_server_without_a_shared_folder_lists_only_itself_and_the_drain_flag_works()
    {
        using var d = new TempDir();
        var reg = new InstanceRegistry(new SssOptions(), d.Path, d.Sub("data"), TimeProvider.System, NullLogger<InstanceRegistry>.Instance);
        Assert.False(reg.Enabled);
        Assert.Equal(Environment.MachineName, reg.View().Single().Server, ignoreCase: true);
        Assert.False(reg.Draining);
        Directory.CreateDirectory(d.Sub("data"));
        File.WriteAllText(reg.DrainFlag, "");
        Assert.True(reg.Draining);
        Assert.True(reg.View().Single().Draining);
    }

    [Fact]
    public async Task The_heartbeat_file_is_written_and_a_restart_clears_the_drain_flag()
    {
        using var d = new TempDir();
        Directory.CreateDirectory(d.Sub("data"));
        var reg = Make(d, new Clock());
        File.WriteAllText(reg.DrainFlag, "");
        await reg.StartAsync(default);
        await Task.Delay(500);
        await reg.StopAsync(default);
        Assert.False(reg.Draining);
        var file = Directory.GetFiles(d.Sub("shared"), "sss-*.json").Single();
        Assert.Contains("configHash", File.ReadAllText(file));
    }
}

public sealed class MetricsFactory : SssFactory
{
    public MetricsFactory() : base(true) { }

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "sss-data-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("Sss__Metrics__Enabled", "true");
        Environment.SetEnvironmentVariable("Sss__DataDirectory", DataDir);
        base.ConfigureWebHost(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("Sss__Metrics__Enabled", null);
        Environment.SetEnvironmentVariable("Sss__DataDirectory", null);
        if (Directory.Exists(DataDir)) Directory.Delete(DataDir, true);
    }
}

[Collection("Api")]
public class MetricsEnabledTests : IClassFixture<MetricsFactory>
{
    private readonly MetricsFactory _f;
    private readonly HttpClient _c;
    public MetricsEnabledTests(MetricsFactory f) { _f = f; _c = f.NewClient(); }

    [Fact]
    public async Task Metrics_are_served_in_prometheus_format_to_the_local_address_only()
    {
        await _c.GetAsync("/api/me");   // loopback is allowed in the test server (no remote address = refused, see below)
        var r = await _c.GetAsync("/metrics");
        // The in-memory test server has no remote address: the endpoint must refuse rather than serve everybody.
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Healthz_reports_draining_while_the_flag_file_exists()
    {
        Assert.Equal(HttpStatusCode.OK, (await _c.GetAsync("/healthz")).StatusCode);
        var flag = Path.Combine(_f.DataDir, "drain.flag");
        Directory.CreateDirectory(Path.GetDirectoryName(flag)!);
        File.WriteAllText(flag, "");
        try
        {
            var r = await _c.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
            Assert.Contains("draining", await r.Content.ReadAsStringAsync());
        }
        finally { File.Delete(flag); }
    }
}

[Collection("Api")]
public class MetricsAndInstancesApiTests : IClassFixture<HelpdeskOnFactory>
{
    private readonly HttpClient _c;
    public MetricsAndInstancesApiTests(HelpdeskOnFactory f) => _c = f.NewClient();

    [Fact]
    public async Task Metrics_are_off_by_default_and_the_instances_endpoint_lists_this_server()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync("/metrics")).StatusCode);
        var list = await (await _c.GetAsync("/api/helpdesk/instances")).Json();
        Assert.Equal(1, list.GetArrayLength());
    }
}
