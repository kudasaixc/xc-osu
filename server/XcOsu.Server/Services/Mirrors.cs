// xc-osu! server — beatmap mirrors (catboy.best → osu.direct → nerinyan). xc softworks (kudasaixc)

using System.Net;
using Newtonsoft.Json.Linq;
using XcOsu.Server.Data;

namespace XcOsu.Server.Services
{
    public sealed class Mirrors
    {
        private static readonly string[] api_bases = { "https://catboy.best/api/v2", "https://osu.direct/api/v2" };

        private readonly HttpClient http;
        private readonly Db db;
        private readonly ILogger<Mirrors> log;
        private readonly string osuCache;

        public Mirrors(IHttpClientFactory factory, Db db, ILogger<Mirrors> log)
        {
            http = factory.CreateClient("mirrors");
            this.db = db;
            this.log = log;
            osuCache = Path.Combine(db.DataDirectory, "osu-files");
            Directory.CreateDirectory(osuCache);
        }

        private async Task<JToken?> getJson(string path, CancellationToken ct)
        {
            foreach (string b in api_bases)
            {
                try
                {
                    using var res = await http.GetAsync(b + path, ct);
                    if (res.StatusCode == HttpStatusCode.NotFound) return null;
                    if (!res.IsSuccessStatusCode) continue;
                    return JToken.Parse(await res.Content.ReadAsStringAsync(ct));
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException)
                {
                    log.LogWarning("mirror {Base} failed: {Msg}", b, e.Message);
                }
            }

            return null;
        }

        /// <summary>Search using the osu! API v2 parameter names (q, m, s, sort, nsfw…). Returns a list of beatmapset JSON objects.</summary>
        public async Task<JArray> Search(IQueryCollection query, int page, CancellationToken ct)
        {
            var qs = new List<string>();
            foreach (var (k, v) in query)
            {
                if (k is "cursor_string" or "access_token" || k.StartsWith("cursor")) continue;
                string key = k, value = v.ToString();

                // the mirrors take "mode" / numeric "status" where osu-web takes "m" / "s".
                if (k == "m") key = "mode";
                else if (k == "s")
                {
                    int? status = value switch { "ranked" => 1, "qualified" => 3, "loved" => 4, "pending" => 0, "wip" => -1, "graveyard" => -2, _ => null };
                    if (status == null) continue; // "any", "leaderboard", "mine", "favourites": no mirror equivalent
                    key = "status";
                    value = status.Value.ToString();
                }

                qs.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
            }

            qs.Add("amount=50");
            qs.Add($"offset={page * 50}");
            qs.Add($"page={page}");

            var res = await getJson("/search?" + string.Join('&', qs), ct);
            var sets = res switch
            {
                JArray a => a,
                JObject o when o["beatmapsets"] is JArray a => a,
                _ => new JArray(),
            };

            foreach (var s in sets.OfType<JObject>())
                cacheSet(s);

            // osu-web's default ("leaderboard") only lists maps that have a leaderboard.
            string sFilter = query["s"].ToString();
            if (sFilter is "" or "leaderboard")
                sets = new JArray(sets.OfType<JObject>().Where(set => Scores.BeatmapStatus(set) > 0));
            return sets;
        }

        public async Task<JObject?> GetBeatmapset(int id, CancellationToken ct)
        {
            using (var c = db.Open())
            {
                string? cached = c.Scalar<string>("SELECT json FROM beatmapsets WHERE id = $i AND fetched_at > $t", ("$i", id), ("$t", now() - 3600));
                if (cached != null) return JObject.Parse(cached);
            }

            var set = await getJson($"/s/{id}", ct) as JObject;
            if (set != null) cacheSet(set);
            return set;
        }

        public async Task<JObject?> GetBeatmap(int? id, string? checksum, string? filename, CancellationToken ct)
        {
            using (var c = db.Open())
            {
                string? cached = id != null
                    ? c.Scalar<string>("SELECT json FROM beatmaps WHERE id = $i AND fetched_at > $t", ("$i", id), ("$t", now() - 3600))
                    : checksum != null
                        ? c.Scalar<string>("SELECT json FROM beatmaps WHERE checksum = $c AND fetched_at > $t", ("$c", checksum), ("$t", now() - 3600))
                        : null;
                if (cached != null) return JObject.Parse(cached);
            }

            JObject? map = null;
            if (id != null) map = await getJson($"/b/{id}", ct) as JObject;
            else if (checksum != null) map = await getJson($"/md5/{checksum}", ct) as JObject;
            if (map == null) return null;

            // ensure the parent set is attached and cached
            if (map["beatmapset"] is not JObject && map["beatmapset_id"] != null)
            {
                var set = await GetBeatmapset(map.Value<int>("beatmapset_id"), ct);
                if (set != null)
                {
                    var s = (JObject)set.DeepClone();
                    s.Remove("beatmaps");
                    map["beatmapset"] = s;
                }
            }

            cacheMap(map);
            return map;
        }

        /// <summary>Caches search results in one transaction (search used to open a connection per set and per difficulty).</summary>
        public void CacheSets(IEnumerable<JObject> sets)
        {
            try
            {
                using var c = db.Open();
                c.Exec("BEGIN");
                foreach (var s in sets)
                    cacheSet(s, c);
                c.Exec("COMMIT");
            }
            catch (Exception e)
            {
                log.LogWarning("caching search results failed: {Msg}", e.Message);
            }
        }

        private void cacheSet(JObject set, Microsoft.Data.Sqlite.SqliteConnection? conn = null)
        {
            int? id = set.Value<int?>("id");
            if (id == null) return;
            var owned = conn == null ? db.Open() : null;
            var c = conn ?? owned!;
            using var _ = owned;
            c.Exec("INSERT OR REPLACE INTO beatmapsets (id, json, fetched_at) VALUES ($i, $j, $t)", ("$i", id), ("$j", set.ToString(Newtonsoft.Json.Formatting.None)), ("$t", now()));
            if (set["beatmaps"] is JArray maps)
            {
                var parent = (JObject)set.DeepClone();
                parent.Remove("beatmaps");
                parent.Remove("converts");
                foreach (var m in maps.OfType<JObject>())
                {
                    var copy = (JObject)m.DeepClone();
                    copy["beatmapset"] = parent;
                    cacheMap(copy, c);
                }
            }
        }

        private void cacheMap(JObject map, Microsoft.Data.Sqlite.SqliteConnection? conn = null)
        {
            int? id = map.Value<int?>("id");
            if (id == null) return;
            var c = conn ?? db.Open();
            try
            {
                c.Exec("INSERT OR REPLACE INTO beatmaps (id, set_id, checksum, status, json, fetched_at) VALUES ($i, $s, $c, $st, $j, $t)",
                    ("$i", id), ("$s", map.Value<int?>("beatmapset_id") ?? 0), ("$c", map.Value<string>("checksum")),
                    ("$st", statusValue(map["status"] ?? map["ranked"])), ("$j", map.ToString(Newtonsoft.Json.Formatting.None)), ("$t", now()));
            }
            finally
            {
                if (conn == null) c.Dispose();
            }
        }

        private static int statusValue(JToken? t) => t?.Type switch
        {
            JTokenType.Integer => t.Value<int>(),
            JTokenType.String => t.Value<string>() switch
            {
                "graveyard" => -2, "wip" => -1, "pending" => 0, "ranked" => 1, "approved" => 2, "qualified" => 3, "loved" => 4, _ => 0,
            },
            _ => 0,
        };

        /// <summary>Raw .osu file, cached on disk (needed for pp computation).</summary>
        public async Task<byte[]?> GetOsuFile(int beatmapId, CancellationToken ct)
        {
            string path = Path.Combine(osuCache, $"{beatmapId}.osu");
            if (File.Exists(path)) return await File.ReadAllBytesAsync(path, ct);

            foreach (string url in new[] { $"https://catboy.best/osu/{beatmapId}", $"https://osu.direct/api/osu/{beatmapId}" })
            {
                try
                {
                    using var res = await http.GetAsync(url, ct);
                    if (!res.IsSuccessStatusCode) continue;
                    byte[] data = await res.Content.ReadAsByteArrayAsync(ct);
                    if (data.Length < 16) continue;
                    await File.WriteAllBytesAsync(path, data, ct);
                    return data;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                    log.LogWarning("osu file mirror {Url} failed: {Msg}", url, e.Message);
                }
            }

            return null;
        }

        /// <summary>Streams a beatmapset archive from the first mirror that answers.</summary>
        public async Task<HttpResponseMessage?> Download(int setId, bool noVideo, CancellationToken ct)
        {
            string[] urls =
            {
                $"https://catboy.best/d/{setId}{(noVideo ? "n" : "")}",
                $"https://osu.direct/api/d/{setId}{(noVideo ? "?noVideo=1" : "")}",
                $"https://api.nerinyan.moe/d/{setId}{(noVideo ? "?nv=1" : "")}",
            };

            foreach (string url in urls)
            {
                try
                {
                    var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (res.IsSuccessStatusCode && res.Content.Headers.ContentType?.MediaType != "application/json" && res.Content.Headers.ContentType?.MediaType != "text/html")
                        return res;
                    res.Dispose();
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                    log.LogWarning("download mirror {Url} failed: {Msg}", url, e.Message);
                }
            }

            return null;
        }

        private static long now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}
