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
