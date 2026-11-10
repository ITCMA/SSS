namespace Sss.Web.Models;

public enum SessionState { Active, Disconnected, Ended }

public enum Health { Unknown, Good, Warning, Critical }

public enum LogoffScope { Selected, Disconnected, All }

public sealed record UserIdentity(string Sid, string Name);

/// <summary>Raw metrics plus the traffic-light rating for the client side (network/WLAN)
/// and the backend side (datacenter/VDA). <c>MeasuredAt</c> is the time of the newest network sample used.</summary>
public sealed record SessionQuality(
    int? IcaRttMs,
    int? BandwidthKbps,
    int? LogonSeconds,
    int? LoadIndex,
    Health Frontend,
    Health Backend,
    DateTimeOffset? MeasuredAt = null);

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
    string? SessionKey = null,
    string? ClientVersion = null,
    string? ClientPlatform = null,
    bool ClientOutdated = false,
    DateTimeOffset? StateChangedAt = null);

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
public sealed record LogonStep(string Id, double StartMs, double DurationMs, bool Estimated, string? Parent = null,
    DateTimeOffset? StartAt = null, DateTimeOffset? EndAt = null, double? AvgUserMs = null);

public sealed record LogonWaterfall(double TotalMs, IReadOnlyList<LogonStep> Steps);

/// <summary>One health check of the status page.</summary>
public sealed record StatusCheck(string Id, bool? Ok, string Detail, string? Label = null);

public sealed record ProcessEntry(int Id, string Name, DateTimeOffset? Started, int MemoryMb, string? Window, bool IsApp = false, string? Path = null, string? Description = null);

/// <summary>One Citrix policy value in force in a session: where it sits below the policy key, its name and its value.</summary>
public sealed record PolicyEntry(int SessionId, string Path, string Name, string Value);

public sealed record PolicyListResult(string? Machine, IReadOnlyList<PolicyEntry> Entries);

/// <summary>One virtual-channel counter of the session read on the VDA (bandwidth, latency ...); the name is the Windows counter name.</summary>
public sealed record HdxCounter(string Name, double Value);
public sealed record HdxPrinter(string Name, string? Driver, bool Default);

/// <summary>What the VDA reports live about one session: the "ICA Session" performance counters, redirected printers and what is known about client drives.</summary>
public sealed record HdxLive(int SessionId, string? Instance, IReadOnlyList<HdxCounter> Counters, IReadOnlyList<HdxPrinter> Printers, IReadOnlyList<string> Drives, IReadOnlyList<string> Notes, DateTimeOffset? Started = null, IReadOnlyList<HdxDevice>? Devices = null);

/// <summary>A device the VDA knows: <c>Kind</c> is gpu, audio, usb or citrix; <c>Detail</c> is a short extra (driver, memory, status).</summary>
public sealed record HdxDevice(string Kind, string Name, string? Detail);

/// <summary>Where to shadow a session: the VDA, the Windows session id on it, and the link for the starter on the helpdesk PC. <c>Problem</c> is set instead when it cannot be found out (ambiguous, notfound).</summary>
public sealed record ShadowResult(string? Url, string? Machine, int? SessionId, string? Problem);
public sealed record BrokerHdx(string? Machine, IReadOnlyList<HdxLive> Sessions);

/// <summary>One HDX channel group (drive, clipboard, printing, audio, usb, graphics, network) as the policies in force set it.
/// <c>State</c> is allowed, limited, blocked, configured (values exist that SSS cannot rate) or unknown (no policy, so the default applies).
/// <c>Mode</c> selects a translated text (readOnly, toClient, toServer, both, off, on, partial); <c>Off</c> names the sub-functions a "partial" group has switched off; <c>Policies</c> are the values it is based on.</summary>
public sealed record HdxChannel(string Id, string State, string? Mode, IReadOnlyList<PolicyEntry> Policies, IReadOnlyList<string>? Off = null);

/// <summary>Findings like the profile hints: <c>Id</c> selects the translated text, <c>Args</c> fills its placeholders.</summary>
public sealed record HdxHint(string Id, string Level, IReadOnlyDictionary<string, string>? Args = null);

/// <summary>The HDX view of a session. <c>Live</c> is null when the VDA delivered no live values (older JEA endpoint, counters missing); <c>LiveNote</c> then says why.</summary>
public sealed record HdxView(string? Machine, int? SessionId, IReadOnlyList<HdxChannel> Channels, HdxLive? Live, string? LiveNote, IReadOnlyList<HdxHint> Hints);

public sealed record ProcessListResult(string? Machine, IReadOnlyList<ProcessEntry> Processes);

public sealed record EndProcessResult(string Result);

/// <summary>A folder or file of the profile with its size; <c>Path</c> is relative to the profile root, separated by backslashes.</summary>
public sealed record ProfileNode(string Path, int Mb);
public sealed record ProfileRedirect(string Name, string Target);
public sealed record ProfileSetting(string Source, string Name, string Value);
public sealed record ProfileExclusion(string Source, string Item);

/// <summary>A finding about the profile. <c>Id</c> selects the translated text, <c>Args</c> fills its placeholders.</summary>
public sealed record ProfileHint(string Id, string Level, IReadOnlyDictionary<string, string>? Args = null);

/// <summary>What the VDA reports about the profile of a session's user. <c>Type</c> is Local, Roaming, Citrix UPM or FSLogix.
/// Sizes are in MB; <c>SizePartial</c> means the folder scan hit its time limit.</summary>
public sealed record ProfileInfo(
    string? Machine, string Type, bool Loaded, string? LocalPath, string? StorePath, string? Container, int? ContainerMb, int? ContainerMaxMb,
    bool Roaming, bool Temporary, bool Mandatory, int? SizeMb, bool SizePartial, int ScanSeconds,
    IReadOnlyList<string> Notes, IReadOnlyList<ProfileNode> Tree, IReadOnlyList<ProfileNode> Files,
    IReadOnlyList<ProfileRedirect> Redirected, IReadOnlyList<ProfileSetting> Settings, IReadOnlyList<ProfileExclusion> Exclusions,
    int? LocalProfiles, IReadOnlyList<ProfileHint> Hints);
