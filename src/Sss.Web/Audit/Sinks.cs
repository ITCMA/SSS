using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sss.Web.Audit;

/// <summary>One line of the local file, parsed once for filtering and formatting.</summary>
public sealed class SinkEvent
{
    public SinkEvent(string line)
    {
        Line = line;
        using var doc = JsonDocument.Parse(line);
        Root = doc.RootElement.Clone();
    }

    public string Line { get; }
    public JsonElement Root { get; }

    public string? Str(string name) => Root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    public string Stream => Str("stream") ?? "audit";
    public string Action => Str("action") ?? "";
    public string Result => Str("result") ?? "";
    public DateTimeOffset Time => DateTimeOffset.TryParse(Str("time"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.UtcNow;

    /// <summary>Failures, rejections, alerts and operational messages: what a notification channel is for.</summary>
    public bool IsProblem =>
        Stream == "ops" || Action.StartsWith("alert:", StringComparison.OrdinalIgnoreCase)
        || Result.StartsWith("fail", StringComparison.OrdinalIgnoreCase) || Result.StartsWith("rejected", StringComparison.OrdinalIgnoreCase)
        || Result.StartsWith("denied", StringComparison.OrdinalIgnoreCase) || Result.StartsWith("error", StringComparison.OrdinalIgnoreCase);

    /// <summary>One readable sentence for chat webhooks.</summary>
    public string Sentence()
    {
        var who = Str("actor") ?? "?";
        var target = Str("user");
        var text = $"SSS {Str("server")}: {who} {Action}" + (target is not null && target != who ? $" → {target}" : "") + $" [{Result}]";
        if (Str("reason") is { Length: > 0 } r) text += $" ({r})";
        if (Stream == "ops" && Str("detail") is { Length: > 0 } d) text += ": " + d;
        return text;
    }
}

public abstract class AuditSink(SinkOptionsBase options, string name)
{
    public string Name { get; } = name;
    public SinkOptionsBase Options { get; } = options;
    public abstract string Kind { get; }
    public abstract string Target { get; }
    public abstract Task SendAsync(IReadOnlyList<SinkEvent> events, CancellationToken ct);

    public bool Wants(SinkEvent e) =>
        Options.Streams.Contains(e.Stream, StringComparer.OrdinalIgnoreCase)
        && (Options.Include.Length == 0 || Options.Include.Any(p => e.Action.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        && (!Options.OnlyProblems || e.IsProblem);
}

public sealed class UncSink(UncSinkOptions o, string name) : AuditSink(o, name)
{
    public override string Kind => "unc";
    public override string Target => o.Path;

    public override Task SendAsync(IReadOnlyList<SinkEvent> events, CancellationToken ct)
    {
        Directory.CreateDirectory(o.Path);
        foreach (var g in events.GroupBy(e => (e.Stream, Day: e.Time.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture), Server: ServerOf(e))))
        {
            ct.ThrowIfCancellationRequested();
            var file = Path.Combine(o.Path, $"{g.Key.Stream}-{g.Key.Day}-{g.Key.Server}.jsonl");
            File.AppendAllText(file, string.Concat(g.Select(e => e.Line + "\n")), new UTF8Encoding(false));
        }
        return Task.CompletedTask;
    }

    private static string ServerOf(SinkEvent e) => Regex.Replace(e.Str("server") ?? Environment.MachineName, @"[^A-Za-z0-9._-]", "_");
}

public sealed class SyslogSink(SyslogSinkOptions o, string name, string version) : AuditSink(o, name)
{
    public override string Kind => "syslog";
    public override string Target => $"{o.Protocol.ToLowerInvariant()}://{o.Host}:{o.Port}";

    /// <summary>RFC 5424: PRI VERSION TIMESTAMP HOSTNAME APP-NAME PROCID MSGID STRUCTURED-DATA MSG.</summary>
    public static string Build(SyslogSinkOptions o, SinkEvent e, string version, string hostname)
    {
        var severity = e.Action.StartsWith("alert:", StringComparison.OrdinalIgnoreCase) ? 1 : e.IsProblem ? 4 : 6;
        var pri = Math.Clamp(o.Facility, 0, 23) * 8 + severity;
        var msg = o.Format.Equals("Cef", StringComparison.OrdinalIgnoreCase) ? Cef.Build(e, version) : e.Line;
        var msgId = Regex.Replace(e.Action.Length == 0 ? "-" : e.Action, @"[^\x21-\x7E]", "_");
        if (msgId.Length > 32) msgId = msgId[..32];
        return $"<{pri}>1 {e.Time.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ} {Token(hostname)} {Token(o.AppName)} {Environment.ProcessId} {msgId} - {msg}";
    }

    private static string Token(string s) => s.Length == 0 ? "-" : Regex.Replace(s, @"[^\x21-\x7E]", "_");

    public override async Task SendAsync(IReadOnlyList<SinkEvent> events, CancellationToken ct)
    {
        var messages = events.Select(e => Build(o, e, version, Environment.MachineName)).ToList();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        if (o.Protocol.Equals("Udp", StringComparison.OrdinalIgnoreCase))
        {
            using var udp = new UdpClient();
            udp.Connect(o.Host, o.Port);
            foreach (var m in messages)
            {
                var bytes = Encoding.UTF8.GetBytes(m);
                await udp.SendAsync(bytes.AsMemory(0, Math.Min(bytes.Length, 60000)), cts.Token);
            }
            return;
        }
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(o.Host, o.Port, cts.Token);
        Stream stream = tcp.GetStream();
        if (o.Protocol.Equals("Tls", StringComparison.OrdinalIgnoreCase))
        {
            var ssl = new SslStream(stream);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = o.Host }, cts.Token);
            stream = ssl;
        }
        await using (stream)
        {
            foreach (var m in messages)
            {
                var bytes = Encoding.UTF8.GetBytes(m);
                if (o.Framing.Equals("OctetCount", StringComparison.OrdinalIgnoreCase))
                    await stream.WriteAsync(Encoding.UTF8.GetBytes($"{bytes.Length} "), cts.Token);
                await stream.WriteAsync(bytes, cts.Token);
                if (!o.Framing.Equals("OctetCount", StringComparison.OrdinalIgnoreCase)) await stream.WriteAsync("\n"u8.ToArray(), cts.Token);
            }
            await stream.FlushAsync(cts.Token);
        }
    }
}

/// <summary>Common Event Format as used by ArcSight, Sentinel, QRadar, Splunk and others.</summary>
public static class Cef
{
    private static string Header(string s) => s.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    private static string Ext(string s) => s.Replace("\\", "\\\\").Replace("=", "\\=").Replace("\r", "\\n").Replace("\n", "\\n");

    public static string Build(SinkEvent e, string version)
    {
        var severity = e.Action.StartsWith("alert:", StringComparison.OrdinalIgnoreCase) ? 8 : e.IsProblem ? 6 : 3;
        var sb = new StringBuilder($"CEF:0|ITCMA|SSS|{Header(version)}|{Header(e.Action)}|{Header(e.Action)}|{severity}|");
        sb.Append("rt=").Append(e.Time.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        void Add(string key, string? v) { if (!string.IsNullOrEmpty(v)) sb.Append(' ').Append(key).Append('=').Append(Ext(v)); }
        Add("act", e.Action);
        Add("outcome", e.Result);
        Add("suser", e.Str("actor"));
        Add("duser", e.Str("user"));
        Add("src", e.Str("ip"));
        Add("dvchost", e.Str("server"));
        Add("externalId", e.Str("id"));
        if (e.Str("reason") is { Length: > 0 } r) Add("cs1", r);
        if (e.Str("detail") is { Length: > 0 } d) Add("cs2", d);
        if (e.Str("requestId") is { Length: > 0 } rq) Add("cs3", rq);
        if (e.Str("role") is { Length: > 0 } ro) Add("cs4", ro);
        if (e.Str("session") is { Length: > 0 } se) Add("cs5", se);
        foreach (var (n, label) in new[] { (1, "reason"), (2, "detail"), (3, "requestId"), (4, "role"), (5, "session") })
        {
            var present = sb.ToString().Contains($" cs{n}=", StringComparison.Ordinal);
            if (present) sb.Append($" cs{n}Label={label}");
        }
        return sb.ToString();
    }
}

public sealed class HttpSink(HttpSinkOptions o, string name) : AuditSink(o, name)
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan };

    public override string Kind => "http";
    public override string Target => o.Url;

    /// <summary>Bodies for one batch: one body for the bulk formats, one per event for "Text".</summary>
    public static IReadOnlyList<(string Body, string ContentType)> Bodies(HttpSinkOptions o, IReadOnlyList<SinkEvent> events)
    {
        switch (o.Format.ToLowerInvariant())
        {
            case "jsonarray":
                return [("[" + string.Join(",", events.Select(e => e.Line)) + "]", "application/json")];
            case "splunkhec":
                return [(string.Concat(events.Select(e => JsonSerializer.Serialize(new
                {
                    time = e.Time.ToUnixTimeMilliseconds() / 1000.0, host = e.Str("server"), source = "sss", sourcetype = "_json", @event = e.Root,
                }))), "application/json")];
            case "elasticbulk":
                var head = JsonSerializer.Serialize(new { index = new { _index = o.Index } });
                return [(string.Concat(events.Select(e => head + "\n" + e.Line + "\n")), "application/x-ndjson")];
            case "text":
                return events.Select(e => (JsonSerializer.Serialize(new { text = e.Sentence() }), "application/json")).ToList();
            default:
                return [(string.Concat(events.Select(e => e.Line + "\n")), "application/x-ndjson")];
        }
    }

    public override async Task SendAsync(IReadOnlyList<SinkEvent> events, CancellationToken ct)
    {
        foreach (var (body, contentType) in Bodies(o, events))
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, o.Url) { Content = new StringContent(body, new UTF8Encoding(false), contentType) };
            foreach (var (k, v) in o.Headers) req.Headers.TryAddWithoutValidation(k, v);
            if (o.AuthorizationFile.Length > 0)
                req.Headers.TryAddWithoutValidation("Authorization", (await File.ReadAllTextAsync(o.AuthorizationFile, ct)).Trim());
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 1, 120)));
            using var resp = await Client.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) throw new IOException($"{o.Url} answered {(int)resp.StatusCode}.");
        }
    }
}

public sealed class SnmpSink(SnmpSinkOptions o, string name) : AuditSink(o, name)
{
    public override string Kind => "snmp";
    public override string Target => $"udp://{o.Host}:{o.Port}";

    public override async Task SendAsync(IReadOnlyList<SinkEvent> events, CancellationToken ct)
    {
        using var udp = new UdpClient();
        udp.Connect(o.Host, o.Port);
        foreach (var e in events)
        {
            var uptime = (uint)(Environment.TickCount64 / 10 & 0xFFFFFFFF);
            var packet = SnmpTrap.Build(o.Community, o.EnterpriseOid, uptime, Random.Shared.Next(1, int.MaxValue),
            [
                e.Action, e.Result, e.Str("actor") ?? "", e.Str("user") ?? "", e.Str("session") ?? "", e.Str("reason") ?? "",
                e.Str("detail") ?? "", e.Str("server") ?? "", e.Str("ip") ?? "", e.Str("time") ?? "",
            ]);
            await udp.SendAsync(packet, ct);
        }
    }
}

/// <summary>SNMPv2c trap, encoded by hand (BER). Variables: sysUpTime.0, snmpTrapOID.0 = enterprise.0.1, then
/// enterprise.1.N for action, result, actor, user, session, reason, detail, server, ip, time (all text).</summary>
public static class SnmpTrap
{
    public static byte[] Build(string community, string enterprise, uint uptimeTicks, int requestId, IReadOnlyList<string> fields)
    {
        var varbinds = new List<byte[]>
        {
            Seq(Oid("1.3.6.1.2.1.1.3.0"), Tlv(0x43, UInt(uptimeTicks))),
            Seq(Oid("1.3.6.1.6.3.1.1.4.1.0"), Oid(enterprise + ".0.1")),
        };
        for (var i = 0; i < fields.Count; i++)
        {
            var text = Encoding.UTF8.GetBytes(fields[i]);
            varbinds.Add(Seq(Oid($"{enterprise}.1.{i + 1}"), Tlv(0x04, text.Length > 255 ? text[..255] : text)));
        }
        var pdu = Tlv(0xA7, Cat(Tlv(0x02, Int(requestId)), Tlv(0x02, Int(0)), Tlv(0x02, Int(0)), Tlv(0x30, Cat([.. varbinds]))));
        return Tlv(0x30, Cat(Tlv(0x02, Int(1)), Tlv(0x04, Encoding.UTF8.GetBytes(community)), pdu));
    }

    private static byte[] Seq(byte[] oid, byte[] value) => Tlv(0x30, Cat(oid, value));
    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Tlv(byte tag, byte[] value)
    {
        var len = value.Length < 128 ? [(byte)value.Length]
            : value.Length < 256 ? new byte[] { 0x81, (byte)value.Length }
            : new byte[] { 0x82, (byte)(value.Length >> 8), (byte)value.Length };
        return Cat([tag], len, value);
    }

    private static byte[] Int(int v)
    {
        var b = new List<byte>();
        var x = v;
        do { b.Insert(0, (byte)(x & 0xFF)); x >>= 8; } while (!((x == 0 && (b[0] & 0x80) == 0) || (x == -1 && (b[0] & 0x80) != 0)));
        return b.ToArray();
    }

    private static byte[] UInt(uint v)
    {
        var b = new List<byte>();
        do { b.Insert(0, (byte)(v & 0xFF)); v >>= 8; } while (v != 0);
        if ((b[0] & 0x80) != 0) b.Insert(0, 0);
        return b.ToArray();
    }

    private static byte[] Oid(string oid)
    {
        var arcs = oid.Split('.').Select(uint.Parse).ToArray();
        var b = new List<byte> { (byte)(arcs[0] * 40 + arcs[1]) };
        foreach (var a in arcs.Skip(2))
        {
            var stack = new Stack<byte>();
            var x = a;
            stack.Push((byte)(x & 0x7F));
            x >>= 7;
            while (x > 0) { stack.Push((byte)(0x80 | (x & 0x7F))); x >>= 7; }
            b.AddRange(stack);
        }
        return Tlv(0x06, b.ToArray());
    }
}

/// <summary>Creates the sinks from the configuration and rejects an unusable one at startup instead of at the first event.</summary>
public static class AuditSinkFactory
{
    public static List<AuditSink> Create(AuditSinks o, string version)
    {
        var sinks = new List<AuditSink>();
        string Name(SinkOptionsBase b, string kind, int i) => b.Name.Length > 0 ? b.Name : kind + (i + 1);
        for (var i = 0; i < o.Unc.Length; i++)
        {
            var u = o.Unc[i];
            if (!u.Enabled) continue;
            if (string.IsNullOrWhiteSpace(u.Path)) throw new InvalidOperationException("Sss:Audit:Sinks:Unc:Path is empty.");
            sinks.Add(new UncSink(u, Name(u, "unc", i)));
        }
        for (var i = 0; i < o.Syslog.Length; i++)
        {
            var s = o.Syslog[i];
            if (!s.Enabled) continue;
            if (string.IsNullOrWhiteSpace(s.Host)) throw new InvalidOperationException("Sss:Audit:Sinks:Syslog:Host is empty.");
            if (!new[] { "udp", "tcp", "tls" }.Contains(s.Protocol.ToLowerInvariant())) throw new InvalidOperationException("Sss:Audit:Sinks:Syslog:Protocol must be Udp, Tcp or Tls.");
            if (!new[] { "json", "cef" }.Contains(s.Format.ToLowerInvariant())) throw new InvalidOperationException("Sss:Audit:Sinks:Syslog:Format must be Json or Cef.");
            if (!new[] { "newline", "octetcount" }.Contains(s.Framing.ToLowerInvariant())) throw new InvalidOperationException("Sss:Audit:Sinks:Syslog:Framing must be Newline or OctetCount.");
            if (s.Port == 0) s.Port = s.Protocol.Equals("Tls", StringComparison.OrdinalIgnoreCase) ? 6514 : 514;
            sinks.Add(new SyslogSink(s, Name(s, "syslog", i), version));
        }
        for (var i = 0; i < o.Http.Length; i++)
        {
            var h = o.Http[i];
            if (!h.Enabled) continue;
            if (!Uri.TryCreate(h.Url, UriKind.Absolute, out var uri) || !(uri.Scheme == Uri.UriSchemeHttps || (h.AllowHttp && uri.Scheme == Uri.UriSchemeHttp)))
                throw new InvalidOperationException("Sss:Audit:Sinks:Http:Url must be an absolute https:// address (http:// only with AllowHttp).");
            if (!new[] { "ndjson", "jsonarray", "splunkhec", "elasticbulk", "text" }.Contains(h.Format.ToLowerInvariant()))
                throw new InvalidOperationException("Sss:Audit:Sinks:Http:Format must be Ndjson, JsonArray, SplunkHec, ElasticBulk or Text.");
            sinks.Add(new HttpSink(h, Name(h, "http", i)));
        }
        for (var i = 0; i < o.Snmp.Length; i++)
        {
            var n = o.Snmp[i];
            if (!n.Enabled) continue;
            if (string.IsNullOrWhiteSpace(n.Host) || string.IsNullOrWhiteSpace(n.Community)) throw new InvalidOperationException("Sss:Audit:Sinks:Snmp needs Host and Community.");
            if (!Regex.IsMatch(n.EnterpriseOid, @"^1\.3\.6\.1\.4\.1(\.\d{1,9}){1,8}$")) throw new InvalidOperationException("Sss:Audit:Sinks:Snmp:EnterpriseOid must be your own OID below 1.3.6.1.4.1, for example 1.3.6.1.4.1.12345.1.");
            sinks.Add(new SnmpSink(n, Name(n, "snmp", i)));
        }
        return sinks;
    }
}
