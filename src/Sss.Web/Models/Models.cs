namespace Sss.Web.Models;

public enum SessionState { Active, Disconnected, Ended }

public enum Health { Unknown, Good, Warning, Critical }

public enum LogoffScope { Selected, Disconnected, All }

public sealed record UserIdentity(string Sid, string Name);

/// <summary>Raw metrics plus the traffic-light rating for the client side (network/WLAN)
/// and the backend side (datacenter/VDA).</summary>
public sealed record SessionQuality(
    int? IcaRttMs,
    int? BandwidthKbps,
    int? LogonSeconds,
    int? LoadIndex,
    Health Frontend,
    Health Backend);

public sealed record SessionInfo(
    string Uid,
    string ResourceName,
    string ResourceType,
    string DeliveryGroup,
    string Machine,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    SessionState State,
    int? IdleSeconds,
    string? ClientName,
    string? ClientAddress,
    SessionQuality Quality,
    string? SessionKey = null);

/// <summary>Sessions plus non-fatal problems (e.g. "history-unavailable") the UI can tell the user about.</summary>
public sealed record SessionQueryResult(IReadOnlyList<SessionInfo> Sessions, IReadOnlyList<string> Warnings);

public sealed record SessionsResponse(
    IReadOnlyList<SessionInfo> Live,
    IReadOnlyList<SessionInfo> History,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<string> Warnings);

/// <summary>A user found by the helpdesk search. Sid identifies the user in all follow-up calls.</summary>
public sealed record HelpdeskUser(string Name, string Sid, bool HasLiveSession);

/// <summary>A live session together with its owner (helpdesk overview of the whole farm).</summary>
public sealed record HelpdeskSession(SessionInfo Session, string UserName, string UserSid);

public sealed record HelpdeskLiveResult(IReadOnlyList<HelpdeskSession> Sessions, bool Truncated);

public sealed record LogoffResult(
    int Requested,
    IReadOnlyList<string> LoggedOff,
    IReadOnlyList<string> Rejected);

/// <summary>One property collected by Citrix. <c>Unit</c> is set when the value was converted (e.g. "kbit/s").</summary>
public sealed record DetailItem(string Key, string Value, string? Unit = null);

/// <summary>A themed block of the expert view (session, logon, client, network, machine, technical).</summary>
public sealed record DetailGroup(string Id, IReadOnlyList<DetailItem> Items);

/// <summary>A metric over time. Points are [unix milliseconds, value], oldest first.</summary>
public sealed record DetailSeries(string Name, string Unit, IReadOnlyList<double[]> Points);

/// <summary>A list of records (connections, failures, applications) with one row of properties each.</summary>
public sealed record DetailTable(string Id, IReadOnlyList<IReadOnlyList<DetailItem>> Rows);

/// <summary>Everything Monitor knows about one session. <c>Unavailable</c> names optional parts the site did not deliver.</summary>
public sealed record SessionDetails(
    string SessionKey,
    IReadOnlyList<DetailGroup> Groups,
    IReadOnlyList<DetailSeries> Series,
    IReadOnlyList<DetailTable> Tables,
    IReadOnlyList<string> Unavailable,
    LogonWaterfall? Waterfall = null);

/// <summary>One step of the logon (brokering, HDX, authentication, GPO, scripts, profile, shell) on a time axis in
/// milliseconds. <c>Estimated</c> = the length is known but the position was derived.</summary>
public sealed record LogonStep(string Id, double StartMs, double DurationMs, bool Estimated);

public sealed record LogonWaterfall(double TotalMs, IReadOnlyList<LogonStep> Steps);

/// <summary>One health check of the status page.</summary>
public sealed record StatusCheck(string Id, bool? Ok, string Detail);

public sealed record ProcessEntry(int Id, string Name, DateTimeOffset? Started, int MemoryMb, string? Window, bool IsApp = false);

public sealed record ProcessListResult(string? Machine, IReadOnlyList<ProcessEntry> Processes);

public sealed record EndProcessResult(string Result);

public sealed record ProfileFolder(string Name, int Mb);

/// <summary>What the VDA reports about the profile of a session's user. <c>Type</c> is Local, Roaming, Citrix UPM or FSLogix.
/// Sizes are in MB; <c>SizePartial</c> means the folder scan hit its time limit.</summary>
public sealed record ProfileInfo(
    string? Machine, string Type, bool Loaded, string? LocalPath, string? StorePath, string? Container, int? ContainerMb, int? ContainerMaxMb,
    bool Roaming, bool Temporary, bool Mandatory, int? SizeMb, bool SizePartial, int ScanSeconds,
    IReadOnlyList<string> Notes, IReadOnlyList<ProfileFolder> TopFolders);
