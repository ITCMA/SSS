namespace Sss.Web;

/// <summary>Bound from the "Sss" section of appsettings.json / sss.config.json.</summary>
public sealed class SssOptions
{
    /// <summary>"Mock" (demo data) or "Citrix" (not implemented yet).</summary>
    public string Provider { get; set; } = "Mock";
    public int RefreshSeconds { get; set; } = 30;

    /// <summary>Optional address of StoreFront; the UI then shows an "Open StoreFront" link (to start or reconnect sessions).</summary>
    public string StoreFrontUrl { get; set; } = "";
    public AuthOptions Auth { get; set; } = new();
    public AuditOptions Audit { get; set; } = new();
    public HealthOptions Health { get; set; } = new();
    public DataProtectionOptions DataProtection { get; set; } = new();
    public RateLimitOptions RateLimit { get; set; } = new();
    public QualityThresholds Quality { get; set; } = new();
    public global::Sss.Web.Citrix.CitrixOptions Citrix { get; set; } = new();
    public HelpdeskOptions Helpdesk { get; set; } = new();
}

public sealed class AuthOptions
{
    /// <summary>"Iis" = Windows authentication of IIS (production). "Dev" = fixed demo user,
    /// only accepted when ASPNETCORE_ENVIRONMENT=Development.</summary>
    public string Mode { get; set; } = "Iis";
    public string DevUserName { get; set; } = "DEMO\\demo.user";
    public string DevUserSid { get; set; } = "S-1-5-21-1000-1000-1000-1001";

    /// <summary>Dev mode only: treat the demo user as a member of the helpdesk group.</summary>
    public bool DevIsHelpdesk { get; set; }
}

public sealed class AuditOptions
{
    /// <summary>Directory for audit-YYYYMMDD.jsonl files; relative paths are based on the app directory.</summary>
    public string Directory { get; set; } = "logs";

    /// <summary>Name the files audit-YYYYMMDD-SERVER.jsonl so several servers can write into one shared folder.</summary>
    public bool PerServerFiles { get; set; }
}

public sealed class HealthOptions
{
    /// <summary>Serves GET /healthz (no details, 200 or 503) for load balancers. IIS must allow anonymous access to that
    /// path; scripts/install.ps1 -HealthEndpoint does that.</summary>
    public bool Enabled { get; set; } = true;
}

public sealed class DataProtectionOptions
{
    /// <summary>Shared folder for the key ring when SSS runs on several servers (CSRF tokens then work on every server).
    /// Empty = keys stay on this server (single server, or a load balancer with sticky sessions).</summary>
    public string KeyDirectory { get; set; } = "";

    /// <summary>Optional: encrypt the keys for this account or group SID (DPAPI-NG), e.g. the SID of the gMSA. Windows only.</summary>
    public string ProtectToSid { get; set; } = "";
}

public sealed class RateLimitOptions
{
    public int LogoffRequestsPerMinute { get; set; } = 10;

    /// <summary>Listing sessions and CSV export; each call queries Citrix, so it is limited as well.</summary>
    public int ReadRequestsPerMinute { get; set; } = 120;

    /// <summary>All helpdesk calls (search, overview, view, log-off) per helpdesk user.</summary>
    public int HelpdeskRequestsPerMinute { get; set; } = 60;
}

public sealed class QualityThresholds
{
    public int RttWarnMs { get; set; } = 80;
    public int RttCritMs { get; set; } = 150;
    public int BandwidthWarnKbps { get; set; } = 1500;
    public int BandwidthCritKbps { get; set; } = 400;
    public int LogonWarnSeconds { get; set; } = 45;
    public int LogonCritSeconds { get; set; } = 90;
    public int LoadWarn { get; set; } = 7000;
    public int LoadCrit { get; set; } = 9000;
}

public sealed class HelpdeskOptions
{
    /// <summary>AD group whose members may see and log off other users' sessions, e.g. CORP\SSS-Helpdesk.
    /// Empty = helpdesk feature off. Resolved to a SID at startup; if it cannot be resolved the feature stays off.</summary>
    public string Group { get; set; } = "";

    /// <summary>Require a free-text reason for every helpdesk log-off (written to the audit log).</summary>
    public bool ReasonRequired { get; set; }

    /// <summary>Upper limit for the farm-wide session overview.</summary>
    public int MaxSessions { get; set; } = 1000;

    /// <summary>Title of messages the helpdesk sends to a session.</summary>
    public string MessageTitle { get; set; } = "IT Helpdesk";
}
