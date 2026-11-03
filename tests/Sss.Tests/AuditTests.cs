using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Sss.Web;
using Sss.Web.Audit;
using Sss.Web.Models;
using Sss.Web.Services;

namespace Sss.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sss-t-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    public string Sub(string n) => System.IO.Path.Combine(Path, n);
}

public class AuditStreamTests
{
    private static readonly UserIdentity Alice = new("S-1-5-21-1", "CORP\\alice");
    private static readonly UserIdentity Bob = new("S-1-5-21-2", "CORP\\bob");

    [Fact]
    public void Every_line_names_the_person_who_acted_also_for_own_actions()
    {
        using var d = new TempDir();
        var log = new AuditLog(d.Path, TimeProvider.System);
        log.Write(new AuditEntry("logoff:selected", Alice, "1", "success", "10.0.0.1"));
        log.Write(new AuditEntry("helpdesk-logoff:selected", Alice, "1", "success", "10.0.0.2", Bob, "INC1", "x"));
        var lines = File.ReadAllLines(Directory.GetFiles(d.Path).Single()).Select(l => JsonDocument.Parse(l).RootElement).ToArray();
        Assert.Equal("CORP\\alice", lines[0].GetProperty("actor").GetString());
        Assert.Equal("CORP\\bob", lines[1].GetProperty("actor").GetString());
        Assert.Equal("CORP\\alice", lines[1].GetProperty("user").GetString());
        Assert.Equal("INC1", lines[1].GetProperty("reason").GetString());
        Assert.Equal("audit", lines[1].GetProperty("stream").GetString());
        Assert.False(string.IsNullOrEmpty(lines[1].GetProperty("id").GetString()));
    }

    [Fact]
    public void The_chain_detects_a_changed_removed_or_inserted_line_and_survives_a_restart()
    {
        using var d = new TempDir();
        new AuditLog(d.Path, TimeProvider.System).Write(new AuditEntry("a", Alice, null, "success", null));
        var log2 = new AuditLog(d.Path, TimeProvider.System);   // a new instance continues the chain of the file
        log2.Write(new AuditEntry("b", Alice, null, "success", null));
        log2.Write(new AuditEntry("c", Alice, null, "success", null));
        var file = Directory.GetFiles(d.Path).Single();
        Assert.Equal((3, 0), JsonlStream.Verify(file));

        var lines = File.ReadAllLines(file).ToList();
        File.WriteAllLines(file, [lines[0], lines[1].Replace("\"b\"", "\"x\""), lines[2]]);
        Assert.Equal(2, JsonlStream.Verify(file).FirstBad);
        File.WriteAllLines(file, [lines[0], lines[2]]);
        Assert.NotEqual(0, JsonlStream.Verify(file).FirstBad);
        File.WriteAllLines(file, [lines[0], lines[0], lines[1], lines[2]]);
        Assert.NotEqual(0, JsonlStream.Verify(file).FirstBad);
    }

    [Fact]
    public void Two_writers_on_one_file_keep_a_valid_chain()
    {
        using var d = new TempDir();
        var a = new AuditLog(d.Path, TimeProvider.System);
        var b = new AuditLog(d.Path, TimeProvider.System);
        Parallel.For(0, 60, i => (i % 2 == 0 ? a : b).Write(new AuditEntry("x" + i, Alice, null, "success", null)));
        Assert.Equal((60, 0), JsonlStream.Verify(Directory.GetFiles(d.Path).Single()));
    }
}

public class AuditSinkTests
{
    private sealed class OptionsStub : SinkOptionsBase;

    private sealed class FakeSink(SinkOptionsBase o) : AuditSink(o, "fake")
    {
        public List<string> Got { get; } = [];
        public bool Fail { get; set; }
        public override string Kind => "fake";
        public override string Target => "memory";
        public override Task SendAsync(IReadOnlyList<SinkEvent> events, CancellationToken ct)
        {
            if (Fail) throw new IOException("down");
            Got.AddRange(events.Select(e => e.Action));
            return Task.CompletedTask;
        }
    }

    private static SinkEvent Ev(string action, string result = "success", string stream = "audit", string reason = "INC1") =>
        new(JsonSerializer.Serialize(new { id = "abc", time = DateTimeOffset.UtcNow, stream, action, user = "CORP\\bob", actor = "CORP\\alice", result, server = "IIS01", reason, detail = "a|b=c\nd", requestId = "r1", role = "L2", ip = "10.0.0.5" }));

    private static UserIdentity Alice() => new("S-1-5-21-1", "CORP\\alice");

    [Fact]
    public async Task The_forwarder_delivers_in_order_and_catches_up_after_an_outage()
    {
        using var d = new TempDir();
        var stream = new JsonlStream(d.Sub("logs"), "audit", TimeProvider.System, false);
        var sink = new FakeSink(new OptionsStub { Streams = ["audit"] });
        var fw = new AuditForwarder([stream], [sink], d.Sub("data"), 0, false, TimeProvider.System, NullLogger<AuditForwarder>.Instance);
        var log = new AuditLog(stream, TimeProvider.System);

        Assert.False(await fw.PumpAsync(stream, sink, default));   // initializes the position; nothing yet
        log.Write(new AuditEntry("one", Alice(), null, "success", null));
        log.Write(new AuditEntry("two", Alice(), null, "success", null));
        sink.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => fw.PumpAsync(stream, sink, default));
        log.Write(new AuditEntry("three", Alice(), null, "success", null));
        sink.Fail = false;
        while (await fw.PumpAsync(stream, sink, default)) { }

        Assert.Equal(["one", "two", "three"], sink.Got);

        // a restart (new forwarder, same data folder) does not send anything twice
        var fw2 = new AuditForwarder([stream], [sink], d.Sub("data"), 0, false, TimeProvider.System, NullLogger<AuditForwarder>.Instance);
        while (await fw2.PumpAsync(stream, sink, default)) { }
        Assert.Equal(3, sink.Got.Count);
        log.Write(new AuditEntry("four", Alice(), null, "success", null));
        while (await fw2.PumpAsync(stream, sink, default)) { }
        Assert.Equal("four", sink.Got[^1]);
    }

    [Fact]
    public void Filters_select_streams_actions_and_problems()
    {
        var all = new FakeSink(new OptionsStub());
        var problems = new FakeSink(new OptionsStub { OnlyProblems = true });
        var helpdesk = new FakeSink(new OptionsStub { Include = ["helpdesk", "alert:"] });
        var auditOnly = new FakeSink(new OptionsStub { Streams = ["audit"] });
        var ok = Ev("helpdesk-logoff:selected");
        var failed = Ev("logoff:selected", "failed");
        var alert = Ev("alert:bulk-logoff", "6 sessions");
        var ops = Ev("ops:BrokerWorkerPool", "warning", "ops");

        Assert.True(all.Wants(ok) && all.Wants(ops));
        Assert.False(problems.Wants(ok));
        Assert.True(problems.Wants(failed) && problems.Wants(alert) && problems.Wants(ops));
        Assert.True(helpdesk.Wants(ok) && helpdesk.Wants(alert));
        Assert.False(helpdesk.Wants(failed));
        Assert.False(auditOnly.Wants(ops));
    }

    [Fact]
    public void Syslog_message_follows_rfc_5424_and_cef_escapes_the_separators()
    {
        var o = new SyslogSinkOptions { Host = "x", Format = "Cef" };
        var m = SyslogSink.Build(o, Ev("helpdesk-logoff:selected"), "0.5.0", "IIS01");
        Assert.StartsWith("<110>1 ", m);                    // facility 13, severity 6
        Assert.Contains(" IIS01 sss ", m);
        Assert.Contains("CEF:0|ITCMA|SSS|0.5.0|helpdesk-logoff:selected|helpdesk-logoff:selected|3|", m);
        Assert.Contains("suser=CORP\\\\alice", m);
        Assert.Contains("cs2=a|b\\=c\\nd", m);               // = and newline escaped
        Assert.Contains("cs1Label=reason", m);

        var alert = SyslogSink.Build(new SyslogSinkOptions { Host = "x" }, Ev("alert:bulk-logoff", "6 sessions"), "1", "IIS01");
        Assert.StartsWith("<105>1 ", alert);                // severity alert (1)
        Assert.Contains("\"action\":\"alert:bulk-logoff\"", alert);
    }

    [Fact]
    public void Http_bodies_exist_for_every_format()
    {
        var events = new[] { Ev("a"), Ev("b") };
        Assert.Equal(2, HttpSink.Bodies(new HttpSinkOptions { Format = "Ndjson" }, events).Single().Body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        using (var arr = JsonDocument.Parse(HttpSink.Bodies(new HttpSinkOptions { Format = "JsonArray" }, events).Single().Body))
            Assert.Equal(2, arr.RootElement.GetArrayLength());
        var hec = HttpSink.Bodies(new HttpSinkOptions { Format = "SplunkHec" }, events).Single().Body;
        Assert.Contains("\"sourcetype\":\"_json\"", hec);
        Assert.Contains("\"event\":{", hec);
        var bulk = HttpSink.Bodies(new HttpSinkOptions { Format = "ElasticBulk", Index = "idx" }, events).Single().Body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, bulk.Length);
        Assert.Contains("\"_index\":\"idx\"", bulk[0]);
        var text = HttpSink.Bodies(new HttpSinkOptions { Format = "Text" }, events);
        Assert.Equal(2, text.Count);
        Assert.Contains("CORP\\\\alice a", text[0].Body);
    }

    private static (int Tag, byte[] Value) Tlv(byte[] b, int at, out int next)
    {
        int len = b[at + 1], start = at + 2;
        if (len == 0x81) { len = b[at + 2]; start = at + 3; }
        else if (len == 0x82) { len = (b[at + 2] << 8) | b[at + 3]; start = at + 4; }
        next = start + len;
        return (b[at], b[start..(start + len)]);
    }

    private static List<(int Tag, byte[] Value)> Items(byte[] b)
    {
        var list = new List<(int, byte[])>();
        for (var at = 0; at < b.Length;)
        {
            list.Add(Tlv(b, at, out var next));
            at = next;
        }
        return list;
    }

    private static string Oid(byte[] b)
    {
        var arcs = new List<uint> { (uint)(b[0] / 40), (uint)(b[0] % 40) };
        uint cur = 0;
        foreach (var x in b.Skip(1)) { cur = (cur << 7) | (uint)(x & 0x7F); if ((x & 0x80) == 0) { arcs.Add(cur); cur = 0; } }
        return string.Join('.', arcs);
    }

    [Fact]
    public void Snmp_trap_is_valid_ber_with_the_expected_variables()
    {
        var pkt = SnmpTrap.Build("c0mm", "1.3.6.1.4.1.12345.1", 123456, 77, ["act", "res"]);
        var (tag, body) = Tlv(pkt, 0, out var end);
        Assert.Equal(0x30, tag);
        Assert.Equal(pkt.Length, end);
        var items = Items(body);
        Assert.Equal(1, items[0].Value[0]);                                    // version 2c
        Assert.Equal("c0mm", Encoding.UTF8.GetString(items[1].Value));
        Assert.Equal(0xA7, items[2].Tag);                                      // SNMPv2-Trap PDU
        var pdu = Items(items[2].Value);
        Assert.Equal(77, pdu[0].Value[0]);
        var vbs = Items(pdu[3].Value).Select(v => Items(v.Value)).ToList();
        Assert.Equal("1.3.6.1.2.1.1.3.0", Oid(vbs[0][0].Value));
        Assert.Equal(0x43, vbs[0][1].Tag);
        Assert.Equal("1.3.6.1.6.3.1.1.4.1.0", Oid(vbs[1][0].Value));
        Assert.Equal("1.3.6.1.4.1.12345.1.0.1", Oid(vbs[1][1].Value));
        Assert.Equal("1.3.6.1.4.1.12345.1.1.1", Oid(vbs[2][0].Value));
        Assert.Equal("act", Encoding.UTF8.GetString(vbs[2][1].Value));
        Assert.Equal("1.3.6.1.4.1.12345.1.1.2", Oid(vbs[3][0].Value));
    }

    [Fact]
    public async Task Syslog_over_tcp_and_udp_and_http_reach_a_real_listener()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var tcpSink = new SyslogSink(new SyslogSinkOptions { Host = "127.0.0.1", Port = ((IPEndPoint)tcp.LocalEndpoint).Port, Protocol = "Tcp" }, "s", "1");
        var accept = Task.Run(async () =>
        {
            using var c = await tcp.AcceptTcpClientAsync();
            using var r = new StreamReader(c.GetStream());
            return await r.ReadToEndAsync();
        });
        await tcpSink.SendAsync([Ev("one"), Ev("two")], default);
        var got = await accept.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, got.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        tcp.Stop();

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var udpSink = new SyslogSink(new SyslogSinkOptions { Host = "127.0.0.1", Port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port, Protocol = "Udp", Format = "Cef" }, "u", "1");
        await udpSink.SendAsync([Ev("udp-event")], default);
        var dgram = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("CEF:0|ITCMA|SSS", Encoding.UTF8.GetString(dgram.Buffer));

        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        using var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/in/");
        http.Start();
        using var tokenDir = new TempDir();
        var tokenFile = tokenDir.Sub("token");
        File.WriteAllText(tokenFile, "Splunk abc-123\n");
        var serve = Task.Run(async () =>
        {
            var ctx = await http.GetContextAsync();
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var auth = ctx.Request.Headers["Authorization"];
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
            return (body, auth);
        });
        var httpSink = new HttpSink(new HttpSinkOptions { Url = $"http://127.0.0.1:{port}/in/", AllowHttp = true, Format = "SplunkHec", AuthorizationFile = tokenFile }, "h");
        await httpSink.SendAsync([Ev("via-http")], default);
        var (reqBody, authHeader) = await serve.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Splunk abc-123", authHeader);
        Assert.Contains("via-http", reqBody);

        http.Stop();
        var bad = new HttpSink(new HttpSinkOptions { Url = $"http://127.0.0.1:{port}/in/", AllowHttp = true, TimeoutSeconds = 2 }, "bad");
        await Assert.ThrowsAnyAsync<Exception>(() => bad.SendAsync([Ev("x")], default));   // an unreachable endpoint is an error, so the forwarder keeps its position
    }

    [Fact]
    public async Task The_unc_sink_writes_one_file_per_day_and_server()
    {
        using var d = new TempDir();
        var sink = new UncSink(new UncSinkOptions { Path = d.Sub("central") }, "unc");
        await sink.SendAsync([Ev("one"), Ev("two", stream: "ops")], default);
        var files = Directory.GetFiles(d.Sub("central")).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(2, files.Length);
        Assert.All(files, f => Assert.EndsWith("-IIS01.jsonl", f));
    }

    [Theory]
    [InlineData("""{"Unc":[{"Path":""}]}""", "Unc:Path")]
    [InlineData("""{"Syslog":[{"Host":"h","Protocol":"Smoke"}]}""", "Protocol")]
    [InlineData("""{"Http":[{"Url":"http://siem/in"}]}""", "https://")]
    [InlineData("""{"Http":[{"Url":"https://siem/in","Format":"Xml"}]}""", "Format")]
    [InlineData("""{"Snmp":[{"Host":"h","Community":"c"}]}""", "EnterpriseOid")]
    public void Unusable_sinks_are_rejected_at_startup(string json, string part)
    {
        var o = JsonSerializer.Deserialize<AuditSinks>(json)!;
        var ex = Assert.Throws<InvalidOperationException>(() => AuditSinkFactory.Create(o, "1"));
        Assert.Contains(part, ex.Message);
    }

    [Fact]
    public void Valid_sinks_get_defaults()
    {
        var o = JsonSerializer.Deserialize<AuditSinks>("""{"Syslog":[{"Host":"h","Protocol":"Tls"}],"Snmp":[{"Host":"h","Community":"c","EnterpriseOid":"1.3.6.1.4.1.12345.1"}]}""")!;
        var sinks = AuditSinkFactory.Create(o, "1");
        Assert.Equal(["syslog1", "snmp1"], sinks.Select(s => s.Name));
        Assert.Equal("tls://h:6514", sinks[0].Target);
        Assert.True(sinks[1].Options.OnlyProblems);
    }
}

public class AuditReaderTests
{
    [Fact]
    public void Reader_merges_folders_removes_duplicates_filters_and_summarizes()
    {
        using var d = new TempDir();
        var local = new AuditLog(d.Sub("local"), TimeProvider.System);
        var alice = new UserIdentity("S-1-5-21-1", "CORP\\alice");
        var bob = new UserIdentity("S-1-5-21-2", "CORP\\bob");
        local.Write(new AuditEntry("helpdesk-logoff:selected", alice, "1", "success", null, bob, "INC1"));
        local.Write(new AuditEntry("helpdesk-logoff:selected", alice, "2", "failed", null, bob, "INC1"));
        local.Write(new AuditEntry("logoff:all", bob, "3", "success", null));
        Directory.CreateDirectory(d.Sub("central"));   // the same files copied to the central folder, as the UNC sink does
        foreach (var f in Directory.GetFiles(d.Sub("local"))) File.Copy(f, Path.Combine(d.Sub("central"), Path.GetFileName(f)));

        var reader = new AuditReader([d.Sub("local"), d.Sub("central")]);
        AuditQuery Q(string? actor = null, string? target = null, string? result = null, string? text = null, int limit = 100) =>
            new(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMinutes(1), actor, target, null, result, null, text, null, limit);

        var all = reader.Query(Q());
        Assert.Equal(3, all.Matching);                                           // duplicates by id removed
        Assert.Equal(1, all.Summary.Problems);
        Assert.Equal("CORP\\bob", all.Summary.ByActor[0].Key);
        Assert.Equal(3, all.Summary.ByDay.Sum(x => x.N));
        Assert.Equal(0, reader.Query(Q(actor: "alice")).Matching);               // alice was only the target
        Assert.Equal(1, reader.Query(Q(target: "alice", result: "fail")).Matching);
        var text = reader.Query(Q(text: "INC1", limit: 1));
        Assert.Equal(2, text.Matching);
        Assert.Single(text.Events);
    }
}

[Collection("Api")]
public class AuditApiTests : IClassFixture<AuditorFactory>, IClassFixture<HelpdeskOnFactory>
{
    private readonly HttpClient _auditor, _helpdesk;
    public AuditApiTests(AuditorFactory a, HelpdeskOnFactory h) { _auditor = a.NewClient(); _helpdesk = h.NewClient(); }

    [Fact]
    public async Task Only_the_auditor_reads_the_trail_and_reading_is_audited()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _helpdesk.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _helpdesk.GetAsync("/api/audit/verify")).StatusCode);

        var res = await (await _auditor.GetAsync("/api/audit?days=1")).Json();
        Assert.True(res.GetProperty("matching").GetInt32() >= 1);                // its own "audit:view"
        var first = res.GetProperty("events")[0];
        Assert.Equal("audit:view", first.GetProperty("action").GetString());
        Assert.Equal("DEMO\\demo.user", first.GetProperty("actor").GetString());
        Assert.Equal("Auditor", first.GetProperty("role").GetString());

        var verify = await (await _auditor.GetAsync("/api/audit/verify")).Json();
        Assert.All(verify.EnumerateArray(), f => Assert.True(f.GetProperty("ok").GetBoolean()));
        Assert.Equal(HttpStatusCode.OK, (await _auditor.GetAsync("/api/audit/export.csv?days=1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _auditor.GetAsync("/api/audit?stream=x")).StatusCode);
        Assert.Equal(JsonValueKind.Array, (await (await _auditor.GetAsync("/api/audit/sinks")).Json()).ValueKind);
    }
}

public class ConfigCheckListTests
{
    private static IReadOnlyList<string> Unknown(string json) =>
        ConfigCheck.UnknownKeys(new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build().GetSection("Sss"));

    [Fact]
    public void Typos_inside_lists_of_destinations_are_reported_and_valid_keys_are_not()
    {
        Assert.Empty(Unknown("""{"Sss":{"Audit":{"Sinks":{"Syslog":[{"Host":"h","Protocol":"Tls","Include":["x"]}],"Http":[{"Url":"https://a","Headers":{"X-Any":"1"}}]}},"Roles":{"AuditorGroup":"A"},"Instances":{"Directory":"d"}}}"""));
        var u = Unknown("""{"Sss":{"Audit":{"Sinks":{"Syslog":[{"Host":"h","Protocl":"Tls"}]}}}}""");
        Assert.Contains(u, k => k.Contains("Syslog:0:Protocl"));
    }
}
