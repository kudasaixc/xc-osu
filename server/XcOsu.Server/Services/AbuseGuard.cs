using Microsoft.Extensions.Caching.Memory;

namespace XcOsu.Server.Services;

// Bounded, expiring counters. Never store credentials or raw tokens.
public static class AbuseGuard
{
    private sealed class Counter { public int Count; }
    private static readonly MemoryCache counters = new(new MemoryCacheOptions { SizeLimit = 20000 });
    private static readonly SemaphoreSlim proxySlots = new(16, 16);
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

    public static async Task Middleware(HttpContext ctx, RequestDelegate next)
    {
        string path = ctx.Request.Path.Value ?? "";
        bool write = ctx.Request.Method is "POST" or "PUT" or "PATCH";
        string category = path == "/oauth/token" ? "login" : path == "/users" ? "register"
            : path == "/api/xc/proxy" ? "proxy" : path == "/api/xc/tm" ? "telemetry"
            : path.Contains("/chat/") && write ? "chat" : path.Contains("/reports") && write ? "reports"
            : (path.Contains("/password") || path.StartsWith("/api/xc/account/email/")) ? "password" : write ? "write" : "read";
        var bodyLimit = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false } && category is "login" or "register" or "password" or "reports")
            bodyLimit.MaxRequestBodySize = 16384;
        int limit = category switch { "login" => 20, "register" => 3, "proxy" => 120, "telemetry" => 30,
            "chat" => 30, "reports" => 5, "password" => 5, "write" => 60, _ => 600 };
        string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!Allow($"ip:{category}:{ip}", limit, TimeSpan.FromMinutes(1))
            || (ctx.User.UserId() is int u && !Allow($"user:{category}:{u}", limit, TimeSpan.FromMinutes(1))))
        {
            ctx.Response.StatusCode = 429;
            ctx.Response.Headers.RetryAfter = "60";
            return;
        }
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        if (category == "proxy")
        {
            if (!await proxySlots.WaitAsync(0, ctx.RequestAborted)) { ctx.Response.StatusCode = 429; return; }
            try { await next(ctx); }
            finally { proxySlots.Release(); }
        }
        else await next(ctx);
    }
}
