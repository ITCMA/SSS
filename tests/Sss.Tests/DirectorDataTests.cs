using System.Net;
using System.Text;
using System.Text.Json;
using Sss.Web;
using Sss.Web.Citrix;
using Sss.Web.Models;
using Sss.Web.Services;

namespace Sss.Tests;

public class LogonStructureTests
{
    private static Dictionary<string, object?> Connection(params (string Key, object? Value)[] items)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in items) d[k] = v is string s && k.EndsWith("Date", StringComparison.Ordinal) ? DateTimeOffset.Parse(s) : v;
        return d;
    }

    private static readonly (string, object?)[] Dates =
    [
        ("LogOnStartDate", "2026-11-04T08:00:00Z"), ("LogOnEndDate", "2026-11-04T08:00:30Z"),
        ("VMStartStartDate", "2026-11-04T07:59:50Z"), ("VMPoweredOnDate", "2026-11-04T07:59:55Z"), ("VMStartEndDate", "2026-11-04T08:00:01Z"),
        ("HdxStartDate", "2026-11-04T08:00:01Z"), ("HdxEndDate", "2026-11-04T08:00:03Z"),
        ("ProfileLoadStartDate", "2026-11-04T08:00:03Z"), ("ProfileLoadEndDate", "2026-11-04T08:00:05Z"),
        ("InteractiveStartDate", "2026-11-04T08:00:05Z"), ("InteractiveEndDate", "2026-11-04T08:00:25Z"),
        ("GpoStartDate", "2026-11-04T08:00:05Z"), ("GpoEndDate", "2026-11-04T08:00:08Z"),
        ("LogOnScriptsStartDate", "2026-11-04T08:00:08Z"), ("LogOnScriptsEndDate", "2026-11-04T08:00:20Z"),
    ];

    [Fact]
    public void Steps_inside_a_longer_step_become_its_children_and_the_machine_start_is_split()
    {
        var w = LogonWaterfallBuilder.Build([Connection(Dates)], 30000)!;
        string? Parent(string id) => w.Steps.Single(s => s.Id == id).Parent;

        Assert.Equal("Interactive", Parent("Gpo"));
        Assert.Equal("Interactive", Parent("LogOnScripts"));
        Assert.Null(Parent("Interactive"));
        Assert.Null(Parent("ProfileLoad"));                       // it is next to the interactive session, not inside it
        Assert.Equal("VMStart", Parent("VMPowerOn"));
        Assert.Equal("VMStart", Parent("VMBoot"));
        Assert.Equal(5000, w.Steps.Single(s => s.Id == "VMPowerOn").DurationMs);
        Assert.Equal(6000, w.Steps.Single(s => s.Id == "VMBoot").DurationMs);
    }

    [Fact]
    public void Steps_carry_their_absolute_times_and_precise_lengths()
    {
        var w = LogonWaterfallBuilder.Build([Connection(Dates)], 30000)!;
        var gpo = w.Steps.Single(s => s.Id == "Gpo");
        Assert.Equal(DateTimeOffset.Parse("2026-11-04T08:00:05Z"), gpo.StartAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-04T08:00:08Z"), gpo.EndAt);
        Assert.Equal(3000, gpo.DurationMs);
    }

    [Fact]
    public void Averages_need_at_least_two_earlier_logons_per_step()
    {
        LogonWaterfall Logon(double gpo, double? scripts) => new(30000, [new("Gpo", 0, gpo, false), .. scripts is { } s ? new[] { new LogonStep("LogOnScripts", 0, s, false) } : []]);
        var avg = LogonWaterfallBuilder.AverageSteps([Logon(2000, 1000), Logon(4000, null), Logon(3000, null)]);
        Assert.Equal(3000, avg["Gpo"]);
        Assert.DoesNotContain("LogOnScripts", avg.Keys);               // only one sample
        Assert.Empty(LogonWaterfallBuilder.AverageSteps([Logon(2000, null)]));
    }
}

public class DirectorDataMonitorTests
{
    private const string Key = "11111111-1111-1111-1111-111111111111";
    private const string Machine = "22222222-2222-2222-2222-222222222222";

    private sealed class Handler(string sessionGuidType = "Edm.Guid") : HttpMessageHandler
    {
        public List<string> Queries { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (path.EndsWith("$metadata"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    $"""<edmx:Edmx xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx" Version="4.0"><edmx:DataServices><Schema xmlns="http://docs.oasis-open.org/odata/ns/edm" Namespace="C"><EntityType Name="Connection"><Property Name="ClientName" Type="Edm.String"/><Property Name="ClientLocationCountry" Type="Edm.String"/><Property Name="ClientLocationCity" Type="Edm.String"/></EntityType><EntityType Name="HistoricalApplicationLaunch"><Property Name="SessionGUID" Type="{sessionGuidType}"/></EntityType></Schema></edmx:DataServices></edmx:Edmx>""",
                    Encoding.UTF8, "application/xml") });
            Queries.Add(path.Split('/').Last() + "?" + query);
            var body = path.Split('/').Last() switch
            {
                "Users" => """{"value":[{"Id":4320}]}""",
                "Sessions" when query.Contains("$select=SessionKey,LogOnDuration") => """{"value":[{"SessionKey":"aaaaaaaa-0000-0000-0000-000000000001","LogOnDuration":30000},{"SessionKey":"aaaaaaaa-0000-0000-0000-000000000002","LogOnDuration":30000}]}""",
                "Sessions" => """{"value":[{"SessionKey":"@KEY@","MachineId":"@MACHINE@","StartDate":"2026-11-04T08:00:00Z","LogOnDuration":30000,"CurrentConnection":{"ClientName":"PC1"}}]}""".Replace("@KEY@", Key).Replace("@MACHINE@", Machine),
                "Connections" when query.Contains("aaaaaaaa-0000") => "{\"value\":[" + string.Join(",", new[] { 1, 2 }.Select(n =>
                    "{\"SessionKey\":\"aaaaaaaa-0000-0000-0000-00000000000" + n + "\",\"IsReconnect\":false,\"LogOnStartDate\":\"2026-11-03T08:00:00Z\",\"LogOnEndDate\":\"2026-11-03T08:00:30Z\"," +
                    "\"GpoStartDate\":\"2026-11-03T08:00:05Z\",\"GpoEndDate\":\"2026-11-03T08:00:0" + (n + 6) + "Z\"}")) + "]}",
                "Connections" => """{"value":[{"SessionKey":"@KEY@","IsReconnect":false,"LogOnStartDate":"2026-11-04T08:00:00Z","LogOnEndDate":"2026-11-04T08:00:30Z","GpoStartDate":"2026-11-04T08:00:05Z","GpoEndDate":"2026-11-04T08:00:11Z"}]}""".Replace("@KEY@", Key),
                "ActiveApplicationLaunches" => """{"value":[{"ProcessGUID":"p1","ProcessName":"chrome","AppVersion":"126.0","ExecutablePath":"C:\\Chrome\\chrome.exe","StartDate":"2026-11-04T08:01:00Z","StartupTimeMs":1800,"UserName":"secret-user","IsPublishedApp":false}]}""",
                "HistoricalApplicationLaunches" when query.Contains("AppId eq") => """{"value":[{"AppId":"a1","ProcessName":"msedge"},{"AppId":"a1","ProcessName":"msedge"}]}""",
                "LoadIndexes" => """{"value":[{"MachineId":"@MACHINE@","EffectiveLoadIndex":6400},{"MachineId":"@MACHINE@","EffectiveLoadIndex":2000}]}""".Replace("@MACHINE@", Machine),
                "LogOnMetrics" => """{"value":[{"Id":1,"SessionKey":"@KEY@","UserInitStartDate":"2026-11-04T08:00:05Z","UserInitEndDate":"2026-11-04T08:00:06Z"}]}""".Replace("@KEY@", Key),
                "SessionLogonMetrics" => """{"value":[{"SessionKey":"@KEY@","ProfileType":"FSLogix","FilesCount":88566,"LargeFilesCount":29,"GPORecords":"[{\"CseName\":\"Registry\",\"DurationMs\":120},{\"CseName\":\"Folder Redirection\",\"DurationMs\":3400}]","AppxLoadPackages":"not json"}]}""".Replace("@KEY@", Key),
                "SessionMetricsLatest" => """{"value":[{"SessionKey":"@KEY@","HdxProtocolName":"EDT","EdtMtu":"1496","GatewayPopName":"pop1"}]}""".Replace("@KEY@", Key),
                "GatewayPopInfo" => """{"value":[{"GatewayPopName":"pop1","GatewayPopCountry":"Germany","GatewayPopLocation":"Frankfurt"}]}""",
                "Machines" => """{"value":[{"AgentVersion":"2507.0","OSType":"Windows 11","IsInMaintenanceMode":false,"CurrentRegistrationState":1},{"AgentVersion":"2507.0","OSType":"Windows 11","IsInMaintenanceMode":true,"CurrentRegistrationState":1},{"AgentVersion":"2402.0","OSType":"Windows 11","IsInMaintenanceMode":false,"CurrentRegistrationState":0}]}""",
                "HistoricalApplicationLaunches" => """{"value":[{"ProcessGUID":"p1","ProcessName":"chrome","StartDate":"2026-11-04T08:01:00Z"},{"ProcessGUID":"p2","ProcessName":"excel","StartDate":"2026-11-04T08:02:00Z","EndDate":"2026-11-04T08:20:00Z","StartupTimeMs":2600}]}""",
                "EndpointMetrics" => """{"value":[{"SessionKey":"x","CollectedDate":"2026-11-04T08:10:00Z","PercentCPU":12,"PercentMemory":40,"WifiSignalStrength":0},{"SessionKey":"x","CollectedDate":"2026-11-04T08:11:00Z","PercentCPU":20,"PercentMemory":41,"WifiSignalStrength":0}]}""",
                "ResourceUtilization" => """{"value":[{"CollectedDate":"2026-11-04T08:10:00Z","PercentCpu":30,"UsedMemory":4000,"TotalMemory":8000,"SessionCount":5},{"CollectedDate":"2026-11-04T08:11:00Z","PercentCpu":50,"UsedMemory":6000,"TotalMemory":8000,"SessionCount":6}]}""",
                "ApplicationFaults" => """{"value":[{"Id":1,"SessionKey":"x","ProcessName":"outlook","FaultingApplicationPath":"C:\\o.exe","Description":"crash"}]}""",
                "ApplicationsUsageSummaryData" => """{"value":[{"AppId":"a1","UniqueUserCount":5,"TotalMachinesCount":3,"PeakConcurrentLaunches":4,"TotalLaunches":10},{"AppId":"a1","UniqueUserCount":7,"TotalMachinesCount":2,"PeakConcurrentLaunches":6,"TotalLaunches":15},{"AppId":"a2","UniqueUserCount":1,"TotalMachinesCount":1,"PeakConcurrentLaunches":1,"TotalLaunches":3}]}""",
                "ApplicationNameIds" => """{"value":[{"AppId":"a1","AppName":"Microsoft Edge"}]}""",
                _ => """{"value":[]}""",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static (ODataMonitorClient Client, Handler Handler) Make(string type = "Edm.Guid")
    {
        var o = new CitrixOptions { Controllers = ["ddc.demo.local"] };
        var h = new Handler(type);
        return (new ODataMonitorClient(new HttpClient(h) { BaseAddress = ODataMonitorClient.BuildBaseAddress(o) }, o), h);
    }

    [Fact]
    public async Task Details_get_application_starts_faults_endpoint_and_vda_series_and_user_averages()
    {
        var (c, h) = Make();
        var d = (await c.GetDetailsAsync("S-1-5-21-1-1-1-1001", Key, full: false, default))!;

        var launches = d.Tables.Single(t => t.Id == "launches");
        Assert.Equal(["chrome", "excel"], launches.Rows.Select(r => r.Single(i => i.Key == "ProcessName").Value).Order().ToArray());   // chrome once, not twice
        Assert.DoesNotContain(launches.Rows.SelectMany(r => r), i => i.Key == "UserName");                                              // no user name in the table
        Assert.Contains(h.Queries, q => q.StartsWith("ActiveApplicationLaunches?") && q.Contains($"SessionGUID eq {Key}") && !q.Contains($"'{Key}'"));
        Assert.Single(d.Tables.Single(t => t.Id == "faults").Rows);

        Assert.Contains(d.Series, s => s.Name == "Endpoint.PercentCPU");
        Assert.DoesNotContain(d.Series, s => s.Name == "Endpoint.WifiSignalStrength");                                                  // always 0: left out
        var mem = d.Series.Single(s => s.Name == "Vda.MemoryPercent");
        Assert.Equal([50.0, 75.0], mem.Points.Select(p => p[1]));
        Assert.Contains(h.Queries, q => q.StartsWith("ResourceUtilization?") && q.Contains($"MachineId eq {Machine}"));
    }

    [Fact]
    public async Task A_text_typed_session_guid_is_quoted_in_the_filter()
    {
        var (c, h) = Make("Edm.String");
        await c.GetDetailsAsync("S-1-5-21-1-1-1-1001", Key, full: false, default);
        Assert.Contains(h.Queries, q => q.StartsWith("ActiveApplicationLaunches?") && q.Contains($"SessionGUID eq '{Key}'"));
    }

    [Fact]
    public async Task The_waterfall_gets_the_average_of_the_users_earlier_logons()
    {
        var (c, h) = Make();
        var d = (await c.GetDetailsAsync("S-1-5-21-1-1-1-1001", Key, full: false, default))!;
        // two earlier logons with 2 s and 3 s of group policy time
        Assert.Contains(h.Queries, q => q.StartsWith("Connections?") && q.Contains("aaaaaaaa-0000"));
        var gpo = d.Waterfall!.Steps.Single(s => s.Id == "Gpo");
        Assert.Equal(6000, gpo.DurationMs);
        Assert.Equal(2500, gpo.AvgUserMs);
    }

    [Fact]
    public async Task Logon_details_profile_figures_gateway_and_load_index_come_from_their_record_sets()
    {
        var (c, _) = Make();
        var d = (await c.GetDetailsAsync("S-1-5-21-1-1-1-1001", Key, full: false, default))!;

        Assert.Contains(d.Waterfall!.Steps, st => st.Id == "UserInit" && st.DurationMs == 1000);                                      // merged from LogOnMetrics
        var logon = d.Groups.Single(g => g.Id == "logon").Items;
        Assert.Equal("29 / 88566", logon.Single(i => i.Key == "LargeFiles").Value);
        Assert.Equal("FSLogix", logon.Single(i => i.Key == "ProfileType").Value);
        Assert.Contains(logon, i => i.Key == "AppxLoadPackages" && i.Value == "not json");                                           // not a list: kept as text
        var gpo = d.Tables.Single(t => t.Id == "gpo").Rows;
        Assert.Equal("Folder Redirection", gpo[0].Single(i => i.Key == "Name").Value);                                               // slowest first
        var net = d.Groups.Single(g => g.Id == "network").Items.ToDictionary(i => i.Key, i => i.Value);
        Assert.Equal("1496", net["EdtMtu"]);
        Assert.Equal("Frankfurt", net["GatewayPopLocation"]);

        var live = await c.GetLiveAsync("S-1-5-21-1-1-1-1001", default);
        Assert.Equal(6400, live.Single().LoadIndex);                                                                                 // the newest row of the machine
    }

    [Fact]
    public async Task Machines_are_counted_by_agent_version_and_the_program_behind_an_application_is_named()
    {
        var (c, _) = Make();
        var machines = Sss.Web.Services.EnvironmentService.GroupMachines(await c.GetMachinesAsync(default));
        var current = machines.Single(m => m.AgentVersion == "2507.0");
        Assert.Equal((2, 1, 0), (current.Machines, current.InMaintenance, current.Unregistered));
        Assert.Equal(1, machines.Single(m => m.AgentVersion == "2402.0").Unregistered);

        var apps = await c.GetAppUsageAsync(7, 10, default);
        Assert.Equal("msedge", apps.Single(a => a.Name == "Microsoft Edge").Process);
    }

    [Fact]
    public async Task Application_usage_is_summed_over_the_days_and_named()
    {
        var (c, _) = Make();
        var apps = await c.GetAppUsageAsync(7, 10, default);
        var edge = apps.Single(a => a.Name == "Microsoft Edge");
        Assert.Equal((25, 6, 7, 3), (edge.Launches, edge.PeakConcurrent, edge.UsersPerDay, edge.Machines));   // launches add up, the others are the highest day
        Assert.Equal("a2", apps[1].Name);                                                                       // no name known: the id is shown
    }

    [Fact]
    public async Task Citrix_locations_are_requested_when_the_site_lists_them_and_counted()
    {
        var (c, h) = Make();
        var all = await c.GetAllLiveAsync(default);
        Assert.Contains(h.Queries, q => q.StartsWith("Sessions?") && q.Contains("ClientLocationCountry,ClientLocationCity"));
        Assert.NotEmpty(all);

        var sessions = new[]
        {
            new MonitorSession("a", null, null, true, null, null, null, "PC1", "10.0.0.1", null, ClientCountry: "Germany", ClientCity: "Munich", UserId: 1),
            new MonitorSession("b", null, null, true, null, null, null, "PC2", "10.0.0.2", null, ClientCountry: "Germany", ClientCity: "Munich", UserId: 2),
            new MonitorSession("c", null, null, true, null, null, null, "PC3", "10.0.0.3", null, UserId: 3),
        };
        var view = new EnvironmentService(new SssOptions(), null, TimeProvider.System).Build(sessions, []);
        Assert.Equal(2, view.CitrixLocations!.Count);
        var row = view.CitrixLocations[0];
        Assert.Equal(("Germany", "Munich", 2, 2, 2), (row.Country, row.City, row.Sessions, row.Devices, row.Users));
        Assert.Equal(("", "", 1), (view.CitrixLocations[1].Country, view.CitrixLocations[1].City, view.CitrixLocations[1].Sessions));   // no location reported: one "unknown" row last
    }
}
