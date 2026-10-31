// SSS – Session Self Service
// Composition root: configuration, security pipeline and the (small) HTTP API.
// All business rules live in Services/SessionService; all Citrix access lives behind ISessionProvider.

using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Sss.Web;
using Sss.Web.Citrix;
using Sss.Web.Models;
using Sss.Web.Providers;
using Sss.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Defaults come from appsettings.json; administrators override them in sss.config.json next to the app.
builder.Configuration.AddJsonFile("sss.config.json", optional: true, reloadOnChange: false);

// ---------------------------------------------------------------------------------------------
// Configuration checks: fail at startup instead of running in an unsafe or unusable state
// ---------------------------------------------------------------------------------------------
var options = builder.Configuration.GetSection("Sss").Get<SssOptions>() ?? new SssOptions();

var devAuth = options.Auth.Mode.Equals("Dev", StringComparison.OrdinalIgnoreCase);
if (devAuth && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Sss:Auth:Mode=Dev is only allowed when ASPNETCORE_ENVIRONMENT=Development.");
if (!devAuth && !options.Auth.Mode.Equals("Iis", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException($"Unknown Sss:Auth:Mode '{options.Auth.Mode}'. Use 'Iis' or 'Dev'.");

var mockProvider = options.Provider.Equals("Mock", StringComparison.OrdinalIgnoreCase);
if (!mockProvider && !options.Provider.Equals("Citrix", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException($"Unknown Sss:Provider '{options.Provider}'. Use 'Citrix' or 'Mock'.");

// The snap-in override dot-sources a script named in the configuration: a test hook, never for production.
if (!mockProvider && !string.IsNullOrWhiteSpace(options.Citrix.SnapinOverride) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Sss:Citrix:SnapinOverride is a test hook and only allowed when ASPNETCORE_ENVIRONMENT=Development.");

// The link is rendered as an href, so only absolute http(s) addresses are accepted.
if (!string.IsNullOrWhiteSpace(options.StoreFrontUrl)
    && !(Uri.TryCreate(options.StoreFrontUrl, UriKind.Absolute, out var sf) && sf.Scheme is "http" or "https"))
    throw new InvalidOperationException("Sss:StoreFrontUrl must be an absolute http:// or https:// address.");

builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

// ---------------------------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------------------------
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new QualityEvaluator(options.Quality));

if (mockProvider)
{
    builder.Services.AddSingleton<MockSessionProvider>();
    builder.Services.AddSingleton<ISessionProvider>(sp => sp.GetRequiredService<MockSessionProvider>());
    builder.Services.AddSingleton<IHelpdeskProvider>(sp => sp.GetRequiredService<MockSessionProvider>());
    builder.Services.AddSingleton<ISessionDetailsProvider>(sp => sp.GetRequiredService<MockSessionProvider>());
    builder.Services.AddSingleton<IUserDirectory, MockUserDirectory>();
}
else
{
    var citrix = options.Citrix;
    builder.Services.AddSingleton(citrix);

    // Live sessions and log-off: warm PowerShell workers (also a hosted service so they start with the app).
    builder.Services.AddSingleton<BrokerWorkerPool>();
    builder.Services.AddSingleton<IBrokerClient>(sp => sp.GetRequiredService<BrokerWorkerPool>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BrokerWorkerPool>());

    // History and quality metrics: Monitor OData, authenticated as the app pool identity (Windows auth).
    if (citrix.Monitor.Enabled)
    {
        builder.Services.AddSingleton<IMonitorClient>(sp => new ODataMonitorClient(
            new HttpClient(new SocketsHttpHandler
            {
                Credentials = System.Net.CredentialCache.DefaultCredentials,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            {
                BaseAddress = ODataMonitorClient.BuildBaseAddress(citrix),
                Timeout = TimeSpan.FromSeconds(citrix.RequestTimeoutSeconds),
            }, citrix));
    }

    builder.Services.AddSingleton(sp => new CitrixSessionProvider(
        sp.GetRequiredService<IBrokerClient>(),
        sp.GetService<IMonitorClient>(),
        sp.GetRequiredService<QualityEvaluator>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<CitrixSessionProvider>>()));
    builder.Services.AddSingleton<ISessionProvider>(sp => sp.GetRequiredService<CitrixSessionProvider>());
    builder.Services.AddSingleton<IHelpdeskProvider>(sp => sp.GetRequiredService<CitrixSessionProvider>());
    builder.Services.AddSingleton<ISessionDetailsProvider>(sp => sp.GetRequiredService<CitrixSessionProvider>());
    builder.Services.AddSingleton<IUserDirectory, WindowsUserDirectory>();
}

builder.Services.AddSingleton(sp => new AuditLog(
    Path.GetFullPath(options.Audit.Directory, builder.Environment.ContentRootPath),
    sp.GetRequiredService<TimeProvider>(), options.Audit.PerServerFiles));
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<DetailsService>();
builder.Services.AddSingleton<StatusService>(sp => new StatusService(
    options, sp.GetRequiredService<HelpdeskAccess>(), sp.GetRequiredService<TimeProvider>(),
    sp.GetService<IBrokerClient>(), sp.GetService<IMonitorClient>()));
builder.Services.AddSingleton<HelpdeskAccess>();
builder.Services.AddSingleton<HelpdeskService>();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

// Several servers behind one name need the same key ring, otherwise a CSRF token issued by one server is rejected by another.
var dp = builder.Services.AddDataProtection().SetApplicationName("SSS");
if (options.DataProtection.KeyDirectory is { Length: > 0 } keyDir)
{
    dp.PersistKeysToFileSystem(new DirectoryInfo(keyDir));
    if (options.DataProtection.ProtectToSid is { Length: > 0 } sid)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Sss:DataProtection:ProtectToSid needs Windows.");
        dp.ProtectKeysWithDpapiNG($"SID={sid}", Microsoft.AspNetCore.DataProtection.XmlEncryption.DpapiNGProtectionDescriptorFlags.None);
    }
}

// CSRF: the token travels in a header, the matching secret in a SameSite=Strict, HttpOnly cookie.
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.Name = "sss.csrf";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});

// Authentication is done by IIS (Windows authentication, anonymous access off); ASP.NET Core only
// surfaces the result. Every endpoint requires an authenticated user. In Dev mode a fixed demo user is used.
if (!devAuth)
{
    builder.Services.AddAuthentication("Windows") /* = IISServerDefaults.AuthenticationScheme; literal keeps the project buildable off Windows */;
    builder.Services.AddAuthorizationBuilder()
        .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser().Build());
}

// Rate limits are per user (per IP if no user is known). Reads are limited too because every read
// triggers Citrix queries and the PowerShell workers are a shared, small resource.
builder.Services.AddRateLimiter(rl =>
{
    rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rl.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
    rl.AddPolicy("logoff", http => PerUserPerMinute(http, options, options.RateLimit.LogoffRequestsPerMinute));
    rl.AddPolicy("read", http => PerUserPerMinute(http, options, options.RateLimit.ReadRequestsPerMinute));
    rl.AddPolicy("health", http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter("health",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
    rl.AddPolicy("helpdesk", http => PerUserPerMinute(http, options, options.RateLimit.HelpdeskRequestsPerMinute));
});

var app = builder.Build();

if (!mockProvider && options.Citrix.Monitor.Enabled && ODataMonitorClient.BuildBaseAddresses(options.Citrix).Any(u => u.Scheme == Uri.UriSchemeHttp))
    app.Logger.LogWarning("Monitor OData is accessed over HTTP (Windows authentication without transport encryption). Remove Sss:Citrix:Monitor:UseHttps=false or set Sss:Citrix:Monitor:BaseUrl to an https:// address.");

// ---------------------------------------------------------------------------------------------
// Pipeline (order matters)
// ---------------------------------------------------------------------------------------------

// Behind IIS the application lives below a path such as /sss (set by the ASP.NET Core Module).
// Sss:PathBase does the same for local runs, e.g. to test the IIS layout on a Mac.
if (builder.Configuration["Sss:PathBase"] is { Length: > 0 } pathBase) app.UsePathBase(pathBase);

// The UI uses relative URLs, so /sss must become /sss/.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.PathBase.HasValue && !ctx.Request.Path.HasValue && HttpMethods.IsGet(ctx.Request.Method))
    {
        ctx.Response.Redirect(ctx.Request.PathBase + "/" + ctx.Request.QueryString);
        return;
    }
    await next();
});

if (!app.Environment.IsDevelopment()) app.UseHsts();

// Backend failures never leak details to the browser: only a status code (503 = Citrix side, 500 = bug).
// The exception itself is logged once by the exception handler middleware.
app.UseExceptionHandler(errorApp => errorApp.Run(ctx =>
{
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    ctx.Response.StatusCode = ex is ProviderUnavailableException ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError;
    return Task.CompletedTask;
}));

// Browser hardening: no inline script/style, no framing, no referrer, no sniffing, no cross-origin embedding.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    h.XContentTypeOptions = "nosniff";
    h.XFrameOptions = "DENY";
    h.Append("Referrer-Policy", "no-referrer");
    h.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
    h.Append("Cross-Origin-Opener-Policy", "same-origin");
    h.Append("Cross-Origin-Resource-Policy", "same-origin");
    if (ctx.Request.Path.StartsWithSegments("/api")) h.CacheControl = "no-store";
    await next();
});

if (!devAuth)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
app.UseRateLimiter(); // after authentication so the limiter can partition by user

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
});

// ---------------------------------------------------------------------------------------------
// API. Every handler resolves the caller with ResolveUser (never from parameters).
// ---------------------------------------------------------------------------------------------
var uidPattern = new Regex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

// Load balancer probe: no details, no identity. 503 only when the Broker (needed to list and log off sessions) fails.
if (options.Health.Enabled)
    app.MapGet("/healthz", async (StatusService svc, CancellationToken ct) =>
    {
        var h = await svc.HealthAsync(ct);
        return Results.Json(new { status = h.Status }, statusCode: h.Ok ? 200 : 503);
    }).AllowAnonymous().RequireRateLimiting("health");

var api = app.MapGroup("/api");

// CSRF validation for every state-changing request.
api.AddEndpointFilter(async (ctx, next) =>
{
    var http = ctx.HttpContext;
    if (HttpMethods.IsPost(http.Request.Method))
    {
        try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return Results.StatusCode(StatusCodes.Status400BadRequest); }
    }
    return await next(ctx);
});

api.MapGet("/csrf", (HttpContext http, IAntiforgery af) =>
    Results.Ok(new { token = af.GetAndStoreTokens(http).RequestToken }));

// Who am I, plus the few settings the UI needs.
api.MapGet("/me", (HttpContext http, HelpdeskAccess helpdesk) =>
{
    var user = ResolveUser(http, options);
    return user is null
        ? Results.Unauthorized()
        : Results.Ok(new
        {
            name = user.Name,
            refreshSeconds = options.RefreshSeconds,
            ranges = SessionService.Ranges.Keys,
            demo = mockProvider,
            helpdesk = helpdesk.IsHelpdesk(http),
            reasonRequired = options.Helpdesk.ReasonRequired,
            storeFrontUrl = options.StoreFrontUrl,
            rttWarnMs = options.Quality.RttWarnMs,
            loadWarn = options.Quality.LoadWarn,
        });
});

// Own live sessions plus own history for the chosen range (1h, 24h, 7d, 30d).
api.MapGet("/sessions", async (HttpContext http, string? range, SessionService svc, CancellationToken ct) =>
{
    var user = ResolveUser(http, options);
    if (user is null) return Results.Unauthorized();
    if (!SessionService.Ranges.TryGetValue(range ?? "24h", out var window)) return Results.BadRequest("Unknown range.");
    return Results.Ok(await svc.GetAsync(user, window, ct));
}).RequireRateLimiting("read");

api.MapGet("/sessions.csv", async (HttpContext http, string? range, SessionService svc, CancellationToken ct) =>
{
    var user = ResolveUser(http, options);
    if (user is null) return Results.Unauthorized();
    var key = range ?? "24h";
    if (!SessionService.Ranges.TryGetValue(key, out var window)) return Results.BadRequest("Unknown range.");
    var data = await svc.GetAsync(user, window, ct);
    var csv = "﻿" + CsvWriter.Write(data.Live.Concat(data.History)); // BOM: Excel detects UTF-8
    return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", $"sss-sessions-{key}.csv");
}).RequireRateLimiting("read");

// Expert view: everything Monitor collected about one session of the caller (404 for anybody else's).
api.MapGet("/sessions/{key}/details", async (HttpContext http, string key, DetailsService svc, CancellationToken ct) =>
{
    var user = ResolveUser(http, options);
    if (user is null) return Results.Unauthorized();
    if (!Guid.TryParse(key, out _)) return Results.BadRequest("Invalid session key.");
    return await svc.GetAsync(user, key, full: false, ct) is { } d ? Results.Ok(d) : Results.NotFound();
}).RequireRateLimiting("read");

// Log-off endpoints: ownership is re-verified in SessionService before anything reaches Citrix.
api.MapPost("/sessions/{uid}/logoff", async (HttpContext http, string uid, SessionService svc, CancellationToken ct) =>
{
    var user = ResolveUser(http, options);
    if (user is null) return Results.Unauthorized();
    if (!uidPattern.IsMatch(uid)) return Results.BadRequest("Invalid session id.");
    return Results.Ok(await svc.LogoffAsync(user, LogoffScope.Selected, [uid], RemoteIp(http), ct));
}).RequireRateLimiting("logoff");

api.MapPost("/sessions/logoff-disconnected", async (HttpContext http, SessionService svc, CancellationToken ct) =>
{
    var user = ResolveUser(http, options);
    return user is null
        ? Results.Unauthorized()
        : Results.Ok(await svc.LogoffAsync(user, LogoffScope.Disconnected, null, RemoteIp(http), ct));
}).RequireRateLimiting("logoff");

api.MapPost("/sessions/logoff-all", async (HttpContext http, SessionService svc, CancellationToken ct) =>
{
    var user = ResolveUser(http, options);
    return user is null
        ? Results.Unauthorized()
        : Results.Ok(await svc.LogoffAsync(user, LogoffScope.All, null, RemoteIp(http), ct));
}).RequireRateLimiting("logoff");

// ---------------------------------------------------------------------------------------------
// Helpdesk: members of Sss:Helpdesk:Group may find users, see their sessions and log them off.
// Separate endpoints so the "own sessions only" rules above never get mixed up with these. The group check
// runs before anything else; every action is audited with actor, target and (optional) reason.
// The target user is identified by SID taken from the helpdesk's own search results.
// ---------------------------------------------------------------------------------------------
var hd = api.MapGroup("/helpdesk");
hd.AddEndpointFilter(async (ctx, next) =>
    ctx.HttpContext.RequestServices.GetRequiredService<HelpdeskAccess>().IsHelpdesk(ctx.HttpContext)
        ? await next(ctx)
        : Results.StatusCode(StatusCodes.Status403Forbidden));
hd.RequireRateLimiting("helpdesk");

hd.MapGet("/users", async (HttpContext http, string? q, HelpdeskService svc, CancellationToken ct) =>
{
    var actor = ResolveUser(http, options);
    if (actor is null) return Results.Unauthorized();
    if (q is null || q.Trim().Length < 2 || !HelpdeskService.IsValidQuery(q.Trim())) return Results.BadRequest("Search text must have 2-64 plain characters.");
    return Results.Ok(await svc.FindUsersAsync(actor, q.Trim(), RemoteIp(http), ct));
});

hd.MapGet("/live", async (HttpContext http, string? q, HelpdeskService svc, CancellationToken ct) =>
{
    var actor = ResolveUser(http, options);
    if (actor is null) return Results.Unauthorized();
    q = q?.Trim() ?? "";
    if (!HelpdeskService.IsValidQuery(q)) return Results.BadRequest("Invalid filter.");
    return Results.Ok(await svc.ListLiveAsync(actor, q, RemoteIp(http), ct));
});

hd.MapGet("/sessions", async (HttpContext http, string? sid, string? range, HelpdeskService svc, CancellationToken ct) =>
{
    var actor = ResolveUser(http, options);
    if (actor is null) return Results.Unauthorized();
    if (sid is null || !HelpdeskService.SidRegex().IsMatch(sid) || sid.Length > 184) return Results.BadRequest("Invalid user.");
    if (!SessionService.Ranges.TryGetValue(range ?? "24h", out var window)) return Results.BadRequest("Unknown range.");
    var view = await svc.GetUserAsync(actor, sid, window, RemoteIp(http), ct);
    return Results.Ok(new { user = new { name = view.User.Name, sid = view.User.Sid }, view.Sessions.Live, view.Sessions.History, view.Sessions.GeneratedAt, view.Sessions.Warnings });
});

hd.MapGet("/sessions/{key}/details", async (HttpContext http, string key, string? sid, HelpdeskService svc, CancellationToken ct) =>
{
    var actor = ResolveUser(http, options);
    if (actor is null) return Results.Unauthorized();
    if (sid is null || !HelpdeskService.SidRegex().IsMatch(sid) || sid.Length > 184) return Results.BadRequest("Invalid user.");
    if (!Guid.TryParse(key, out _)) return Results.BadRequest("Invalid session key.");
    return await svc.GetDetailsAsync(actor, sid, key, RemoteIp(http), ct) is { } d ? Results.Ok(d) : Results.NotFound();
});

hd.MapPost("/message", async (HttpContext http, HelpdeskMessageRequest body, HelpdeskService svc, CancellationToken ct) =>
{
    var actor = ResolveUser(http, options);
    if (actor is null) return Results.Unauthorized();
    if (body.Sid is null || !HelpdeskService.SidRegex().IsMatch(body.Sid) || body.Sid.Length > 184) return Results.BadRequest("Invalid user.");
    var uids = body.Uids ?? [];
    if (uids.Length is 0 or > 100 || uids.Any(u => !uidPattern.IsMatch(u))) return Results.BadRequest("Invalid session id.");
    var text = HelpdeskService.NormalizeMessage(body.Text);
    if (text is null) return Results.BadRequest("A message is required.");
    return Results.Ok(await svc.SendMessageAsync(actor, body.Sid, uids, text, RemoteIp(http), ct));
});

// Health checks for the status page; technical details are for helpdesk eyes only.
hd.MapGet("/status", async (StatusService svc, CancellationToken ct) => Results.Ok(await svc.RunAsync(ct)));

hd.MapPost("/logoff", async (HttpContext http, HelpdeskLogoffRequest body, HelpdeskService svc, CancellationToken ct) =>
{
    var actor = ResolveUser(http, options);
    if (actor is null) return Results.Unauthorized();
    if (body.Sid is null || !HelpdeskService.SidRegex().IsMatch(body.Sid) || body.Sid.Length > 184) return Results.BadRequest("Invalid user.");
    if (!Enum.TryParse<LogoffScope>(body.Scope, ignoreCase: true, out var scope)) return Results.BadRequest("Invalid scope.");
    var uids = body.Uids ?? [];
    if (uids.Length > 100 || uids.Any(u => !uidPattern.IsMatch(u))) return Results.BadRequest("Invalid session id.");
    var reason = HelpdeskService.NormalizeReason(body.Reason);
    if (options.Helpdesk.ReasonRequired && reason is null) return Results.BadRequest("A reason is required.");
    return Results.Ok(await svc.LogoffAsync(actor, body.Sid, scope, uids, reason, RemoteIp(http), ct));
});

app.Run();

// ---------------------------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------------------------
static string? RemoteIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

/// <summary>The only place that decides who the caller is. Never reads identity from request parameters.</summary>
static UserIdentity? ResolveUser(HttpContext http, SssOptions o)
{
    if (o.Auth.Mode.Equals("Dev", StringComparison.OrdinalIgnoreCase))
        return new UserIdentity(o.Auth.DevUserSid, o.Auth.DevUserName);
    return OperatingSystem.IsWindows() && http.User.Identity is WindowsIdentity { IsAuthenticated: true, User: { } sid } wi
        ? new UserIdentity(sid.Value, wi.Name)
        : null;
}

/// <summary>Fixed one-minute window per user SID (falls back to the client IP).</summary>
static RateLimitPartition<string> PerUserPerMinute(HttpContext http, SssOptions o, int permits) =>
    RateLimitPartition.GetFixedWindowLimiter(
        ResolveUser(http, o)?.Sid ?? http.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });

/// <summary>Body of <c>POST api/helpdesk/logoff</c>. Scope: selected, disconnected or all.</summary>
/// <summary>Body of <c>POST api/helpdesk/message</c>.</summary>
public sealed record HelpdeskMessageRequest(string? Sid, string[]? Uids, string? Text);

public sealed record HelpdeskLogoffRequest(string? Sid, string? Scope, string[]? Uids, string? Reason);

public partial class Program;
