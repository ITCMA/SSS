using System.Diagnostics;
using System.Reflection;
using Sss.Web.Audit;
using Sss.Web.Citrix;
using Sss.Web.Models;

namespace Sss.Web.Services;

/// <summary>Live health checks for the status page (helpdesk only). Each check has a stable id the UI translates;
/// the detail text is technical and only shown to helpdesk members. Nothing here changes any state.</summary>
public sealed class StatusService(
    SssOptions options, HelpdeskAccess helpdesk, TimeProvider clock,
    IBrokerClient? broker = null, IMonitorClient? monitor = null, IReadOnlyList<string>? unknownConfigKeys = null,
    ControllerDirectory? controllers = null, ControllerDiscoveryService? discovery = null, AuditForwarder? forwarder = null, string? auditDirectory = null,
    InstanceRegistry? instances = null)
{
    private static readonly DateTimeOffset Started = new(Process.GetCurrentProcess().StartTime.ToUniversalTime());

    private (DateTimeOffset At, bool Ok, string Status)? _health;

    /// <summary>Cheap and cached for 10 s, so a load balancer polling often does not load the Delivery Controller.</summary>
    public string LastStatus => _health?.Status ?? "ok";

    public async Task<(bool Ok, string Status)> HealthAsync(CancellationToken ct)
    {
        if (instances?.Draining == true) return (false, "draining");   // not cached: the load balancer must see it at once
        if (_health is { } h && clock.GetUtcNow() - h.At < TimeSpan.FromSeconds(10)) return (h.Ok, h.Status);
        var ok = true;
        var status = "ok";
        if (broker is not null)
            try { await broker.PingAsync(ct); } catch (Exception ex) when (ex is not OperationCanceledException) { ok = false; status = "unavailable"; }
        if (ok && monitor is not null)
            try { await monitor.PingAsync(ct); } catch (Exception ex) when (ex is not OperationCanceledException) { status = "degraded"; }
        _health = (clock.GetUtcNow(), ok, status);
        return (ok, status);
    }

    public async Task<IReadOnlyList<StatusCheck>> RunAsync(CancellationToken ct)
    {
        var checks = new List<StatusCheck>
        {
            new("version", true, AppInfo.Version),
            new("provider", true, options.Provider),
            new("identity", true, CurrentIdentity()),
            new("uptime", true, (clock.GetUtcNow() - Started).ToString(@"d\.hh\:mm\:ss")),
            AuditCheck(),
            unknownConfigKeys is { Count: > 0 }
                ? new("config", null, "unknown key(s) ignored: " + string.Join("; ", unknownConfigKeys))
                : new("config", true, "ok"),
            new("helpdesk", helpdesk.Enabled ? true : null, helpdesk.Enabled ? RoleGroups() : "not configured"),
        };

        if (broker is not null)
            checks.Add(await TimedAsync("broker", () => broker.PingAsync(ct)));
        if (monitor is not null)
        {
            checks.Add(await TimedAsync("monitor", () => monitor.PingAsync(ct)));
            var https = ODataMonitorClient.BuildBaseAddresses(options.Citrix).All(u => u.Scheme == Uri.UriSchemeHttps);
            checks.Add(new("monitor-https", https ? true : null, https ? "https" : "http (set Sss:Citrix:Monitor:UseHttps to true or BaseUrl to https://)"));
        }
        else if (!options.Provider.Equals("Mock", StringComparison.OrdinalIgnoreCase))
            checks.Add(new("monitor", null, "disabled (no history, no quality data)"));

        if (controllers is not null) checks.AddRange(ControllerChecks(controllers, discovery));
        if (forwarder is not null) checks.AddRange(SinkChecks(forwarder));
        if (instances is not null) checks.AddRange(InstanceChecks(instances));

        return checks;
    }

    private string RoleGroups()
    {
        var r = options.Roles;
        var groups = new[] { options.Helpdesk.Group, r.Level1Group, r.Level2Group, r.Level3Group, r.AuditorGroup, r.AdminGroup }.Where(g => g.Length > 0).ToList();
        return groups.Count > 0 ? string.Join(", ", groups) : "development roles";
    }

    private IEnumerable<StatusCheck> ControllerChecks(ControllerDirectory dir, ControllerDiscoveryService? discovery)
    {
        var all = dir.All;
        var versions = all.Select(c => c.Version).Where(v => !string.IsNullOrEmpty(v)).Distinct().ToList();
        var summary = $"{all.Count} known, {all.Count(c => c.Source == "discovered")} discovered, strategy {dir.Strategy}";
        if (dir.DiscoveryEnabled)
            summary += discovery?.LastError is { } e ? $"; discovery failing: {e}" : discovery?.LastRun is { } t ? $"; discovered {t:u}" : "; discovery not run yet";
        if (versions.Count > 1) summary += $"; different versions: {string.Join(", ", versions)}";
        yield return new("controllers", discovery?.LastError is not null || versions.Count > 1 ? null : true, summary);
        var now = clock.GetUtcNow();
        foreach (var c in all)
        {
            var h = dir.HealthOf(c.Name);
            var open = h.OpenUntil is { } until && until > now;
            var parts = new List<string> { c.Source };
            if (!string.IsNullOrEmpty(c.State)) parts.Add(c.State);
            if (!string.IsNullOrEmpty(c.Version)) parts.Add("v" + c.Version);
            if (!string.IsNullOrEmpty(c.Zone)) parts.Add("zone " + c.Zone);
            if (h.EwmaMs is { } ms) parts.Add($"{ms:0} ms");
            parts.Add($"ok {h.Ok}, failed {h.Failed}");
            if (open) parts.Add("skipped for a short while after repeated failures");
            yield return new("controller", open ? false : h.Ok + h.Failed == 0 ? null : true, string.Join("; ", parts), c.Name);
        }
    }

    private static IEnumerable<StatusCheck> InstanceChecks(InstanceRegistry registry)
    {
        if (!registry.Enabled)
        {
            yield return new("instances", true, "single server (Sss:Instances:Directory is not set)");
            yield break;
        }
        var views = registry.View();
        var bad = views.Count(v => v.State is "missing" or "stale");
        var warn = views.Count(v => v.State is "unexpected" or "draining" || v.InSync == false || v.VersionDiffers);
        yield return new("instances", bad > 0 ? false : warn > 0 ? null : true,
            $"{views.Count(v => v.State is "online" or "draining")} of {views.Count(v => v.State != "unexpected")} servers online" + (bad > 0 ? $", {bad} not reporting" : "") + (warn > 0 ? $", {warn} to look at" : ""));
        foreach (var v in views)
        {
            var parts = new List<string> { v.State };
            if (v.Version is not null) parts.Add("v" + v.Version);
            if (v.LastSeen is { } seen) parts.Add("last seen " + seen.ToString("u"));
            if (v.InSync == false) parts.Add("configuration differs in: " + (v.DiffSections.Count > 0 ? string.Join(", ", v.DiffSections) : "the shared file"));
            if (v.VersionDiffers) parts.Add("other version than the majority");
            if (v.LocalKeys.Count > 0) parts.Add("local settings: " + string.Join(", ", v.LocalKeys));
            if (v.Health is not ("ok" or "unknown")) parts.Add("health " + v.Health);
            yield return new("instance", v.State is "missing" or "stale" ? false : v.State is "unexpected" or "draining" || v.InSync == false || v.VersionDiffers ? null : true,
                string.Join("; ", parts), v.Server);
        }
    }

    private static IEnumerable<StatusCheck> SinkChecks(AuditForwarder forwarder)
    {
        foreach (var (sink, state) in forwarder.States)
        {
            var failing = state.Error is not null && (state.LastSuccess is null || state.LastError > state.LastSuccess);
            var detail = $"{sink.Kind} {sink.Target}; sent {state.Sent}; waiting {state.PendingBytes / 1024} KB" + (failing ? $"; {state.Error}" : "");
            yield return new("sink", failing ? false : state.LastSuccess is null ? null : true, detail, sink.Name);
        }
    }

    private static string CurrentIdentity() =>
        OperatingSystem.IsWindows() ? System.Security.Principal.WindowsIdentity.GetCurrent().Name : Environment.UserName;

    private StatusCheck AuditCheck()
    {
        try
        {
            var dir = auditDirectory ?? Path.GetFullPath(options.Audit.Directory, AppContext.BaseDirectory);
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return new("audit", true, dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new("audit", false, ex.Message);
        }
    }

    private static async Task<StatusCheck> TimedAsync(string id, Func<Task> call)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await call();
            return new(id, true, $"{sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(id, false, ex.Message);
        }
    }
}
