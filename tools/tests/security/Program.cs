using Microsoft.Extensions.Configuration;
using XcOsu.Server.Data;
using XcOsu.Server.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

string dir = Path.Combine(Path.GetTempPath(), "xc-security-" + Guid.NewGuid().ToString("N"));
// a throwaway PostgreSQL database: this suite rewrites tokens and users, never the live one.
using var database = new TestDatabase();
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
    ["DataDirectory"] = dir, ["Database:ConnectionString"] = database.ConnectionString }).Build();
int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
try
{
    var db = new Db(config);
    var accounts = new Accounts(db);
    Check(accounts.Register("TestPlayer", "test@example.invalid", "secure-password-123", "XX") == null, "registration");
    var user = accounts.GetByName("TestPlayer")!;
    Check(!user.IsAdmin, "first registrant is not administrator");
    Check(accounts.Verify("TestPlayer", "secure-password-123")?.Id == user.Id, "password login");
    Check(accounts.Verify("TestPlayer", new string('x', 257)) == null, "oversized password rejected");
    var issued = accounts.IssueToken(user.Id);
    Check(accounts.ValidateAccessToken(issued.access) == user.Id, "access token valid");
    using var c = db.Open();
    Check(c.Scalar<long>("SELECT COUNT(*) FROM tokens WHERE access_token = @a OR refresh_token = @r", ("@a", issued.access), ("@r", issued.refresh)) == 0, "tokens stored as digests");
    c.Exec("UPDATE tokens SET expires_at = 0");
    Check(accounts.ValidateAccessToken(issued.access) == null, "expired access rejected");
    var refreshed = accounts.Refresh(issued.refresh);
    Check(refreshed?.UserId == user.Id, "refresh survives access expiry");
    Check(accounts.Refresh(issued.refresh) == null, "refresh single use");
    var rotated = accounts.IssueToken(user.Id, refreshed!.Value.SessionExpiresAt);
    Check(c.Scalar<long>("SELECT session_expires_at FROM tokens") == refreshed.Value.SessionExpiresAt, "rotation preserves absolute deadline");
    c.Exec("UPDATE users SET restricted = 1");
    Check(accounts.ValidateAccessToken(rotated.access) == null && accounts.Refresh(rotated.refresh) == null, "restriction rejects both tokens");
    c.Exec("UPDATE users SET restricted = 0");
    c.Exec("UPDATE tokens SET refresh_expires_at = 0");
    Check(accounts.Refresh(rotated.refresh) == null, "expired refresh rejected");
    c.Exec("DELETE FROM tokens");
    string legacyAccess = new string('a', 64), legacyRefresh = new string('b', 64);
    c.Exec("INSERT INTO tokens (access_token, refresh_token, user_id, expires_at) VALUES (@a, @r, @u, @e)",
        ("@a", legacyAccess), ("@r", legacyRefresh), ("@u", user.Id), ("@e", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()));
    _ = new Db(config);
    Check(accounts.ValidateAccessToken(legacyAccess) == user.Id, "migration preserves legacy access");
    Check(accounts.Refresh(legacyRefresh)?.UserId == user.Id, "migration preserves legacy refresh");
    using var im = new Image<Rgba32>(8, 8);
    using var ms = new MemoryStream();
    await im.SaveAsPngAsync(ms);
    var png = await SafeImages.Reencode(ms.ToArray(), false, default);
    Check(Image.Identify(png).Width == 8, "PNG upload round trip");
    var jpg = await SafeImages.Reencode(ms.ToArray(), true, default);
    Check(Image.DetectFormat(jpg).Name == "JPEG", "screenshot normalized to JPEG");
    bool rejected = false;
    try { await SafeImages.Reencode(new byte[] {1,2,3,4}, false, default); } catch (Microsoft.AspNetCore.Http.BadHttpRequestException) { rejected = true; }
    Check(rejected, "invalid image rejected");
    using var animation = new Image<Rgba32>(8, 8);
    animation.Frames.AddFrame(animation.Frames.RootFrame);
    using var gifStream = new MemoryStream();
    await animation.SaveAsGifAsync(gifStream);
    var gif = await SafeImages.Reencode(gifStream.ToArray(), false, default);
    using var decodedGif = Image.Load(gif);
    Check(decodedGif.Frames.Count == 2, "GIF animation preserved");
    using var webpStream = new MemoryStream();
    await animation.SaveAsWebpAsync(webpStream);
    var webp = await SafeImages.Reencode(webpStream.ToArray(), false, default);
    Check(Image.DetectFormat(webp).Name == "Webp", "WebP upload preserved");
    using var oversized = new Image<Rgba32>(8193, 1);
    using var oversizedStream = new MemoryStream();
    await oversized.SaveAsPngAsync(oversizedStream);
    rejected = false;
    try { await SafeImages.Reencode(oversizedStream.ToArray(), false, default); } catch (Microsoft.AspNetCore.Http.BadHttpRequestException) { rejected = true; }
    Check(rejected, "oversized image dimensions rejected");
    var mail = new AccountMail(db, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<AccountMail>.Instance, accounts);
    string verify = new string('c', 64);
    c.Exec("INSERT INTO account_challenges VALUES (@t, @u, 'verify', @e)", ("@t", Accounts.TokenHash(verify)), ("@u", user.Id), ("@e", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()));
    Check(mail.Consume(verify, "verify"), "email verification succeeds");
    Check(!mail.Consume(verify, "verify"), "email link single use");
    var beforeReset = accounts.IssueToken(user.Id);
    string reset = new string('d', 64);
    c.Exec("INSERT INTO account_challenges VALUES (@t, @u, 'reset', @e)", ("@t", Accounts.TokenHash(reset)), ("@u", user.Id), ("@e", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()));
    Check(mail.Consume(reset, "reset", "replacement-password-123"), "verified email password reset");
    Check(accounts.ValidateAccessToken(beforeReset.access) == null && accounts.Refresh(beforeReset.refresh) == null, "reset revokes both sessions");
    Check(accounts.Verify("TestPlayer", "replacement-password-123")?.Id == user.Id, "login after email reset");
    foreach (string address in new[] { "127.0.0.1", "10.1.1.1", "172.16.0.1", "192.168.0.1", "169.254.169.254", "::1", "::ffff:127.0.0.1", "fc00::1", "fe80::1" })
        Check(!PublicNetworkHandler.IsPublic(System.Net.IPAddress.Parse(address)), "proxy rejects " + address);
    Check(PublicNetworkHandler.IsPublic(System.Net.IPAddress.Parse("8.8.8.8")), "proxy permits public IPv4");
    var users = new Users(db, accounts, new Presence(), config);
    var chat = new Chat(db, accounts, users);
    long roomId = c.Insert("INSERT INTO rooms (json, host_id, category, type, created_at) VALUES ('{}', @u, 'realtime', 'head_to_head', @at)", ("@u", user.Id), ("@at", DateTimeOffset.UtcNow.ToString("O")));
    int channelId = chat.CreateRoomChannel(roomId, "Private test room");
    Check(chat.Join(channelId, user.Id) == null, "non-member cannot join multiplayer chat");
    c.Exec("INSERT INTO chat_members (channel_id, user_id) VALUES (@c, @u)", ("@c", channelId), ("@u", user.Id));
    Check(chat.Join(channelId, user.Id) == null, "stale chat membership cannot rejoin realtime room");
    Check(!chat.CanRead(channelId, user.Id), "stale chat membership cannot read realtime room");
    Check(chat.PostRestriction(channelId, user.Id) != null, "stale chat membership cannot send to realtime room");
    var leases = Enumerable.Range(0, 32).Select(_ => ConnectionLimits.Acquire(user.Id, "192.0.2.1")!).ToList();
    Check(leases.All(x => x != null) && ConnectionLimits.Acquire(user.Id, "192.0.2.1") == null, "per-account concurrent connections bounded");
    foreach (var lease in leases) { lease.Dispose(); lease.Dispose(); }
    using var reopened = ConnectionLimits.Acquire(user.Id, "192.0.2.1");
    Check(reopened != null, "disconnect releases connection quota idempotently");
    var proxyOptions = Microsoft.Extensions.Options.Options.Create(new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
    { ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto, ForwardLimit = 1 });
    var proxyMiddleware = new Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersMiddleware(_ => Task.CompletedTask,
        Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, proxyOptions);
    var forged = new Microsoft.AspNetCore.Http.DefaultHttpContext();
    forged.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.8");
    forged.Request.Scheme = "http";
    forged.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
    forged.Request.Headers["X-Forwarded-Proto"] = "https";
    await proxyMiddleware.Invoke(forged);
    Check(forged.Connection.RemoteIpAddress.ToString() == "203.0.113.8" && forged.Request.Scheme == "http", "untrusted peer cannot forge proxy headers");
    forged.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
    forged.Request.Headers["X-Forwarded-For"] = "198.51.100.12";
    await proxyMiddleware.Invoke(forged);
    Check(forged.Connection.RemoteIpAddress!.ToString() == "198.51.100.12" && forged.Request.Scheme == "https", "trusted local reverse proxy preserved");
    var mirrors = new Mirrors(new TestHttpFactory(), db, Microsoft.Extensions.Logging.Abstractions.NullLogger<Mirrors>.Instance);
    var rooms = new RoomStore(db, mirrors);
    var roomJson = Newtonsoft.Json.Linq.JObject.Parse("{\"name\":\"Atomic playlist\",\"playlist\":[{\"beatmap_id\":123}],\"ends_at\":null}");
    var created = rooms.CreatePlaylistRoom(user.Id, roomJson);
    long itemId = roomJson["playlist"]![0]!.Value<long>("id");
    Check(c.Scalar<long>("SELECT room_id FROM playlist_items WHERE id = @i", ("@i", itemId)) == created.Id
        && roomJson["current_playlist_item"]!.Value<long>("id") == itemId, "playlist room and items created consistently");
    long beforeRooms = c.Scalar<long>("SELECT COUNT(*) FROM rooms"), beforeChannels = c.Scalar<long>("SELECT COUNT(*) FROM chat_channels");
    try { rooms.CreatePlaylistRoom(user.Id, Newtonsoft.Json.Linq.JObject.Parse("{\"name\":\"Invalid\",\"playlist\":[null]}")); throw new Exception("invalid room accepted"); }
    catch (InvalidOperationException) { }
    Check(c.Scalar<long>("SELECT COUNT(*) FROM rooms") == beforeRooms && c.Scalar<long>("SELECT COUNT(*) FROM chat_channels") == beforeChannels,
        "failed playlist creation rolls back room and channel");
    var performance = new Performance(mirrors, Microsoft.Extensions.Logging.Abstractions.NullLogger<Performance>.Instance);
    var scores = new Scores(db, users, accounts, mirrors, performance, Microsoft.Extensions.Logging.Abstractions.NullLogger<Scores>.Instance);
    for (int i = 0; i < 99; i++) c.Exec("INSERT INTO score_tokens (user_id, beatmap_id, ruleset_id, created_at) VALUES (@u, 123, 0, @at)", ("@u", user.Id), ("@at", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
    {
        try { scores.CreateToken(user.Id, 123, 0, null, null, null); }
        catch (Scores.SubmitException e) when (e.Status == 429) { }
    })));
    Check(c.Scalar<long>("SELECT COUNT(*) FROM score_tokens WHERE user_id = @u AND score_id IS NULL", ("@u", user.Id)) == 100, "concurrent score token creation respects capacity");

    // ---- post-merge review ----
    // A: a live connection belongs to the session that opened it.
    c.Exec("DELETE FROM tokens");
    Check(accounts.Register("SocketPlayer", "socket@example.invalid", "secure-password-123", "XX") == null, "second registration");
    var socketUser = accounts.GetByName("SocketPlayer")!;
    using (var connections = new SessionConnections(accounts, Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionConnections>.Instance))
    {
        var first = accounts.IssueToken(socketUser.Id);
        var other = accounts.IssueToken(socketUser.Id);
        string firstSession = accounts.ValidateAccess(first.access)!.Value.SessionId, otherSession = accounts.ValidateAccess(other.access)!.Value.SessionId;
        Check(firstSession != otherSession, "each sign-in is its own session");
        int firstAborted = 0, otherAborted = 0;
        using var firstLease = connections.Register(socketUser.Id, firstSession, () => firstAborted++);
        using var otherLease = connections.Register(socketUser.Id, otherSession, () => otherAborted++);
        Check(firstLease != null && otherLease != null && connections.Count == 2, "connections registered against their session");
        Check(connections.Register(socketUser.Id, "no-such-session", () => { }) == null && connections.Register(socketUser.Id, null, () => { }) == null, "connection without a live session refused");
        var turn = accounts.Refresh(first.refresh)!.Value;
        var renewed = accounts.IssueToken(turn.UserId, turn.SessionExpiresAt, turn.SessionId);
        accounts.NotifyRevoked(socketUser.Id);
        Check(turn.SessionId == firstSession && accounts.ValidateAccess(renewed.access)!.Value.SessionId == firstSession && firstAborted == 0 && connections.Count == 2, "token rotation keeps the connection");
        accounts.Revoke(renewed.access);
        Check(firstAborted == 1 && otherAborted == 0 && connections.Count == 1, "sign-out closes that session's connection only");
        accounts.RevokeAll(socketUser.Id);
        Check(otherAborted == 1 && connections.Count == 0, "revoking every session closes the remaining connections");
        var expiring = accounts.IssueToken(socketUser.Id);
        int expiredAborted = 0;
        using var expiringLease = connections.Register(socketUser.Id, accounts.ValidateAccess(expiring.access)!.Value.SessionId, () => expiredAborted++);
        c.Exec("DELETE FROM tokens WHERE user_id = @u", ("@u", socketUser.Id));
        accounts.NotifyRevoked(null);
        Check(expiredAborted == 1, "session removed behind the server's back closes its connection");
    }

    // C: one password length everywhere.
    Check(Accounts.PASSWORD_MIN == 8 && Accounts.PASSWORD_MAX == 256, "password bounds");
    Check(accounts.Register("LongPassword", "long@example.invalid", new string('p', 256), "XX") == null && accounts.Verify("LongPassword", new string('p', 256)) != null, "longest accepted password can sign in");
    Check(accounts.Register("TooLong", "toolong@example.invalid", new string('p', 257), "XX") != null && accounts.Register("TooShort", "tooshort@example.invalid", "1234567", "XX") != null, "registration refuses what sign-in could not verify");

    // D: players behind one address do not share a single budget; anonymous traffic and credential guessing still do.
    string nat = "198.51.100." + Random.Shared.Next(1, 250);
    bool everyone = true;
    for (int player = 1000; player < 1000 + AbuseGuard.SHARED_ADDRESS_FACTOR; player++)
        for (int i = 0; i < AbuseGuard.LimitFor("write"); i++)
            everyone &= AbuseGuard.Admit("write", nat, player);
    Check(everyone, "a full venue behind one address keeps every player's write budget");
    Check(!AbuseGuard.Admit("write", nat, 1000), "one account cannot exceed its own budget");
    Check(!AbuseGuard.Admit("write", nat, 5000), "an address has a ceiling whatever the number of accounts");
    string anonymous = "198.51.100.251";
    bool anonymousOk = true;
    for (int i = 0; i < AbuseGuard.LimitFor("write"); i++) anonymousOk &= AbuseGuard.Admit("write", anonymous, null);
    Check(anonymousOk && !AbuseGuard.Admit("write", anonymous, null), "anonymous requests are limited per address");
    bool registerOk = true;
    for (int i = 0; i < AbuseGuard.LimitFor("register"); i++) registerOk &= AbuseGuard.Admit("register", anonymous, null);
    Check(registerOk && !AbuseGuard.Admit("register", anonymous, 77), "account creation stays per address even when signed in");
    Check(AbuseGuard.REFRESHES_PER_IP >= 20 * AbuseGuard.PASSWORD_SIGNINS_PER_IP && AbuseGuard.LimitFor("login") >= AbuseGuard.REFRESHES_PER_IP, "refreshes have far more room than password attempts");

    // E: DELETE is a write, and what lands on someone else is counted apart.
    Check(AbuseGuard.Categorise("DELETE", "/api/v2/scores/5") == "write" && AbuseGuard.Categorise("GET", "/api/v2/scores/5") == "read", "DELETE counted as a write");
    Check(AbuseGuard.Categorise("DELETE", "/api/v2/friends/3") == "social" && AbuseGuard.Categorise("POST", "/api/v2/blocks") == "social"
          && AbuseGuard.Categorise("DELETE", "/api/v2/chat/channels/1/messages/2") == "social" && AbuseGuard.Categorise("DELETE", "/api/v2/comments/9") == "social"
          && AbuseGuard.Categorise("DELETE", "/api/v2/rooms/4/users/9") == "social" && AbuseGuard.Categorise("DELETE", "/api/xc/teams/2/members/9") == "social", "sensitive removals have their own budget");
    Check(AbuseGuard.Categorise("POST", "/api/v2/chat/channels/1/messages") == "chat" && AbuseGuard.Categorise("POST", "/api/v2/reports") == "reports"
          && AbuseGuard.Categorise("POST", "/api/xc/account/password") == "password" && AbuseGuard.Categorise("POST", "/oauth/token") == "login", "existing categories unchanged");
    Check(AbuseGuard.Categorise("GET", "/api/xc/password-reset") == "read" && AbuseGuard.Categorise("POST", "/api/xc/password-reset/request") == "password"
          && AbuseGuard.Categorise("POST", "/api/xc/password-reset/complete") == "password" && AbuseGuard.Categorise("GET", "/api/xc/account/email/verify") == "password",
        "asking whether recovery is available does not spend the password budget; requesting, completing and email links do");
    Check(AbuseGuard.LimitFor("social") < AbuseGuard.LimitFor("write") && AbuseGuard.LimitFor("write") < AbuseGuard.LimitFor("read"), "budgets ordered by sensitivity");

    // F: confirmation gates new accounts only, and never sign-in.
    Accounts.EmailConfirmationEnforced = true;
    Check(accounts.NeedsEmailConfirmation(socketUser.Id) && accounts.Verify("SocketPlayer", "secure-password-123") != null, "unconfirmed account can sign in but is gated");
    c.Exec("UPDATE users SET verification_required = 0 WHERE id = @u", ("@u", socketUser.Id));
    Check(!accounts.NeedsEmailConfirmation(socketUser.Id), "accounts older than the requirement are exempt");
    c.Exec("UPDATE users SET verification_required = 1, email_verified = 1 WHERE id = @u", ("@u", socketUser.Id));
    Check(!accounts.NeedsEmailConfirmation(socketUser.Id), "confirmed account is not gated");
    c.Exec("UPDATE users SET email_verified = 0 WHERE id = @u", ("@u", socketUser.Id));
    Accounts.EmailConfirmationEnforced = false;
    Check(!accounts.NeedsEmailConfirmation(socketUser.Id), "no gate when the server cannot send mail");
    Accounts.EmailConfirmationEnforced = true;

    // G: a redirect may only lead to a host that is expected to serve beatmap data.
    Check(XcOsu.Server.Api.Proxy.RedirectAllowed("assets.ppy.sh") && XcOsu.Server.Api.Proxy.RedirectAllowed("cdn.catboy.best"), "proxy follows redirects inside known mirrors");
    Check(!XcOsu.Server.Api.Proxy.RedirectAllowed("example.com") && !XcOsu.Server.Api.Proxy.RedirectAllowed("ppy.sh.example.com")
          && !XcOsu.Server.Api.Proxy.RedirectAllowed("evilppy.sh") && !XcOsu.Server.Api.Proxy.RedirectAllowed("8.8.8.8"), "proxy refuses redirects to other public hosts");

    // 3: a ranked score must be reproduced by its replay.
    Check(ReplayVerification.Reproduces(1000575, 0.9163, 33, true, 1000575, 0.9163, 33), "identical simulation verifies");
    Check(ReplayVerification.Reproduces(176943, 0.733148, 338, true, 175845, 0.732027, 337), "measured simulation drift verifies");
    Check(ReplayVerification.Reproduces(500000, 0.90, 300, true, 900000, 0.99, 900), "claiming less than the replay is never punished");
    Check(!ReplayVerification.Reproduces(1400000, 0.9163, 33, true, 1000575, 0.9163, 33), "inflated score refused");
    Check(!ReplayVerification.Reproduces(378481, 0.95, 61, true, 378481, 0.668, 61), "inflated accuracy refused");
    Check(!ReplayVerification.Reproduces(378481, 0.668, 400, true, 378481, 0.668, 61), "inflated combo refused");
    Check(!ReplayVerification.Reproduces(1000, 0.5, 5, false, 1000, 0.5, 5), "replay that fails the map refused");
    var noVerifier = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ReplayVerification:Path"] = Path.Combine(dir, "absent.dll") }).Build();
    ReplayVerification.Configure(noVerifier);
    Check(ReplayVerification.ReplayGraceSeconds == null, "no replay deadline when the verifier is not installed");
    Check(c.Scalar<long>("SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'scores' AND column_name IN ('verification', 'replay_due')") == 2, "score verification columns migrated");
    Console.WriteLine($"{checks} checks passed.");
}
finally
{
        Directory.Delete(dir, true);
}

sealed class TestHttpFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
