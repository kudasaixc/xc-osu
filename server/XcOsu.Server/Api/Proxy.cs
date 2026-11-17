// xc-osu! server: same-origin proxy for the web client's cross-origin assets (CORS / COEP). xc softworks (kudasaixc)
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

        // Mirrors hand downloads over to their own storage hosts (dl.nerinyan.moe, storage.osu.direct, ...): a redirect may stay
        // inside the family of an allowed host, and nowhere else. Proxy:RedirectHosts adds exact hosts for a mirror's external CDN.
        private static readonly string[] redirect_suffixes = { ".ppy.sh", ".catboy.best", ".nerinyan.moe", ".osu.direct" };
        private static HashSet<string> extra_redirect_hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static bool RedirectAllowed(string host) =>
            allowed_hosts.Contains(host) || extra_redirect_hosts.Contains(host)
            || redirect_suffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        /// <summary>The large versioned client resources (cached on disk) may be big; anything else the game fetches (covers, previews, avatars) is small.</summary>
        private const long max_bytes = 256L * 1024 * 1024;
        private const long max_bytes_other = 32L * 1024 * 1024;

        /// <summary>What one address may pull through the proxy in an hour: plenty for playing, nothing like a free CDN.</summary>
        private const long bytes_per_address_hour = 3L * 1024 * 1024 * 1024;

        /// <summary>Held only while the cache directory itself changes (eviction, a file moving in); downloads run outside it.</summary>
        internal static readonly SemaphoreSlim cache_gate = new(1, 1);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> file_gates = new();

        public static void Map(WebApplication app)
        {
            string cacheDir = Path.Combine(app.Configuration["DataDirectory"] ?? "data", "proxy-cache");
            Directory.CreateDirectory(cacheDir);
            extra_redirect_hosts = new HashSet<string>(app.Configuration.GetSection("Proxy:RedirectHosts").Get<string[]>() ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            app.MapGet("/api/xc/proxy", async (HttpContext ctx, IHttpClientFactory factory, string u) =>
            {
                if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !allowed_hosts.Contains(uri.Host) || !uri.IsDefaultPort)
                    return Results.StatusCode(403);

                var http = factory.CreateClient("proxy");
                string address = Services.ClientAddress.Key(ctx);
                if (Services.AbuseGuard.Count("proxy-bytes:" + address) > bytes_per_address_hour) return Results.StatusCode(429);

                // large, versioned client resources (online.db etc.) are cached on disk.
                if (uri.Host == "assets.ppy.sh" && uri.AbsolutePath.StartsWith("/client-resources/", StringComparison.Ordinal))
                {
                    string name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
                    string file = Path.Combine(cacheDir, name);
                    // one download per file at a time; other files (and the rest of the proxy) are never held up by it.
                    var gate = file_gates.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
                    await gate.WaitAsync(ctx.RequestAborted);

                    FileStream cachedStream;
                    try
                    {
                        if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1))
                        {
                            using var resp = await fetch(http, uri, ctx.RequestAborted);
                            if (!resp.IsSuccessStatusCode) return Results.StatusCode((int)resp.StatusCode);
                            string tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                            try
                            {
                                await using (var fs = File.Create(tmp))
                                await using (var source = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted))
                                    await copyCapped(source, fs, max_bytes, null, ctx.RequestAborted);

                                await cache_gate.WaitAsync(ctx.RequestAborted);
                                try
                                {
                                    var files = new DirectoryInfo(cacheDir).GetFiles().Where(f => f.Extension != ".tmp").OrderBy(f => f.LastAccessTimeUtc).ToArray();
                                    long size = files.Sum(f => f.Length);
                                    foreach (var old in files)
                                    {
                                        if (size <= 2L * 1024 * 1024 * 1024 - max_bytes) break;
                                        long length = old.Length;
                                        old.Delete();
                                        size -= length;
                                    }
                                    File.Move(tmp, file, true);
                                }
                                finally { cache_gate.Release(); }
                            }
                            finally { if (File.Exists(tmp)) File.Delete(tmp); }
                        }
                        cachedStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                        File.SetLastAccessTimeUtc(file, DateTime.UtcNow);
                    }
                    finally
                    {
                        gate.Release();
                    }

                    Services.AbuseGuard.Spend("proxy-bytes:" + address, cachedStream.Length, long.MaxValue, TimeSpan.FromHours(1));
                    ctx.Response.Headers.CacheControl = "public, max-age=3600";
                    return Results.Stream(cachedStream, "application/octet-stream", enableRangeProcessing: true);
                }

                var upstream = await fetch(http, uri, ctx.RequestAborted);
                if (!upstream.IsSuccessStatusCode)
                {
                    upstream.Dispose();
                    return Results.StatusCode((int)upstream.StatusCode);
                }

                if (upstream.Content.Headers.ContentLength > max_bytes_other)
                {
                    upstream.Dispose();
                    return Results.StatusCode(413);
                }

                ctx.Response.RegisterForDispose(upstream);
                ctx.Response.Headers.CacheControl = "public, max-age=86400";
                string type = upstream.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                if (type is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp" or "audio/mpeg" or "audio/ogg" or "application/octet-stream" or "application/zip"))
                {
                    type = "application/octet-stream";
                    ctx.Response.Headers.ContentDisposition = "attachment";
                }
                ctx.Response.Headers.XContentTypeOptions = "nosniff";
                ctx.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
                return Results.Stream(async output =>
                {
                    using var source = await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted);
                    await copyCapped(source, output, max_bytes_other, "proxy-bytes:" + address, ctx.RequestAborted);
                }, type);
            });
        }

        private static async Task<HttpResponseMessage> fetch(HttpClient http, Uri uri, CancellationToken ct)
        {
            for (int redirects = 0; redirects < 5; redirects++)
            {
                // the first hop must be an allowed host and every redirect must stay with that mirror's own hosts, so an open
                // redirect upstream cannot turn this into a general-purpose fetcher (private / loopback targets are additionally
                // refused by PublicNetworkHandler at connect time).
                if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
                    || !(redirects == 0 ? allowed_hosts.Contains(uri.Host) : RedirectAllowed(uri.Host)))
                    throw new BadHttpRequestException("Invalid upstream", 403);
                var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null) throw new BadHttpRequestException("Invalid redirect", 502);
                uri = new Uri(uri, location);
            }
            throw new BadHttpRequestException("Too many redirects", 502);
        }

        private static async Task copyCapped(Stream from, Stream to, long cap, string? budget, CancellationToken ct)
        {
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;

            while ((read = await from.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > cap) throw new InvalidOperationException("proxied file too large");
                if (budget != null) Services.AbuseGuard.Spend(budget, read, long.MaxValue, TimeSpan.FromHours(1));
                await to.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
    }
}
