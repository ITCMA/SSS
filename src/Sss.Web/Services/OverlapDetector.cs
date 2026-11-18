using Sss.Web.Models;

namespace Sss.Web.Services;

/// <summary>Finds sessions of one user that ran at the same time. Pure function of the session times, so the user's own view, the helpdesk
/// view and the Insights list use the same rule and need no extra query.</summary>
public static class OverlapDetector
{
    private const int MaxItems = 30, MaxSessions = 200;

    /// <summary>The overlaps among <paramref name="sessions"/> (open sessions run until <paramref name="now"/>), or null when there are none.</summary>
    public static OverlapSummary? Detect(IEnumerable<OverlapSession> sessions, DateTimeOffset now, OverlapOptions o)
    {
        if (!o.Enabled) return null;
        var days = Math.Clamp(o.Days, 1, 30);
        var since = now.AddDays(-days);
        var ignore = new HashSet<string>(o.IgnoreDeliveryGroups.Where(g => !string.IsNullOrWhiteSpace(g)), StringComparer.OrdinalIgnoreCase);
        var list = sessions.Where(s => (s.End ?? now) >= since).OrderByDescending(s => s.Start).Take(MaxSessions).ToList();
        var minSeconds = Math.Max(0, o.MinMinutes) * 60;
        var found = new List<SessionOverlap>();
        for (var i = 0; i < list.Count; i++)
        {
            for (var j = i + 1; j < list.Count; j++)
            {
                var a = list[i]; var b = list[j];
                if (string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase)) continue;
                if (ignore.Contains(a.Group) || ignore.Contains(b.Group)) continue;
                var from = a.Start > b.Start ? a.Start : b.Start;
                var to = (a.End ?? now) < (b.End ?? now) ? (a.End ?? now) : (b.End ?? now);
                var seconds = (int)Math.Min(int.MaxValue, Math.Max(0, (to - from).TotalSeconds));
                if (seconds <= 0 || seconds < minSeconds) continue;
                var same = a.Group.Length > 0 && string.Equals(a.Group, b.Group, StringComparison.OrdinalIgnoreCase);
                if (o.SameDeliveryGroupOnly && !same) continue;
                found.Add(new SessionOverlap(a, b, from, to, a.End is null && b.End is null, same, seconds));
            }
        }
        if (found.Count == 0) return null;
        found = found.OrderByDescending(x => x.From).ToList();
        return new OverlapSummary(found.Count, found.Max(x => x.Seconds), found.Any(x => x.Ongoing), found.Any(x => x.SameGroup), days, found.Take(MaxItems).ToList());
    }

    public static OverlapSession Of(SessionInfo s) => new(s.SessionKey ?? s.Uid, s.DeliveryGroup ?? "", s.Machine ?? "", s.ClientName,
        s.State.ToString().ToLowerInvariant(), s.StartTime, s.EndTime);
}
