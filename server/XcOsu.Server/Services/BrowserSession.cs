namespace XcOsu.Server.Services;

public static class BrowserSession
{
    public const string AccessCookie = "__Host-xc-access";
    public const string RefreshCookie = "__Host-xc-refresh";
    public static bool SameOrigin(HttpRequest request) =>
        Uri.TryCreate(request.Headers.Origin, UriKind.Absolute, out var origin)
        && origin.GetLeftPart(UriPartial.Authority).Equals($"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    public static void Set(HttpResponse response, string access, string refresh)
    {
        var options = new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromDays(30) };
        response.Cookies.Append(AccessCookie, access, options);
        response.Cookies.Append(RefreshCookie, refresh, options);
        response.Headers.CacheControl = "no-store";
    }
    public static void Clear(HttpResponse response)
    {
        var options = new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" };
        response.Cookies.Delete(AccessCookie, options);
        response.Cookies.Delete(RefreshCookie, options);
    }
}
