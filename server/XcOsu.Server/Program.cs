using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
// xc-osu! server: entry point. Same-origin with the web client (https://osu.xcsoftworks.com). xc softworks (kudasaixc)

using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using osu.Game.Online;
using XcOsu.Server.Api;
using XcOsu.Server.Data;
using XcOsu.Server.Hubs;
using XcOsu.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// database calls are synchronous: keep enough workers ready that a burst of a few hundred requests is served at once
// instead of waiting for the pool to grow one thread at a time.
ThreadPool.GetMinThreads(out int minWorkers, out int minIo);
ThreadPool.SetMinThreads(Math.Max(minWorkers, 128), Math.Max(minIo, 64));

builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5080");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 18 * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => { o.MultipartBodyLengthLimit = 17 * 1024 * 1024; o.ValueCountLimit = 64; o.ValueLengthLimit = 65536; o.MultipartHeadersCountLimit = 16; });
// a line per request (routing, results, outgoing mirror calls) costs more under load than it tells us; our own logs stay at Information.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

builder.Services.AddHttpClient("mirrors", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("xc-osu-server/1.0 (+https://osu.xcsoftworks.com; xc softworks)");
});
builder.Services.AddHttpClient("proxy", c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => PublicNetworkHandler.Create());

builder.Services.AddSingleton<Db>();
builder.Services.AddHostedService<ProxyCacheMaintenance>();
builder.Services.AddSingleton<Accounts>();
// new accounts must confirm their address before anything social, unless nobody could ever receive the confirmation.
Accounts.EmailConfirmationEnforced = builder.Configuration.GetValue<bool?>("Accounts:RequireConfirmedEmail") != false
                                     && builder.Configuration.GetValue<bool?>("AccountMail:Enabled") != false;
builder.Services.AddSingleton<PasswordRecovery>();
builder.Services.AddSingleton<AccountMail>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AccountMail>());
builder.Services.AddSingleton<Presence>();
builder.Services.AddSingleton<Users>();
builder.Services.AddSingleton<Mirrors>();
builder.Services.AddSingleton<BeatmapSearch>();
builder.Services.AddSingleton<Performance>();
builder.Services.AddSingleton<SessionConnections>();
builder.Services.AddSingleton<Scores>();
builder.Services.AddSingleton<Chat>();
builder.Services.AddSingleton<RoomStore>();
builder.Services.AddSingleton<Notifications>();
builder.Services.AddSingleton<Medals>();
builder.Services.AddSingleton<MapPool>();
builder.Services.AddSingleton<Matchmaking>();
builder.Services.AddSingleton<DailyChallenge>();
builder.Services.AddSingleton<Telemetry>();
builder.Services.AddHostedService<Maintenance>();
ReplayVerification.Configure(builder.Configuration);
builder.Services.AddHostedService<ReplayVerification>();

builder.Services.AddAuthentication(XcTokenAuthHandler.SCHEME)
       .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, XcTokenAuthHandler>(XcTokenAuthHandler.SCHEME, null);
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("password-reset", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(15), QueueLimit = 0 }));
});

builder.Services.AddSingleton<IUserIdProvider, UserIdProvider>();
builder.Services.AddSignalR(o =>
       {
           o.AddFilter<SecurityFilter>();
           o.EnableDetailedErrors = builder.Environment.IsDevelopment();
           o.MaximumReceiveMessageSize = 1 << 20;
           o.KeepAliveInterval = TimeSpan.FromSeconds(15);
       })
       .AddMessagePackProtocol(o => o.SerializerOptions = SignalRUnionWorkaroundResolver.OPTIONS)
       .AddNewtonsoftJsonProtocol(o => o.PayloadSerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore);

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.ForwardLimit = 1; // Keep the framework loopback-only trusted proxies.
});

var app = builder.Build();

// Local operator command; never exposed through HTTP.
if (args.Length == 2 && args[0] == "grant-admin")
{
    if (!int.TryParse(args[1], out int adminId) || adminId <= 0)
        throw new ArgumentException("Usage: grant-admin <existing-user-id>");
    using var connection = app.Services.GetRequiredService<Db>().Open();
    if (connection.Exec("UPDATE users SET is_admin = 1 WHERE id = @id AND restricted = 0", ("@id", adminId)) != 1)
        throw new InvalidOperationException("Eligible account not found.");
    return;
}

// Local operator command: load the former SQLite files (xc-osu.db, telemetry.db) into PostgreSQL, replacing what is there.
if (args.Length >= 2 && args[0] == "import-sqlite")
{
    var target = app.Services.GetRequiredService<Db>();
    _ = app.Services.GetRequiredService<Telemetry>(); // its tables are created on first use
    var files = args.Skip(1).SelectMany(a => Directory.Exists(a)
        ? new[] { "xc-osu.db", "telemetry.db" }.Select(f => Path.Combine(a, f)).Where(File.Exists)
        : new[] { a }).ToList();
    if (files.Count == 0) throw new ArgumentException("Usage: import-sqlite <directory | file.db ...>");
    foreach (string f in files) Console.WriteLine(f);
    SqliteImport.Run(target, files, Console.WriteLine);
    Console.WriteLine("import complete");
    return;
}

// score pipeline: medals, daily challenge streaks, live room feeds.
{
    var scores = app.Services.GetRequiredService<Scores>();
    var medals = app.Services.GetRequiredService<Medals>();
    var daily = app.Services.GetRequiredService<DailyChallenge>();
    var rooms = app.Services.GetRequiredService<RoomStore>();
    _ = app.Services.GetRequiredService<Matchmaking>();
    scores.Submitted += async s =>
    {
        bool isDaily = daily.IsDailyRoom(s.RoomId);
        bool isMultiplayer = s.RoomId is long r && rooms.Rooms.TryGetValue(r, out var room) && room.Realtime;
        await medals.Check(s.Score, s.StarRating, s.BeatmapMaxCombo, isDaily, isMultiplayer);
        await daily.OnScore(s);
    };
}

app.UseForwardedHeaders();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(20) });
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.Use(AbuseGuard.Middleware);

app.MapGet("/api/xc/health", () => Results.Ok(new { name = "xc-osu! server", by = "xc softworks (kudasaixc)", ok = true }));

// Anonymous telemetry uses a short-lived browser session and bounded ingestion.
_ = app.Services.GetRequiredService<Telemetry>();
app.MapGet("/api/xc/tm/session", TelemetrySession.Issue);
app.MapPost("/api/xc/tm", async (HttpRequest req, Telemetry telemetry) =>
{
    if (!TelemetrySession.Valid(req)) return Results.Unauthorized();
    if (req.ContentLength > Telemetry.MAX_BODY_BYTES) return Results.StatusCode(413);

    // sendBeacon posts text/plain: read the body whatever its declared type
    using var reader = new StreamReader(req.Body);
    char[] buffer = new char[Telemetry.MAX_BODY_BYTES + 1];
    int read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
    if (read > Telemetry.MAX_BODY_BYTES) return Results.StatusCode(413);

    long? seq;
    try { seq = await telemetry.IngestAsync(new string(buffer, 0, read), req.HttpContext.RequestAborted); }
    catch (Exception e) when (e is Newtonsoft.Json.JsonException or FormatException or InvalidCastException or ArgumentException or OverflowException)
    { return Results.BadRequest(); }
    return seq == null ? Results.StatusCode(400) : Results.Json(new { seq });
});

Endpoints.Map(app);
AccountEmail.Map(app);
Proxy.Map(app);

app.Map("/notifications", async (HttpContext ctx, Chat chat, SessionConnections sessions) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest();
    if (ctx.User.UserId() is not int u) return Results.Unauthorized();
    using var connection = ConnectionLimits.Acquire(u, ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    if (connection == null) return Results.StatusCode(429);
    using var revoked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
    using var bound = sessions.Register(u, ctx.User.SessionId(), () => { try { revoked.Cancel(); } catch (ObjectDisposedException) { } });
    if (bound == null) return Results.Unauthorized();
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    try { await chat.Serve(ws, u, revoked.Token); }
    catch (OperationCanceledException) when (revoked.IsCancellationRequested) { ws.Abort(); }
    return Results.Empty;
});

app.MapHub<SpectatorHub>("/signalr/spectator");
app.MapHub<MultiplayerHub>("/signalr/multiplayer");
app.MapHub<MetadataHub>("/signalr/metadata");

app.Run();

internal sealed class UserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User?.UserId()?.ToString();
}
