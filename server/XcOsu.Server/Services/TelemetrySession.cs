using System.Security.Cryptography;
using System.Text;

namespace XcOsu.Server.Services;

// Anonymous, short-lived proof of a same-origin browser session. Contains no user identifier.
public static class TelemetrySession
{
    private const string cookie = "__Host-xc-telemetry";
    private static readonly byte[] key = RandomNumberGenerator.GetBytes(32);

    public static IResult Issue(HttpContext context)
    {
        if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site") return Results.StatusCode(403);
        if (!AbuseGuard.Allow("telemetry-session:" + context.Connection.RemoteIpAddress, 30, TimeSpan.FromHours(1)))
            return Results.StatusCode(429);
        string payload = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        string token = payload + "." + Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));
        context.Response.Cookies.Append(cookie, token, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromHours(1), IsEssential = true });
        context.Response.Headers.CacheControl = "no-store";
        return Results.NoContent();
    }

    public static bool Valid(HttpRequest request)
    {
        if (!BrowserSession.SameOrigin(request)) return false;
        string? token = request.Cookies[cookie];
        if (token == null || token.Length > 128) return false;
        var parts = token.Split('.');
        if (parts.Length != 3 || !long.TryParse(parts[0], out long expiry)
            || expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(parts[2]),
                HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(parts[0] + "." + parts[1])));
        }
        catch (FormatException) { return false; }
    }
}
