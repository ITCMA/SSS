using System.Net;
using System.Text.RegularExpressions;
using Sss.Web.Audit;
using Sss.Web.Citrix;

namespace Sss.Web.Services;

public sealed record ConfigFinding(string Level, string Message);

/// <summary>All checks of the configuration in one place. SSS runs them at startup (the first error stops the application with a clear
/// message instead of running in an unsafe or unusable state); <c>Sss.Web.exe --check-config</c> and <c>Test-SssConfig.ps1</c> run them
/// without starting it and also list the warnings.</summary>
public static partial class ConfigValidator
{
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")] private static partial Regex ConfigNameRegex();
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,253}$")] private static partial Regex HostRegex();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,63}$")] private static partial Regex FieldRegex();

    public static IReadOnlyList<ConfigFinding> Validate(SssOptions o, IConfigurationSection sss, bool development)
    {
        var f = new List<ConfigFinding>();
        void Error(string m) => f.Add(new("error", m));
        void Warn(string m) => f.Add(new("warning", m));

        var dev = o.Auth.Mode.Equals("Dev", StringComparison.OrdinalIgnoreCase);
        if (dev && !development) Error("Sss:Auth:Mode=Dev is only allowed when ASPNETCORE_ENVIRONMENT=Development.");
        if (!dev && !o.Auth.Mode.Equals("Iis", StringComparison.OrdinalIgnoreCase)) Error($"Unknown Sss:Auth:Mode '{o.Auth.Mode}'. Use 'Iis' or 'Dev'.");

        var mock = o.Provider.Equals("Mock", StringComparison.OrdinalIgnoreCase);
        if (!mock && !o.Provider.Equals("Citrix", StringComparison.OrdinalIgnoreCase)) Error($"Unknown Sss:Provider '{o.Provider}'. Use 'Citrix' or 'Mock'.");
        if (mock && !development) Warn("Sss:Provider is 'Mock': SSS shows demo data. Set it to 'Citrix' for a real site.");

        // The snap-in override dot-sources a script named in the configuration: a test hook, never for production.
        if (!mock && !string.IsNullOrWhiteSpace(o.Citrix.SnapinOverride) && !development)
            Error("Sss:Citrix:SnapinOverride is a test hook and only allowed when ASPNETCORE_ENVIRONMENT=Development.");

        if (!o.Citrix.Processes.Audience.Equals("All", StringComparison.OrdinalIgnoreCase) && !o.Citrix.Processes.Audience.Equals("Helpdesk", StringComparison.OrdinalIgnoreCase))
            Error($"Unknown Sss:Citrix:Processes:Audience '{o.Citrix.Processes.Audience}'. Use 'All' or 'Helpdesk'.");
        if (!ConfigNameRegex().IsMatch(o.Citrix.Processes.Configuration))
            Error("Sss:Citrix:Processes:Configuration may only contain letters, digits, '.', '_' and '-'.");

        // The link is rendered as an href, so only absolute http(s) addresses are accepted.
        if (!string.IsNullOrWhiteSpace(o.StoreFrontUrl) && !(Uri.TryCreate(o.StoreFrontUrl, UriKind.Absolute, out var sf) && sf.Scheme is "http" or "https"))
            Error("Sss:StoreFrontUrl must be an absolute http:// or https:// address.");

        if (o.RefreshSeconds < 5) Warn("Sss:RefreshSeconds below 5 loads the Delivery Controller needlessly.");

        var q = o.Quality;
        if (q.RttWarnMs >= q.RttCritMs) Error("Sss:Quality: RttWarnMs must be lower than RttCritMs.");
        if (q.BandwidthWarnKbps <= q.BandwidthCritKbps) Error("Sss:Quality: BandwidthWarnKbps must be higher than BandwidthCritKbps (less bandwidth is worse).");
        if (q.LogonWarnSeconds >= q.LogonCritSeconds) Error("Sss:Quality: LogonWarnSeconds must be lower than LogonCritSeconds.");
        if (q.LoadWarn >= q.LoadCrit) Error("Sss:Quality: LoadWarn must be lower than LoadCrit.");

        var r = o.RateLimit;
        if (r.LogoffRequestsPerMinute < 1 || r.ReadRequestsPerMinute < 1 || r.HelpdeskRequestsPerMinute < 1) Error("Sss:RateLimit: every limit must be at least 1.");

        var h = o.Helpdesk;
        if (h.MaxSessions < 1) Error("Sss:Helpdesk:MaxSessions must be at least 1.");
        if (h.BulkAlertThreshold < 0) Error("Sss:Helpdesk:BulkAlertThreshold must not be negative (0 = off).");
        if (!h.ReasonRequired) Warn("Sss:Helpdesk:ReasonRequired is false: helpdesk actions need no ticket number or reason.");

        foreach (var (key, value) in new[]
                 {
                     ("Sss:Helpdesk:Group", h.Group), ("Sss:Roles:Level1Group", o.Roles.Level1Group), ("Sss:Roles:Level2Group", o.Roles.Level2Group),
                     ("Sss:Roles:Level3Group", o.Roles.Level3Group), ("Sss:Roles:AuditorGroup", o.Roles.AuditorGroup), ("Sss:Roles:AdminGroup", o.Roles.AdminGroup),
                 })
            if (value.Length > 0 && !value.Contains('\\') && !value.Contains('@'))
                Warn($"{key} '{value}' has no domain: write it as DOMAIN\\Group.");
        var roles = o.Roles;
        var anyHelpdesk = new[] { h.Group, roles.Level1Group, roles.Level2Group, roles.Level3Group }.Any(g => g.Length > 0);
        if (!anyHelpdesk && !dev) Warn("No helpdesk group is set (Sss:Helpdesk:Group or Sss:Roles): the helpdesk feature is off.");
        if (roles.AuditorGroup.Length == 0 && !dev) Warn("Sss:Roles:AuditorGroup is empty: the audit trail is written but nobody can read it in the Audit tab.");

        if (o.Audit.RetentionDays < 0) Error("Sss:Audit:RetentionDays must not be negative (0 = keep).");
        if (o.Audit.Directory.StartsWith(@"\\", StringComparison.Ordinal) && !o.Audit.PerServerFiles)
            Warn("Sss:Audit:Directory is a network share but PerServerFiles is false: several servers would write into the same files. Set PerServerFiles to true.");
        try { AuditSinkFactory.Create(o.Audit.Sinks, "check"); }
        catch (InvalidOperationException ex) { Error(ex.Message); }

        var i = o.Instances;
        if (i.HeartbeatSeconds < 10) Warn("Sss:Instances:HeartbeatSeconds below 10 is raised to 10.");
        if (i.StaleSeconds <= i.HeartbeatSeconds) Error("Sss:Instances:StaleSeconds must be longer than HeartbeatSeconds.");
        foreach (var name in i.Expected)
            if (!HostRegex().IsMatch(name)) Error($"Sss:Instances:Expected contains an invalid computer name '{name}'.");
        if (i.Directory.Length > 0 && i.Expected.Length == 0) Warn("Sss:Instances:Expected is empty: a server that never reports cannot be noticed. List all SSS servers.");
        if (i.Directory.Length > 0 && o.DataProtection.KeyDirectory.Length == 0)
            Warn("Several servers are configured (Sss:Instances) but Sss:DataProtection:KeyDirectory is empty: a CSRF token of one server is rejected by the others unless the load balancer is sticky.");

        foreach (var a in o.Metrics.AllowedIps)
        {
            var ok = a.Contains('/') ? IPNetwork.TryParse(a, out _) : IPAddress.TryParse(a, out _);
            if (!ok) Error($"Sss:Metrics:AllowedIps contains '{a}', which is not an address or a network like 10.0.5.0/24.");
        }
        if (o.Metrics.Enabled && o.Metrics.AllowedIps.Length == 0) Warn("Sss:Metrics is on but AllowedIps is empty: only the server itself can read /metrics.");

        if (!mock) ValidateCitrix(o, Error, Warn);

        foreach (var key in ConfigCheck.UnknownKeys(sss)) Warn($"Unknown configuration key {key} is ignored.");
        return f;
    }

    private static void ValidateCitrix(SssOptions o, Action<string> error, Action<string> warn)
    {
        var c = o.Citrix;
        try { _ = new ControllerDirectory(c, Path.Combine(Path.GetTempPath(), "sss-check-unused"), TimeProvider.System); }
        catch (InvalidOperationException ex) { error(ex.Message); }
        if (c.Workers is < 1 or > 16) warn("Sss:Citrix:Workers is limited to 1-16.");
        if (c.RequestTimeoutSeconds < 1) error("Sss:Citrix:RequestTimeoutSeconds must be at least 1.");
        if (c.Discovery.RefreshMinutes < 1) error("Sss:Citrix:Discovery:RefreshMinutes must be at least 1.");
        if (c.Discovery.Enabled && c.Discovery.AllowedSuffixes.Length == 0 && !(c.Discovery.Bootstrap.Length > 0 ? c.Discovery.Bootstrap : c.Controllers.FirstOrDefault() ?? "").Contains('.'))
            warn("Sss:Citrix:Discovery: the bootstrap controller has no domain part, so the allowed domain cannot be derived. Set AllowedSuffixes.");
        if (c.Balancing.FailureThreshold < 1) error("Sss:Citrix:Balancing:FailureThreshold must be at least 1.");
        if (c.Monitor.Enabled)
        {
            foreach (var field in new[] { c.Monitor.MetricsDateField, c.Monitor.IcaRttField, c.Monitor.BandwidthField })
                if (!FieldRegex().IsMatch(field)) error($"Sss:Citrix:Monitor field name '{field}' is not a valid OData property name.");
            foreach (var u in c.Monitor.BaseUrls.Append(c.Monitor.BaseUrl ?? "").Where(u => u.Length > 0))
                if (!(Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")) error("Sss:Citrix:Monitor:BaseUrl(s) must be http:// or https:// addresses.");
            var plainHttp = !c.Monitor.UseHttps;
            try { plainHttp |= ODataMonitorClient.BuildBaseAddresses(c).Any(u => u.Scheme == Uri.UriSchemeHttp); }
            catch (UriFormatException) { /* an invalid controller name is reported above */ }
            if (plainHttp)
                warn("Monitor OData is accessed over HTTP (Windows authentication without transport encryption). Use HTTPS where the controller supports it.");
        }
        if (c.Processes.Enabled && c.Processes.TimeoutSeconds < 5) warn("Sss:Citrix:Processes:TimeoutSeconds below 5 is likely too short for a VDA connection.");
    }
}
