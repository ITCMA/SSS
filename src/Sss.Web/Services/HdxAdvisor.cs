using System.Globalization;
using System.Text.RegularExpressions;
using Sss.Web.Models;

namespace Sss.Web.Services;

/// <summary>Sorts the Citrix policy values in force into the HDX channel groups a support person knows (drives, clipboard, printing, audio,
/// USB, graphics, network) and rates each group: allowed, limited, blocked. Pure rules, no I/O. The VDA stores policies as registry values whose
/// names differ a little between versions, so the rules match on name fragments and read the usual value shapes (0/1, Allowed/Prohibited,
/// ReadOnly, ToClient ...). A value the rules cannot rate leaves the group at "configured" and is shown as it is.</summary>
public static class HdxAdvisor
{
    public static readonly string[] ChannelIds = ["drive", "clipboard", "printing", "audio", "usb", "graphics", "network"];

    private static readonly (string Id, Regex Match)[] Groups =
    [
        ("drive", new(@"drive(?!r(?!edir))|cdm|clientdisk", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("clipboard", new(@"clipboard", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("printing", new(@"print", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("audio", new(@"audio|sound|microphone|\bmic", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("usb", new(@"usb", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("graphics", new(@"visualquality|framespersecond|framerate|fps|colordepth|compression|thinwire|h26[45]|hdx3d|graphics|image|lossless|monitors|displaymemory|video", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("network", new(@"bandwidth|edt|udp|adaptive|reliab|multistream|transport", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    ];

    // The switch of a channel group is one policy ("Allow...Redir"); the other values of the group refine it and must not block the whole group.
    private static readonly Dictionary<string, Regex> Primary = new()
    {
        ["drive"] = new(@"^Allow(Client)?Drive(s)?Redir(ection)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        ["clipboard"] = new(@"^Allow(Client)?Clipboard(s)?Redir(ection)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        ["printing"] = new(@"^Allow(Client)?Printer(s)?Redir(ection)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        ["audio"] = new(@"^Allow(Client)?Audio(s)?Redir(ection)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        ["usb"] = new(@"^Allow(Client)?USB(s)?(Device)?Redir(ection)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    private static readonly Regex Negative = new(@"prohibit|disable|deny|denied|block|forbid", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Allowing = new(@"allow|enable|redir|redirect|auto|create|use", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BandwidthName = new(@"bandwidth.*limit|limit.*bandwidth|overallsession", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The policy rows of one session, grouped. Every channel is always present, so the view stays the same.</summary>
    public static IReadOnlyList<HdxChannel> Channels(IEnumerable<PolicyEntry> entries)
    {
        var all = entries.ToList();
        var result = new List<HdxChannel>();
        foreach (var (id, match) in Groups)
        {
            var rows = all.Where(e => match.IsMatch(e.Name) || (id != "drive" && id != "network" && id != "graphics" && match.IsMatch(e.Path))).ToList();
            var (state, mode, off) = Rate(id, rows);
            result.Add(new HdxChannel(id, state, mode, rows, off));
        }
        return result;
    }

    private static (string State, string? Mode, IReadOnlyList<string>? Off) Rate(string id, List<PolicyEntry> rows)
    {
        if (rows.Count == 0) return ("unknown", null, null);
        if (Primary.TryGetValue(id, out var primary) && rows.Where(e => primary.IsMatch(e.Name)).ToList() is { Count: > 0 } main)
        {
            if (main.Any(e => RateValue(e.Name, e.Value.Trim(), out _) == "blocked")) return ("blocked", "off", null);
            // a word such as Prohibited in another value of the group is a block too (only a bare 0 in a sub-function narrows it down)
            if (rows.Except(main).Any(e => !int.TryParse(e.Value.Trim(), out _) && RateValue(e.Name, e.Value.Trim(), out _) == "blocked")) return ("blocked", "off", null);
            // the group is on; sub-functions that are switched off (Allow...=0) and direction words in the values narrow it down
            var off = rows.Except(main).Where(e => e.Name.StartsWith("Allow", StringComparison.OrdinalIgnoreCase) && e.Value.Trim() == "0"
                    && Groups.First(g => g.Id == id).Match.IsMatch(e.Name) && !Regex.IsMatch(e.Name, "Driver|Inbox", RegexOptions.IgnoreCase))
                .Select(e => e.Name[5..]).Distinct().ToList();
            var text = rows.Except(main).Select(e => { var r = RateValue(e.Name, e.Value.Trim(), out var m); return (r, m); }).FirstOrDefault(x => x.r == "limited" && x.m is not null);
            if (text.m is not null) return ("limited", text.m, off.Count > 0 ? off : null);
            if (off.Count > 0) return ("limited", "partial", off);
            return ("allowed", "both", null);
        }
        string? mode = null;
        var blocked = false; var limited = false; var allowed = false;
        foreach (var e in rows)
        {
            var v = e.Value.Trim();
            var rating = RateValue(e.Name, v, out var m);
            if (rating == "blocked") blocked = true;
            else if (rating == "limited") { limited = true; mode ??= m; }
            else if (rating == "allowed") { allowed = true; mode ??= m; }
        }
        if (blocked) return ("blocked", "off", null);
        if (limited) return ("limited", mode, null);
        if (allowed) return ("allowed", mode, null);
        return ("configured", null, null);
    }

    /// <summary>blocked, limited, allowed or null (cannot say) for one value; <paramref name="mode"/> names a direction or mode when the value tells.</summary>
    private static string? RateValue(string name, string value, out string? mode)
    {
        mode = null;
        if (Regex.IsMatch(value, @"read.?only", RegexOptions.IgnoreCase)) { mode = "readOnly"; return "limited"; }
        if (Regex.IsMatch(value, @"to.?client|server.?to.?client", RegexOptions.IgnoreCase)) { mode = "toClient"; return "limited"; }
        if (Regex.IsMatch(value, @"to.?server|client.?to.?server", RegexOptions.IgnoreCase)) { mode = "toServer"; return "limited"; }
        if (Regex.IsMatch(value, @"^(prohibited|disabled|denied|blocked|none|off|no|false)$", RegexOptions.IgnoreCase)) return "blocked";
        if (Regex.IsMatch(value, @"^(allowed|enabled|on|yes|true|bidirectional|both)$", RegexOptions.IgnoreCase)) { mode = "both"; return "allowed"; }
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n is < 0 or > 1) return null;
        // a plain 0/1: the word in the name says which way round it reads
        if (Negative.IsMatch(name)) return n == 1 ? "blocked" : "allowed";
        if (Allowing.IsMatch(name)) return n == 1 ? "allowed" : "blocked";
        return null;
    }

    /// <summary>Hints that tie the policy to what the support person sees ("pasting does not work", "graphics stutter").</summary>
    public static IReadOnlyList<HdxHint> Hints(IReadOnlyList<HdxChannel> channels, HdxLive? live)
    {
        var hints = new List<HdxHint>();
        foreach (var c in channels)
        {
            if (c.State == "blocked") hints.Add(new HdxHint("blocked", "info", new Dictionary<string, string> { ["channel"] = c.Id }));
            else if (c.State == "limited") hints.Add(new HdxHint("limited", "info", new Dictionary<string, string> { ["channel"] = c.Id, ["mode"] = c.Mode ?? "", ["off"] = string.Join(", ", c.Off ?? []) }));
        }
        // a bandwidth limit that the session almost uses up
        var limit = channels.First(c => c.Id == "network").Policies
            .Where(p => BandwidthName.IsMatch(p.Name) && double.TryParse(p.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
            .Select(p => (double?)double.Parse(p.Value, CultureInfo.InvariantCulture)).FirstOrDefault();
        var used = live?.Counters.FirstOrDefault(c => c.Name.StartsWith("Output Session Bandwidth", StringComparison.OrdinalIgnoreCase))?.Value;
        if (limit is { } l && used is { } u)
        {
            // the counter is read as bits per second (policy: kbit/s)
            var usedKbit = u / 1000;
            if (usedKbit >= l * 0.85)
                hints.Add(new HdxHint("bandwidthLimit", "warn", new Dictionary<string, string>
                    { ["limit"] = l.ToString("0", CultureInfo.InvariantCulture), ["used"] = usedKbit.ToString("0", CultureInfo.InvariantCulture) }));
        }
        return hints;
    }
}
