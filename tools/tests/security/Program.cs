using Microsoft.Extensions.Configuration;
using XcOsu.Server.Data;
using XcOsu.Server.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

string dir = Path.Combine(Path.GetTempPath(), "xc-security-" + Guid.NewGuid().ToString("N"));
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDirectory"] = dir }).Build();
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
    Check(c.Scalar<long>("SELECT COUNT(*) FROM tokens WHERE access_token = $a OR refresh_token = $r", ("$a", issued.access), ("$r", issued.refresh)) == 0, "tokens stored as digests");
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
    c.Exec("INSERT INTO tokens (access_token, refresh_token, user_id, expires_at) VALUES ($a, $r, $u, $e)",
        ("$a", legacyAccess), ("$r", legacyRefresh), ("$u", user.Id), ("$e", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()));
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
    var mail = new AccountMail(db, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<AccountMail>.Instance);
    string verify = new string('c', 64);
    c.Exec("INSERT INTO account_challenges VALUES ($t, $u, 'verify', $e)", ("$t", Accounts.TokenHash(verify)), ("$u", user.Id), ("$e", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()));
    Check(mail.Consume(verify, "verify"), "email verification succeeds");
    Check(!mail.Consume(verify, "verify"), "email link single use");
    var beforeReset = accounts.IssueToken(user.Id);
    string reset = new string('d', 64);
    c.Exec("INSERT INTO account_challenges VALUES ($t, $u, 'reset', $e)", ("$t", Accounts.TokenHash(reset)), ("$u", user.Id), ("$e", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()));
    Check(mail.Consume(reset, "reset", "replacement-password-123"), "verified email password reset");
    Check(accounts.ValidateAccessToken(beforeReset.access) == null && accounts.Refresh(beforeReset.refresh) == null, "reset revokes both sessions");
    Check(accounts.Verify("TestPlayer", "replacement-password-123")?.Id == user.Id, "login after email reset");
    foreach (string address in new[] { "127.0.0.1", "10.1.1.1", "172.16.0.1", "192.168.0.1", "169.254.169.254", "::1", "::ffff:127.0.0.1", "fc00::1", "fe80::1" })
        Check(!PublicNetworkHandler.IsPublic(System.Net.IPAddress.Parse(address)), "proxy rejects " + address);
    Check(PublicNetworkHandler.IsPublic(System.Net.IPAddress.Parse("8.8.8.8")), "proxy permits public IPv4");
    var users = new Users(db, accounts, new Presence(), config);
    var chat = new Chat(db, accounts, users);
    long roomId = c.Insert("INSERT INTO rooms (json, host_id, category, type, created_at) VALUES ('{}', $u, 'realtime', 'head_to_head', $at)", ("$u", user.Id), ("$at", DateTimeOffset.UtcNow.ToString("O")));
    int channelId = chat.CreateRoomChannel(roomId, "Private test room");
    Check(chat.Join(channelId, user.Id) == null, "non-member cannot join multiplayer chat");
    c.Exec("INSERT INTO chat_members (channel_id, user_id) VALUES ($c, $u)", ("$c", channelId), ("$u", user.Id));
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
    Check(c.Scalar<long>("SELECT room_id FROM playlist_items WHERE id = $i", ("$i", itemId)) == created.Id
        && roomJson["current_playlist_item"]!.Value<long>("id") == itemId, "playlist room and items created consistently");
    long beforeRooms = c.Scalar<long>("SELECT COUNT(*) FROM rooms"), beforeChannels = c.Scalar<long>("SELECT COUNT(*) FROM chat_channels");
    try { rooms.CreatePlaylistRoom(user.Id, Newtonsoft.Json.Linq.JObject.Parse("{\"name\":\"Invalid\",\"playlist\":[null]}")); throw new Exception("invalid room accepted"); }
    catch (InvalidOperationException) { }
    Check(c.Scalar<long>("SELECT COUNT(*) FROM rooms") == beforeRooms && c.Scalar<long>("SELECT COUNT(*) FROM chat_channels") == beforeChannels,
        "failed playlist creation rolls back room and channel");
    var performance = new Performance(mirrors, Microsoft.Extensions.Logging.Abstractions.NullLogger<Performance>.Instance);
    var scores = new Scores(db, users, accounts, mirrors, performance, Microsoft.Extensions.Logging.Abstractions.NullLogger<Scores>.Instance);
    for (int i = 0; i < 99; i++) c.Exec("INSERT INTO score_tokens (user_id, beatmap_id, ruleset_id, created_at) VALUES ($u, 123, 0, $at)", ("$u", user.Id), ("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
    {
        try { scores.CreateToken(user.Id, 123, 0, null, null, null); }
        catch (Scores.SubmitException e) when (e.Status == 429) { }
    })));
    Check(c.Scalar<long>("SELECT COUNT(*) FROM score_tokens WHERE user_id = $u AND score_id IS NULL", ("$u", user.Id)) == 100, "concurrent score token creation respects capacity");
    Console.WriteLine($"{checks} checks passed.");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(dir, true);
}

sealed class TestHttpFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
