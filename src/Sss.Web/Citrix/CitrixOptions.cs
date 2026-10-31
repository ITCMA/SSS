namespace Sss.Web.Citrix;

/// <summary>Settings for Sss:Provider = "Citrix" (section "Sss:Citrix").</summary>
public sealed class CitrixOptions
{
    /// <summary>Delivery Controller host names, tried in order (failover).</summary>
    public string[] Controllers { get; set; } = [];

    /// <summary>Windows PowerShell 5.1 host that has the Citrix snap-ins (or the Remote PowerShell SDK) installed.</summary>
    public string PowerShellPath { get; set; } = "powershell.exe";

    /// <summary>Defaults to Citrix/sss-broker-worker.ps1 next to the application.</summary>
    public string? WorkerScript { get; set; }

    /// <summary>Test hook: a script that is dot-sourced instead of loading the Citrix snap-in (provides fake cmdlets).</summary>
    public string? SnapinOverride { get; set; }

    /// <summary>Number of warm PowerShell workers (each holds one loaded snap-in, roughly 60-100 MB).</summary>
    public int Workers { get; set; } = 2;

    public int RequestTimeoutSeconds { get; set; } = 20;

    public MonitorOptions Monitor { get; set; } = new();

    public ProcessOptions Processes { get; set; } = new();
}

/// <summary>Listing and ending the processes of a session. Needs the JEA endpoint on the VDAs (scripts/Install-SssJea.ps1).</summary>
public sealed class ProcessOptions
{
    public bool Enabled { get; set; }

    /// <summary>Name of the JEA session configuration on the VDAs.</summary>
    public string Configuration { get; set; } = "SSS.Processes";

    /// <summary>"All" = users for their own sessions and the helpdesk; "Helpdesk" = helpdesk only.</summary>
    public string Audience { get; set; } = "All";

    /// <summary>Connecting to a VDA takes longer than a Broker call, so process requests get their own time limit.</summary>
    public int TimeoutSeconds { get; set; } = 60;
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

    public int MaxPages { get; set; } = 5;
}
