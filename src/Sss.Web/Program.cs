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
using Sss.Web.Audit;
using Sss.Web.Citrix;
using Sss.Web.Models;
using Sss.Web.Providers;
using Sss.Web.Services;

// "Sss.Web.exe --check-config [file]" validates the configuration and exits without starting the web server.
if (args.Length > 0 && args[0] == "--check-config") Environment.Exit(ConfigCli.Run(args, Console.Out));

var builder = WebApplication.CreateBuilder(args);

// Defaults come from appsettings.json; administrators override them in sss.config.json next to the app.
builder.Configuration.AddJsonFile("sss.config.json", optional: true, reloadOnChange: false);
// Settings that belong to one server only (paths, a local bootstrap controller ...); sss.config.json stays identical on all servers.
builder.Configuration.AddJsonFile("sss.local.json", optional: true, reloadOnChange: false);

// ---------------------------------------------------------------------------------------------
// Configuration checks: fail at startup instead of running in an unsafe or unusable state
// ---------------------------------------------------------------------------------------------
var options = builder.Configuration.GetSection("Sss").Get<SssOptions>() ?? new SssOptions();

var unknownConfigKeys = ConfigCheck.UnknownKeys(builder.Configuration.GetSection("Sss"));
var validation = ConfigValidator.Validate(options, builder.Configuration.GetSection("Sss"), builder.Environment.IsDevelopment());
if (validation.FirstOrDefault(f => f.Level == "error") is { } firstError) throw new InvalidOperationException(firstError.Message);

var devAuth = options.Auth.Mode.Equals("Dev", StringComparison.OrdinalIgnoreCase);
var mockProvider = options.Provider.Equals("Mock", StringComparison.OrdinalIgnoreCase);

builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

// ---------------------------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------------------------
var auditDir = Path.GetFullPath(options.Audit.Directory, builder.Environment.ContentRootPath);
var dataDir = Path.GetFullPath(options.DataDirectory, builder.Environment.ContentRootPath);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IConfigurationRoot>(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new QualityEvaluator(options.Quality));

if (mockProvider)
{
    builder.Services.AddSingleton<MockSessionProvider>();
    builder.Services.AddSingleton<ISessionProvider>(sp => sp.GetRequiredService<MockSessionProvider>());
    builder.Services.AddSingleton<IHelpdeskProvider>(sp => sp.GetRequiredService<MockSessionProvider>());
    builder.Services.AddSingleton<ISessionDetailsProvider>(sp => sp.GetRequiredService<MockSessionProvider>());
    builder.Services.AddSingleton<IProcessProvider>(sp => sp.GetRequiredService<MockSessionProvider>());
    builder.Services.AddSingleton<IUserDirectory, MockUserDirectory>();
}
else
{
    var citrix = options.Citrix;
    builder.Services.AddSingleton(citrix);

    // The Delivery Controllers: configured and discovered ones, their health, and the order requests try them in.
    var controllerDirectory = new ControllerDirectory(citrix, dataDir, TimeProvider.System);
    builder.Services.AddSingleton(controllerDirectory);
    builder.Services.AddSingleton<ControllerDiscoveryService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ControllerDiscoveryService>());

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
            }, citrix, controllerDirectory));
    }

    builder.Services.AddSingleton(sp => new CitrixSessionProvider(
        sp.GetRequiredService<IBrokerClient>(),
        sp.GetService<IMonitorClient>(),
        sp.GetRequiredService<QualityEvaluator>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<CitrixSessionProvider>>(),
        options.Environment));
    builder.Services.AddSingleton<ISessionProvider>(sp => sp.GetRequiredService<CitrixSessionProvider>());
    builder.Services.AddSingleton<IHelpdeskProvider>(sp => sp.GetRequiredService<CitrixSessionProvider>());
    builder.Services.AddSingleton<ISessionDetailsProvider>(sp => sp.GetRequiredService<CitrixSessionProvider>());
    builder.Services.AddSingleton<IProcessProvider>(sp => sp.GetRequiredService<CitrixSessionProvider>());
    builder.Services.AddSingleton<IUserDirectory, WindowsUserDirectory>();
}

// Audit trail: local hash-chained file first, then a copy to every configured central sink (see docs/AUDIT.md).
var auditStream = new JsonlStream(auditDir, "audit", TimeProvider.System, options.Audit.PerServerFiles);
var opsStream = new JsonlStream(auditDir, "ops", TimeProvider.System, options.Audit.PerServerFiles);
if (options.Audit.OpsStream) builder.Logging.AddProvider(new OpsLoggerProvider(opsStream, TimeProvider.System));
var auditSinks = AuditSinkFactory.Create(options.Audit.Sinks, AppInfo.Version);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(sp => new AuditLog(auditStream, sp.GetRequiredService<TimeProvider>(), () =>
{
    var h = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
    return h is null ? (null, null) : (h.TraceIdentifier, sp.GetRequiredService<HelpdeskAccess>().RoleName(h));
}));
builder.Services.AddSingleton(new AuditReader([auditDir, .. options.Audit.Sinks.Unc.Where(u => u.Enabled && u.Path.Length > 0).Select(u => u.Path)]));
builder.Services.AddSingleton(sp => new AuditForwarder(options.Audit.OpsStream ? [auditStream, opsStream] : [auditStream], auditSinks, dataDir,
    options.Audit.RetentionDays, options.Audit.PerServerFiles, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<AuditForwarder>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditForwarder>());
builder.Services.AddSingleton(sp => new EnvironmentService(options, sp.GetService<IMonitorClient>(), sp.GetRequiredService<TimeProvider>(), mockProvider));
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<DetailsService>();
builder.Services.AddSingleton<ProcessService>();
builder.Services.AddSingleton<StatusService>(sp => new StatusService(
    options, sp.GetRequiredService<HelpdeskAccess>(), sp.GetRequiredService<TimeProvider>(),
    sp.GetService<IBrokerClient>(), sp.GetService<IMonitorClient>(), unknownConfigKeys,
    sp.GetService<ControllerDirectory>(), sp.GetService<ControllerDiscoveryService>(), sp.GetRequiredService<AuditForwarder>(), auditDir,
    sp.GetRequiredService<InstanceRegistry>()));
builder.Services.AddSingleton(sp => new InstanceRegistry(options, builder.Environment.ContentRootPath, dataDir, sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<InstanceRegistry>>(), () => sp.GetRequiredService<StatusService>().LastStatus));
builder.Services.AddHostedService(sp => sp.GetRequiredService<InstanceRegistry>());
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

foreach (var key in unknownConfigKeys)
    app.Logger.LogWarning("Unknown configuration key {Key} is ignored.", key);

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

// Prometheus metrics: off by default, read only from the configured addresses.
if (options.Metrics.Enabled)
{
    var allowed = options.Metrics.AllowedIps.Select(a => a.Contains('/') ? System.Net.IPNetwork.Parse(a) : new System.Net.IPNetwork(System.Net.IPAddress.Parse(a), a.Contains(':') ? 128 : 32)).ToArray();
    app.MapGet("/metrics", (HttpContext http, StatusService status, InstanceRegistry instances, AuditForwarder forwarder, IServiceProvider sp) =>
    {
        var ip = http.Connection.RemoteIpAddress;
        if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4();   // dual-stack sockets report IPv4 clients as ::ffff:a.b.c.d
        if (ip is null || !(System.Net.IPAddress.IsLoopback(ip) || allowed.Any(n => n.Contains(ip)))) return Results.StatusCode(StatusCodes.Status403Forbidden);
        Metrics.Set("sss_up", 1);
        Metrics.Set("sss_build_info", 1, "version", AppInfo.Version);
        Metrics.Set("sss_draining", instances.Draining ? 1 : 0);
        if (sp.GetService<ControllerDirectory>() is { } dir)
            foreach (var c in dir.All)
            {
                var h = dir.HealthOf(c.Name);
                Metrics.Set("sss_controller_up", h.OpenUntil is { } u && u > DateTimeOffset.UtcNow ? 0 : 1, "controller", c.Name);
                if (h.EwmaMs is { } ms) Metrics.Set("sss_controller_latency_ms", ms, "controller", c.Name);
                Metrics.Set("sss_controller_requests_total", h.Ok, "controller", c.Name);
                Metrics.Set("sss_controller_failures_total", h.Failed, "controller", c.Name);
            }
        foreach (var (sink, st) in forwarder.States)
        {
            Metrics.Set("sss_audit_sink_pending_bytes", st.PendingBytes, "sink", sink.Name);
            Metrics.Set("sss_audit_sink_sent_total", st.Sent, "sink", sink.Name);
            Metrics.Set("sss_audit_sink_failing", st.Error is not null && (st.LastSuccess is null || st.LastError > st.LastSuccess) ? 1 : 0, "sink", sink.Name);
        }
        if (instances.Enabled)
        {
            var views = instances.View();
            Metrics.Set("sss_instances_online", views.Count(v => v.State is "online" or "draining"));
            Metrics.Set("sss_instances_missing", views.Count(v => v.State is "missing" or "stale"));
            Metrics.Set("sss_instances_config_drift", views.Count(v => v.InSync == false));
        }
        return Results.Text(Metrics.Render(), "text/plain; version=0.0.4; charset=utf-8");
    }).AllowAnonymous().RequireRateLimiting("health");
}

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
api.MapGet("/me", (HttpContext http, HelpdeskAccess helpdesk, ProcessService processes) =>
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
            caps = new
            {
                act = helpdesk.Has(http, Cap.Act),
                deep = helpdesk.Has(http, Cap.Deep),
                audit = helpdesk.Has(http, Cap.Audit),
                admin = helpdesk.Has(http, Cap.Admin),
            },
            role = helpdesk.RoleName(http),
            reasonRequired = options.Helpdesk.ReasonRequired,
            storeFrontUrl = options.StoreFrontUrl,
            rttWarnMs = options.Quality.RttWarnMs,
            loadWarn = options.Quality.LoadWarn,
            quality = options.Quality,
            processes = processes.Available(helpdesk.IsHelpdesk(http)),
            profile = processes.ProfileAvailable(helpdesk.IsHelpdesk(http)),
            deepProcesses = processes.Available(true) && helpdesk.Has(http, Cap.Deep),
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

// Processes of an own session (needs the JEA endpoint on the VDAs). Ownership is checked in ProcessService and again in the worker.
api.MapGet("/sessions/{uid}/processes", async (HttpContext http, string uid, ProcessService svc, CancellationToken ct) =>
{
    var user = ResolveUser(http, options);
    if (user is null) return Results.Unauthorized();
    if (!svc.Available(false)) return Results.NotFound();
    if (!uidPattern.IsMatch(uid)) return Results.BadRequest("Invalid session id.");
    return await svc.ListAsync(user, uid, null, null, RemoteIp(http), ct) is { } list ? Results.Ok(list) : Results.NotFound();
}).RequireRateLimiting("read");

api.MapGet("/sessions/{uid}/profile", async (HttpContext http, string uid, ProcessService svc, CancellationToken ct) =>
{
    var user = ResolveUser(http, options);
    if (user is null) return Results.Unauthorized();
    if (!svc.ProfileAvailable(false)) return Results.NotFound();
    if (!uidPattern.IsMatch(uid)) return Results.BadRequest("Invalid session id.");
    return await svc.ProfileAsync(user, uid, null, null, RemoteIp(http), ct) is { } info ? Results.Ok(info) : Results.NotFound();
}).RequireRateLimiting("read");

api.MapPost("/sessions/{uid}/processes/{pid:int}/end", async (HttpContext http, string uid, int pid, EndProcessRequest? body, ProcessService svc, CancellationToken ct) =>
{
    var user = ResolveUser(http, options);
    if (user is null) return Results.Unauthorized();
    if (!svc.Available(false)) return Results.NotFound();
    if (!uidPattern.IsMatch(uid)) return Results.BadRequest("Invalid session id.");
    return await svc.EndAsync(user, uid, pid, body?.Force == true, null, null, RemoteIp(http), ct) is { } r ? Results.Ok(new EndProcessResult(r)) : Results.NotFound();
}).RequireRateLimiting("logoff");

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
    ctx.HttpContext.RequestServices.GetRequiredService<HelpdeskAccess>().Caps(ctx.HttpContext) != Cap.None
        ? await next(ctx)
        : Results.StatusCode(StatusCodes.Status403Forbidden));
hd.RequireRateLimiting("helpdesk");

hd.MapGet("/users", async (HttpContext http, string? q, HelpdeskService svc, CancellationToken ct) =>
{
    var actor = ResolveUser(http, options);
    if (actor is null) return Results.Unauthorized();
    if (q is null || q.Trim().Length < 2 || !HelpdeskService.IsValidQuery(q.Trim())) return Results.BadRequest("Search text must have 2-64 plain characters.");
    return Results.Ok(await svc.FindUsersAsync(actor, q.Trim(), RemoteIp(http), ct));
}).RequireCap(Cap.View);

hd.MapGet("/live", async (HttpContext http, string? q, HelpdeskService svc, CancellationToken ct) =>
{
    var actor = ResolveUser(http, options);
    if (actor is null) return Results.Unauthorized();
    q = q?.Trim() ?? "";
    if (!HelpdeskService.IsValidQuery(q)) return Results.BadRequest("Invalid filter.");
    return Results.Ok(await svc.ListLiveAsync(actor, q, RemoteIp(http), ct));
}).RequireCap(Cap.View);

hd.MapGet("/sessions", async (HttpContext http, string? sid, string? range, HelpdeskService svc, CancellationToken ct) =>
{
    var actor = ResolveUser(http, options);
    if (actor is null) return Results.Unauthorized();
    if (sid is null || !HelpdeskService.SidRegex().IsMatch(sid) || sid.Length > 184) return Results.BadRequest("Invalid user.");
    if (!SessionService.Ranges.TryGetValue(range ?? "24h", out var window)) return Results.BadRequest("Unknown range.");
    var view = await svc.GetUserAsync(actor, sid, window, RemoteIp(http), ct);
    return Results.Ok(new { user = new { name = view.User.Name, sid = view.User.Sid }, view.Sessions.Live, view.Sessions.History, view.Sessions.GeneratedAt, view.Sessions.Warnings });
}).RequireCap(Cap.View);

hd.MapGet("/sessions/{key}/details", async (HttpContext http, string key, string? sid, HelpdeskService svc, CancellationToken ct) =>
{
    var actor = ResolveUser(http, options);
    if (actor is null) return Results.Unauthorized();
    if (sid is null || !HelpdeskService.SidRegex().IsMatch(sid) || sid.Length > 184) return Results.BadRequest("Invalid user.");
    if (!Guid.TryParse(key, out _)) return Results.BadRequest("Invalid session key.");
    return await svc.GetDetailsAsync(actor, sid, key, RemoteIp(http), ct) is { } d ? Results.Ok(d) : Results.NotFound();
}).RequireCap(Cap.View);

hd.MapGet("/sessions/{uid}/processes", async (HttpContext http, string uid, string? sid, string? reason, ProcessService svc, IUserDirectory directory, CancellationToken ct) =>
{
    var actor = ResolveUser(http, options);
    if (actor is null) return Results.Unauthorized();
    if (!svc.Available(true)) return Results.NotFound();
    if (sid is null || !HelpdeskService.SidRegex().IsMatch(sid) || sid.Length > 184) return Results.BadRequest("Invalid user.");
    if (!uidPattern.IsMatch(uid)) return Results.BadRequest("Invalid session id.");
    var why = HelpdeskService.NormalizeReason(reason);
    if (options.Helpdesk.ReasonRequired && why is null) return Results.BadRequest("A reason is required.");
    return await svc.ListAsync(new UserIdentity(sid, directory.NameOf(sid)), uid, actor, why, RemoteIp(http), ct) is { } list ? Results.Ok(list) : Results.NotFound();
}).RequireCap(Cap.Deep);
hd.MapGet("/sessions/{uid}/policies", async (HttpContext http, string uid, string? sid, string? reason, ProcessService svc, IUserDirectory directory, CancellationToken ct) =>
{
    var actor = ResolveUser(http, options);
    if (actor is null) return Results.Unauthorized();
    if (!svc.ProfileAvailable(true)) return Results.NotFound();
    if (sid is null || !HelpdeskService.SidRegex().IsMatch(sid) || sid.Length > 184) return Results.BadRequest("Invalid user.");
    if (!uidPattern.IsMatch(uid)) return Results.BadRequest("Invalid session id.");
    var why = HelpdeskService.NormalizeReason(reason);
    if (options.Helpdesk.ReasonRequired && why is null) return Results.BadRequest("A reason is required.");
    return await svc.PoliciesAsync(new UserIdentity(sid, directory.NameOf(sid)), uid, actor, why, RemoteIp(http), ct) is { } list ? Results.Ok(list) : Results.NotFound();
}).RequireCap(Cap.Deep);
hd.MapGet("/sessions/{uid}/profile", async (HttpContext http, string uid, string? sid, string? reason, ProcessService svc, IUserDirectory directory, CancellationToken ct) =>
{
    var actor = ResolveUser(http, options);
    if (actor is null) return Results.Unauthorized();
    if (!svc.ProfileAvailable(true)) return Results.NotFound();
    if (sid is null || !HelpdeskService.SidRegex().IsMatch(sid) || sid.Length > 184) return Results.BadRequest("Invalid user.");
    if (!uidPattern.IsMatch(uid)) return Results.BadRequest("Invalid session id.");
    var why = HelpdeskService.NormalizeReason(reason);
    if (options.Helpdesk.ReasonRequired && why is null) return Results.BadRequest("A reason is required.");
    return await svc.ProfileAsync(new UserIdentity(sid, directory.NameOf(sid)), uid, actor, why, RemoteIp(http), ct) is { } info ? Results.Ok(info) : Results.NotFound();
}).RequireCap(Cap.Deep);
hd.MapPost("/sessions/{uid}/processes/{pid:int}/end", async (HttpContext http, string uid, int pid, HelpdeskEndProcessRequest body, ProcessService svc, IUserDirectory directory, CancellationToken ct) =>
{
    var actor = ResolveUser(http, options);
    if (actor is null) return Results.Unauthorized();
    if (!svc.Available(true)) return Results.NotFound();
    if (body.Sid is null || !HelpdeskService.SidRegex().IsMatch(body.Sid) || body.Sid.Length > 184) return Results.BadRequest("Invalid user.");
    if (!uidPattern.IsMatch(uid)) return Results.BadRequest("Invalid session id.");
    var why = HelpdeskService.NormalizeReason(body.Reason);
    if (options.Helpdesk.ReasonRequired && why is null) return Results.BadRequest("A reason is required.");
    return await svc.EndAsync(new UserIdentity(body.Sid, directory.NameOf(body.Sid)), uid, pid, body.Force == true, actor, why, RemoteIp(http), ct) is { } r
        ? Results.Ok(new EndProcessResult(r)) : Results.NotFound();
}).RequireCap(Cap.Deep);
hd.MapPost("/message", async (HttpContext http, HelpdeskMessageRequest body, HelpdeskService svc, CancellationToken ct) =>
{
    var actor = ResolveUser(http, options);
    if (actor is null) return Results.Unauthorized();
    if (body.Sid is null || !HelpdeskService.SidRegex().IsMatch(body.Sid) || body.Sid.Length > 184) return Results.BadRequest("Invalid user.");
    var uids = body.Uids ?? [];
    if (uids.Length is 0 or > 100 || uids.Any(u => !uidPattern.IsMatch(u))) return Results.BadRequest("Invalid session id.");
    var text = HelpdeskService.NormalizeMessage(body.Text);
    if (text is null) return Results.BadRequest("A message is required.");
    var why = HelpdeskService.NormalizeReason(body.Reason);
    if (options.Helpdesk.ReasonRequired && why is null) return Results.BadRequest("A reason is required.");
    return Results.Ok(await svc.SendMessageAsync(actor, body.Sid, uids, text, why, RemoteIp(http), ct));
}).RequireCap(Cap.Act);

// Health checks for the status page; technical details are for helpdesk eyes only.
// What SSS runs with and where each value comes from (secrets masked). Read only: the files are edited on the server.
hd.MapGet("/config", (IConfigurationRoot root) => Results.Ok(EffectiveConfig.Build(options, root))).RequireCap(Cap.Admin);
// Environment tab: Workspace app versions and locations of all live sessions (counts only).
hd.MapGet("/environment", async (EnvironmentService svc, CancellationToken ct) => Results.Ok(await svc.GetAsync(ct))).RequireCap(Cap.Admin);
hd.MapGet("/instances", (InstanceRegistry registry) => Results.Ok(registry.View())).RequireCap(Cap.Admin);
hd.MapGet("/status", async (StatusService svc, CancellationToken ct) => Results.Ok(await svc.RunAsync(ct))).RequireCap(Cap.Admin);

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
}).RequireCap(Cap.Act);


// ---------------------------------------------------------------------------------------------
// Audit trail: only for the Auditor role. Reads the local and central folders, evaluates, exports. Reading it is audited too.
// ---------------------------------------------------------------------------------------------
var auditApi = api.MapGroup("/audit");
auditApi.AddEndpointFilter(async (ctx, next) =>
    ctx.HttpContext.RequestServices.GetRequiredService<HelpdeskAccess>().Has(ctx.HttpContext, Cap.Audit)
        ? await next(ctx)
        : Results.StatusCode(StatusCodes.Status403Forbidden));
auditApi.RequireRateLimiting("helpdesk");
var auditViews = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>();

AuditQuery? ParseAuditQuery(IQueryCollection q)
{
    var now = TimeProvider.System.GetUtcNow();
    var days = int.TryParse(q["days"], out var d) ? Math.Clamp(d, 1, 365) : 7;
    var from = DateTimeOffset.TryParse(q["from"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var f) ? f : now.AddDays(-days);
    var to = DateTimeOffset.TryParse(q["to"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : now.AddMinutes(1);
    if (to < from || to - from > TimeSpan.FromDays(366)) return null;
    string? V(string n) => q[n].ToString() is { Length: > 0 and <= 100 } v && !v.Any(char.IsControl) ? v : null;
    var stream = V("stream");
    if (stream is not (null or "audit" or "ops")) return null;
    return new AuditQuery(from, to, V("actor"), V("target"), V("action"), V("result"), V("server"), V("q"), stream,
        int.TryParse(q["limit"], out var l) ? Math.Clamp(l, 1, 5000) : 300);
}

auditApi.MapGet("", (HttpContext http, AuditReader reader, AuditLog log) =>
{
    var actor = ResolveUser(http, options);
    if (actor is null) return Results.Unauthorized();
    if (ParseAuditQuery(http.Request.Query) is not { } query) return Results.BadRequest("Invalid filter.");
    var key = actor.Sid;
    var now = TimeProvider.System.GetUtcNow();
    if (!auditViews.TryGetValue(key, out var last) || now - last > TimeSpan.FromMinutes(5))
    {
        auditViews[key] = now;
        log.Write(new AuditEntry("audit:view", actor, null, "success", RemoteIp(http), actor));
    }
    return Results.Ok(reader.Query(query));
});

auditApi.MapGet("/export.csv", (HttpContext http, AuditReader reader, AuditLog log) =>
{
    var actor = ResolveUser(http, options);
    if (actor is null) return Results.Unauthorized();
    if (ParseAuditQuery(http.Request.Query) is not { } query) return Results.BadRequest("Invalid filter.");
    var res = reader.Query(query with { Limit = 5000 });
    log.Write(new AuditEntry("audit:export", actor, null, $"{res.Events.Count} rows", RemoteIp(http), actor));
    var sb = new System.Text.StringBuilder("Time,Stream,Server,Actor,Role,Action,User,Session,Result,Ip,Reason,Detail,RequestId\r\n");
    foreach (var r in res.Events)
        sb.Append(string.Join(',', new[] { r.Time.ToString("o"), r.Stream, r.Server ?? "", r.Actor ?? "", r.Role ?? "", r.Action, r.User ?? "", r.Session ?? "", r.Result, r.Ip ?? "", r.Reason ?? "", r.Detail ?? "", r.RequestId ?? "" }.Select(CsvWriter.Escape))).Append("\r\n");
    return Results.File(System.Text.Encoding.UTF8.GetBytes("\uFEFF" + sb), "text/csv; charset=utf-8", "sss-audit.csv");
});

// Integrity of this server's local files: a changed, removed or inserted line in the middle breaks the hash chain.
auditApi.MapGet("/verify", (int? days) =>
{
    var from = TimeProvider.System.GetUtcNow().AddDays(-Math.Clamp(days ?? 7, 1, 90)).UtcDateTime.ToString("yyyyMMdd");
    var files = Directory.Exists(auditDir) ? Directory.GetFiles(auditDir, "*.jsonl").Order(StringComparer.Ordinal).ToArray() : [];
    var result = new List<object>();
    foreach (var f in files)
    {
        var name = Path.GetFileName(f);
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(audit|ops)-(\d{8})");
        if (!m.Success || string.CompareOrdinal(m.Groups[2].Value, from) < 0) continue;
        if (options.Audit.PerServerFiles && !name.EndsWith($"-{Environment.MachineName}.jsonl", StringComparison.OrdinalIgnoreCase)) continue;
        try { var (lines, bad) = JsonlStream.Verify(f); result.Add(new { file = name, lines, ok = bad == 0, firstBad = bad }); }
        catch (IOException) { result.Add(new { file = name, lines = 0, ok = false, firstBad = -1 }); }
    }
    return Results.Ok(result);
});

auditApi.MapGet("/sinks", (AuditForwarder fw) => Results.Ok(fw.States.Select(x => new
{
    name = x.Sink.Name, kind = x.Sink.Kind, target = x.Sink.Target, streams = x.Sink.Options.Streams,
    lastSuccess = x.State.LastSuccess, lastError = x.State.LastError, error = x.State.Error, sent = x.State.Sent, pendingBytes = x.State.PendingBytes,
})));

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
public sealed record EndProcessRequest(bool? Force);
public sealed record HelpdeskEndProcessRequest(string? Sid, bool? Force, string? Reason);
public sealed record HelpdeskMessageRequest(string? Sid, string[]? Uids, string? Text, string? Reason);

public sealed record HelpdeskLogoffRequest(string? Sid, string? Scope, string[]? Uids, string? Reason);

public partial class Program;
