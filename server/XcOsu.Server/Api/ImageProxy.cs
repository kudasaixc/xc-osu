// xc-osu! server: images players put in their profile text, fetched by us instead of by every visitor. xc softworks (kudasaixc)

using System.Security.Cryptography;
using System.Text;
using XcOsu.Server.Data;
using XcOsu.Server.Services;

namespace XcOsu.Server.Api;

/// <summary>
/// <c>[img]</c>, imagemaps and Markdown images in a "me!" page point anywhere on the web. Loading them straight from the visitor's
/// browser handed every visitor's address to whoever wrote the page (a tracking pixel). The site loads them through here instead:
/// fetched once by the server (public addresses only, size-capped), decoded and re-encoded (no metadata, no active content),
/// cached on disk for a day.
/// </summary>
public static class ImageProxy
{
    private const long max_bytes = 8L * 1024 * 1024;
    private const long cache_limit = 1L * 1024 * 1024 * 1024;

    public static void Map(WebApplication app)
    {
        string dir = Path.Combine(app.Configuration["DataDirectory"] ?? "data", "image-cache");
        Directory.CreateDirectory(dir);

        app.MapGet("/api/xc/image", async (HttpContext ctx, IHttpClientFactory factory, string u) =>
        {
            if (u.Length > 2048 || !Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
                return Results.StatusCode(400);
            string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
            string file = Path.Combine(dir, name);
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            ctx.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
            ctx.Response.Headers.CacheControl = "public, max-age=86400";

            if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1))
            {
                if (!AbuseGuard.Allow("image-fetch:" + ClientAddress.Key(ctx), 120, TimeSpan.FromMinutes(1))) return Results.StatusCode(429);
                byte[] data;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    // the "proxy" client refuses private and loopback addresses at connect time and never follows redirects.
                    using var response = await factory.CreateClient("proxy").GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > max_bytes) return Results.StatusCode(404);
                    using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                    using var buffer = new MemoryStream();
                    byte[] chunk = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(chunk, timeout.Token)) > 0)
                    {
                        if (buffer.Length + read > max_bytes) return Results.StatusCode(404);
                        buffer.Write(chunk, 0, read);
                    }
                    if (!Media.IsImage(buffer.GetBuffer().AsSpan(0, (int)Math.Min(buffer.Length, 16)))) return Results.StatusCode(404);
                    data = await SafeImages.Reencode(buffer.ToArray(), false, timeout.Token);
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or BadHttpRequestException or InvalidOperationException)
                {
                    return Results.StatusCode(404);
                }
                prune(dir);
                await SafeImages.WriteAtomic(file, data, ctx.RequestAborted);
            }
            return Media.Serve(file, "badge-xc.png");
        });
    }

    private static void prune(string dir)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles().OrderBy(f => f.LastWriteTimeUtc).ToArray();
            long total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= cache_limit) break;
                total -= f.Length;
                f.Delete();
            }
        }
        catch (IOException) { }
    }
}
