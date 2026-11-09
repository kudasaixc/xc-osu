// xc-osu! server — same-origin proxy for the web client's cross-origin assets (CORS / COEP). xc softworks (kudasaixc)
// Only allow-listed hosts are reachable; no client headers (auth, cookies) are forwarded.

using System.Collections.Concurrent;

namespace XcOsu.Server.Api
{
    public static class Proxy
    {
        private static readonly HashSet<string> allowed_hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "assets.ppy.sh", "a.ppy.sh", "b.ppy.sh", "i.ppy.sh", "s.ppy.sh", "osu.ppy.sh",
            "catboy.best", "central.catboy.best", "api.nerinyan.moe", "osu.direct",
        };

        private const long max_bytes = 256L * 1024 * 1024;

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> cache_locks = new ConcurrentDictionary<string, SemaphoreSlim>();

        public static void Map(WebApplication app)
        {
            string cacheDir = Path.Combine(app.Configuration["DataDirectory"] ?? "data", "proxy-cache");
            Directory.CreateDirectory(cacheDir);

            app.MapGet("/api/xc/proxy", async (HttpContext ctx, IHttpClientFactory factory, string u) =>
            {
                if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !allowed_hosts.Contains(uri.Host) || !uri.IsDefaultPort)
                    return Results.StatusCode(403);

                var http = factory.CreateClient("mirrors");

                // large, versioned client resources (online.db etc.) are cached on disk.
                if (uri.Host == "assets.ppy.sh" && uri.AbsolutePath.StartsWith("/client-resources/", StringComparison.Ordinal))
                {
                    string name = string.Concat(uri.AbsolutePath.Split('/').Last().Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'));
                    string file = Path.Combine(cacheDir, name);
                    var gate = cache_locks.GetOrAdd(file, _ => new SemaphoreSlim(1, 1));
                    await gate.WaitAsync(ctx.RequestAborted);

                    try
                    {
                        if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1))
                        {
                            using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                            if (!resp.IsSuccessStatusCode) return Results.StatusCode((int)resp.StatusCode);
                            string tmp = file + ".tmp";
                            await using (var fs = File.Create(tmp))
                                await copyCapped(await resp.Content.ReadAsStreamAsync(ctx.RequestAborted), fs, ctx.RequestAborted);
                            File.Move(tmp, file, true);
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }

                    ctx.Response.Headers.CacheControl = "public, max-age=3600";
                    return Results.File(file, "application/octet-stream", enableRangeProcessing: true);
                }

                var upstream = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                if (!upstream.IsSuccessStatusCode)
                {
                    upstream.Dispose();
                    return Results.StatusCode((int)upstream.StatusCode);
                }

                if (upstream.Content.Headers.ContentLength > max_bytes)
                {
                    upstream.Dispose();
                    return Results.StatusCode(413);
                }

                ctx.Response.RegisterForDispose(upstream);
                ctx.Response.Headers.CacheControl = "public, max-age=86400";
                string type = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                return Results.Stream(await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted), type);
            });
        }

        private static async Task copyCapped(Stream from, Stream to, CancellationToken ct)
        {
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;

            while ((read = await from.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > max_bytes) throw new InvalidOperationException("proxied file too large");
                await to.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
    }
}
