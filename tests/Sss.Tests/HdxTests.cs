using System.Net;
using System.Text.Json;
using Sss.Web.Models;
using Sss.Web.Providers;
using Sss.Web.Services;

namespace Sss.Tests;

public class HdxAdvisorTests
{
    private static PolicyEntry P(string name, string value, string path = "User") => new(2, path, name, value);
    private static HdxChannel Get(IEnumerable<PolicyEntry> rows, string id) => HdxAdvisor.Channels(rows).Single(c => c.Id == id);

    [Fact]
    public void Every_channel_is_always_present_and_unknown_without_a_policy()
    {
        var all = HdxAdvisor.Channels([]);
        Assert.Equal(HdxAdvisor.ChannelIds, all.Select(c => c.Id).ToArray());
        Assert.All(all, c => Assert.Equal("unknown", c.State));
    }

    [Theory]
    [InlineData("ClientDriveRedirection", "Prohibited", "blocked")]
    [InlineData("ClientDriveRedirection", "ReadOnly", "limited")]
    [InlineData("ClientDriveRedirection", "Allowed", "allowed")]
    [InlineData("AllowDriveRedir", "0", "blocked")]
    [InlineData("AllowDriveRedir", "1", "allowed")]
    [InlineData("DisableClientDrives", "1", "blocked")]
    [InlineData("DisableClientDrives", "0", "allowed")]
    [InlineData("DriveSomething", "7", "configured")]
    public void Drive_values_are_rated(string name, string value, string state) =>
        Assert.Equal(state, Get([P(name, value)], "drive").State);

    [Fact]
    public void The_switch_of_a_group_decides_and_the_sub_functions_only_narrow_it_down()
    {
        // generic Citrix names: the group is on, one sub-function is off, and an unrelated "Driver" value does not count
        var drive = Get([P("AllowDriveRedir", "1"), P("AllowNetworkDrives", "0"), P("AllowFixedDrives", "1"), P("AllowInboxDriverAutoInstall", "0"), P("TryMapToClientDriveLetter", "0")], "drive");
        Assert.Equal("limited", drive.State);
        Assert.Equal("partial", drive.Mode);
        Assert.Equal(["NetworkDrives"], drive.Off);

        var printing = Get([P("AllowPrinterRedir", "1"), P("AutoCreatePDFPrinter", "1"), P("AllowInboxDriverAutoInstall", "0", "User\\Printing"), P("DPILimit", "0", "User\\Printing")], "printing");
        Assert.Equal("allowed", printing.State);

        Assert.Equal("blocked", Get([P("AllowDriveRedir", "0"), P("AllowFixedDrives", "1")], "drive").State);
        Assert.Equal("unknown", Get([P("DPILimit", "0")], "network").State);   // a DPI limit is not a network policy
    }

    [Fact]
    public void A_direction_is_kept_as_the_mode()
    {
        var c = Get([P("ClipboardSelectionMode", "ServerToClient")], "clipboard");
        Assert.Equal("limited", c.State);
        Assert.Equal("toClient", c.Mode);
    }

    [Fact]
    public void One_blocking_value_wins_and_the_values_are_kept()
    {
        var c = Get([P("AllowUsbRedir", "1"), P("UsbDeviceRedirection", "Prohibited")], "usb");
        Assert.Equal("blocked", c.State);
        Assert.Equal(2, c.Policies.Count);
    }

    [Fact]
    public void A_bandwidth_limit_that_is_nearly_used_up_is_a_hint()
    {
        var channels = HdxAdvisor.Channels([P("OverallSessionBandwidthLimit", "4000", "Computer\\ICA")]);
        var live = new HdxLive(2, "Session 2", [new("Output Session Bandwidth", 3_650_000)], [], [], []);
        var hints = HdxAdvisor.Hints(channels, live);
        Assert.Contains(hints, h => h.Id == "bandwidthLimit" && h.Args!["limit"] == "4000" && h.Args["used"] == "3650");
        Assert.DoesNotContain(HdxAdvisor.Hints(channels, live with { Counters = [new("Output Session Bandwidth", 400_000)] }), h => h.Id == "bandwidthLimit");
    }
}

[Collection("Api")]
public class HdxApiTests : IClassFixture<HelpdeskOnFactory>
{
    private readonly HelpdeskOnFactory _f;
    private readonly HttpClient _c;
    public HdxApiTests(HelpdeskOnFactory f) { _f = f; _c = f.NewClient(); }

    [Fact]
    public async Task The_helpdesk_gets_channels_with_policies_live_values_and_the_read_is_audited()
    {
        var r = await _c.GetAsync($"/api/helpdesk/sessions/2001/hdx?sid={MockSessionProvider.OtherSid}&reason=INC1");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Json();
        var channels = j.GetProperty("channels").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!);
        Assert.Equal("blocked", channels["usb"].GetProperty("state").GetString());
        Assert.Equal("limited", channels["drive"].GetProperty("state").GetString());
        Assert.True(channels["usb"].GetProperty("policies").GetArrayLength() > 0);
        Assert.True(j.GetProperty("live").GetProperty("counters").GetArrayLength() > 0);
        Assert.Contains(j.GetProperty("hints").EnumerateArray(), h => h.GetProperty("id").GetString() == "bandwidthLimit");
        Assert.Contains("hdx-info", string.Concat(Directory.GetFiles(_f.AuditDir).Select(File.ReadAllText)));
        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync($"/api/helpdesk/sessions/9999/hdx?sid={MockSessionProvider.OtherSid}&reason=r")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.GetAsync($"/api/helpdesk/sessions/2001/hdx?sid={MockSessionProvider.OtherSid}")).StatusCode);   // reason required
    }

    [Fact]
    public async Task A_user_sees_the_rated_channels_of_an_own_session_without_the_policy_names()
    {
        var sessions = await (await _c.GetAsync("/api/sessions?range=24h")).Json();
        var uid = sessions.GetProperty("live").EnumerateArray().First().GetProperty("uid").GetString()!;
        var j = await (await _c.GetAsync($"/api/sessions/{uid}/hdx")).Json();
        Assert.Equal(7, j.GetProperty("channels").GetArrayLength());
        Assert.All(j.GetProperty("channels").EnumerateArray(), c => Assert.Equal(0, c.GetProperty("policies").GetArrayLength()));
        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync("/api/sessions/does-not-exist/hdx")).StatusCode);
    }
}
