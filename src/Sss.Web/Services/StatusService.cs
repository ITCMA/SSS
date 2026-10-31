using System.Diagnostics;
using System.Reflection;
using Sss.Web.Citrix;
using Sss.Web.Models;

namespace Sss.Web.Services;

/// <summary>Live health checks for the status page (helpdesk only). Each check has a stable id the UI translates;
/// the detail text is technical and only shown to helpdesk members. Nothing here changes any state.</summary>
public sealed class StatusService(
    SssOptions options, HelpdeskAccess helpdesk, TimeProvider clock,
    IBrokerClient? broker = null, IMonitorClient? monitor = null)
{
    private static readonly DateTimeOffset Started = new(Process.GetCurrentProcess().StartTime.ToUniversalTime());

    public async Task<IReadOnlyList<StatusCheck>> RunAsync(CancellationToken ct)
    {
        var checks = new List<StatusCheck>
        {
            new("version", true, Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?"),
            new("provider", true, options.Provider),
            new("identity", true, CurrentIdentity()),
            new("uptime", true, (clock.GetUtcNow() - Started).ToString(@"d\.hh\:mm\:ss")),
            AuditCheck(),
            new("helpdesk", helpdesk.Enabled ? true : null, helpdesk.Enabled ? options.Helpdesk.Group : "not configured"),
        };

        if (broker is not null)
            checks.Add(await TimedAsync("broker", () => broker.PingAsync(ct)));
        if (monitor is not null)
        {
            checks.Add(await TimedAsync("monitor", () => monitor.PingAsync(ct)));
            var https = ODataMonitorClient.BuildBaseAddress(options.Citrix).Scheme == Uri.UriSchemeHttps;
            checks.Add(new("monitor-https", https ? true : null, https ? "https" : "http (set Sss:Citrix:Monitor:UseHttps to true or BaseUrl to https://)"));
        }
        else if (!options.Provider.Equals("Mock", StringComparison.OrdinalIgnoreCase))
            checks.Add(new("monitor", null, "disabled (no history, no quality data)"));

        return checks;
    }

    private static string CurrentIdentity() =>
        OperatingSystem.IsWindows() ? System.Security.Principal.WindowsIdentity.GetCurrent().Name : Environment.UserName;

    private StatusCheck AuditCheck()
    {
        try
        {
            var dir = Path.GetFullPath(options.Audit.Directory, AppContext.BaseDirectory);
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
