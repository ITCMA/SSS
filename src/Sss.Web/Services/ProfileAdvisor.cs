using System.Globalization;
using Sss.Web.Models;

namespace Sss.Web.Services;

/// <summary>Turns what the VDA reported about a profile into findings a support person can act on: full containers,
/// roaming profiles, heavy caches, data that is not redirected, missing exclusions, leftover local profiles.
/// Pure rules, no I/O; the texts are translated in the browser by the hint id.</summary>
public static class ProfileAdvisor
{
    private const int GB = 1024;

    // Folders that are known to grow and can usually be excluded or redirected. Paths are relative to the profile root.
    private static readonly (string Path, string Id)[] Heavy =
    [
        (@"AppData\Local\Microsoft\Outlook", "cacheOutlook"),
        (@"AppData\Local\Microsoft\Teams", "cacheTeams"),
        (@"AppData\Roaming\Microsoft\Teams", "cacheTeams"),
        (@"AppData\Local\Google\Chrome", "cacheBrowser"),
        (@"AppData\Local\Microsoft\Edge", "cacheBrowser"),
        (@"AppData\Local\Mozilla", "cacheBrowser"),
        (@"AppData\Local\Temp", "cacheTemp"),
    ];

    private static readonly string[] UserData = ["Documents", "Desktop", "Downloads", "Pictures", "Videos", "Music"];

    public static IReadOnlyList<ProfileHint> Analyze(ProfileInfo p)
    {
        var hints = new List<ProfileHint>();
        void Add(string id, string level, params (string K, string V)[] args) =>
            hints.Add(new ProfileHint(id, level, args.Length == 0 ? null : args.ToDictionary(a => a.K, a => a.V)));
        static string Size(int mb) => mb >= GB ? $"{(mb / (double)GB).ToString("0.#", CultureInfo.InvariantCulture)} GB" : $"{mb} MB";

        if (p.Temporary) Add("tempProfile", "crit");
        if (p.Notes.Contains("corrupted")) Add("corrupted", "crit");

        if (p.ContainerMb is { } used && p.ContainerMaxMb is > 0 and var max)
        {
            var pct = (int)Math.Round(used * 100.0 / max);
            if (pct >= 85) Add("containerFull", pct >= 95 ? "crit" : "warn", ("pct", pct.ToString(CultureInfo.InvariantCulture)), ("used", Size(used)), ("max", Size(max)));
            if (p.SizeMb is { } folder && used > folder * 1.5 + 2 * GB) Add("containerBloated", "info", ("container", Size(used)), ("folder", Size(folder)));
        }

        var redirected = p.Redirected.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var total = p.SizeMb ?? 0;
        var byPath = p.Tree.ToDictionary(n => n.Path, n => n.Mb, StringComparer.OrdinalIgnoreCase);

        foreach (var (path, id) in Heavy)
            if (byPath.TryGetValue(path, out var mb) && (mb >= GB || (total > 0 && mb >= 200 && mb * 100 >= total * 25)))
                Add(id, mb >= 5 * GB ? "warn" : "info", ("size", Size(mb)), ("path", path), ("share", Share(mb, total)));

        foreach (var name in UserData)
            if (byPath.TryGetValue(name, out var mb) && mb >= 2 * GB && !redirected.Contains(name))
                Add("userDataLocal", "info", ("size", Size(mb)), ("path", name));

        foreach (var (path, mb) in p.Tree)
            if (path.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase) && !path.Contains('\\') && mb >= GB)
                Add("cacheOneDrive", mb >= 5 * GB ? "warn" : "info", ("size", Size(mb)), ("path", path), ("share", Share(mb, total)));

        if (byPath.TryGetValue("AppData", out var appData) && total >= 2 * GB && appData * 100 >= total * 70)
            Add("appdataDominates", "info", ("size", Size(appData)), ("share", Share(appData, total)));

        if (p.Type == "Roaming")
        {
            if (total >= GB) Add("roamingLarge", "warn", ("size", Size(total)));
            if (p.Redirected.Count == 0) Add("roamingNoRedirection", "info");
        }

        if (p.Type == "Citrix UPM")
        {
            var streaming = p.Settings.FirstOrDefault(s => s.Source == "Citrix UPM" && s.Name.Equals("PSEnabled", StringComparison.OrdinalIgnoreCase));
            if (streaming is { Value: "0" }) Add("upmNoStreaming", "info");
            if (p.Exclusions.Count == 0 && total >= 2 * GB) Add("upmNoExclusions", "info", ("size", Size(total)));
        }

        if (p.Type == "FSLogix" && p.Exclusions.Count == 0 && total >= 2 * GB && !p.Notes.Contains("redirections-xml-not-readable"))
            Add("fslogixNoExclusions", "info", ("size", Size(total)));

        if (p.LocalProfiles is >= 20 and var n) Add("manyLocalProfiles", "warn", ("count", n.ToString(CultureInfo.InvariantCulture)));
        if (p.SizePartial) Add("scanPartial", "info", ("seconds", p.ScanSeconds.ToString(CultureInfo.InvariantCulture)));

        return hints;
    }

    private static string Share(int mb, int total) => total > 0 ? Math.Round(mb * 100.0 / total).ToString("0", CultureInfo.InvariantCulture) : "0";
}
