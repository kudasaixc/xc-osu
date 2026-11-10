// xc-osu! server — entry point. Same-origin with the web client (https://osu.xcsoftworks.com). xc softworks (kudasaixc)

using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using osu.Game.Online;
using XcOsu.Server.Api;
using XcOsu.Server.Data;
using XcOsu.Server.Hubs;
using XcOsu.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5080");
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

builder.Services.AddHttpClient("mirrors", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("xc-osu-server/1.0 (+https://osu.xcsoftworks.com; xc softworks)");
});

builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<Accounts>();
builder.Services.AddSingleton<Presence>();
builder.Services.AddSingleton<Users>();
builder.Services.AddSingleton<Mirrors>();
builder.Services.AddSingleton<Performance>();
builder.Services.AddSingleton<Scores>();
builder.Services.AddSingleton<Chat>();
builder.Services.AddSingleton<RoomStore>();
builder.Services.AddSingleton<Notifications>();
builder.Services.AddSingleton<Medals>();
builder.Services.AddSingleton<MapPool>();
builder.Services.AddSingleton<Matchmaking>();
builder.Services.AddSingleton<DailyChallenge>();
builder.Services.AddHostedService<Maintenance>();

builder.Services.AddAuthentication(XcTokenAuthHandler.SCHEME)
       .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, XcTokenAuthHandler>(XcTokenAuthHandler.SCHEME, null);
builder.Services.AddAuthorization();

builder.Services.AddSingleton<IUserIdProvider, UserIdProvider>();
builder.Services.AddSignalR(o =>
       {
           o.EnableDetailedErrors = true;
           o.MaximumReceiveMessageSize = 1 << 20;
           o.KeepAliveInterval = TimeSpan.FromSeconds(15);
       })
       .AddMessagePackProtocol(o => o.SerializerOptions = SignalRUnionWorkaroundResolver.OPTIONS)
       .AddNewtonsoftJsonProtocol(o => o.PayloadSerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore);

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

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
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/xc/health", () => Results.Ok(new { name = "xc-osu! server", by = "xc softworks (kudasaixc)", ok = true }));

Endpoints.Map(app);
Proxy.Map(app);

app.Map("/notifications", async (HttpContext ctx, Chat chat) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest();
    if (ctx.User.UserId() is not int u) return Results.Unauthorized();
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await chat.Serve(ws, u, ctx.RequestAborted);
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
