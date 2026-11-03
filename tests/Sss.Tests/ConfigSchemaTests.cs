using System.Text.Json;
using Sss.Web.Services;

namespace Sss.Tests;

internal static class RepoFiles
{
    public static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "docs"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

public class ConfigSchemaTests
{
    [Fact]
    public void Every_option_has_a_description()
    {
        var missing = ConfigSchema.Undocumented();
        Assert.True(missing.Count == 0, "Add an XML comment (<summary>) to: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_committed_schema_matches_the_option_classes()
    {
        var file = Path.Combine(RepoFiles.Root(), "docs", "sss.config.schema.json");
        var generated = ConfigSchema.Build();
        if (Environment.GetEnvironmentVariable("SSS_UPDATE_SCHEMA") == "1") File.WriteAllText(file, generated);
        Assert.True(File.Exists(file) && File.ReadAllText(file) == generated,
            "docs/sss.config.schema.json is out of date. Run: SSS_UPDATE_SCHEMA=1 dotnet test --filter ConfigSchemaTests");
    }

    [Fact]
    public void The_schema_describes_nested_sections_lists_enums_and_defaults()
    {
        using var doc = JsonDocument.Parse(ConfigSchema.Build());
        var sss = doc.RootElement.GetProperty("properties").GetProperty("Sss");
        Assert.False(sss.GetProperty("additionalProperties").GetBoolean());                 // a typo is flagged by the editor
        var balancing = sss.GetProperty("properties").GetProperty("Citrix").GetProperty("properties").GetProperty("Balancing").GetProperty("properties").GetProperty("Strategy");
        Assert.Equal(["Failover", "RoundRobin", "LeastLatency"], balancing.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("Failover", balancing.GetProperty("default").GetString());
        Assert.False(string.IsNullOrWhiteSpace(balancing.GetProperty("description").GetString()));
        var syslog = sss.GetProperty("properties").GetProperty("Audit").GetProperty("properties").GetProperty("Sinks").GetProperty("properties").GetProperty("Syslog");
        Assert.Equal("array", syslog.GetProperty("type").GetString());
        Assert.Equal("object", syslog.GetProperty("items").GetProperty("type").GetString());
        Assert.True(sss.GetProperty("properties").TryGetProperty("PathBase", out _));
        Assert.Equal("boolean", sss.GetProperty("properties").GetProperty("Helpdesk").GetProperty("properties").GetProperty("ReasonRequired").GetProperty("type").GetString());
    }
}
