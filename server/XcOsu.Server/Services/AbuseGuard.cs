using Microsoft.Extensions.Caching.Memory;

namespace XcOsu.Server.Services;

// Bounded, expiring counters. Never store credentials or raw tokens.
public static class AbuseGuard
{
    private sealed class Counter { public int Count; }
    private static readonly MemoryCache counters = new(new MemoryCacheOptions { SizeLimit = 20000 });
    private static readonly SemaphoreSlim proxySlots = new(48, 48);
    private static readonly object sync = new();
    public static bool Allow(string key, int limit, TimeSpan window)
    {
        lock (sync)
        {
            if (!counters.TryGetValue(key, out Counter? counter))
            {
                if (counters.Count >= 19999) return false;
                counter = new Counter();
                counters.Set(key, counter, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = window, Size = 1 });
            }
            return ++counter!.Count <= limit;
        }
    }

    /// <summary>Current value of a counter without spending from it.</summary>
    public static int Count(string key)
    {
        lock (sync) return counters.TryGetValue(key, out Counter? counter) ? counter!.Count : 0;
    }

    // /oauth/token budgets, per minute and per address. Password attempts are what an attacker can iterate on; a refresh carries an
    // unguessable single-use secret, so a campus, a LAN party or a mobile carrier's CGNAT must be able to refresh freely.
    public const int PASSWORD_SIGNINS_PER_IP = 20;
    public const int REFRESHES_PER_IP = 600;
    public const int FAILED_REFRESHES_PER_IP = 30;

    /// <summary>How many signed-in accounts' worth of traffic one address may carry before the address itself is throttled.</summary>
    public const int SHARED_ADDRESS_FACTOR = 24;

    /// <summary>Per-minute budget of a category for one account (or for one anonymous address).</summary>
    public static int LimitFor(string category) => category switch
    {
        "login" => 720, "register" => 3, "proxy" => 900, "telemetry" => 60, "chat" => 90, "reports" => 5,
        "password" => 5, "social" => 30, "write" => 120, _ => 1800,
    };

    public static string Categorise(string method, string path)
    {
        bool write = method is "POST" or "PUT" or "PATCH" or "DELETE";
        if (path == "/oauth/token") return "login";
        if (path == "/users") return "register";
        if (path == "/api/xc/proxy") return "proxy";
        if (path == "/api/xc/tm") return "telemetry";
        if (path.Contains("/reports") && write) return "reports";
        // reading whether recovery is available is not an attempt at anything; only what changes or mails a secret spends this budget.
        if ((write && path.Contains("/password")) || path.StartsWith("/api/xc/account/email/")) return "password";
        // changes that land on somebody else: relationships, removing messages or comments, votes, kicking from a room or a team.
        if (write && (path.Contains("/friends") || path.Contains("/blocks")
                      || (method == "DELETE" && (path.Contains("/chat/") || path.Contains("/comments") || path.Contains("/rooms/") || path.Contains("/teams/")))))
            return "social";
        if (path.Contains("/chat/") && write) return "chat";
        return write ? "write" : "read";
    }

    /// <summary>
    /// A signed-in request spends from its account's budget; the address only has a much larger ceiling, so players sharing one
    /// address (NAT, school, tournament venue) do not starve each other. Anonymous requests have nothing but the address.
    /// Account creation and password/email flows stay strictly per address either way.
    /// </summary>
    public static bool Admit(string category, string ip, int? user)
    {
        int limit = LimitFor(category);
        var minute = TimeSpan.FromMinutes(1);
        if (user == null || category is "register" or "password" or "login")
            return Allow($"ip:{category}:{ip}", limit, minute) && (user == null || Allow($"user:{category}:{user}", limit, minute));
        return Allow($"user:{category}:{user}", limit, minute) && Allow($"ip-shared:{category}:{ip}", limit * SHARED_ADDRESS_FACTOR, minute);
    }

    public static async Task Middleware(HttpContext ctx, RequestDelegate next)
    {
        string path = ctx.Request.Path.Value ?? "";
        string category = Categorise(ctx.Request.Method, path);
        var bodyLimit = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false } && category is "login" or "register" or "password" or "reports")
            bodyLimit.MaxRequestBodySize = 16384;
        string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!Admit(category, ip, ctx.User.UserId()))
        {
            ctx.Response.StatusCode = 429;
            ctx.Response.Headers.RetryAfter = "60";
            return;
        }
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        if (category == "proxy")
        {
            // Queue briefly instead of failing: a song select page requests dozens of covers at once.
            if (!await proxySlots.WaitAsync(TimeSpan.FromSeconds(20), ctx.RequestAborted)) { ctx.Response.StatusCode = 429; return; }
            try { await next(ctx); }
            finally { proxySlots.Release(); }
        }
        else await next(ctx);
    }
}
