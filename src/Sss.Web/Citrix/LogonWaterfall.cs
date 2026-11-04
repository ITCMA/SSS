using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sss.Web.Models;

namespace Sss.Web.Citrix;

/// <summary>Builds the logon waterfall (brokering, HDX connection, authentication, GPO, logon scripts, profile load,
/// interactive session) from the properties of a Monitor connection record. Nothing is hard-wired to one Citrix
/// version: every <c>XStartDate</c>/<c>XEndDate</c> pair becomes the step X, and every <c>XDuration</c> without such a
/// pair a step with a known length but an estimated position.</summary>
public static partial class LogonWaterfallBuilder
{
    [GeneratedRegex(@"^(?<n>.+)StartDate$")] private static partial Regex StartRegex();

    // Director's order; steps that are not listed follow by start time.
    private static readonly string[] KnownOrder = ["Brokering", "VMStart", "VMPowerOn", "VMBoot", "Hdx", "Authentication", "Gpo", "LogOnScripts", "ProfileLoad", "Interactive"];

    // Totals and bookkeeping, not steps.
    private static readonly HashSet<string> NotSteps = new(StringComparer.OrdinalIgnoreCase)
        { "LogOn", "ClientLogOn", "Created", "Modified", "Establishment", "Session" };

    /// <summary>Flattens the simple (non-object, non-array) properties of a JSON object for <see cref="Build"/>.</summary>
    public static Dictionary<string, object?> Flatten(JsonElement e)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (e.ValueKind != JsonValueKind.Object) return d;
        foreach (var p in e.EnumerateObject())
        {
            d[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.Number => p.Value.TryGetDouble(out var n) ? n : null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => DateTimeOffset.TryParse(p.Value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) && p.Name.EndsWith("Date", StringComparison.Ordinal)
                    ? t : p.Value.GetString(),
                _ => null,
            };
        }
        return d;
    }

    /// <summary>Picks the connection that holds the logon (the first one that is not a reconnect, otherwise the one
    /// with the most steps) and turns it into a waterfall. Null when no step can be derived.</summary>
    public static LogonWaterfall? Build(IEnumerable<Dictionary<string, object?>> connections, double? sessionLogOnMs)
    {
        LogonWaterfall? best = null;
        var bestScore = -1;
        foreach (var c in connections)
        {
            var w = BuildOne(c, sessionLogOnMs);
            if (w is null) continue;
            var reconnect = c.TryGetValue("IsReconnect", out var r) && r is true;
            var score = w.Steps.Count * 2 + (reconnect ? 0 : 1000);
            if (score > bestScore) { best = w; bestScore = score; }
        }
        return best;
    }

    private static LogonWaterfall? BuildOne(Dictionary<string, object?> c, double? sessionLogOnMs)
    {
        DateTimeOffset? Date(string name) => c.TryGetValue(name, out var v) && v is DateTimeOffset t ? t : null;
        double? Num(string name) => c.TryGetValue(name, out var v) && v is double n ? n : null;

        var dated = new List<(string Id, DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var key in c.Keys)
        {
            var m = StartRegex().Match(key);
            if (!m.Success || NotSteps.Contains(m.Groups["n"].Value)) continue;
            if (Date(key) is { } s && Date(m.Groups["n"].Value + "EndDate") is { } e && e >= s)
                dated.Add((m.Groups["n"].Value, s, e));
        }
        var timed = c.Keys.Where(k => k.EndsWith("Duration", StringComparison.Ordinal))
            .Select(k => k[..^"Duration".Length])
            .Where(n => n.Length > 0 && !NotSteps.Contains(n) && !dated.Any(d => d.Id == n) && Num(n + "Duration") is > 0)
            .ToList();
        if (dated.Count == 0 && timed.Count == 0) return null;

        var logStart = Date("LogOnStartDate");
        var steps = new List<(string Id, double Start, double Duration, bool Estimated)>();
        var at = new Dictionary<string, (DateTimeOffset Start, DateTimeOffset End)>(StringComparer.OrdinalIgnoreCase);
        var explicitParent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var origin = logStart ?? (dated.Count > 0 ? dated.Min(d => d.Start) : (DateTimeOffset?)null);

        foreach (var (id, s, e) in dated)
        {
            steps.Add((id, origin is { } o ? (s - o).TotalMilliseconds : 0, (e - s).TotalMilliseconds, false));
            at[id] = (s, e);
        }

        // The machine start has two parts: power on, then boot and register (Director shows them below "machine start").
        if (Date("VMStartStartDate") is { } vs && Date("VMPoweredOnDate") is { } vp && Date("VMStartEndDate") is { } ve && vs <= vp && vp <= ve && origin is { } vo)
        {
            steps.Add(("VMPowerOn", (vs - vo).TotalMilliseconds, (vp - vs).TotalMilliseconds, false));
            steps.Add(("VMBoot", (vp - vo).TotalMilliseconds, (ve - vp).TotalMilliseconds, false));
            at["VMPowerOn"] = (vs, vp);
            at["VMBoot"] = (vp, ve);
            explicitParent["VMPowerOn"] = "VMStart";
            explicitParent["VMBoot"] = "VMStart";
        }

        // Brokering happens before the logon starts. Other steps with a length but no position are placed one after
        // another, right behind the HDX connection when there is one, otherwise at the start (flagged as estimated).
        var cursor = dated.FirstOrDefault(d => d.Id.Equals("Hdx", StringComparison.OrdinalIgnoreCase)) is { Id: not null } hdx && origin is { } o2
            ? (hdx.End - o2).TotalMilliseconds : 0;
        foreach (var id in timed.OrderBy(OrderOf))
        {
            var dur = Num(id + "Duration")!.Value;
            if (id.Equals("Brokering", StringComparison.OrdinalIgnoreCase)) { steps.Add((id, -dur, dur, true)); continue; }
            steps.Add((id, cursor, dur, true));
            cursor += dur;
        }

        // Time inside the logon window that no measured step explains (connection set-up, authentication, session start ...)
        // becomes a visible "Unaccounted" bar, so a pause in the chart is not mistaken for a gap in the data.
        var measured = steps.Where(x => !x.Estimated).OrderBy(x => x.Start).ToList();
        var frameEnd = Date("LogOnEndDate") is { } le && origin is { } o3 ? (le - o3).TotalMilliseconds : (measured.Count > 0 ? measured.Max(x => x.Start + x.Duration) : 0);
        var covered = 0.0;
        foreach (var m in measured)
        {
            if (m.Start - covered >= 1000) steps.Add(("Unaccounted", covered, m.Start - covered, false));
            covered = Math.Max(covered, m.Start + m.Duration);
        }
        if (frameEnd - covered >= 1000) steps.Add(("Unaccounted", covered, frameEnd - covered, false));

        // chronological, like the logon itself; known steps break ties in Director's order
        var ordered = steps.OrderBy(s => s.Start).ThenBy(s => OrderOf(s.Id)).ToList();
        var min = ordered.Min(s => s.Start);
        var max = ordered.Max(s => s.Start + s.Duration);
        var total = sessionLogOnMs is > 0 ? sessionLogOnMs.Value : max - Math.Min(0, min);
        // A measured step that lies completely inside a longer measured step belongs to it (group policies and logon scripts inside the
        // interactive session); the smallest such step is the parent.
        string? ParentOf((string Id, double Start, double Duration, bool Estimated) s)
        {
            if (explicitParent.TryGetValue(s.Id, out var fixedParent)) return fixedParent;
            if (s.Estimated || s.Id == "Unaccounted" || s.Duration <= 0) return null;
            var best = ordered.Where(p => !p.Estimated && p.Id != "Unaccounted" && p.Id != s.Id && p.Duration > s.Duration && !explicitParent.ContainsKey(p.Id)
                    && s.Start >= p.Start - 1 && s.Start + s.Duration <= p.Start + p.Duration + 1)
                .OrderBy(p => p.Duration).FirstOrDefault();
            return best.Id;
        }
        return new LogonWaterfall(Math.Round(Math.Max(total, max - min)),
            ordered.Select(s => new LogonStep(s.Id, Math.Round(s.Start - min), Math.Round(s.Duration), s.Estimated, ParentOf(s),
                at.TryGetValue(s.Id, out var t) ? t.Start : null, at.TryGetValue(s.Id, out var t2) ? t2.End : null)).ToList());
    }

    /// <summary>Average length of each step over several earlier logons (milliseconds). Used as the comparison value next to a step.</summary>
    public static Dictionary<string, double> AverageSteps(IEnumerable<LogonWaterfall> logons)
    {
        var sums = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in logons)
            foreach (var st in w.Steps.Where(x => !x.Estimated && x.Id != "Unaccounted" && x.DurationMs > 0))
            {
                if (!sums.TryGetValue(st.Id, out var l)) sums[st.Id] = l = [];
                l.Add(st.DurationMs);
            }
        return sums.Where(kv => kv.Value.Count >= 2).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value.Average()), StringComparer.OrdinalIgnoreCase);
    }

    private static int OrderOf(string id)
    {
        var i = Array.FindIndex(KnownOrder, k => k.Equals(id, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? KnownOrder.Length : i;
    }
}
