using System.Text;
using Microsoft.Extensions.Configuration;
using Sss.Web;
using Sss.Web.Services;

namespace Sss.Tests;

public class ConfigValidatorTests
{
    private static IReadOnlyList<ConfigFinding> Check(string json, bool development = false)
    {
        var cfg = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
        var sss = cfg.GetSection("Sss");
        return ConfigValidator.Validate(sss.Get<SssOptions>() ?? new SssOptions(), sss, development);
    }

    private static string[] Errors(string json) => Check(json).Where(f => f.Level == "error").Select(f => f.Message).ToArray();
    private static string[] Warnings(string json) => Check(json).Where(f => f.Level == "warning").Select(f => f.Message).ToArray();

    [Fact]
    public void The_defaults_have_no_errors() => Assert.Empty(Errors("{}"));

    [Theory]
    [InlineData("""{"Sss":{"Quality":{"RttWarnMs":200,"RttCritMs":100}}}""", "RttWarnMs")]
    [InlineData("""{"Sss":{"Quality":{"BandwidthWarnKbps":100,"BandwidthCritKbps":400}}}""", "BandwidthWarnKbps")]
    [InlineData("""{"Sss":{"Provider":"Oracle"}}""", "Sss:Provider")]
    [InlineData("""{"Sss":{"Auth":{"Mode":"Dev"}}}""", "Development")]
    [InlineData("""{"Sss":{"Provider":"Citrix"}}""", "Controllers")]
    [InlineData("""{"Sss":{"Provider":"Citrix","Citrix":{"Controllers":["a b"]}}}""", "invalid host name")]
    [InlineData("""{"Sss":{"Provider":"Citrix","Citrix":{"Controllers":["a"],"Balancing":{"Strategy":"Random"}}}}""", "Strategy")]
    [InlineData("""{"Sss":{"Audit":{"Sinks":{"Http":[{"Url":"http://siem/in"}]}}}}""", "https://")]
    [InlineData("""{"Sss":{"Instances":{"Directory":"d","HeartbeatSeconds":60,"StaleSeconds":30}}}""", "StaleSeconds")]
    [InlineData("""{"Sss":{"Metrics":{"AllowedIps":["10.0.0.0/99x"]}}}""", "AllowedIps")]
    [InlineData("""{"Sss":{"StoreFrontUrl":"javascript:alert(1)"}}""", "StoreFrontUrl")]
    [InlineData("""{"Sss":{"RateLimit":{"LogoffRequestsPerMinute":0}}}""", "RateLimit")]
    public void Wrong_values_are_errors_with_a_clear_message(string json, string part) =>
        Assert.Contains(Errors(json), m => m.Contains(part, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Warnings_point_at_risky_or_incomplete_settings()
    {
        var w = Warnings("""
            {"Sss":{"Provider":"Citrix","Helpdesk":{"Group":"SSS-Helpdesk","ReasonRequired":false},
              "Citrix":{"Controllers":["ddc01.corp.local"],"Monitor":{"UseHttps":false}},
              "Instances":{"Directory":"\\\\fs\\sss"},"Audit":{"Directory":"\\\\fs\\audit"},"Metrics":{"Enabled":true},"Typo":1}}
            """);
        string[] expected = ["no domain", "ReasonRequired", "AuditorGroup", "HTTP", "Expected", "KeyDirectory", "PerServerFiles", "AllowedIps", "Typo"];
        foreach (var part in expected) Assert.Contains(w, m => m.Contains(part, StringComparison.Ordinal));
    }

    [Fact]
    public void A_clean_enterprise_configuration_has_no_warnings_about_roles_or_servers()
    {
        var w = Warnings("""
            {"Sss":{"Provider":"Citrix","Roles":{"Level2Group":"CORP\\H2","AuditorGroup":"CORP\\A","AdminGroup":"CORP\\M"},
              "Citrix":{"Controllers":["ddc01.corp.local"],"Discovery":{"Enabled":true}},
              "Instances":{"Directory":"\\\\fs\\sss","Expected":["IIS01","IIS02"]},"DataProtection":{"KeyDirectory":"\\\\fs\\keys"}}}
            """);
        Assert.DoesNotContain(w, m => m.Contains("helpdesk group") || m.Contains("AuditorGroup") || m.Contains("Expected") || m.Contains("KeyDirectory"));
    }
}

public class ConfigCliTests
{
    private static (int Code, string Text) Run(TempDir d, string json, string name = "sss.config.json")
    {
        var path = Path.Combine(d.Path, name);
        File.WriteAllText(path, json);
        var sw = new StringWriter();
        var code = ConfigCli.Run(["--check-config", path], sw, d.Path);
        return (code, sw.ToString());
    }

    [Fact]
    public void A_valid_file_exits_with_0_and_says_ok()
    {
        using var d = new TempDir();
        var (code, text) = Run(d, """{"Sss":{"Provider":"Citrix","Citrix":{"Controllers":["ddc01.corp.local"]},"Roles":{"AuditorGroup":"CORP\\A"}}}""");
        Assert.Equal(0, code);
        Assert.Contains("OK", text);
    }

    [Fact]
    public void Errors_exit_with_1_and_are_listed_before_warnings()
    {
        using var d = new TempDir();
        var (code, text) = Run(d, """{"Sss":{"Provider":"Citrix","Quality":{"RttWarnMs":500},"Wrong":1}}""");
        Assert.Equal(1, code);
        Assert.True(text.IndexOf("[ERROR]", StringComparison.Ordinal) < text.IndexOf("[WARN ]", StringComparison.Ordinal));
        Assert.Contains("would not start", text);
    }

    [Fact]
    public void Broken_json_a_wrong_type_and_a_missing_file_are_reported()
    {
        using var d = new TempDir();
        var (code, text) = Run(d, """{"Sss":{ "Provider": "Citrix", } oops""");
        Assert.Equal(1, code);
        Assert.Contains("not valid JSON", text);

        (code, text) = Run(d, """{"Sss":{"Citrix":{"Workers":"many"}}}""");
        Assert.Equal(1, code);
        Assert.Contains("wrong type", text);

        var sw = new StringWriter();
        Assert.Equal(2, ConfigCli.Run(["--check-config", Path.Combine(d.Path, "nope.json")], sw, d.Path));
    }

    [Fact]
    public void The_local_file_next_to_the_checked_one_is_included()
    {
        using var d = new TempDir();
        File.WriteAllText(Path.Combine(d.Path, "sss.local.json"), """{"Sss":{"Quality":{"RttWarnMs":900}}}""");
        var (code, text) = Run(d, """{"Sss":{"Provider":"Citrix","Citrix":{"Controllers":["a.corp.local"]}}}""");
        Assert.Equal(1, code);
        Assert.Contains("RttWarnMs", text);
    }
}

public class ExampleConfigTests
{
    public static IEnumerable<object[]> Files() =>
        Directory.GetFiles(Path.Combine(RepoFiles.Root(), "docs", "examples"), "*.json").Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Files))]
    public void Every_example_is_valid_without_unknown_keys(string name)
    {
        var sw = new StringWriter();
        var path = Path.Combine(RepoFiles.Root(), "docs", "examples", name);
        var code = ConfigCli.Run(["--check-config", path], sw, AppContext.BaseDirectory);
        var text = sw.ToString();
        Assert.True(code == 0, text);
        Assert.DoesNotContain("Unknown configuration key", text);
    }
}

public class EffectiveConfigTests
{
    [Fact]
    public void Rows_show_values_sources_defaults_and_mask_secrets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sss-eff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "sss.config.json"),
                """{"Sss":{"Provider":"Citrix","Citrix":{"Controllers":["a.corp.local","b.corp.local"]},"Audit":{"Sinks":{"Snmp":[{"Host":"nms","Community":"geheim","EnterpriseOid":"1.3.6.1.4.1.1.1"}],"Http":[{"Url":"https://x/y","Headers":{"X-Api-Key":"s3cret-key"}}]}}}}""");
            File.WriteAllText(Path.Combine(dir, "sss.local.json"), """{"Sss":{"RefreshSeconds":10}}""");
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), """{"Sss":{"Quality":{"RttWarnMs":80},"RefreshSeconds":30}}""");
            var root = new ConfigurationBuilder().SetBasePath(dir).AddJsonFile("appsettings.json").AddJsonFile("sss.config.json").AddJsonFile("sss.local.json")
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Sss:Citrix:Workers"] = "4" }).Build();
            var rows = EffectiveConfig.Build(root.GetSection("Sss").Get<SssOptions>()!, root).ToDictionary(r => r.Path);

            Assert.Equal(("Citrix", "sss.config.json", true), (rows["Provider"].Value, rows["Provider"].Source, rows["Provider"].Explicit));
            Assert.Equal("a.corp.local, b.corp.local", rows["Citrix:Controllers"].Value);
            Assert.Equal("sss.config.json", rows["Citrix:Controllers"].Source);
            Assert.Equal("sss.local.json", rows["RefreshSeconds"].Source);
            Assert.Equal("Memory", rows["Citrix:Workers"].Source);
            Assert.Equal(("appsettings.json", false), (rows["Quality:RttWarnMs"].Source, rows["Quality:RttWarnMs"].Explicit));   // shipped default: own source, not "changed"
            Assert.Equal(("true", "default", false), (rows["Helpdesk:ReasonRequired"].Value, rows["Helpdesk:ReasonRequired"].Source, rows["Helpdesk:ReasonRequired"].Explicit));
            Assert.Equal("••••••", rows["Audit:Sinks:Snmp:0:Community"].Value);                       // secret masked
            Assert.DoesNotContain(rows.Values, r => r.Value.Contains("geheim") || r.Value.Contains("s3cret"));
            Assert.Equal("nms", rows["Audit:Sinks:Snmp:0:Host"].Value);
            Assert.Equal("••••••", rows["Audit:Sinks:Http:0:Headers:X-Api-Key"].Value);                // header values are masked
        }
        finally { Directory.Delete(dir, true); }
    }
}

[Collection("Api")]
public class EffectiveConfigApiTests : IClassFixture<Level1Factory>, IClassFixture<AuditorFactory>
{
    private readonly HttpClient _l1, _auditor;
    public EffectiveConfigApiTests(Level1Factory a, AuditorFactory b) { _l1 = a.NewClient(); _auditor = b.NewClient(); }

    [Fact]
    public async Task The_admin_reads_the_configuration_and_the_auditor_does_not()
    {
        var rows = await (await _l1.GetAsync("/api/helpdesk/config")).Json();   // no admin group configured: every helpdesk member
        Assert.Contains(rows.EnumerateArray(), r => r.GetProperty("path").GetString() == "Provider");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await _auditor.GetAsync("/api/helpdesk/config")).StatusCode);
    }
}
