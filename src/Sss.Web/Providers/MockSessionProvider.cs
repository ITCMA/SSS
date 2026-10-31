using Sss.Web.Models;
using Sss.Web.Services;

namespace Sss.Web.Providers;

/// <summary>Demo data for development and tests. Contains sessions of several users to prove isolation.</summary>
public sealed class MockSessionProvider : ISessionProvider, IHelpdeskProvider, ISessionDetailsProvider, IProcessProvider
{
    /// <summary>Messages "sent" by the helpdesk (uid, text); lets tests check the call.</summary>
    public List<(string Uid, string Text)> Messages { get; } = [];

    public const string DemoSid = "S-1-5-21-1000-1000-1000-1001";
    public const string OtherSid = "S-1-5-21-1000-1000-1000-2002";

    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        [DemoSid] = "DEMO\\demo.user",
        [OtherSid] = "DEMO\\other.user",
    };

    private sealed class Entry(string ownerSid, SessionInfo info)
    {
        public string OwnerSid { get; } = ownerSid;
        public SessionInfo Info { get; set; } = info;
    }

    private readonly object _gate = new();
    private readonly List<Entry> _items = [];
    private readonly TimeProvider _clock;
    private readonly QualityEvaluator _quality;

    public MockSessionProvider(TimeProvider clock, QualityEvaluator quality)
    {
        _clock = clock;
        _quality = quality;
        Seed();
    }

    private void Seed()
    {
        var now = _clock.GetUtcNow();
        var rnd = new Random(42);

        Add(DemoSid, "1001", "Windows 11 Desktop", "Desktop", "DG-Desktops", "VDI-W11-014", now.AddHours(-5), null,
            SessionState.Active, 120, "LAPTOP-M42", "10.20.30.41", 38, 6200, 22, 3100);
        Add(DemoSid, "1002", "Microsoft Excel", "App", "DG-Apps", "RDS-APP-03", now.AddHours(-26), null,
            SessionState.Disconnected, 5400, "LAPTOP-M42", "10.20.30.41", 210, 300, 61, 8200);
        Add(DemoSid, "1003", "SAP GUI", "App", "DG-Apps", "RDS-APP-01", now.AddMinutes(-40), null,
            SessionState.Active, 15, "THINCLIENT-07", "10.20.31.9", 95, 2400, 38, 6100);
        Add(OtherSid, "2001", "Windows 11 Desktop", "Desktop", "DG-Desktops", "VDI-W11-020", now.AddHours(-2), null,
            SessionState.Active, 60, "OTHER-PC", "10.20.40.5", 30, 7000, 18, 2000);
        Add(OtherSid, "2002", "Microsoft Outlook", "App", "DG-Apps", "RDS-APP-02", now.AddHours(-30), null,
            SessionState.Disconnected, 7000, "OTHER-PC", "10.20.40.5", 70, 3000, 25, 3000);

        string[] resources = ["Windows 11 Desktop", "Microsoft Excel", "SAP GUI", "Google Chrome", "Microsoft Teams"];
        for (var i = 0; i < 14; i++)
        {
            var start = now.AddHours(-rnd.Next(3, 24 * 30));
            var end = start.AddMinutes(rnd.Next(10, 600));
            if (end > now.AddMinutes(-5)) end = now.AddMinutes(-5);
            var res = resources[rnd.Next(resources.Length)];
            Add(i % 5 == 4 ? OtherSid : DemoSid, (3000 + i).ToString(), res,
                res.Contains("Desktop") ? "Desktop" : "App",
                res.Contains("Desktop") ? "DG-Desktops" : "DG-Apps",
                $"VDI-W11-{rnd.Next(1, 30):000}", start, end, SessionState.Ended, null,
                "LAPTOP-M42", "10.20.30.41", rnd.Next(20, 260), rnd.Next(200, 8000), rnd.Next(15, 100), rnd.Next(1000, 9500));
        }
    }

    private void Add(string sid, string uid, string name, string type, string dg, string machine,
        DateTimeOffset start, DateTimeOffset? end, SessionState state, int? idle, string client, string ip,
        int rtt, int kbps, int logon, int load) =>
        _items.Add(new Entry(sid, new SessionInfo(uid, name, type, dg, machine, start, end, state, idle, client, ip,
            _quality.Evaluate(rtt, kbps, logon, load), Guid.CreateVersion7().ToString())));

    public Task<SessionQueryResult> GetSessionsAsync(UserIdentity user, TimeSpan historyWindow, CancellationToken ct)
    {
        var cutoff = _clock.GetUtcNow() - historyWindow;
        lock (_gate)
        {
            IReadOnlyList<SessionInfo> result = _items
                .Where(e => e.OwnerSid == user.Sid)
                .Select(e => e.Info)
                .Where(s => s.State != SessionState.Ended || s.EndTime >= cutoff)
                .OrderByDescending(s => s.StartTime)
                .ToList();
            return Task.FromResult(new SessionQueryResult(result, []));
        }
    }

    // ---- helpdesk (demo) -----------------------------------------------------------------------

    public Task<HelpdeskLiveResult> ListLiveAsync(string? userFilter, int max, CancellationToken ct)
    {
        lock (_gate)
        {
            var rows = _items
                .Where(e => e.Info.State != SessionState.Ended)
                .Select(e => new HelpdeskSession(e.Info, Names.GetValueOrDefault(e.OwnerSid, e.OwnerSid), e.OwnerSid))
                .Where(h => string.IsNullOrEmpty(userFilter) || h.UserName.Contains(userFilter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(h => h.UserName).ThenByDescending(h => h.Session.StartTime)
                .ToList();
            return Task.FromResult(new HelpdeskLiveResult(rows.Take(max).ToList(), rows.Count > max));
        }
    }

    public Task<IReadOnlyList<HelpdeskUser>> FindUsersAsync(string query, int max, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<HelpdeskUser> users = Names
                .Where(n => n.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(n => new HelpdeskUser(n.Value, n.Key, _items.Any(e => e.OwnerSid == n.Key && e.Info.State != SessionState.Ended)))
                .Take(max).ToList();
            return Task.FromResult(users);
        }
    }

    private readonly Dictionary<string, List<ProcessEntry>> _processes = [];

    private List<ProcessEntry> ProcessesOf(string uid)
    {
        if (!_processes.TryGetValue(uid, out var list))
        {
            var t = DateTimeOffset.UtcNow;
            _processes[uid] = list =
            [
                new(4120, "explorer", t.AddHours(-3), 148, null, true),
                new(5304, "chrome", t.AddHours(-2), 612, "Wiki - Google Chrome", true),
                new(6212, "EXCEL", t.AddMinutes(-95), 284, "Budget.xlsx - Excel", true),
                new(7188, "OUTLOOK", t.AddHours(-3), 402, "Inbox - Outlook", true),
                new(8040, "notepad", t.AddMinutes(-12), 18, "Untitled - Notepad", true),
                new(9020, "backgroundTask", t.AddHours(-3), 41, null),
            ];
        }
        return list;
    }

    public Task<ProcessListResult> ListProcessesAsync(string userSid, string uid, CancellationToken ct)
    {
        lock (_gate)
        {
            var owned = _items.Any(e => e.OwnerSid == userSid && e.Info.Uid == uid && e.Info.State != SessionState.Ended);
            return Task.FromResult(owned ? new ProcessListResult("demo-vda.demo.local", ProcessesOf(uid).ToList()) : throw new InvalidOperationException("Session not found."));
        }
    }

    public Task<ProfileInfo> GetProfileAsync(string userSid, string uid, int scanSeconds, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_items.Any(e => e.OwnerSid == userSid && e.Info.Uid == uid && e.Info.State != SessionState.Ended)) throw new InvalidOperationException("Session not found.");
            return Task.FromResult(new ProfileInfo("demo-vda.demo.local", "FSLogix", true, "C:\\Users\\demo.user", null, "\\\\fileserver\\profiles\\demo.user_S-1-5-21\\Profile_demo.user.vhdx", 9120, 30720,
                false, false, false, 6480, false, 4,
                ["Status=0"], [new("AppData", 4210), new("Documents", 1360), new("Desktop", 540), new("Downloads", 260)]));
        }
    }

    public Task<string> EndProcessAsync(string userSid, string uid, int processId, bool force, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_items.Any(e => e.OwnerSid == userSid && e.Info.Uid == uid && e.Info.State != SessionState.Ended)) throw new InvalidOperationException("Session not found.");
            var list = ProcessesOf(uid);
            var p = list.FirstOrDefault(x => x.Id == processId);
            if (p is null) return Task.FromResult("not-found");
            if (!force && p.Window is null) return Task.FromResult("no-window");
            list.Remove(p);
            return Task.FromResult(force ? "killed" : "closed");
        }
    }

    public Task<IReadOnlyList<string>> SendMessageAsync(string userSid, IReadOnlyCollection<string> uids, string title, string text, CancellationToken ct)
    {
        lock (_gate)
        {
            var sent = _items.Where(e => e.OwnerSid == userSid && uids.Contains(e.Info.Uid) && e.Info.State != SessionState.Ended)
                .Select(e => e.Info.Uid).ToList();
            foreach (var u in sent) Messages.Add((u, text));
            return Task.FromResult<IReadOnlyList<string>>(sent);
        }
    }

    public Task<SessionDetails?> GetDetailsAsync(UserIdentity user, string sessionKey, bool full, CancellationToken ct)
    {
        Entry? e;
        lock (_gate) e = _items.FirstOrDefault(x => x.OwnerSid == user.Sid && x.Info.SessionKey == sessionKey);
        if (e is null) return Task.FromResult<SessionDetails?>(null);

        var now = _clock.GetUtcNow();
        var rnd = new Random(e.Info.Uid.GetHashCode());
        var rtt = new List<double[]>();
        var bw = new List<double[]>();
        var dc = new List<double[]>();
        for (var i = 60; i >= 0; i--)
        {
            var t = now.AddMinutes(-i * 2).ToUnixTimeMilliseconds();
            rtt.Add([t, (e.Info.Quality.IcaRttMs ?? 40) + rnd.Next(-8, 25) + (i % 17 == 0 ? 60 : 0)]);
            bw.Add([t, (e.Info.Quality.BandwidthKbps ?? 5000) + rnd.Next(-400, 400)]);
            dc.Add([t, 2 + rnd.Next(0, 6)]);
        }
        var i0 = e.Info;
        var groups = new List<DetailGroup>
        {
            new("session", [new("StartDate", i0.StartTime.ToString("o")), new("LifecycleState", i0.State.ToString()), new("SessionType", i0.ResourceType)]),
            new("logon", [new("LogOnDuration", ((i0.Quality.LogonSeconds ?? 20) * 1000).ToString(), "ms"), new("BrokeringDuration", "850", "ms"),
                new("ProfileLoadDuration", "4200", "ms"), new("GroupPolicyDuration", "2300", "ms"), new("LogonScriptsDuration", "1100", "ms")]),
            new("client", [new("ClientName", i0.ClientName ?? ""), new("ClientAddress", i0.ClientAddress ?? ""), new("ClientVersion", "2507.1"), new("Protocol", "HDX")]),
            new("network", [new("IcaRttMS", (i0.Quality.IcaRttMs ?? 0).ToString(), "ms"), new("OutputBandwidthAvailable", (i0.Quality.BandwidthKbps ?? 0).ToString(), "kbit/s")]),
            new("machine", [new("DnsName", i0.Machine), new("OSType", "Windows 11"), new("AgentVersion", "2507.0.0.1")]),
            new("technical", [new("SessionKey", sessionKey), new("Uid", i0.Uid)]),
        };
        IReadOnlyList<DetailSeries> series =
        [
            new("IcaRttMS", "ms", rtt),
            new("OutputBandwidthAvailable", "kbit/s", bw),
            new("DcLatency", "ms", dc),
        ];
        // Demo logon: the profile load is the slow part, like in many real sites.
        var logonMs = (i0.Quality.LogonSeconds ?? 20) * 1000.0;
        var steps = new List<LogonStep>
        {
            new("Brokering", 0, 850, true), new("Hdx", 850, 1900, false), new("Authentication", 2750, 1400, true), new("Gpo", 2750, 2300, false),
            new("LogOnScripts", 5050, 1100, false), new("ProfileLoad", 6150, 4200 + (rnd.Next(0, 3) * 4000), false), new("Interactive", 10350 + (rnd.Next(0, 3) * 4000), 3200, false),
        };
        var total = steps.Max(s => s.StartMs + s.DurationMs);
        return Task.FromResult<SessionDetails?>(new SessionDetails(sessionKey, groups, series, [], [], new LogonWaterfall(Math.Max(total, logonMs), steps)));
    }

    public Task<IReadOnlyList<string>> LogoffAsync(UserIdentity user, IReadOnlyCollection<string> uids, CancellationToken ct)
    {
        var done = new List<string>();
        lock (_gate)
        {
            foreach (var e in _items.Where(e => e.OwnerSid == user.Sid && uids.Contains(e.Info.Uid) && e.Info.State != SessionState.Ended))
            {
                e.Info = e.Info with { State = SessionState.Ended, EndTime = _clock.GetUtcNow(), IdleSeconds = null };
                done.Add(e.Info.Uid);
            }
        }
        return Task.FromResult<IReadOnlyList<string>>(done);
    }
}

/// <summary>Demo names for the SIDs used by <see cref="MockSessionProvider"/>.</summary>
public sealed class MockUserDirectory : Sss.Web.Services.IUserDirectory
{
    public string NameOf(string sid) => MockSessionProvider.Names.GetValueOrDefault(sid, sid);
}
