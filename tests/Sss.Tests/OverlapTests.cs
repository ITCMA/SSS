using Sss.Web;
using Sss.Web.Models;
using Sss.Web.Services;

namespace Sss.Tests;

public class OverlapTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static OverlapSession S(string key, string group, double startHoursAgo, double? endHoursAgo, string machine = "m1") =>
        new(key, group, machine, "PC", endHoursAgo is null ? "open" : "ended", Now.AddHours(-startHoursAgo), endHoursAgo is null ? null : Now.AddHours(-endHoursAgo.Value));

    [Fact]
    public void Sessions_after_each_other_do_not_overlap()
    {
        Assert.Null(OverlapDetector.Detect([S("a", "G", 10, 8), S("b", "G", 7, 5)], Now, new OverlapOptions()));
        Assert.Null(OverlapDetector.Detect([S("a", "G", 10, 8), S("b", "G", 8, 5)], Now, new OverlapOptions()));   // touching is not overlapping
    }

    [Fact]
    public void Overlapping_sessions_are_found_with_the_length_of_the_overlap()
    {
        var r = OverlapDetector.Detect([S("a", "G", 10, 6), S("b", "G", 8, 4)], Now, new OverlapOptions())!;
        Assert.Equal(1, r.Count);
        Assert.Equal(2 * 3600, r.LongestSeconds);
        Assert.True(r.SameGroup);
        Assert.False(r.Ongoing);
    }

    [Fact]
    public void An_overlap_of_a_few_seconds_is_found_with_the_default_settings()
    {
        // a logon that starts while the previous short session is still closing: 13 seconds in the same delivery group
        var t = new DateTimeOffset(2026, 11, 17, 7, 41, 0, TimeSpan.Zero);
        var a = new OverlapSession("1", "Group", "vda-1", "PC", "ended", t, t.AddSeconds(176));
        var b = new OverlapSession("2", "Group", "vda-4", "PC", "ended", t.AddSeconds(163), t.AddHours(4).AddMinutes(27));
        var r = OverlapDetector.Detect([a, b], t.AddDays(1), new OverlapOptions())!;
        Assert.Equal(1, r.Count);
        Assert.Equal(13, r.LongestSeconds);
        Assert.True(r.SameGroup);
    }

    [Fact]
    public void Sessions_connected_by_overlaps_form_one_group_with_one_row_each()
    {
        // one long session, five sessions next to each other inside it, and a late one that touches two of them for a few seconds
        var day = new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
        OverlapSession At(string key, int h1, int m1, int s1, int h2, int m2, int s2) =>
            new(key, "G", key, "PC", "ended", day.AddHours(h1).AddMinutes(m1).AddSeconds(s1), day.AddHours(h2).AddMinutes(m2).AddSeconds(s2));
        var sessions = new List<OverlapSession> { At("long", 14, 3, 26, 23, 2, 57), At("late", 22, 8, 52, 22, 40, 19) };
        for (var i = 1; i <= 5; i++) sessions.Add(At($"vda-{i}", 14, 12 + (i > 3 ? 1 : 0), 40 + i, 22, 8, 29 + (i == 5 ? 31 : i)));
        sessions.Add(At("earlier", 6, 0, 0, 7, 0, 0));                       // overlaps with nothing

        var r = OverlapDetector.Detect(sessions, day.AddDays(1), new OverlapOptions())!;

        var g = Assert.Single(r.Groups);
        Assert.Equal(1, r.GroupCount);
        Assert.Equal(17, r.Count);                                            // pairs, as the Insights list counts them
        Assert.Equal(7, g.Sessions.Count);
        Assert.Equal("long", g.Sessions[0].Key);                              // rows in the order the sessions started
        Assert.Equal("late", g.Sessions[^1].Key);
        Assert.Equal(6, g.Peak);
        var shared = Assert.Single(g.Shared);                                 // the late session starts while one of the five still runs
        Assert.Equal(sessions[1].End, shared.To);
        Assert.Equal(g.Seconds, (int)g.Shared.Sum(x => (x.To - x.From).TotalSeconds));
        Assert.All(g.Shared, x => Assert.True(x.From >= g.From && x.To <= g.To));
    }

    [Fact]
    public void Unrelated_overlaps_stay_separate_groups_newest_first()
    {
        var r = OverlapDetector.Detect([S("a", "G", 30, 28), S("b", "G", 29, 27), S("c", "G", 10, 8), S("d", "G", 9, 7)], Now, new OverlapOptions())!;
        Assert.Equal(2, r.GroupCount);
        Assert.True(r.Groups[0].To > r.Groups[1].To);
        Assert.All(r.Groups, g => { Assert.Equal(2, g.Peak); Assert.Equal(3600, g.Seconds); });
    }

    [Fact]
    public void Two_open_sessions_overlap_until_now_and_are_ongoing()
    {
        var r = OverlapDetector.Detect([S("a", "G", 5, null), S("b", "G", 2, null)], Now, new OverlapOptions())!;
        Assert.True(r.Ongoing);
        Assert.Equal(2 * 3600, r.LongestSeconds);
    }

    [Fact]
    public void Different_groups_are_a_note_and_can_be_switched_off()
    {
        var sessions = new[] { S("a", "G1", 10, 6), S("b", "G2", 8, 4) };
        var r = OverlapDetector.Detect(sessions, Now, new OverlapOptions())!;
        Assert.False(r.SameGroup);
        Assert.Null(OverlapDetector.Detect(sessions, Now, new OverlapOptions { SameDeliveryGroupOnly = true }));
    }

    [Fact]
    public void Minimum_minutes_default_to_zero_and_ignore_short_overlaps_when_set()
    {
        var three = new[] { S("a", "G", 10, 5.95), S("b", "G", 6, 4) };      // 3 minutes
        Assert.NotNull(OverlapDetector.Detect(three, Now, new OverlapOptions()));
        Assert.Null(OverlapDetector.Detect(three, Now, new OverlapOptions { MinMinutes = 5 }));
        Assert.Equal(0, new OverlapOptions().MinMinutes);
    }

    [Fact]
    public void Ignored_delivery_groups_and_the_switch_are_respected()
    {
        var sessions = new[] { S("a", "Harmless", 10, 6), S("b", "Harmless", 8, 4) };
        Assert.Null(OverlapDetector.Detect(sessions, Now, new OverlapOptions { IgnoreDeliveryGroups = ["harmless"] }));
        Assert.Null(OverlapDetector.Detect([S("a", "G", 10, 6), S("b", "G", 8, 4)], Now, new OverlapOptions { Enabled = false }));
    }

    [Fact]
    public void Old_sessions_outside_the_days_are_not_compared_and_the_list_is_capped()
    {
        Assert.Null(OverlapDetector.Detect([S("a", "G", 24 * 10, 24 * 10 - 5), S("b", "G", 24 * 10 - 1, 24 * 10 - 4)], Now, new OverlapOptions { Days = 7 }));
        var many = Enumerable.Range(0, 12).Select(i => S($"s{i}", "G", 20 - i * 0.1, 1)).ToList();   // 66 overlapping pairs
        var r = OverlapDetector.Detect(many, Now, new OverlapOptions())!;
        Assert.Equal(66, r.Count);
        Assert.Equal(30, r.Items.Count);
    }

    [Fact]
    public async Task The_service_reports_overlaps_for_the_demo_user_and_asks_for_no_history_when_only_live_sessions_are_needed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sss-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new Sss.Web.Providers.MockSessionProvider(TimeProvider.System, new QualityEvaluator(new QualityThresholds()));
            var svc = new SessionService(provider, new AuditLog(dir, TimeProvider.System), TimeProvider.System, new SssOptions());
            var me = new UserIdentity(Sss.Web.Providers.MockSessionProvider.DemoSid, "DEMO\\me");
            var withRange = await svc.GetAsync(me, TimeSpan.FromHours(1), default);
            Assert.NotNull(withRange.Overlap);          // the 1 hour range still looks 7 days back for overlaps
            Assert.True(withRange.Overlap!.Ongoing);
            var liveOnly = await svc.GetAsync(me, TimeSpan.Zero, default);
            Assert.Null(liveOnly.Overlap);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
