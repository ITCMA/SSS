using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Sss.Web.Providers;
using Sss.Web.Services;

namespace Sss.Tests;

/// <summary>Starts the real application in Development mode (fixed demo user, mock data). Settings are passed as
/// environment variables because the app reads its configuration at the very start of Program.cs.</summary>
public abstract class SssFactory(bool helpdesk) : WebApplicationFactory<Program>
{
    public string AuditDir { get; } = Path.Combine(Path.GetTempPath(), "sss-api-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        Environment.SetEnvironmentVariable("Sss__Auth__DevIsHelpdesk", helpdesk ? "true" : "false");
        Environment.SetEnvironmentVariable("Sss__Audit__Directory", AuditDir);
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("Sss__Auth__DevIsHelpdesk", null);
        Environment.SetEnvironmentVariable("Sss__Audit__Directory", null);
        if (Directory.Exists(AuditDir)) Directory.Delete(AuditDir, true);
    }
}

public sealed class HelpdeskOnFactory() : SssFactory(true);
public sealed class HelpdeskOffFactory() : SssFactory(false);

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
        var r = await _c.PostJson("/api/helpdesk/message", new { sid = MockSessionProvider.OtherSid, uids = new[] { "2001", "1001" }, text = "Please save\nyour work" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Json();
        Assert.Equal(["2001"], body.GetProperty("loggedOff").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(["1001"], body.GetProperty("rejected").EnumerateArray().Select(x => x.GetString()!).ToArray());
        var audit = string.Concat(Directory.GetFiles(_f.AuditDir).Select(File.ReadAllText));
        Assert.Contains("helpdesk-message", audit);
        Assert.Contains("Please saveyour work", audit);
    }

    [Fact]
    public async Task Message_requires_text_and_sessions()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.PostJson("/api/helpdesk/message", new { sid = MockSessionProvider.OtherSid, uids = new[] { "2001" }, text = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.PostJson("/api/helpdesk/message", new { sid = MockSessionProvider.OtherSid, uids = Array.Empty<string>(), text = "hi" })).StatusCode);
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
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.PostJson("/api/helpdesk/logoff", new { sid = MockSessionProvider.OtherSid, scope = "everything" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.PostJson("/api/helpdesk/logoff", new { sid = MockSessionProvider.OtherSid, scope = "selected", uids = new[] { "1; calc" } })).StatusCode);
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
