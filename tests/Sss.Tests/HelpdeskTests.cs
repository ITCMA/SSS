using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Sss.Web;
using Sss.Web.Providers;
using Sss.Web.Services;

namespace Sss.Tests;

/// <summary>Starts the real application in Development mode (fixed demo user, mock data). Settings are passed as
/// environment variables because the app reads its configuration at the very start of Program.cs.</summary>
public abstract class SssFactory(bool helpdesk, string roles = "") : WebApplicationFactory<Program>
{
    public string AuditDir { get; } = Path.Combine(Path.GetTempPath(), "sss-api-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        Environment.SetEnvironmentVariable("Sss__Auth__DevIsHelpdesk", helpdesk ? "true" : "false");
        Environment.SetEnvironmentVariable("Sss__Audit__Directory", AuditDir);
        Environment.SetEnvironmentVariable("Sss__Auth__DevRoles", roles);
        Environment.SetEnvironmentVariable("Sss__Shadow__Enabled", "true");
        Environment.SetEnvironmentVariable("Sss__Helpdesk__ReasonRequired", "true");   // the API tests cover the requirement; it is off by default
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("Sss__Auth__DevIsHelpdesk", null);
        Environment.SetEnvironmentVariable("Sss__Audit__Directory", null);
        Environment.SetEnvironmentVariable("Sss__Auth__DevRoles", null);
        Environment.SetEnvironmentVariable("Sss__Shadow__Enabled", null);
        Environment.SetEnvironmentVariable("Sss__Helpdesk__ReasonRequired", null);
        if (Directory.Exists(AuditDir)) Directory.Delete(AuditDir, true);
    }
}

public sealed class HelpdeskOnFactory() : SssFactory(true);
public sealed class HelpdeskOffFactory() : SssFactory(false);
public sealed class Level1Factory() : SssFactory(false, "L1");
public sealed class Level2Factory() : SssFactory(false, "L2");
public sealed class AuditorFactory() : SssFactory(false, "Auditor");

/// <summary>Level 2 with the shadow button switched off for level 2 (Sss:Shadow:AllowLevel2 = false).</summary>
public sealed class Level2NoShadowFactory : SssFactory
{
    public Level2NoShadowFactory() : base(false, "L2") { }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Environment.SetEnvironmentVariable("Sss__Shadow__AllowLevel2", "false");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("Sss__Shadow__AllowLevel2", null);
    }
}

[CollectionDefinition("Api", DisableParallelization = true)]
public sealed class ApiCollection;

internal static class ApiHelpers
{
    public static async Task<JsonElement> Json(this HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>POST with a valid anti-forgery token (cookie jar of the client + header).</summary>
    public static async Task<HttpResponseMessage> PostJson(this HttpClient c, string url, object body, bool csrf = true)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (csrf) req.Headers.Add("X-CSRF-TOKEN", (await (await c.GetAsync("/api/csrf")).Json()).GetProperty("token").GetString());
        return await c.SendAsync(req);
    }
}

[Collection("Api")]
public class HelpdeskApiTests : IClassFixture<HelpdeskOnFactory>
{
    private readonly HelpdeskOnFactory _f;
    private readonly HttpClient _c;
    public HelpdeskApiTests(HelpdeskOnFactory f) { _f = f; _c = f.NewClient(); }

    [Fact]
    public async Task Processes_can_be_listed_and_ended_for_an_own_session_and_are_audited()
    {
        var sessions = await (await _c.GetAsync("/api/sessions?range=24h")).Json();
        var uid = sessions.GetProperty("live").EnumerateArray().First().GetProperty("uid").GetString()!;

        var list = await (await _c.GetAsync($"/api/sessions/{uid}/processes")).Json();
        Assert.True(list.GetProperty("processes").GetArrayLength() >= 5);

        var noWindow = await (await _c.PostJson($"/api/sessions/{uid}/processes/9020/end", new { force = false })).Json();
        Assert.Equal("no-window", noWindow.GetProperty("result").GetString());
        var killed = await (await _c.PostJson($"/api/sessions/{uid}/processes/9020/end", new { force = true })).Json();
        Assert.Equal("killed", killed.GetProperty("result").GetString());

        var audit = string.Concat(Directory.GetFiles(_f.AuditDir).Select(File.ReadAllText));
        Assert.Contains("end-process", audit);
        Assert.Contains("killed", audit);
    }

    [Fact]
    public async Task Profile_info_is_available_for_an_own_session_and_audited()
    {
        var sessions = await (await _c.GetAsync("/api/sessions?range=24h")).Json();
        var uid = sessions.GetProperty("live").EnumerateArray().First().GetProperty("uid").GetString()!;

        var p = await (await _c.GetAsync($"/api/sessions/{uid}/profile")).Json();

        Assert.Equal("FSLogix", p.GetProperty("type").GetString());
        Assert.True(p.GetProperty("sizeMb").GetInt32() > 0);
        Assert.Contains("profile-info", string.Concat(Directory.GetFiles(_f.AuditDir).Select(File.ReadAllText)));
        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync("/api/sessions/does-not-exist/profile")).StatusCode);
    }

    [Fact]
    public async Task The_shadow_link_names_the_vda_and_the_session_id_and_is_audited()
    {
        var url = $"/api/helpdesk/sessions/2001/shadow?sid={MockSessionProvider.OtherSid}";
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.PostJson(url, new { })).StatusCode);   // reason required
        var ok = await _c.PostJson(url + "&reason=INC1", new { });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var j = await ok.Json();
        Assert.Equal("sss-shadow://demo-vda.demo.local/2?control=1", j.GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _c.PostJson($"/api/helpdesk/sessions/9999/shadow?sid={MockSessionProvider.OtherSid}&reason=r", new { })).StatusCode);
        var audit = string.Concat(Directory.GetFiles(_f.AuditDir).Select(File.ReadAllText));
        Assert.Contains("\"shadow\"", audit);
        Assert.Contains("demo-vda.demo.local session 2 control", audit);
        Assert.Contains("rejected-not-owned-or-not-live", audit);
        Assert.True((await (await _c.GetAsync("/api/me")).Json()).GetProperty("shadow").GetBoolean());
    }

    [Fact]
    public async Task The_policies_of_a_session_are_read_audited_and_foreign_sessions_are_not_found()
    {
        var r = await _c.GetAsync($"/api/helpdesk/sessions/2001/policies?sid={MockSessionProvider.OtherSid}&reason=INC1");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains(await (r).Json() is var j ? j.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray() : [], n => n == "ClipboardRedirection");
        Assert.Contains("list-policies", string.Concat(Directory.GetFiles(_f.AuditDir).Select(File.ReadAllText)));
        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync($"/api/helpdesk/sessions/9999/policies?sid={MockSessionProvider.OtherSid}&reason=r")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.GetAsync($"/api/helpdesk/sessions/2001/policies?sid={MockSessionProvider.OtherSid}")).StatusCode);   // reason required in these tests
    }

    [Fact]
    public async Task Processes_of_a_foreign_session_are_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync("/api/sessions/does-not-exist/processes")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.GetAsync("/api/sessions/bad;uid/processes")).StatusCode);
    }

    [Fact]
    public async Task Health_probe_answers_without_details()
    {
        var r = await _c.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("""{"status":"ok"}""", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Me_reports_helpdesk_membership()
    {
        var me = await (await _c.GetAsync("/api/me")).Json();
        Assert.True(me.GetProperty("helpdesk").GetBoolean());
    }

    [Fact]
    public async Task Search_finds_users_by_part_of_the_name()
    {
        var r = await _c.GetAsync("/api/helpdesk/users?q=other");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var user = Assert.Single((await r.Json()).EnumerateArray());
        Assert.Equal(MockSessionProvider.OtherSid, user.GetProperty("sid").GetString());
        Assert.True(user.GetProperty("hasLiveSession").GetBoolean());
    }

    [Theory]
    [InlineData("x'; drop")]
    [InlineData("a")]
    [InlineData("%27%20or%201%20eq%201")]
    public async Task Search_rejects_unsafe_or_too_short_text(string q) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.GetAsync($"/api/helpdesk/users?q={Uri.EscapeDataString(q)}")).StatusCode);

    [Fact]
    public async Task Overview_lists_sessions_of_all_users()
    {
        var res = await (await _c.GetAsync("/api/helpdesk/live")).Json();
        var sids = res.GetProperty("sessions").EnumerateArray().Select(s => s.GetProperty("userSid").GetString()).Distinct().ToArray();
        Assert.Contains(MockSessionProvider.DemoSid, sids);
        Assert.Contains(MockSessionProvider.OtherSid, sids);
    }

    [Fact]
    public async Task Helpdesk_sees_other_users_sessions_but_the_normal_endpoint_stays_own_only()
    {
        var other = await (await _c.GetAsync($"/api/helpdesk/sessions?sid={MockSessionProvider.OtherSid}&range=24h")).Json();
        Assert.Equal(2, other.GetProperty("live").GetArrayLength());

        var mine = await (await _c.GetAsync("/api/sessions?range=24h")).Json();
        var mineIds = mine.GetProperty("live").EnumerateArray().Select(s => s.GetProperty("uid").GetString()).ToArray();
        Assert.DoesNotContain("2001", mineIds);
        Assert.DoesNotContain("2002", mineIds);
    }

    [Theory]
    [InlineData("S-1-5' or 1 eq 1")]
    [InlineData("not-a-sid")]
    public async Task Invalid_user_ids_are_rejected(string sid) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.GetAsync($"/api/helpdesk/sessions?sid={Uri.EscapeDataString(sid)}")).StatusCode);

    [Fact]
    public async Task Logoff_without_csrf_token_is_refused()
    {
        var r = await _c.PostJson("/api/helpdesk/logoff", new { sid = MockSessionProvider.OtherSid, scope = "all" }, csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Logoff_works_and_the_audit_log_records_actor_and_reason()
    {
        var r = await _c.PostJson("/api/helpdesk/logoff", new { sid = MockSessionProvider.OtherSid, scope = "selected", uids = new[] { "2002" }, reason = "ticket 4711\nline2" });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(["2002"], (await r.Json()).GetProperty("loggedOff").EnumerateArray().Select(x => x.GetString()!).ToArray());

        var audit = string.Concat(Directory.GetFiles(_f.AuditDir).Select(File.ReadAllText));
        Assert.Contains("helpdesk-logoff:selected", audit);
        Assert.Contains("ticket 4711line2", audit);          // control characters removed
        Assert.Contains("DEMO\\\\demo.user", audit);          // actor (JSON-escaped)
        Assert.Contains("DEMO\\\\other.user", audit);         // affected user
    }

    private async Task<string> KeyOf(string url, int index = 0) =>
        (await (await _c.GetAsync(url)).Json()).GetProperty("live")[index].GetProperty("sessionKey").GetString()!;

    [Fact]
    public async Task Details_of_an_own_session_are_returned_with_groups_and_series()
    {
        var key = await KeyOf("/api/sessions?range=24h");
        var r = await _c.GetAsync($"/api/sessions/{key}/details");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var d = await r.Json();
        Assert.Contains(d.GetProperty("groups").EnumerateArray(), g => g.GetProperty("id").GetString() == "logon");
        Assert.NotEmpty(d.GetProperty("series").EnumerateArray());
    }

    [Fact]
    public async Task Details_of_somebody_elses_session_are_not_found_for_the_normal_endpoint()
    {
        var otherKey = await KeyOf($"/api/helpdesk/sessions?sid={MockSessionProvider.OtherSid}&range=24h");
        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync($"/api/sessions/{otherKey}/details")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _c.GetAsync($"/api/helpdesk/sessions/{otherKey}/details?sid={MockSessionProvider.OtherSid}")).StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("1%27%20or%201%20eq%201")]
    public async Task Details_reject_invalid_keys(string key) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.GetAsync($"/api/sessions/{key}/details")).StatusCode);

    [Fact]
    public async Task Message_is_sent_and_audited_and_foreign_sessions_are_rejected()
    {
        var r = await _c.PostJson("/api/helpdesk/message", new { sid = MockSessionProvider.OtherSid, uids = new[] { "2001", "1001" }, text = "Please save\nyour work", reason = "INC0001" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Json();
        Assert.Equal(["2001"], body.GetProperty("loggedOff").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(["1001"], body.GetProperty("rejected").EnumerateArray().Select(x => x.GetString()!).ToArray());
        var audit = string.Concat(Directory.GetFiles(_f.AuditDir).Select(File.ReadAllText));
        Assert.Contains("helpdesk-message", audit);
        Assert.Contains("Please saveyour work", audit);
    }

    [Fact]
    public async Task Helpdesk_actions_need_a_reason_but_its_content_is_not_checked()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.PostJson("/api/helpdesk/logoff", new { sid = MockSessionProvider.OtherSid, scope = "selected", uids = new[] { "2002" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.PostJson("/api/helpdesk/message", new { sid = MockSessionProvider.OtherSid, uids = new[] { "2001" }, text = "hi", reason = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.GetAsync($"/api/helpdesk/sessions/2001/processes?sid={MockSessionProvider.OtherSid}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _c.GetAsync($"/api/helpdesk/sessions/2001/processes?sid={MockSessionProvider.OtherSid}&reason=whatever")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _c.PostJson("/api/helpdesk/logoff", new { sid = MockSessionProvider.OtherSid, scope = "selected", uids = new[] { "9999" }, reason = "x" })).StatusCode);
    }

    [Fact]
    public async Task Message_requires_text_and_sessions()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.PostJson("/api/helpdesk/message", new { sid = MockSessionProvider.OtherSid, uids = new[] { "2001" }, text = "  ", reason = "r" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.PostJson("/api/helpdesk/message", new { sid = MockSessionProvider.OtherSid, uids = Array.Empty<string>(), text = "hi", reason = "r" })).StatusCode);
    }

    [Fact]
    public async Task Status_lists_the_health_checks()
    {
        var checks = (await (await _c.GetAsync("/api/helpdesk/status")).Json()).EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToArray();
        Assert.Contains("version", checks);
        Assert.Contains("audit", checks);
    }

    [Fact]
    public async Task Logoff_rejects_bad_scope_and_session_ids()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.PostJson("/api/helpdesk/logoff", new { sid = MockSessionProvider.OtherSid, scope = "everything", reason = "r" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.PostJson("/api/helpdesk/logoff", new { sid = MockSessionProvider.OtherSid, scope = "selected", uids = new[] { "1; calc" }, reason = "r" })).StatusCode);
    }
}

[Collection("Api")]
public class NonHelpdeskApiTests : IClassFixture<HelpdeskOffFactory>
{
    private readonly HttpClient _c;
    public NonHelpdeskApiTests(HelpdeskOffFactory f) => _c = f.NewClient();

    [Fact]
    public async Task Me_reports_no_helpdesk_membership() =>
        Assert.False((await (await _c.GetAsync("/api/me")).Json()).GetProperty("helpdesk").GetBoolean());

    [Theory]
    [InlineData("/api/helpdesk/users?q=other")]
    [InlineData("/api/helpdesk/live")]
    [InlineData("/api/helpdesk/status")]
    [InlineData("/api/helpdesk/sessions/11111111-1111-1111-1111-111111111111/details?sid=S-1-5-21-1000-1000-1000-2002")]
    [InlineData("/api/helpdesk/sessions?sid=S-1-5-21-1000-1000-1000-2002")]
    public async Task Helpdesk_reads_are_forbidden(string url) =>
        Assert.Equal(HttpStatusCode.Forbidden, (await _c.GetAsync(url)).StatusCode);

    [Fact]
    public async Task Helpdesk_logoff_is_forbidden_even_with_a_valid_token()
    {
        var r = await _c.PostJson("/api/helpdesk/logoff", new { sid = MockSessionProvider.OtherSid, scope = "all" });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Helpdesk_message_is_forbidden_even_with_a_valid_token()
    {
        var r = await _c.PostJson("/api/helpdesk/message", new { sid = MockSessionProvider.OtherSid, uids = new[] { "2001" }, text = "hi" });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task The_normal_endpoints_still_work_for_everyone()
    {
        var r = await _c.GetAsync("/api/sessions?range=24h");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
}

public class HelpdeskServiceTests
{
    [Fact]
    public void A_ticket_number_is_optional_by_default() => Assert.False(new SssOptions().Helpdesk.ReasonRequired);

    [Theory]
    [InlineData("  ticket 1  ", "ticket 1")]
    [InlineData("a\r\nb\tc", "abc")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Reason_is_trimmed_and_stripped_of_control_characters(string? input, string? expected) =>
        Assert.Equal(expected, HelpdeskService.NormalizeReason(input));

    [Fact]
    public void Reason_is_capped()
    {
        var r = HelpdeskService.NormalizeReason(new string('x', 500));
        Assert.Equal(HelpdeskService.MaxReasonLength, r!.Length);
    }

    [Theory]
    [InlineData("  hello  ", "hello")]
    [InlineData("a\r\nb", "ab")]
    [InlineData("  ", null)]
    public void Message_is_trimmed_and_stripped_of_control_characters(string? input, string? expected) =>
        Assert.Equal(expected, HelpdeskService.NormalizeMessage(input));

    [Fact]
    public void Message_is_capped() =>
        Assert.Equal(HelpdeskService.MaxMessageLength, HelpdeskService.NormalizeMessage(new string('x', 900))!.Length);

    [Theory]
    [InlineData("mueller", true)]
    [InlineData("DOMAIN\\user", true)]
    [InlineData("ana.lopez@corp", true)]
    [InlineData("o'brien", false)]
    [InlineData("a;b", false)]
    [InlineData("x$filter", false)]
    public void Query_allows_only_plain_name_characters(string q, bool valid) =>
        Assert.Equal(valid, HelpdeskService.IsValidQuery(q));
}

[Collection("Api")]
public class RoleTests : IClassFixture<Level1Factory>, IClassFixture<Level2Factory>, IClassFixture<AuditorFactory>
{
    private readonly HttpClient _l1, _l2, _auditor;
    public RoleTests(Level1Factory a, Level2Factory b, AuditorFactory c) { _l1 = a.NewClient(); _l2 = b.NewClient(); _auditor = c.NewClient(); }

    private static async Task<JsonElement> Me(HttpClient c) => await (await c.GetAsync("/api/me")).Json();

    [Fact]
    public async Task Level_1_only_looks()
    {
        var me = await Me(_l1);
        Assert.True(me.GetProperty("helpdesk").GetBoolean());
        Assert.False(me.GetProperty("caps").GetProperty("act").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await _l1.GetAsync("/api/helpdesk/live")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _l1.PostJson("/api/helpdesk/logoff", new { sid = MockSessionProvider.OtherSid, scope = "all", reason = "r" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _l1.PostJson("/api/helpdesk/message", new { sid = MockSessionProvider.OtherSid, uids = new[] { "2001" }, text = "hi", reason = "r" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _l1.GetAsync($"/api/helpdesk/sessions/2001/processes?sid={MockSessionProvider.OtherSid}&reason=r")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _l1.GetAsync("/api/helpdesk/status")).StatusCode);   // no admin group configured: every helpdesk member
    }

    [Fact]
    public async Task Every_helpdesk_level_may_shadow_by_default()
    {
        var url = $"/api/helpdesk/sessions/2001/shadow?sid={MockSessionProvider.OtherSid}&reason=r";
        Assert.Equal(HttpStatusCode.OK, (await _l1.PostJson(url, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _l2.PostJson(url, new { })).StatusCode);
        Assert.True((await (await _l1.GetAsync("/api/me")).Json()).GetProperty("shadow").GetBoolean());
    }

    [Fact]
    public async Task An_auditor_without_a_helpdesk_role_cannot_shadow()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _auditor.PostJson($"/api/helpdesk/sessions/2001/shadow?sid={MockSessionProvider.OtherSid}&reason=r", new { })).StatusCode);
    }

    [Fact]
    public async Task Level_2_cannot_read_the_hdx_values_either()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _l2.GetAsync($"/api/helpdesk/sessions/2001/hdx?sid={MockSessionProvider.OtherSid}&reason=r")).StatusCode);
    }

    [Fact]
    public async Task Level_2_cannot_read_the_policies_either()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _l2.GetAsync($"/api/helpdesk/sessions/2001/policies?sid={MockSessionProvider.OtherSid}&reason=r")).StatusCode);
    }

    [Fact]
    public async Task Level_2_may_act_but_not_look_into_processes()
    {
        Assert.Equal(HttpStatusCode.OK, (await _l2.PostJson("/api/helpdesk/message", new { sid = MockSessionProvider.OtherSid, uids = new[] { "2001" }, text = "hi", reason = "r" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _l2.GetAsync($"/api/helpdesk/sessions/2001/processes?sid={MockSessionProvider.OtherSid}&reason=r")).StatusCode);
    }

    [Fact]
    public async Task The_auditor_is_not_a_helpdesk_member()
    {
        var me = await Me(_auditor);
        Assert.False(me.GetProperty("helpdesk").GetBoolean());
        Assert.True(me.GetProperty("caps").GetProperty("audit").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await _auditor.GetAsync("/api/helpdesk/live")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _auditor.GetAsync("/api/helpdesk/status")).StatusCode);
    }
}

[Collection("Api")]
public class ShadowLevelTests : IClassFixture<Level2NoShadowFactory>
{
    private readonly HttpClient _c;
    public ShadowLevelTests(Level2NoShadowFactory f) { _c = f.NewClient(); }

    [Fact]
    public async Task A_level_switched_off_in_the_configuration_gets_no_button_and_no_link()
    {
        var me = await (await _c.GetAsync("/api/me")).Json();
        Assert.False(me.GetProperty("shadow").GetBoolean());
        Assert.Equal("level-off", me.GetProperty("shadowState").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await _c.PostJson($"/api/helpdesk/sessions/2001/shadow?sid={MockSessionProvider.OtherSid}&reason=r", new { })).StatusCode);
    }
}
