using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sss.Web.Audit;

public sealed record AuditQuery(DateTimeOffset From, DateTimeOffset To, string? Actor, string? Target, string? Action, string? Result,
    string? Server, string? Text, string? Stream, int Limit);

public sealed record AuditRow(string? Id, DateTimeOffset Time, string Stream, string Action, string? Actor, string? ActorSid, string? Role, string? User,
    string? Sid, string? Session, string Result, string? Ip, string? Server, string? Reason, string? Detail, string? RequestId);

public sealed record Count(string Key, int N);

public sealed record AuditSummary(IReadOnlyList<Count> ByDay, IReadOnlyList<Count> ByActor, IReadOnlyList<Count> ByAction,
    IReadOnlyList<Count> ByResult, IReadOnlyList<Count> ByServer, int Problems);

public sealed record AuditResult(IReadOnlyList<AuditRow> Events, int Matching, int Scanned, bool Truncated, AuditSummary Summary, IReadOnlyList<string> Sources);

/// <summary>Reads the JSONL files of the local folder and of every central folder (a UNC sink), merges them and removes
/// duplicates by event id, so an auditor sees all servers even when the web server they are connected to is one of many.</summary>
public sealed partial class AuditReader(IReadOnlyList<string> directories)
{
    private const int MaxScanned = 500_000;

    [GeneratedRegex(@"^(audit|ops)-(\d{8})(-.+)?\.jsonl$")] private static partial Regex FileRegex();

    public AuditResult Query(AuditQuery q)
    {
        var rows = new Dictionary<string, AuditRow>();
        var scanned = 0;
        var truncated = false;
        var fromDay = q.From.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var toDay = q.To.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var sources = new List<string>();

        foreach (var dir in directories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sources.Add(dir);
            foreach (var path in Directory.GetFiles(dir, "*.jsonl").Order(StringComparer.Ordinal))
            {
                var m = FileRegex().Match(Path.GetFileName(path));
                if (!m.Success) continue;
                if (q.Stream is { Length: > 0 } st && !m.Groups[1].Value.Equals(st, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.CompareOrdinal(m.Groups[2].Value, fromDay) < 0 || string.CompareOrdinal(m.Groups[2].Value, toDay) > 0) continue;
                IEnumerable<string> lines;
                try { lines = File.ReadLines(path); } catch (IOException) { continue; }
                using var e = lines.GetEnumerator();
                while (true)
                {
                    try { if (!e.MoveNext()) break; } catch (IOException) { break; }
                    var line = e.Current;
                    if (line.Length == 0) continue;
                    if (++scanned > MaxScanned) { truncated = true; break; }
                    var row = Parse(line);
                    if (row is null || row.Time < q.From || row.Time > q.To || !Matches(row, q)) continue;
                    rows[row.Id ?? line] = row;
                }
                if (truncated) break;
            }
            if (truncated) break;
        }

        var all = rows.Values.OrderByDescending(r => r.Time).ToList();
        Count[] Top(IEnumerable<string?> keys, int n) => keys.Where(k => !string.IsNullOrEmpty(k)).GroupBy(k => k!).OrderByDescending(g => g.Count()).Take(n).Select(g => new Count(g.Key, g.Count())).ToArray();
        var summary = new AuditSummary(
            all.GroupBy(r => r.Time.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).OrderBy(g => g.Key).Select(g => new Count(g.Key, g.Count())).ToArray(),
            Top(all.Select(r => r.Actor), 10), Top(all.Select(r => r.Action), 12), Top(all.Select(r => r.Result), 8), Top(all.Select(r => r.Server), 8),
            all.Count(r => IsProblem(r)));
        return new AuditResult(all.Take(Math.Clamp(q.Limit, 1, 5000)).ToList(), all.Count, scanned, truncated, summary, sources);
    }

    private static bool IsProblem(AuditRow r) =>
        r.Stream == "ops" || r.Action.StartsWith("alert:", StringComparison.OrdinalIgnoreCase)
        || r.Result.StartsWith("fail", StringComparison.OrdinalIgnoreCase) || r.Result.StartsWith("rejected", StringComparison.OrdinalIgnoreCase)
        || r.Result.StartsWith("denied", StringComparison.OrdinalIgnoreCase) || r.Result.StartsWith("error", StringComparison.OrdinalIgnoreCase);

    private static bool Has(string? value, string? needle) => string.IsNullOrWhiteSpace(needle) || (value?.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool Matches(AuditRow r, AuditQuery q) =>
        Has(r.Actor, q.Actor) && Has(r.User, q.Target) && Has(r.Action, q.Action) && Has(r.Result, q.Result) && Has(r.Server, q.Server)
        && (string.IsNullOrWhiteSpace(q.Text) || new[] { r.Actor, r.User, r.Action, r.Result, r.Reason, r.Detail, r.Session, r.Ip, r.RequestId }.Any(v => Has(v, q.Text)));

    private static AuditRow? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            if (!DateTimeOffset.TryParse(S("time"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)) return null;
            return new AuditRow(S("id"), time, S("stream") ?? "audit", S("action") ?? "", S("actor") ?? S("user"), S("actorSid") ?? S("sid"), S("role"),
                S("user"), S("sid"), S("session"), S("result") ?? "", S("ip"), S("server"), S("reason"), S("detail"), S("requestId"));
        }
        catch (JsonException) { return null; }
    }
}
