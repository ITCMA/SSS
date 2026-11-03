using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Sss.Web.Services;

/// <summary>A handful of counters and gauges for GET /metrics (Prometheus text format). Nothing here stores personal data.</summary>
public static class Metrics
{
    private static readonly ConcurrentDictionary<string, long> Counters = new();
    private static readonly ConcurrentDictionary<string, double> Gauges = new();

    public static void Count(string name, string? label = null, string? value = null, long by = 1) =>
        Counters.AddOrUpdate(Key(name, label, value), by, (_, v) => v + by);

    public static void Set(string name, double v, string? label = null, string? value = null) => Gauges[Key(name, label, value)] = v;

    public static void Reset()
    {
        Counters.Clear();
        Gauges.Clear();
    }

    private static string Key(string name, string? label, string? value) =>
        label is null ? name : $"{name}{{{label}=\"{Escape(value ?? "")}\"}}";

    private static string Escape(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");

    public static string Render()
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in Counters.OrderBy(x => x.Key, StringComparer.Ordinal)) sb.Append(k).Append(' ').Append(v).Append('\n');
        foreach (var (k, v) in Gauges.OrderBy(x => x.Key, StringComparer.Ordinal)) sb.Append(k).Append(' ').Append(v.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }
}
