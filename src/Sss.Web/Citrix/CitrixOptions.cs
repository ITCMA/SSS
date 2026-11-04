namespace Sss.Web.Citrix;

/// <summary>Settings for Sss:Provider = "Citrix" (section "Sss:Citrix").</summary>
public sealed class CitrixOptions
{
    /// <summary>Delivery Controller host names, tried in order (failover). With <see cref="Discovery"/> this may be empty or
    /// only a few names; the rest of the site is found automatically.</summary>
    public string[] Controllers { get; set; } = [];

    /// <summary>Finds all Delivery Controllers of the site from one of them and keeps the list up to date.</summary>
    public DiscoveryOptions Discovery { get; set; } = new();

    /// <summary>How requests are spread over the controllers.</summary>
    public BalancingOptions Balancing { get; set; } = new();

    /// <summary>Windows PowerShell 5.1 host that has the Citrix snap-ins (or the Remote PowerShell SDK) installed.</summary>
    public string PowerShellPath { get; set; } = "powershell.exe";

    /// <summary>Defaults to Citrix/sss-broker-worker.ps1 next to the application.</summary>
    public string? WorkerScript { get; set; }

    /// <summary>Test hook: a script that is dot-sourced instead of loading the Citrix snap-in (provides fake cmdlets).</summary>
    public string? SnapinOverride { get; set; }

    /// <summary>Number of warm PowerShell workers (each holds one loaded snap-in, roughly 60-100 MB).</summary>
    public int Workers { get; set; } = 2;

    /// <summary>Time limit of one Broker or Monitor request in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 20;

    /// <summary>Citrix Cloud (DaaS) instead of an on-premises site: read-only, through the Monitor service in the cloud.</summary>
    public CloudOptions Cloud { get; set; } = new();

    /// <summary>History, quality data and the expert view (Monitor OData).</summary>
    public MonitorOptions Monitor { get; set; } = new();

    /// <summary>Ending processes and profile analysis through the JEA endpoint on the VDAs.</summary>
    public ProcessOptions Processes { get; set; } = new();
}

/// <summary>Controller discovery: ask one controller (Get-BrokerController) for all others of its site.</summary>
public sealed class DiscoveryOptions
{
    /// <summary>Turn controller discovery on.</summary>
    public bool Enabled { get; set; }

    /// <summary>Controller to ask. Empty = the first entry of <c>Controllers</c>. If it is down, the cached and configured controllers are tried.</summary>
    public string Bootstrap { get; set; } = "";

    /// <summary>How often the controller list is refreshed, in minutes.</summary>
    public int RefreshMinutes { get; set; } = 15;

    /// <summary>Only controllers whose name ends with one of these suffixes are accepted (for example ".corp.local").
    /// Empty = the domain part of the bootstrap controller. A manipulated answer can therefore not send requests elsewhere.</summary>
    public string[] AllowedSuffixes { get; set; } = [];

    /// <summary>Also use controllers the Broker reports as not active (for example failed or in maintenance).</summary>
    public bool IncludeInactive { get; set; }
}

public sealed class BalancingOptions
{
    /// <summary>"Failover" (default: the controller that answered last, the others as fallback), "RoundRobin" or
    /// "LeastLatency" (the faster of two random healthy controllers; spreads load and avoids slow ones).</summary>
    public string Strategy { get; set; } = "Failover";

    /// <summary>Controllers (or, if discovered, their zone) that are used first while they are healthy, for example the ones in the same data center.</summary>
    public string[] Preferred { get; set; } = [];

    /// <summary>A controller that failed this many times in a row is skipped for <see cref="OpenSeconds"/>.</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>How long a controller that keeps failing is skipped, in seconds.</summary>
    public int OpenSeconds { get; set; } = 30;
}

/// <summary>Listing and ending the processes of a session. Needs the JEA endpoint on the VDAs (scripts/Install-SssJea.ps1).</summary>
public sealed class ProcessOptions
{
    /// <summary>Turn process control and profile analysis on (needs the JEA endpoint on the VDAs).</summary>
    public bool Enabled { get; set; }

    /// <summary>Name of the JEA session configuration on the VDAs.</summary>
    public string Configuration { get; set; } = "SSS.Processes";

    /// <summary>"All" = users for their own sessions and the helpdesk; "Helpdesk" = helpdesk only.</summary>
    public string Audience { get; set; } = "All";

    /// <summary>Connecting to a VDA takes longer than a Broker call, so process requests get their own time limit.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Shows the profile type and size in the expert view (the "Get-SssProfile" command of the JEA endpoint).</summary>
    public bool ProfileInfo { get; set; } = true;

    /// <summary>Time limit of the profile folder scan on the VDA; 0 = type only, no size.</summary>
    public int ProfileScanSeconds { get; set; } = 20;
}

public sealed class MonitorOptions
{
    /// <summary>Set to false to run without history and metrics (live sessions and log-off only).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Defaults to {scheme}://{first controller}/Citrix/Monitor/OData/v4/Data/ (see UseHttps).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>One Monitor address per Delivery Controller, tried in order (failover). Empty = derived from Controllers.</summary>
    public string[] BaseUrls { get; set; } = [];

    /// <summary>Scheme of the default address when BaseUrl is empty. Set to false only for a controller without an HTTPS binding.</summary>
    public bool UseHttps { get; set; } = true;

    /// <summary>Property of SessionMetrics used to pick the newest sample. Verify with scripts/Test-SssCitrix.ps1.</summary>
    public string MetricsDateField { get; set; } = "CollectedDate";

    /// <summary>Property of SessionMetrics holding the ICA round-trip time in ms.</summary>
    public string IcaRttField { get; set; } = "IcaRttMS";

    /// <summary>Property of SessionMetrics with the bandwidth available towards the client. Seen on CVAD 2507:
    /// OutputBandwidthAvailable = 105379594 on a 100 Mbit/s LAN, i.e. bits per second.</summary>
    public string BandwidthField { get; set; } = "OutputBandwidthAvailable";

    /// <summary>Converts the bandwidth field to kbit/s (1000 for bit/s).</summary>
    public int BandwidthDivisor { get; set; } = 1000;

    /// <summary>Maximum number of pages read for one history query.</summary>
    public int MaxPages { get; set; } = 5;

    /// <summary>Property of Connection with the Workspace app version. Used when the site delivers it (checked in $metadata).</summary>
    public string ClientVersionField { get; set; } = "ClientVersion";

    /// <summary>Property of Connection with the client platform (Windows, Mac, iOS ...). Used when the site delivers it.</summary>
    public string ClientPlatformField { get; set; } = "ClientPlatform";
}
