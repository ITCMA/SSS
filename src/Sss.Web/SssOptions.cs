namespace Sss.Web;

/// <summary>Bound from the "Sss" section of appsettings.json / sss.config.json.</summary>
public sealed class SssOptions
{
    /// <summary>"Citrix" for a real site, "Mock" for demo data (with a banner).</summary>
    public string Provider { get; set; } = "Mock";
    /// <summary>Automatic refresh interval of the web interface in seconds.</summary>
    public int RefreshSeconds { get; set; } = 30;

    /// <summary>Optional address of StoreFront; the UI then shows an "Open StoreFront" link (to start or reconnect sessions).</summary>
    public string StoreFrontUrl { get; set; } = "";
    /// <summary>Authentication mode (IIS Windows authentication, or a fixed demo user in development).</summary>
    public AuthOptions Auth { get; set; } = new();
    /// <summary>Audit trail: local files, retention and central destinations.</summary>
    public AuditOptions Audit { get; set; } = new();
    /// <summary>The anonymous health probe GET /healthz for load balancers.</summary>
    public HealthOptions Health { get; set; } = new();
    /// <summary>Shared key ring for several SSS servers.</summary>
    public DataProtectionOptions DataProtection { get; set; } = new();
    /// <summary>Request limits per user and minute.</summary>
    public RateLimitOptions RateLimit { get; set; } = new();
    /// <summary>Thresholds of the quality traffic lights.</summary>
    public QualityThresholds Quality { get; set; } = new();
    /// <summary>Connection to the Citrix site (used when Provider is Citrix).</summary>
    public global::Sss.Web.Citrix.CitrixOptions Citrix { get; set; } = new();
    /// <summary>Helpdesk behaviour: group, ticket requirement, limits.</summary>
    public HelpdeskOptions Helpdesk { get; set; } = new();
    /// <summary>AD groups for the helpdesk levels and the auditor and admin roles.</summary>
    public RolesOptions Roles { get; set; } = new();
    /// <summary>Server list for several SSS servers (shared folder).</summary>
    public InstancesOptions Instances { get; set; } = new();
    /// <summary>Prometheus metrics endpoint.</summary>
    public MetricsOptions Metrics { get; set; } = new();

    /// <summary>The Environment view (client versions, locations) and the outdated-client hint.</summary>
    public EnvironmentOptions Environment { get; set; } = new();

    /// <summary>Folder for state SSS keeps between restarts (forwarding positions, controller cache, drain flag).
    /// Relative paths are based on the app directory; the app pool identity needs write access.</summary>
    public string DataDirectory { get; set; } = "data";
}

/// <summary>The Environment tab shows which Workspace app versions and platforms connect and from which of your locations. Citrix delivers no
/// location for an on-premises site (the cloud derives it from the address), so locations are the networks you assign to a name.</summary>
public sealed class EnvironmentOptions
{
    /// <summary>Your sites or offices, each with the networks (CIDR or single address) its clients connect from.</summary>
    public LocationOptions[] Locations { get; set; } = [];

    /// <summary>Oldest acceptable Workspace app version per platform, for example {"Windows": "26.3.0", "Mac": "26.3.0"}; "*" applies to every other platform.
    /// Clients below it are counted as outdated in the Environment tab and their users get a hint. Empty = no check.</summary>
    public Dictionary<string, string> MinClientVersion { get; set; } = [];

    /// <summary>Show the user a hint in the session list when the own Workspace app is below the minimum version.</summary>
    public bool HintOutdatedClient { get; set; } = true;
}

public sealed class LocationOptions
{
    /// <summary>Name shown in the Environment tab, for example the office or data center.</summary>
    public string Name { get; set; } = "";

    /// <summary>Networks of this location, for example 10.20.0.0/16. The most specific match wins.</summary>
    public string[] Networks { get; set; } = [];
}

/// <summary>GET /metrics in the Prometheus text format (counters and states, no personal data). IIS must allow anonymous access
/// to that path (scripts/install.ps1 -MetricsEndpoint); the addresses below are the protection.</summary>
public sealed class MetricsOptions
{
    /// <summary>Serve GET /metrics (Prometheus text format, no personal data).</summary>
    public bool Enabled { get; set; }

    /// <summary>Addresses or networks (for example 10.0.5.0/24) that may read the metrics. Empty = only the server itself.</summary>
    public string[] AllowedIps { get; set; } = [];
}

/// <summary>The SSS servers know each other through a shared folder: every server writes a small status file there and reads the
/// others, so the list of servers, differences in version or configuration and silent servers become visible.</summary>
public sealed class InstancesOptions
{
    /// <summary>Shared folder (for example \\fs01\sss-state) that every SSS server can write. Empty = single server, feature off.</summary>
    public string Directory { get; set; } = "";

    /// <summary>The names of all SSS servers (computer names). A listed server that does not report is shown as missing, a reporting
    /// server that is not listed as unexpected. Empty = no completeness check, the servers that report are the list.</summary>
    public string[] Expected { get; set; } = [];

    /// <summary>How often this server reports to the shared folder, in seconds.</summary>
    public int HeartbeatSeconds { get; set; } = 60;

    /// <summary>A server that has not reported for this long counts as not running.</summary>
    public int StaleSeconds { get; set; } = 180;
}

/// <summary>AD groups for the roles beyond the normal user. A member of a higher level has the lower levels too.
/// Auditor and Admin are separate: a helpdesk member sees neither the audit trail nor the system pages unless granted.</summary>
public sealed class RolesOptions
{
    /// <summary>Looks at users, sessions and the expert view; changes nothing.</summary>
    public string Level1Group { get; set; } = "";

    /// <summary>Level 1 plus: log off sessions and send messages.</summary>
    public string Level2Group { get; set; } = "";

    /// <summary>Level 2 plus: processes and profile of other users, end processes.</summary>
    public string Level3Group { get; set; } = "";

    /// <summary>Reads the audit trail and its evaluation. Not part of any helpdesk level.</summary>
    public string AuditorGroup { get; set; } = "";

    /// <summary>Status, controllers and the list of SSS servers. Empty = every helpdesk member (as before roles existed).</summary>
    public string AdminGroup { get; set; } = "";
}

public sealed class AuthOptions
{
    /// <summary>"Iis" = Windows authentication of IIS (production). "Dev" = fixed demo user,
    /// only accepted when ASPNETCORE_ENVIRONMENT=Development.</summary>
    public string Mode { get; set; } = "Iis";
    /// <summary>Development mode only: name of the fixed demo user.</summary>
    public string DevUserName { get; set; } = "DEMO\\demo.user";
    /// <summary>Development mode only: SID of the fixed demo user.</summary>
    public string DevUserSid { get; set; } = "S-1-5-21-1000-1000-1000-1001";

    /// <summary>Dev mode only: treat the demo user as a member of the helpdesk group.</summary>
    public bool DevIsHelpdesk { get; set; }

    /// <summary>Dev mode only: roles of the demo user, a list of L1, L2, L3, Auditor, Admin.</summary>
    public string DevRoles { get; set; } = "";
}

public sealed class AuditOptions
{
    /// <summary>Directory for audit-YYYYMMDD.jsonl files; relative paths are based on the app directory.</summary>
    public string Directory { get; set; } = "logs";

    /// <summary>Name the files audit-YYYYMMDD-SERVER.jsonl so several servers can write into one shared folder.</summary>
    public bool PerServerFiles { get; set; }

    /// <summary>Delete local audit and ops files older than this many days once every sink has passed them. 0 = keep.</summary>
    public int RetentionDays { get; set; }

    /// <summary>Operational messages (warnings and errors of SSS itself) go into a second stream "ops" next to the audit trail.</summary>
    public bool OpsStream { get; set; } = true;

    /// <summary>Central destinations. The local file is always written first; every sink gets a copy and catches up after an outage.</summary>
    public AuditSinks Sinks { get; set; } = new();
}

public sealed class AuditSinks
{
    /// <summary>Destinations that copy the events to a shared folder.</summary>
    public UncSinkOptions[] Unc { get; set; } = [];
    /// <summary>Syslog destinations (RFC 5424, JSON or CEF).</summary>
    public SyslogSinkOptions[] Syslog { get; set; } = [];
    /// <summary>HTTP(S) destinations (SIEM, log platforms, chat webhooks).</summary>
    public HttpSinkOptions[] Http { get; set; } = [];
    /// <summary>SNMP v2c trap destinations.</summary>
    public SnmpSinkOptions[] Snmp { get; set; } = [];
}

/// <summary>Settings every sink shares.</summary>
public abstract class SinkOptionsBase
{
    /// <summary>Set to false to switch this destination off without removing it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Name in the status page and for the position file; defaults to the sink type and number.</summary>
    public string Name { get; set; } = "";

    /// <summary>"audit", "ops" or both.</summary>
    public string[] Streams { get; set; } = ["audit", "ops"];

    /// <summary>Only events whose action starts with one of these (for example "helpdesk", "alert:"). Empty = all.</summary>
    public string[] Include { get; set; } = [];

    /// <summary>Only failures, rejections, alerts and operational problems (for a notification channel).</summary>
    public bool OnlyProblems { get; set; }

    /// <summary>Maximum number of events sent in one batch.</summary>
    public int BatchSize { get; set; } = 200;
}

/// <summary>Copies the events into a (shared) folder, one file per day and server, for example \\fs01\sss-audit.</summary>
public sealed class UncSinkOptions : SinkOptionsBase
{
    /// <summary>Shared folder (UNC path) the events are copied to: one file per day, server and stream.</summary>
    public string Path { get; set; } = "";
}

/// <summary>Syslog (RFC 5424) over UDP, TCP or TLS. Format "Json" sends the event as JSON, "Cef" as Common Event Format.</summary>
public sealed class SyslogSinkOptions : SinkOptionsBase
{
    /// <summary>Syslog server name or address.</summary>
    public string Host { get; set; } = "";
    /// <summary>Port; 0 = the default of the protocol (514, or 6514 for TLS).</summary>
    public int Port { get; set; }
    /// <summary>Udp, Tcp or Tls.</summary>
    public string Protocol { get; set; } = "Udp";
    /// <summary>Json (the record as it is) or Cef (Common Event Format).</summary>
    public string Format { get; set; } = "Json";
    /// <summary>Syslog facility number (13 = log audit).</summary>
    public int Facility { get; set; } = 13;
    /// <summary>Application name in the syslog header.</summary>
    public string AppName { get; set; } = "sss";

    /// <summary>TCP/TLS framing: "Newline" (default of most collectors) or "OctetCount" (RFC 6587).</summary>
    public string Framing { get; set; } = "Newline";
}

/// <summary>HTTP(S) POST of JSON. Format: "Ndjson" (one JSON object per line), "JsonArray", "SplunkHec", "ElasticBulk" or "Text"
/// (one short sentence per event as {"text": ...}, for chat webhooks such as Teams, Slack or Mattermost).</summary>
public sealed class HttpSinkOptions : SinkOptionsBase
{
    /// <summary>Address that receives the events by POST; must be https:// unless AllowHttp is set.</summary>
    public string Url { get; set; } = "";
    /// <summary>Ndjson, JsonArray, SplunkHec, ElasticBulk or Text (short sentences for chat webhooks).</summary>
    public string Format { get; set; } = "Ndjson";

    /// <summary>Elasticsearch/OpenSearch index for the bulk format.</summary>
    public string Index { get; set; } = "sss-audit";

    /// <summary>Extra headers without secrets.</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>File whose content is the complete Authorization header value (for example "Splunk 1234-..." or "Bearer ..."),
    /// read for every request so the secret never sits in the configuration and can be rotated.</summary>
    public string AuthorizationFile { get; set; } = "";

    /// <summary>Time limit of one request in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Allow plain http:// (otherwise the address must be https://).</summary>
    public bool AllowHttp { get; set; }
}

/// <summary>SNMP v2c trap per event (UDP). The community is sent in clear text: use it inside the management network only.</summary>
public sealed class SnmpSinkOptions : SinkOptionsBase
{
    /// <summary>Trap receiver name or address.</summary>
    public string Host { get; set; } = "";
    /// <summary>UDP port of the trap receiver.</summary>
    public int Port { get; set; } = 162;
    /// <summary>SNMP community string (sent in clear text: management network only).</summary>
    public string Community { get; set; } = "";

    /// <summary>Your own enterprise OID, for example 1.3.6.1.4.1.NNNNN.1 (no default: pick the number your organisation owns).</summary>
    public string EnterpriseOid { get; set; } = "";

    public SnmpSinkOptions() => OnlyProblems = true;
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
    /// <summary>Log-off and end-process requests per user and minute.</summary>
    public int LogoffRequestsPerMinute { get; set; } = 10;

    /// <summary>Listing sessions and CSV export; each call queries Citrix, so it is limited as well.</summary>
    public int ReadRequestsPerMinute { get; set; } = 120;

    /// <summary>All helpdesk calls (search, overview, view, log-off) per helpdesk user.</summary>
    public int HelpdeskRequestsPerMinute { get; set; } = 60;
}

public sealed class QualityThresholds
{
    /// <summary>ICA round-trip time in ms from which the light is yellow.</summary>
    public int RttWarnMs { get; set; } = 80;
    /// <summary>ICA round-trip time in ms from which the light is red.</summary>
    public int RttCritMs { get; set; } = 150;
    /// <summary>Available bandwidth in kbit/s at or below which the light is yellow.</summary>
    public int BandwidthWarnKbps { get; set; } = 1500;
    /// <summary>Available bandwidth in kbit/s at or below which the light is red.</summary>
    public int BandwidthCritKbps { get; set; } = 400;
    /// <summary>Logon duration in seconds from which the light is yellow.</summary>
    public int LogonWarnSeconds { get; set; } = 45;
    /// <summary>Logon duration in seconds from which the light is red.</summary>
    public int LogonCritSeconds { get; set; } = 90;
    /// <summary>Server load index from which the light is yellow.</summary>
    public int LoadWarn { get; set; } = 7000;
    /// <summary>Server load index from which the light is red.</summary>
    public int LoadCrit { get; set; } = 9000;
}

public sealed class HelpdeskOptions
{
    /// <summary>AD group whose members have all helpdesk rights (level 3), e.g. CORP\SSS-Helpdesk. Kept for existing
    /// installations; <c>Sss:Roles</c> allows finer levels. Empty = not used. Resolved to a SID at startup.</summary>
    public string Group { get; set; } = "";

    /// <summary>Require a ticket number or reason for every helpdesk action that changes something or reads processes or
    /// profiles (log-off, message, end process). Free text; the content is not checked, it is written to the audit log. Off by default: the field stays
    /// available and is logged when filled in.</summary>
    public bool ReasonRequired { get; set; }

    /// <summary>A helpdesk request that logs off this many sessions or more is reported as an alert event (audit sinks can notify).</summary>
    public int BulkAlertThreshold { get; set; } = 5;

    /// <summary>Upper limit for the farm-wide session overview.</summary>
    public int MaxSessions { get; set; } = 1000;

    /// <summary>Title of messages the helpdesk sends to a session.</summary>
    public string MessageTitle { get; set; } = "IT Helpdesk";
}
